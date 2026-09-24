#!/usr/bin/env python3
"""Remove packages from a Unity project's manifest and lock file, for a CI leg that runs without them.

BroAudio treats Addressables and Localization as optional: the runtime and editor asmdefs raise
PACKAGE_ADDRESSABLES / PACKAGE_LOCALIZATION only while those packages resolve, and CLAUDE.md's Definition of
Done #3 requires the package to compile with both absent. This project pins both, so nothing proves that
unless a run takes them out. This script does, in the CI workspace only - it is never meant to be committed.

It removes each named package from Packages/manifest.json and from Packages/packages-lock.json, then prunes
lock entries that were only there as their dependencies, so Unity resolves exactly the project a consumer
without those packages would have. It refuses to run when a package that stays declares a dependency on one
being removed: Unity would silently pull it back in, and the leg would test the full configuration under a
false name.

Usage: strip_optional_packages.py <Packages-dir> <package> [<package> ...]
"""
import json
import sys
from pathlib import Path


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path, data):
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def main(argv):
    if len(argv) < 3:
        sys.exit(f"usage: {Path(argv[0]).name} <Packages-dir> <package> [<package> ...]")
    packages_dir, removed = Path(argv[1]), set(argv[2:])
    manifest_path = packages_dir / "manifest.json"
    lock_path = packages_dir / "packages-lock.json"

    manifest = load(manifest_path)
    direct = manifest.get("dependencies", {})
    absent = sorted(removed - set(direct))
    if absent:
        sys.exit(f"{manifest_path} does not declare {', '.join(absent)}, so there is nothing to remove - "
                 "if they were dropped from the project on purpose, drop them from this CI step too.")
    for name in removed:
        del direct[name]

    if lock_path.exists():
        lock = load(lock_path)
        entries = lock.get("dependencies", {})

        # Anything that stays and still wants a removed package would bring it straight back.
        dependants = sorted(
            f"{name} -> {dependency}"
            for name, entry in entries.items() if name not in removed
            for dependency in entry.get("dependencies", {}) if dependency in removed
        )
        # A lock entry's dependencies only matter if the entry itself survives, which is decided below;
        # check against what the manifest still declares or what those declare in turn.
        keep = set(direct)
        frontier = list(direct)
        while frontier:
            entry = entries.get(frontier.pop(), {})
            for dependency in entry.get("dependencies", {}):
                if dependency not in keep and dependency not in removed:
                    keep.add(dependency)
                    frontier.append(dependency)
        blocking = [pair for pair in dependants if pair.split(" -> ", 1)[0] in keep]
        if blocking:
            sys.exit("These packages stay in the project yet depend on one being removed, so Unity would resolve "
                     f"it again: {', '.join(blocking)}.")

        pruned = sorted(name for name in entries if name not in keep)
        lock["dependencies"] = {name: entry for name, entry in entries.items() if name in keep}
        save(lock_path, lock)
        print(f"{lock_path}: removed {', '.join(pruned)}")

    save(manifest_path, manifest)
    print(f"{manifest_path}: removed {', '.join(sorted(removed))}")


if __name__ == "__main__":
    main(sys.argv)
