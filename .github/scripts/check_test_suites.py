#!/usr/bin/env python3
"""Fail a test leg when a required suite is missing from its results.

A suite that compiles to nothing - an optional package that did not resolve, an assembly
that failed to build - does not appear in the NUnit results at all. It does not fail; it
is simply not there, and the leg reports every test it did run as green. Workflow run 11
passed that way twice over: PlayMode ran 124 tests instead of 132 with the whole
AddressablesTests suite gone, and EditMode ran 209 instead of 214 with
LocalizationClipStrategyTests gone. Both legs were green, and the Addressables failure
they were hiding reappeared on the next push.

Usage: check_test_suites.py <testMode> <results-dir> <manifest.json>
"""
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def read_report(path):
    """Returns (fixture names, test count) for an NUnit report, or None for any other XML."""
    root = ET.parse(path).getroot()
    if root.tag != "test-run":
        return None
    names = {
        suite.get("name")
        for suite in root.iter("test-suite")
        if suite.get("type") == "TestFixture" and suite.get("name")
    }
    return names, int(root.get("testcasecount") or 0)


def main(argv):
    if len(argv) != 4:
        sys.exit(f"usage: {Path(argv[0]).name} <testMode> <results-dir> <manifest.json>")
    mode, results_dir, manifest_path = argv[1], Path(argv[2]), Path(argv[3])

    suites = json.loads(manifest_path.read_text())["suites"]
    if mode not in suites:
        sys.exit(f"{manifest_path} lists no required suites for '{mode}'. It knows: {', '.join(sorted(suites))}.")
    required = set(suites[mode])

    reports = [r for r in sorted(results_dir.glob("*.xml")) if read_report(r)]
    if not reports:
        sys.exit(f"No NUnit results under {results_dir}/ - the {mode} leg produced nothing to check.")

    found, total = set(), 0
    for report in reports:
        names, count = read_report(report)
        found |= names
        total += count

    print(f"{mode}: {total} tests across {len(found)} fixtures in {', '.join(r.name for r in reports)}")

    missing = sorted(required - found)
    if missing:
        print(f"::error title=Missing {mode} test suites::{', '.join(missing)}")
        sys.exit(
            f"\n{len(missing)} required {mode} suite(s) never ran: {', '.join(missing)}.\n"
            "They are absent from the results rather than failing, which means they were compiled out. Check "
            "that every package in Packages/manifest.json resolved and that both test assemblies built."
        )

    print(f"{mode}: all {len(required)} required suites ran.")


if __name__ == "__main__":
    main(sys.argv)