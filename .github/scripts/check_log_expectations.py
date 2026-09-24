#!/usr/bin/env python3
"""Reject any LogAssert.Expect that matches a log by its text rather than by BroAudio's tag.

The suite's rule for expected logs: match the LogType plus the [BroAudio] tag, never the sentence. A test
that pins a message's wording breaks the moment the wording improves, and says nothing about behavior. So
every LogAssert.Expect under Assets/Tests/ must pass, as its message pattern, the one shared regex -
TestAudioLibrary.BroAudioLogPrefix (or BroAudioLogPrefix from inside TestAudioLibrary or a class that
imports it statically). A field of that name declared anywhere else must be built from Utility.LogTitle
exactly as the shared one is, so a local copy cannot quietly widen or narrow the match.

The one exception is a log Unity itself raises without BroAudio's tag, which a test can only expect by its
LogType. Those are listed in ALLOWED_UNTAGGED below, each with the file it lives in and how many
occurrences it may have; adding one is a decision that belongs in review, not a pattern anyone can reuse.

Usage: check_log_expectations.py [project-root]
"""
import re
import sys
from pathlib import Path

TEST_ROOT = Path("Assets") / "Tests"
SHARED_PREFIX = {"TestAudioLibrary.BroAudioLogPrefix", "BroAudioLogPrefix"}
PREFIX_DEFINITION = re.compile(r"new\s+Regex\s*\(\s*Regex\.Escape\s*\(\s*Utility\.LogTitle\s*\)\s*\)")

# (file name, message argument) -> the number of LogAssert.Expect calls that may use it.
ALLOWED_UNTAGGED = {
    # AudioClip.GetData on a streaming clip: Unity's own error, untagged, logged before BroAudio's tagged one.
    # One per test that Trims a streaming clip: the Trim pin, #61's pin and its no-Trim contrast.
    ("ClipEditingTests.cs", "TestAudioLibrary.AnyLogMessage"): 3,
}

EXPECT_CALL = re.compile(r"\bLogAssert\s*\.\s*Expect\s*\(")
PREFIX_FIELD = re.compile(r"\bBroAudioLogPrefix\s*=\s*([^;]+);")


def strip_comments(text):
    """Blanks // and /* */ comments, keeping strings intact and line breaks in place."""
    out, i, n = [], 0, len(text)
    while i < n:
        c, nxt = text[i], text[i + 1] if i + 1 < n else ""
        if c == '"':
            j = i + 1
            verbatim = i > 0 and text[i - 1] == "@"
            while j < n and text[j] != '"':
                j += 2 if (text[j] == "\\" and not verbatim) else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                i += 1
        elif c == "/" and nxt == "*":
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            out.append("".join("\n" if ch == "\n" else " " for ch in text[i:end]))
            i = end
        else:
            out.append(c)
            i += 1
    return "".join(out)


def call_arguments(text, open_paren):
    """Splits the argument list starting after text[open_paren] at top-level commas."""
    args, depth, start, i = [], 0, open_paren + 1, open_paren + 1
    while i < len(text):
        c = text[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            if depth == 0:
                args.append(text[start:i])
                return [" ".join(a.split()) for a in args]
            depth -= 1
        elif c == "," and depth == 0:
            args.append(text[start:i])
            start = i + 1
        elif c == '"':
            i += 1
            while i < len(text) and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        i += 1
    return None


def main(argv):
    root = Path(argv[1] if len(argv) > 1 else ".")
    problems, used_untagged, calls = [], {}, 0

    for path in sorted((root / TEST_ROOT).rglob("*.cs")):
        text = strip_comments(path.read_text(encoding="utf-8-sig"))
        where = lambda index: f"{path.relative_to(root)}:{text.count(chr(10), 0, index) + 1}"  # noqa: E731

        for match in EXPECT_CALL.finditer(text):
            calls += 1
            args = call_arguments(text, match.end() - 1)
            if not args or len(args) != 2:
                problems.append(f"{where(match.start())}: could not read LogAssert.Expect's two arguments.")
                continue
            message = args[1]
            if message in SHARED_PREFIX:
                continue
            key = (path.name, message)
            if key in ALLOWED_UNTAGGED:
                used_untagged[key] = used_untagged.get(key, 0) + 1
                if used_untagged[key] <= ALLOWED_UNTAGGED[key]:
                    continue
                problems.append(f"{where(match.start())}: {message} is allowed {ALLOWED_UNTAGGED[key]} time(s) in "
                                f"{path.name}; this is one more.")
                continue
            problems.append(f"{where(match.start())}: LogAssert.Expect matches on '{message}'. Expect the LogType "
                            "with TestAudioLibrary.BroAudioLogPrefix instead of the message text.")

        for match in PREFIX_FIELD.finditer(text):
            if not PREFIX_DEFINITION.fullmatch(match.group(1).strip()):
                problems.append(f"{where(match.start())}: a BroAudioLogPrefix built from '{match.group(1).strip()}' "
                                "rather than new Regex(Regex.Escape(Utility.LogTitle)). Use TestAudioLibrary's.")

    if calls == 0:
        problems.append(f"No LogAssert.Expect call found under {TEST_ROOT}/ - the scan is not seeing the suite.")

    if problems:
        for problem in problems:
            print(f"::error title=Log expectation matches text::{problem}")
        sys.exit(f"\n{len(problems)} log expectation problem(s):\n  " + "\n  ".join(problems))
    print(f"All {calls} LogAssert.Expect calls under {TEST_ROOT}/ match by LogType and BroAudio's tag "
          f"({sum(used_untagged.values())} documented untagged Unity log(s)).")


if __name__ == "__main__":
    main(sys.argv)
