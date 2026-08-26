---
name: git-branch-graph
description: Render a repository's commit and branch history as a self-contained, mobile-friendly HTML page — the lanes, merges and branch tips of `git log --graph`, but legible on a phone and tappable for detail. Use this whenever someone wants to SEE git history rather than read it: asks for a branch graph, commit graph, network graph, history timeline or visual changelog; asks "what happened this month/week/sprint" in a repo; wants to review branches on their phone, share history with someone who won't run git, or put a picture of the release history in front of a team. Reach for this before hand-writing any HTML or pasting `git log --graph` ASCII — a bundled script does extraction, lane layout and rendering in one deterministic pass.
---

# Git branch graph

`scripts/render_graph.py` does the whole job: reads the repo, assigns lanes,
computes statistics, and writes one standalone HTML file. Run it and report the
result. Everything below exists so you don't have to open the script.

## Run it

```bash
python3 .claude/skills/git-branch-graph/scripts/render_graph.py --out /tmp/graph.html
```

That covers the common ask ("this month's branches") — the default window is the
start of the current month, across all refs. Only the flags below change.

| Flag | Default | Use when |
|---|---|---|
| `--since` | start of this month | any git date: `2026-08-01`, `"3 weeks ago"`, `"last monday"` |
| `--until` | none | closing an explicit window |
| `--out` | `git-branch-graph.html` | where to write |
| `--repo` | `.` | the repo isn't the working directory |
| `--refs` | all refs | narrowing, e.g. `--refs origin/main origin/develop` |
| `--title` / `--name` | derived | overriding the heading or repo label |
| `--max-commits` | 400 | `0` lifts the cap; the script says when it truncated |
| `--fetch` | off | remote branches are missing locally (see below) |
| `--check` | off | you changed the layout code and want the invariants verified |
| `--json` | none | something else needs the graph data |

The script prints a short summary — counts, date span, lane count, branch names,
and any warning. **That summary is your report material. Don't read the HTML** —
it's 30–60 KB of generated markup and reading it buys nothing.

## Deliver it

The point is usually a phone, so publish the file as an Artifact and hand over
the URL; a local path is useless on a phone. Pass the written file straight to
the Artifact tool — no design pass, the template is already designed. If the
user only wants a local file, say where it is and stop.

## Two things that silently produce a wrong graph

**Unfetched branches.** A fresh or CI clone often has only one branch, so the
graph looks plausible while missing most of the history. The script checks
`origin` and warns, naming the branches; re-run with `--fetch` when it does.
Worth pre-empting in a clone you didn't set up.

**Author date vs. commit date.** The window filters on when work *landed*
(committer date, matching `git log --since`), while rows show when it was
*authored*. A rebased branch therefore shows older dates than its window — that
is correct, and the page's detail sheet flags each such commit. Don't "fix" it.

## Reading the page

Node shape encodes commit type, so the graph is scannable without a legend:
hollow ring = ordinary commit, ring with a core = merge, filled with a halo =
branch tip, dashed = an ancestor from before the window (drawn so in-window
merges converge somewhere instead of dangling). A merge's incoming line wears
the merged branch's colour, so two merges of one branch read as one line. Branch
chips filter to a tip's ancestry; tapping a commit opens its hash, refs and
parents, and parent buttons jump through the graph.

## Changing it

Layout, statistics and date formatting are Python (`scripts/render_graph.py`);
the page is `assets/template.html`, which receives a `__GRAPH_DATA__` JSON blob
and only converts precomputed lanes into pixels. Keeping the algorithm on one
side of that line is what makes output byte-identical across runs — resist
recomputing anything in the browser.

Styling changes go in the template's `:root` token blocks, which define light
first and redefine dark twice (a `prefers-color-scheme` block guarded against an
explicit light choice, and a `[data-theme="dark"]` block). A colour defined only
inside one of those blocks renders unreadably in the other states.

After touching the layout, run with `--check`: it verifies that every parent link
is drawn exactly once, that no two edges share a lane over overlapping rows, and
that no edge passes through a commit node. Overlapping lanes still *look* like a
graph, which is why the check exists rather than trusting a glance.
