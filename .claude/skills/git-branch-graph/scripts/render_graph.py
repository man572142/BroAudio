#!/usr/bin/env python3
"""Render a git commit/branch graph as a self-contained, mobile-friendly HTML page.

The whole pipeline lives here on purpose: extraction, lane layout, statistics and
formatting all happen at build time, so the same repository state and the same
arguments always produce the same bytes. The page itself only turns precomputed
lanes into pixels.

    python3 render_graph.py --since "2026-08-01" --out graph.html
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timedelta, timezone

SEP, REC = "\x1f", "\x1e"
FIELDS = ["%H", "%P", "%an", "%aI", "%cI", "%D", "%s"]
FMT = SEP.join(FIELDS) + REC
LANE_COLORS = 8
MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun",
          "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]


# --------------------------------------------------------------------------- git

class GitError(RuntimeError):
    pass


def git(repo: str, *args: str) -> str:
    proc = subprocess.run(
        ["git", "-C", repo, *args], capture_output=True, text=True, encoding="utf-8"
    )
    if proc.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed:\n{proc.stderr.strip()}")
    return proc.stdout


def parse_log(raw: str) -> list[dict]:
    out = []
    for rec in raw.split(REC):
        rec = rec.strip("\n")
        if not rec.strip():
            continue
        h, parents, author, adate, cdate, decor, subject = rec.split(SEP)
        refs = []
        if decor:
            for ref in decor.split(","):
                ref = ref.strip().replace("HEAD -> ", "")
                if ref and ref != "HEAD" and not ref.startswith("tag: "):
                    refs.append(ref)
        out.append({
            "h": h,
            "p": parents.split() if parents else [],
            "an": author,
            "d": adate,
            "cd": cdate,
            "refs": refs,
            "s": subject,
        })
    return out


def unfetched_branches(repo: str) -> list[str]:
    """Remote branches with no local ref.

    A fresh CI clone often carries only the checked-out branch, and a graph built
    from that silently omits every other line of development — the failure mode is
    a plausible-looking but wrong picture, so it is worth reporting loudly.
    """
    try:
        remote = git(repo, "ls-remote", "--heads", "origin")
    except GitError:
        return []
    have = {
        line.strip() for line in
        git(repo, "for-each-ref", "--format=%(refname:short)", "refs/remotes/origin").splitlines()
    }
    missing = []
    for line in remote.splitlines():
        if "\t" not in line:
            continue
        name = line.split("\t", 1)[1].replace("refs/heads/", "").strip()
        if name and f"origin/{name}" not in have:
            missing.append(name)
    return sorted(missing)


# ------------------------------------------------------------------------ layout

def layout(commits: list[dict]) -> tuple[list[dict], list[dict], int]:
    """Assign every commit a lane and build the edge list.

    Walks newest-first keeping one slot per in-flight edge. A commit takes the
    leftmost lane already waiting on it; its first parent inherits that lane and
    extra parents open new ones. Reserving a lane at the child row is what keeps
    the vertical runs collision-free all the way down to the parent.
    """
    row_of = {c["h"]: i for i, c in enumerate(commits)}
    lanes: list[dict | None] = []
    edges: list[dict] = []
    placed: list[dict] = []
    color_cursor = 0
    max_lane = 0

    def claim() -> int:
        for i, slot in enumerate(lanes):
            if slot is None:
                return i
        lanes.append(None)
        return len(lanes) - 1

    for row, c in enumerate(commits):
        arriving = [i for i, slot in enumerate(lanes) if slot and slot["target"] == c["h"]]
        if arriving:
            lane = arriving[0]
            color = lanes[arriving[0]]["color"]
        else:
            lane = claim()
            color = color_cursor
            color_cursor += 1

        for i in arriving:
            lanes[i]["parentRow"] = row
            lanes[i]["parentLane"] = lane
            lanes[i] = None
        lanes[lane] = None
        placed.append({"lane": lane, "color": color})

        for n, parent in enumerate(c["p"]):
            if parent not in row_of:
                continue
            reserved = lane if n == 0 else claim()
            if n == 0:
                edge_color = color
            else:
                edge_color = color_cursor
                color_cursor += 1
            edge = {
                "target": parent,
                "childRow": row,
                "childLane": lane,
                "parentRow": None,
                "parentLane": None,
                "lane": reserved,
                "color": edge_color,
                "merge": n > 0,
            }
            lanes[reserved] = edge
            edges.append(edge)

        max_lane = max(max_lane, lane, *(i for i, s in enumerate(lanes) if s), 0)

    # A second-parent line *is* the branch being merged in, so repaint it with
    # that branch's colour now that the parent's own lane colour has settled.
    # Two merges of the same branch then read as one consistent line.
    for e in edges:
        if e["merge"] and e["parentRow"] is not None:
            e["color"] = placed[e["parentRow"]]["color"]
        e.pop("target", None)

    return placed, edges, max_lane + 1


# ---------------------------------------------------------------------- formatting

def fmt_dates(iso: str) -> tuple[str, str]:
    """Format in the commit's own timezone, the way git shows it.

    Formatting here rather than in the browser keeps the page identical for every
    viewer and stops a commit sliding across a date boundary on someone else's clock.
    """
    dt = datetime.fromisoformat(iso)
    short = f"{MONTHS[dt.month - 1]} {dt.day}, {dt:%H:%M}"
    long = f"{MONTHS[dt.month - 1]} {dt.day}, {dt.year} at {dt:%H:%M}"
    return short, long


def parse_date(value: str) -> datetime | None:
    """Parse an ISO-ish --since. Git also takes relative dates we can't resolve here."""
    for fmt in ("%Y-%m-%d", "%Y-%m-%dT%H:%M:%S", "%Y/%m/%d", "%Y-%m"):
        try:
            return datetime.strptime(value, fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    return None


def span_label(first: datetime, last: datetime) -> str:
    """Carry the year only when the two ends disagree about it; the page heading
    already names the window, so repeating it on every span is noise."""
    def part(d: datetime, with_year: bool) -> str:
        base = f"{MONTHS[d.month - 1]} {d.day}"
        return f"{base}, {d.year}" if with_year else base

    if first.date() == last.date():
        return part(first, True)
    cross = first.year != last.year
    return f"{part(first, cross)} – {part(last, cross)}"


def month_start(now: datetime) -> str:
    return now.replace(day=1).strftime("%Y-%m-%d")


def window_title(since: str, until: str | None) -> str:
    try:
        start = datetime.fromisoformat(since)
    except ValueError:
        return f"Since {since}"
    if until:
        return f"{MONTHS[start.month - 1]} {start.day} – {until}"
    nxt = (start.replace(day=28) + timedelta(days=4)).replace(day=1)
    if start.day == 1 and (nxt - start).days >= 28:
        return f"{MONTHS[start.month - 1]} {start.year}"
    return f"Since {MONTHS[start.month - 1]} {start.day}, {start.year}"


# --------------------------------------------------------------------------- build

def build(args) -> dict:
    repo = os.path.abspath(args.repo)
    try:
        git(repo, "rev-parse", "--git-dir")
    except GitError:
        raise SystemExit(f"error: {repo} is not a git repository")

    if args.fetch:
        git(repo, "fetch", "origin", "+refs/heads/*:refs/remotes/origin/*", "--no-tags")

    log_args = ["log", "--date-order", f"--pretty=format:{FMT}"]
    log_args += args.refs if args.refs else ["--all"]
    log_args.append(f"--since={args.since}")
    if args.until:
        log_args.append(f"--until={args.until}")
    if args.max_commits:
        log_args.append(f"--max-count={args.max_commits}")

    commits = parse_log(git(repo, *log_args))
    capped = bool(args.max_commits) and len(commits) >= args.max_commits
    if not commits:
        raise SystemExit(f"error: no commits in the window (since={args.since}). Widen --since.")

    have = {c["h"] for c in commits}

    # Pull in parents that fall outside the window as dimmed boundary nodes, so
    # in-window merges converge on something instead of dangling in mid-air.
    missing = []
    seen = set()
    for c in commits:
        for p in c["p"]:
            if p not in have and p not in seen:
                seen.add(p)
                missing.append(p)
    boundary = []
    if missing and not args.no_boundary:
        raw = git(repo, "show", "-s", f"--format={FMT}", *missing)
        for b in parse_log(raw):
            b["boundary"] = True
            b["p"] = []          # stop the walk here
            b["refs"] = []
            boundary.append(b)
        boundary.sort(key=lambda c: c["d"], reverse=True)

    rows = commits + boundary
    row_of = {c["h"]: i for i, c in enumerate(rows)}
    placed, edges, lane_count = layout(rows)

    for i, c in enumerate(rows):
        c.setdefault("boundary", False)
        c["dshort"], c["dlong"] = fmt_dates(c["d"])
        _, c["cdlong"] = fmt_dates(c["cd"])
        # Surface a rebase/cherry-pick rather than let the two dates quietly disagree.
        c["rebased"] = c["cd"][:10] != c["d"][:10]
        c["short"] = c["h"][:7]

    branches = []
    for line in git(repo, "for-each-ref", "--format=%(refname:short)%09%(objectname)",
                    "refs/remotes/origin", "refs/heads").splitlines():
        if "\t" not in line:
            continue
        name, sha = line.split("\t")
        if sha in have:
            branches.append({"name": name, "row": row_of[sha], "color": placed[row_of[sha]]["color"]})
    # Collapse origin/x and x to one entry, then read top-to-bottom like the graph.
    deduped, taken = [], set()
    for b in sorted(branches, key=lambda b: (b["row"], b["name"])):
        label = re.sub(r"^origin/", "", b["name"])
        if label in taken:
            continue
        taken.add(label)
        deduped.append({"name": label, "row": b["row"], "color": b["color"]})

    real = [c for c in rows if not c["boundary"]]
    # Report the authored range *inside* the window: a rebased commit can carry an
    # author date from long before it landed, and letting one drag the span back
    # mislabels the whole page. Relative dates ("2 weeks ago") can't be anchored,
    # so those fall back to the full range.
    start = parse_date(args.since)
    dated = sorted(((datetime.fromisoformat(c["d"]), c) for c in real), key=lambda t: t[0])
    inside = [t for t in dated if start is None or t[0] >= start] or dated
    span = span_label(inside[0][0], inside[-1][0])
    merges = sum(1 for c in real if len(c["p"]) > 1)

    return {
        "meta": {
            "repo": args.name or os.path.basename(repo),
            "title": args.title or window_title(args.since, args.until),
            "span": span,
            "stats": [
                [len(real), "commit" if len(real) == 1 else "commits"],
                [len(deduped), "branch" if len(deduped) == 1 else "branches"],
                [merges, "merge" if merges == 1 else "merges"],
                [len({c["an"] for c in real}), "author" if len({c["an"] for c in real}) == 1 else "authors"],
            ],
            "boundaryCount": len(boundary),
        },
        "capped": capped,
        "commits": rows,
        "lanes": placed,
        "edges": edges,
        "branches": deduped,
        "laneCount": lane_count,
    }


def check_layout(data: dict) -> list[str]:
    """Assert the drawing invariants. Overlapping lanes still *look* like a graph,
    so a silent violation is worse than a crash — this makes it loud."""
    rows, lanes, edges = data["commits"], data["lanes"], data["edges"]
    row_of = {c["h"]: i for i, c in enumerate(rows)}
    fails = []

    want = {(i, row_of[p]) for i, c in enumerate(rows) for p in c["p"] if p in row_of}
    got = {(e["childRow"], e["parentRow"]) for e in edges}
    if want != got:
        fails.append(f"edge set mismatch: missing {sorted(want - got)}, extra {sorted(got - want)}")

    for e in edges:
        if lanes[e["childRow"]]["lane"] != e["childLane"]:
            fails.append(f"edge from row {e['childRow']} does not start in that commit's lane")
        if lanes[e["parentRow"]]["lane"] != e["parentLane"]:
            fails.append(f"edge into row {e['parentRow']} does not end in that commit's lane")

    by_lane: dict[int, list[tuple[int, int]]] = {}
    for e in edges:
        by_lane.setdefault(e["lane"], []).append((e["childRow"], e["parentRow"]))
    for lane, spans in by_lane.items():
        spans.sort()
        for (a1, b1), (a2, b2) in zip(spans, spans[1:]):
            if a2 < b1:
                fails.append(f"lane {lane}: [{a1},{b1}] overlaps [{a2},{b2}]")

    for i in range(len(rows)):
        for e in edges:
            if e["lane"] == lanes[i]["lane"] and e["childRow"] < i < e["parentRow"]:
                fails.append(f"an edge runs straight through the node at row {i}")
    return fails


def main() -> int:
    now = datetime.now(timezone.utc)
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=".", help="repository path (default: cwd)")
    ap.add_argument("--since", default=month_start(now),
                    help="window start, any git date (default: start of this month)")
    ap.add_argument("--until", default=None, help="window end, any git date")
    ap.add_argument("--out", default="git-branch-graph.html", help="output HTML path")
    ap.add_argument("--title", default=None, help="page heading (default: derived from window)")
    ap.add_argument("--name", default=None, help="repo label (default: directory name)")
    ap.add_argument("--refs", nargs="*", default=None,
                    help="limit to these refs (default: --all)")
    ap.add_argument("--max-commits", type=int, default=400,
                    help="cap rows; 0 for no cap (default: 400)")
    ap.add_argument("--fetch", action="store_true",
                    help="fetch all remote branches first (mutates refs)")
    ap.add_argument("--no-boundary", action="store_true",
                    help="omit the dimmed pre-window ancestor rows")
    ap.add_argument("--json", default=None, help="also write the graph data as JSON")
    ap.add_argument("--check", action="store_true",
                    help="verify the layout invariants and exit non-zero on violation")
    args = ap.parse_args()

    data = build(args)

    if args.check:
        fails = check_layout(data)
        for f in fails:
            print(f"FAIL: {f}", file=sys.stderr)
        print(f"layout check: {len(data['commits'])} rows, {len(data['edges'])} edges, "
              f"{'OK' if not fails else str(len(fails)) + ' violation(s)'}")
        if fails:
            return 2

    here = os.path.dirname(os.path.abspath(__file__))
    template = os.path.join(here, "..", "assets", "template.html")
    with open(template, encoding="utf-8") as f:
        html = f.read()
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
    if "__GRAPH_DATA__" not in html:
        raise SystemExit(f"error: template {template} has no __GRAPH_DATA__ placeholder")
    html = html.replace("__GRAPH_DATA__", payload)
    html = html.replace("__PAGE_TITLE__", f"{data['meta']['repo']} Branch Graph")

    with open(args.out, "w", encoding="utf-8") as f:
        f.write(html)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2, sort_keys=True)

    m = data["meta"]
    print(f"{args.out}  ({os.path.getsize(args.out) // 1024} KB)")
    print("  " + "  ".join(f"{n} {w}" for n, w in m["stats"]) + f"  |  {m['span']}")
    print(f"  {data['laneCount']} lanes, {m['boundaryCount']} pre-window ancestor rows")
    for b in data["branches"]:
        print(f"    {b['name']}")

    if data["capped"]:
        print(f"\n  NOTE: stopped at --max-commits={args.max_commits}; older commits in the")
        print("  window are not shown. Raise it or pass --max-commits 0 for no cap.")

    stale = unfetched_branches(os.path.abspath(args.repo)) if not args.fetch else []
    if stale:
        print(f"\n  WARNING: {len(stale)} remote branch(es) not fetched locally, so they are")
        print(f"  missing from this graph: {', '.join(stale[:6])}"
              + (" …" if len(stale) > 6 else ""))
        print("  Re-run with --fetch to include them.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except BrokenPipeError:
        os._exit(0)          # a closed pipe (e.g. `| head`) is not an error
    except GitError as exc:
        raise SystemExit(f"error: {exc}")
