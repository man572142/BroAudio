#!/usr/bin/env python3
"""Fail a test leg whose green result does not mean what it says.

Two ways a Unity test run reports green while covering less than it claims, neither of which the runner
treats as a failure:

1. A suite is missing. A fixture that compiles to nothing - an optional package that did not resolve, an
   assembly that failed to build - does not appear in the NUnit results at all. The fixtures a leg must run
   are derived from the test sources by derive_test_suites.py, against the packages this checkout resolves
   (so a leg that removed a package requires exactly the fixtures that survive without it). The check also
   runs the other way: a fixture in the results that the derivation did not predict means the derivation is
   wrong, and a wrong derivation is how a missing suite would slip through, so that fails too.

2. A test did not run to a verdict. Assert.Ignore reports Skipped, Assume reports Inconclusive, and both
   count as not-failed. Every such result fails the leg unless test-results-policy.json allows it, and an
   entry can be conditional: realtime-gated tests may be ignored only on a leg that promised no audio
   device, and the order-dependent IsolationContractTests only on a random-order run.

Usage:
    check_test_suites.py <testMode> <results-dir> [--policy <json>] [--project <dir>]
                         [--expects-audio] [--random-order]
"""
import argparse
import fnmatch
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ left in the checkout
sys.path.insert(0, str(Path(__file__).resolve().parent))
import derive_test_suites  # noqa: E402

# A parameterized fixture reports as a ParameterizedFixture suite whose children are TestFixtures named
# "Thing(1)", "Thing(2)" - so match on the class rather than the display name, or nothing ever matches.
FIXTURE_TYPES = ("TestFixture", "ParameterizedFixture")

# The flags an allowlist entry's "when" / "unless" may name, and the command-line switch that sets each.
CONDITIONS = ("expectsAudio", "randomOrder")


class Report:
    def __init__(self, fixtures, total, non_passing):
        self.fixtures = fixtures
        self.total = total
        self.non_passing = non_passing  # [(fullname, outcome, message)]


def outcome_of(case):
    """Ignored / Explicit / Skipped / Inconclusive for a test-case that did not reach a verdict, else None."""
    result = case.get("result")
    if result == "Inconclusive":
        return "Inconclusive"
    if result == "Skipped":
        label = case.get("label")
        return label if label in ("Ignored", "Explicit") else "Skipped"
    return None


def message_of(case):
    for path in ("reason/message", "failure/message"):
        node = case.find(path)
        if node is not None and node.text:
            return node.text.strip()
    return ""


# Every fixture and test the suite declares lives in an Ami.* namespace. An installed package can ship
# its own tests into the same run (the full-package leg reports a TestStub fixture that no BroAudio source
# declares), and those are neither the derivation's business nor this check's. Judged by the qualified
# name, which the report always carries, rather than by where Unity nests the assembly node.
OWN_NAMESPACE_PREFIX = "Ami."


def is_own(qualified_name):
    return bool(qualified_name) and qualified_name.startswith(OWN_NAMESPACE_PREFIX)


def read_report(path):
    """Returns a Report for an NUnit report, or None for anything else.

    Anything else includes XML this script has no business reading and XML it cannot read at all: a
    half-written report from an editor that died mid-run is exactly the case the check exists for, and it
    must not surface as a traceback.
    """
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        print(f"warning: {path.name} is not parseable XML ({error}); ignoring it.")
        return None
    if root.tag != "test-run":
        return None
    fixtures = set()
    for suite in root.iter("test-suite"):
        if suite.get("type") not in FIXTURE_TYPES:
            continue
        # classname is absent on a ParameterizedFixture; fullname carries the class there.
        name = suite.get("classname") or suite.get("fullname") or suite.get("name")
        if is_own(name):
            # "Thing(1)" - a parameterized instance without a classname - is still the class Thing.
            fixtures.add(re.sub(r"\(.*\)$", "", name.rsplit(".", 1)[-1]))
    non_passing = []
    for case in root.iter("test-case"):
        outcome = outcome_of(case)
        if outcome and is_own(case.get("fullname")):
            non_passing.append((case.get("fullname") or case.get("name") or "?", outcome, message_of(case)))
    return Report(fixtures, int(root.get("testcasecount") or 0), non_passing)


def load_policy(path):
    policy = json.loads(Path(path).read_text(encoding="utf-8"))
    for entry in policy.get("allowedNonPassing", []):
        for key in ("when", "unless"):
            if key in entry and entry[key] not in CONDITIONS:
                sys.exit(f"{path}: '{key}: {entry[key]}' names no known condition; use one of {', '.join(CONDITIONS)}.")
        if not entry.get("reason"):
            sys.exit(f"{path}: every allowedNonPassing entry needs a reason; {entry.get('test')} has none.")
    return policy


def allowing_entry(policy, fullname, outcome, message, flags):
    for entry in policy.get("allowedNonPassing", []):
        if outcome not in entry.get("results", []):
            continue
        if not fnmatch.fnmatchcase(fullname, entry.get("test", "*")):
            continue
        if "message" in entry and not fnmatch.fnmatchcase(message, entry["message"]):
            continue
        if "when" in entry and not flags[entry["when"]]:
            continue
        if "unless" in entry and flags[entry["unless"]]:
            continue
        return entry
    return None


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("mode", choices=("EditMode", "PlayMode"))
    parser.add_argument("results", type=Path)
    parser.add_argument("--policy", default=str(Path(__file__).resolve().parent.parent / "test-results-policy.json"))
    parser.add_argument("--project", default=".", type=Path)
    parser.add_argument("--expects-audio", action="store_true",
                        help="this leg's image promises a realtime audio device (BROAUDIO_CI_EXPECTS_AUDIO)")
    parser.add_argument("--random-order", action="store_true", help="this leg ran with -randomOrderSeed")
    args = parser.parse_args(argv[1:])
    flags = {"expectsAudio": args.expects_audio, "randomOrder": args.random_order}
    policy = load_policy(args.policy)

    reports = []
    for path in sorted(args.results.glob("*.xml")):
        report = read_report(path)
        if report:
            reports.append((path, report))
    if not reports:
        sys.exit(f"No NUnit results under {args.results}/ - the {args.mode} leg produced nothing to check.")

    found, total, non_passing = set(), 0, []
    for _, report in reports:
        found |= report.fixtures
        total += report.total
        non_passing += report.non_passing
    print(f"{args.mode}: {total} tests across {len(found)} fixtures in {', '.join(p.name for p, _ in reports)}")

    errors = []

    required = derive_test_suites.derive(args.project)[args.mode]
    floor = policy.get("minimumFixtures", {}).get(args.mode, 1)
    if len(required) < floor:
        errors.append(
            f"Only {len(required)} {args.mode} fixture(s) derived from {derive_test_suites.TEST_ROOT}, below the floor "
            f"of {floor} in {args.policy}. The source scan has stopped seeing the suite, which would make every "
            "check below vacuous.")

    missing = sorted(required - found)
    if missing:
        print(f"::error title=Missing {args.mode} test suites::{', '.join(missing)}")
        errors.append(
            f"{len(missing)} {args.mode} suite(s) the sources declare never ran: {', '.join(missing)}.\n"
            "They are absent from the results rather than failing, so either they were compiled out - check that "
            "every package in Packages/manifest.json resolved and that both test assemblies built - or the "
            "derivation in derive_test_suites.py expects a fixture Unity does not build.")

    unexpected = sorted(found - required)
    if unexpected:
        print(f"::error title=Undeclared {args.mode} test suites::{', '.join(unexpected)}")
        errors.append(
            f"{len(unexpected)} {args.mode} fixture(s) ran that derive_test_suites.py did not predict: "
            f"{', '.join(unexpected)}. The derivation is what decides which suites a leg must run, so a fixture "
            "it cannot see is one whose absence would go unnoticed. Teach it the declaration it missed.")

    refused = []
    for fullname, outcome, message in non_passing:
        entry = allowing_entry(policy, fullname, outcome, message, flags)
        if entry is None:
            refused.append(f"{fullname}: {outcome}" + (f" - {message}" if message else ""))
    if refused:
        for line in refused:
            print(f"::error title={args.mode} test did not reach a verdict::{line}")
        errors.append(
            f"{len(refused)} {args.mode} test(s) were ignored, skipped or inconclusive, which the runner does not "
            "count as failures:\n  " + "\n  ".join(refused) + "\n"
            f"Either make the test run to a verdict, or allow that outcome in {args.policy} with a reason and, "
            f"where it only holds on some legs, a 'when' or 'unless' condition ({', '.join(CONDITIONS)}).")
    allowed = len(non_passing) - len(refused)
    if allowed:
        print(f"{args.mode}: {allowed} ignored/inconclusive result(s) allowed by {args.policy}.")

    if errors:
        sys.exit("\n\n".join(errors))
    print(f"{args.mode}: all {len(required)} derived suites ran, and every test reached a verdict or is allowed not to.")


if __name__ == "__main__":
    main(sys.argv)
