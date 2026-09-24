#!/usr/bin/env python3
"""Flag commits that change production code and tests together without recording it in FIXED_ISSUES.md.

Docs/GOAL.md: production code changes only when the maintainer asks, every production change that lands is
recorded in Docs/FIXED_ISSUES.md with its commit, and it lands in its own commit, never folded into the diff
that adds a test. A commit touching both Assets/BroAudio/**/*.cs and Assets/Tests/ is exactly how a fix gets
folded into a test diff unrecorded, so each such commit must also touch Docs/FIXED_ISSUES.md.

The same holds for the range as a whole (a pull request, or a push): if it changes production .cs and tests
anywhere, FIXED_ISSUES.md must change somewhere in it.

A commit that genuinely needs neither - a comment-only edit, a rename a test follows - says so with a
trailer, which is printed so a reviewer sees it:

    No-Fixed-Issue: <reason>

Merge commits are skipped; their content is checked through the commits they merge.

Usage: check_fixed_issues_record.py <base> <head>
       Checks base..head. A base of all zeros (a branch's first push) or one missing from the clone checks
       head alone.
"""
import subprocess
import sys

FIXED_ISSUES = "Docs/FIXED_ISSUES.md"
TRAILER = "No-Fixed-Issue:"


def git(*args):
    return subprocess.run(("git",) + args, check=True, capture_output=True, text=True).stdout


def exists(revision):
    return subprocess.run(("git", "cat-file", "-e", revision + "^{commit}"), capture_output=True).returncode == 0


def is_production(path):
    return path.startswith("Assets/BroAudio/") and path.endswith(".cs")


def is_test(path):
    return path.startswith("Assets/Tests/")


def main(argv):
    if len(argv) != 3:
        sys.exit(f"usage: {argv[0]} <base> <head>")
    base, head = argv[1], argv[2]

    if base.strip("0") and exists(base):
        commits = git("rev-list", "--no-merges", "--reverse", f"{base}..{head}").split()
    else:
        print(f"No usable base ({base or 'none'}); checking {head} alone.")
        commits = git("rev-list", "--no-merges", "-n", "1", head).split()

    violations, exempted = [], []
    range_production, range_tests, range_fixed = [], [], False
    for commit in commits:
        # -M so a moved file reads as its new path; --root so a parentless commit still lists its files.
        files = git("diff-tree", "--no-commit-id", "--name-only", "-r", "-M", "--root", commit).split("\n")
        files = [f for f in files if f]
        production = [f for f in files if is_production(f)]
        tests = [f for f in files if is_test(f)]
        fixed = FIXED_ISSUES in files
        subject = git("log", "-1", "--format=%s", commit).strip()
        body = git("log", "-1", "--format=%B", commit)
        trailer = next((line for line in body.splitlines() if line.startswith(TRAILER)), None)

        if trailer:
            if production and tests:
                exempted.append(f"{commit[:10]} {subject}: {trailer}")
            range_fixed = range_fixed or fixed
            continue

        range_production += production
        range_tests += tests
        range_fixed = range_fixed or fixed
        if production and tests and not fixed:
            violations.append(f"{commit[:10]} {subject} - production: {', '.join(production[:3])}"
                              + (" ..." if len(production) > 3 else ""))

    print(f"Checked {len(commits)} commit(s).")
    for line in exempted:
        print(f"::notice title=Production and tests changed without a FIXED_ISSUES entry, by declaration::{line}")

    problems = [f"commit {line}" for line in violations]
    if range_production and range_tests and not range_fixed and not violations:
        problems.append(f"the range changes production code ({', '.join(sorted(set(range_production))[:3])}) and "
                        f"tests, but no commit in it touches {FIXED_ISSUES}")
    if problems:
        for problem in problems:
            print(f"::error title=Production change without a FIXED_ISSUES entry::{problem}")
        sys.exit(
            f"\n{len(problems)} change(s) touch Assets/BroAudio/**/*.cs and Assets/Tests/ without {FIXED_ISSUES}:\n  "
            + "\n  ".join(problems) + "\n"
            "Record the production change in FIXED_ISSUES.md, in its own commit (Docs/GOAL.md). If it genuinely "
            f"needs no entry, add a '{TRAILER} <reason>' trailer to the commit that makes it.")
    print("No commit folds an unrecorded production change into a test change.")


if __name__ == "__main__":
    main(sys.argv)
