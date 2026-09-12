#!/usr/bin/env python3
"""Delete the Library caches this branch has superseded, keeping only the current key.

GitHub Actions cache keys are immutable: a save can add an entry, never replace one. The
Library key in test.yml hashes Assets/**, which in this repository is the entire product,
so all but the rarest push produces a key nobody has used and uploads another ~870 MB
alongside the copy the previous push left behind. Nothing will ever read that older copy -
the restore-keys prefix resolves to the newest match - but it holds its share of the
repository's 10 GB allowance until eviction reclaims it, and eviction is by least-recent
use, so what it reclaims first is usually the entry the next run wanted. Two test modes
make it ~1.75 GB a push, which is a handful of pushes to fill 10 GB.

So after each save, drop every entry on this branch whose key starts with this leg's prefix
except the one this run just used. The branch is left holding exactly one Library per test
mode. Scoping the listing to this ref keeps main's caches and the other leg's out of reach,
which is what lets the two matrix legs prune in parallel without racing.

Reads its inputs from the environment, all of which test.yml sets:
    GH_TOKEN         a token with actions: write, for the gh calls
    GITHUB_REPOSITORY / GITHUB_REF   the repository and branch to prune (set by the runner)
    CACHE_PREFIX     the key prefix owned by this leg, e.g. "Library-EditMode-"
    KEEP_KEY         the one key to spare - this run's primary key
"""
import json
import os
import subprocess
import sys

MEGABYTE = 1024 * 1024


def gh(*args):
    """Runs a gh subcommand and returns its stdout, or None if the call failed."""
    try:
        return subprocess.run(
            ("gh",) + args, check=True, capture_output=True, text=True
        ).stdout
    except FileNotFoundError:
        print("warning: gh is not installed; leaving the caches alone.")
    except subprocess.CalledProcessError as error:
        # Most likely the token lacks actions: write. Worth reporting, never worth failing a
        # test run over - the workflow step is continue-on-error for the same reason.
        print(f"warning: `gh {' '.join(args)}` failed: {error.stderr.strip()}")
    return None


def main():
    repository = os.environ["GITHUB_REPOSITORY"]
    ref = os.environ["GITHUB_REF"]
    prefix = os.environ["CACHE_PREFIX"]
    keep = os.environ.get("KEEP_KEY", "")

    listing = gh(
        "cache", "list",
        "--repo", repository,
        "--ref", ref,
        "--limit", "100",
        "--json", "id,key,sizeInBytes",
    )
    if listing is None:
        return 0

    # Match the prefix here rather than through `gh cache list --key`, so that this stays a
    # prefix match whatever that flag is documented to mean in the runner's version of gh.
    superseded = [
        entry for entry in json.loads(listing)
        if entry["key"].startswith(prefix) and entry["key"] != keep
    ]
    if not superseded:
        print(f"No superseded {prefix}* caches on {ref}.")
        return 0

    reclaimed = 0
    for entry in superseded:
        size = entry["sizeInBytes"]
        print(f"Deleting {entry['key']} ({size // MEGABYTE} MB)")
        if gh("cache", "delete", str(entry["id"]), "--repo", repository) is not None:
            reclaimed += size
    print(f"Reclaimed {reclaimed // MEGABYTE} MB on {ref}; kept {keep or '(nothing)'}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
