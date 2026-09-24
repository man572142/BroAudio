#!/usr/bin/env python3
"""Derive the test fixtures a leg must run from the test sources, instead of from a hand-kept list.

A fixture is a non-abstract, non-static class that declares a [Test]-family method, or inherits one from a
class declared under the same tree. Which leg runs it follows from the assembly that owns its file: the
nearest .asmdef up the directory tree, EditMode when that asmdef is Editor-only, PlayMode otherwise. Whether
it exists at all follows from the #if conditions around it, evaluated against the symbols that assembly
would be compiled with in this checkout:

  - its asmdef's versionDefines, raised for each package the checkout resolves (Packages/manifest.json and
    Packages/packages-lock.json - read after any CI step has edited them, so a leg that removed a package
    gets the fixtures of the project without it);
  - the project's Standalone scripting define symbols, and any -define: in Assets/csc.rsp;
  - the Editor's own UNITY_* platform and version symbols for a Linux Editor on a Standalone target.

Anything else is undefined. A fixture gated on a symbol this gets wrong is not silently dropped from the
check: check_test_suites.py also fails on a fixture the results contain but this derivation does not.

Usage, to print what each leg would require:
    derive_test_suites.py [project-root]
"""
import json
import re
import sys
from pathlib import Path

TEST_ROOT = Path("Assets") / "Tests"

# Attribute names that make a method a test. Matched as whole identifiers inside an attribute list, with
# strings removed first, so [TestFixture] or [Category("Test")] never count.
TEST_ATTRIBUTES = {"Test", "UnityTest", "TestCase", "TestCaseSource", "Theory"}

# Symbols a Linux Editor on the default Standalone target defines for every assembly under Assets/.
EDITOR_SYMBOLS = {
    "UNITY_EDITOR", "UNITY_EDITOR_64", "UNITY_EDITOR_LINUX", "UNITY_STANDALONE", "UNITY_STANDALONE_LINUX",
    "UNITY_64", "UNITY_INCLUDE_TESTS", "ENABLE_MONO", "DEBUG", "TRACE",
}
VERSION_SYMBOL = re.compile(r"^UNITY_\d+(_\d+)*_OR_NEWER$")

DIRECTIVE = re.compile(r"^\s*#\s*(if|elif|else|endif)\b(.*)$")
CLASS_DECL = re.compile(
    r"\b((?:(?:public|internal|private|protected|abstract|static|sealed|partial|unsafe|new)\s+)*)"
    r"class\s+([A-Za-z_]\w*)\s*(?:<[^>{]*>)?\s*(?::\s*([^{]+?))?\s*(?:\bwhere\b[^{]*)?$"
)
ATTRIBUTE_LIST = re.compile(r"\[([^\[\]]*)\]")
IDENTIFIER = re.compile(r"[A-Za-z_][\w.]*")


def strip_comments_and_strings(text):
    """Blanks out comments and string/char literal contents, keeping line breaks so line numbers hold."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and nxt == "*":
            i += 2
            while i < n and not (text[i] == "*" and i + 1 < n and text[i + 1] == "/"):
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            i += 2
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'" and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            out.append("''")
            i = j + 1
            continue
        if c == '"':
            verbatim = (i > 0 and text[i - 1] == "@") or (i > 1 and text[i - 1] == "$" and text[i - 2] == "@")
            j = i + 1
            while j < n:
                if text[j] == "\\" and not verbatim:
                    j += 2
                    continue
                if text[j] == '"':
                    if verbatim and j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    break
                j += 1
            out.append('""')
            i = j + 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


class Condition:
    """Evaluates a C# preprocessor expression: symbols, true/false, !, &&, ||, ==, != and parentheses."""

    TOKEN = re.compile(r"\s*(&&|\|\||==|!=|!|\(|\)|[A-Za-z_]\w*)")

    def __init__(self, expression, defined):
        self.tokens, self.pos, self.defined, self.expression = [], 0, defined, expression
        index = 0
        expression = expression.strip()
        while index < len(expression):
            match = self.TOKEN.match(expression, index)
            if not match:
                raise ValueError(f"cannot parse preprocessor condition '{expression}'")
            self.tokens.append(match.group(1))
            index = match.end()
            while index < len(expression) and expression[index].isspace():
                index += 1

    def evaluate(self):
        value = self._or()
        if self.pos != len(self.tokens):
            raise ValueError(f"trailing tokens in preprocessor condition '{self.expression}'")
        return value

    def _peek(self):
        return self.tokens[self.pos] if self.pos < len(self.tokens) else None

    def _or(self):
        value = self._and()
        while self._peek() == "||":
            self.pos += 1
            right = self._and()
            value = value or right
        return value

    def _and(self):
        value = self._equality()
        while self._peek() == "&&":
            self.pos += 1
            right = self._equality()
            value = value and right
        return value

    def _equality(self):
        value = self._unary()
        while self._peek() in ("==", "!="):
            op = self.tokens[self.pos]
            self.pos += 1
            right = self._unary()
            value = (value == right) if op == "==" else (value != right)
        return value

    def _unary(self):
        token = self._peek()
        if token is None:
            raise ValueError(f"malformed preprocessor condition '{self.expression}'")
        self.pos += 1
        if token == "!":
            return not self._unary()
        if token == "(":
            value = self._or()
            if self._peek() != ")":
                raise ValueError(f"unbalanced parentheses in '{self.expression}'")
            self.pos += 1
            return value
        if token == "true":
            return True
        if token == "false":
            return False
        if token in ("&&", "||", "==", "!=", ")"):
            raise ValueError(f"malformed preprocessor condition '{self.expression}'")
        return token in self.defined or bool(VERSION_SYMBOL.match(token))


def resolved_packages(root):
    """Package names this checkout resolves: the manifest's dependencies plus everything the lock lists."""
    names = set()
    for relative in ("Packages/manifest.json", "Packages/packages-lock.json"):
        path = root / relative
        if path.exists():
            names |= set(json.loads(path.read_text(encoding="utf-8-sig")).get("dependencies", {}))
    return names


def project_symbols(root):
    """Project-wide scripting defines: Standalone's list in ProjectSettings.asset, and Assets/csc.rsp."""
    symbols = set()
    settings = root / "ProjectSettings" / "ProjectSettings.asset"
    if settings.exists():
        text = settings.read_text(encoding="utf-8", errors="replace")
        block = re.search(r"^  scriptingDefineSymbols:\n((?:    .*\n)*)", text, re.MULTILINE)
        if block:
            for line in block.group(1).splitlines():
                key, _, value = line.strip().partition(":")
                if key == "Standalone":
                    symbols |= {s for s in re.split(r"[;,\s]+", value.strip()) if s}
    rsp = root / "Assets" / "csc.rsp"
    if rsp.exists():
        for match in re.finditer(r"-define:(\S+)", rsp.read_text(encoding="utf-8-sig")):
            symbols |= {s for s in re.split(r"[;,]", match.group(1)) if s}
    return symbols


def owning_asmdef(path, root):
    for directory in path.parents:
        asmdefs = sorted(directory.glob("*.asmdef"))
        if asmdefs:
            return asmdefs[0]
        if directory == root:
            break
    return None


def assembly_context(asmdef, packages, base_symbols):
    """(mode, defined symbols, compiles) for the assembly an .asmdef describes."""
    data = json.loads(asmdef.read_text(encoding="utf-8-sig"))
    mode = "EditMode" if data.get("includePlatforms") == ["Editor"] else "PlayMode"
    defined = set(base_symbols)
    for version_define in data.get("versionDefines", []):
        # An empty expression matches any version; this project's asmdefs use nothing else. A non-empty one
        # is treated as satisfied whenever the package resolves - the reverse check in check_test_suites.py
        # catches a fixture that this over-requires.
        if version_define.get("name") in packages:
            defined.add(version_define["define"])
    compiles = all(Condition(constraint, defined).evaluate() for constraint in data.get("defineConstraints", []))
    return mode, defined, compiles


def scan_file(path, defined):
    """Returns ({class name: (is_concrete, bases)}, {class names declaring a test}) for one source file."""
    text = strip_comments_and_strings(path.read_text(encoding="utf-8-sig"))
    classes, with_tests = {}, set()
    branch_stack = []  # per open #if: [any earlier branch taken, this branch active]
    depth = 0
    class_stack = []   # (qualified name, brace depth of its body)
    pending = None     # a class declared on this line or an earlier one whose "{" has not been seen yet
    pending_attributes = []

    def active():
        return all(frame[1] for frame in branch_stack)

    for line in text.split("\n"):
        directive = DIRECTIVE.match(line)
        if directive:
            kind, expression = directive.group(1), directive.group(2).strip()
            if kind == "if":
                taken = active() and Condition(expression, defined).evaluate()
                branch_stack.append([taken, taken])
            elif kind == "elif" and branch_stack:
                frame = branch_stack[-1]
                outer = all(f[1] for f in branch_stack[:-1])
                frame[1] = outer and not frame[0] and Condition(expression, defined).evaluate()
                frame[0] = frame[0] or frame[1]
            elif kind == "else" and branch_stack:
                frame = branch_stack[-1]
                outer = all(f[1] for f in branch_stack[:-1])
                frame[1] = outer and not frame[0]
                frame[0] = True
            elif kind == "endif" and branch_stack:
                branch_stack.pop()
            continue
        if not active():
            continue

        # Walk the line piece by piece between braces, so a one-line "class X { [Test] void T() {} }" puts the
        # attribute inside X rather than before it.
        for piece in re.split(r"([{}])", line):
            if piece == "{":
                depth += 1
                if pending is not None:
                    class_stack.append((pending, depth))
                    pending = None
                continue
            if piece == "}":
                if class_stack and class_stack[-1][1] == depth:
                    class_stack.pop()
                depth -= 1
                continue

            for attribute_list in ATTRIBUTE_LIST.findall(piece):
                names = {name.rsplit(".", 1)[-1] for name in IDENTIFIER.findall(attribute_list)}
                if names & TEST_ATTRIBUTES and class_stack:
                    with_tests.add(class_stack[-1][0])

            declaration = CLASS_DECL.search(ATTRIBUTE_LIST.sub(" ", piece).rstrip())
            if declaration:
                modifiers = declaration.group(1).split()
                name = declaration.group(2)
                if class_stack:
                    name = class_stack[-1][0] + "+" + name
                bases = [b.strip().split("<", 1)[0].rsplit(".", 1)[-1]
                         for b in (declaration.group(3) or "").split(",") if b.strip()]
                concrete = "abstract" not in modifiers and "static" not in modifiers
                classes[name] = (concrete, bases)
                pending = name
    return classes, with_tests


def derive(root="."):
    """Returns {"EditMode": set(fixture names), "PlayMode": set(...)} for the checkout at root."""
    root = Path(root).resolve()
    test_root = root / TEST_ROOT
    packages = resolved_packages(root)
    base_symbols = EDITOR_SYMBOLS | project_symbols(root)

    classes, with_tests, modes = {}, set(), {}
    for path in sorted(test_root.rglob("*.cs")):
        asmdef = owning_asmdef(path, test_root)
        if asmdef is None:
            continue  # not in a test assembly; Assembly-CSharp tests are not a thing this project has
        mode, defined, compiles = assembly_context(asmdef, packages, base_symbols)
        if not compiles:
            continue
        file_classes, file_tests = scan_file(path, defined)
        for name, info in file_classes.items():
            classes[name] = info
            modes[name] = mode
        with_tests |= file_tests

    def has_tests(name, seen=()):
        if name in with_tests:
            return True
        if name in seen or name not in classes:
            return False
        return any(has_tests(base, seen + (name,)) for base in classes[name][1])

    result = {"EditMode": set(), "PlayMode": set()}
    for name, (concrete, _) in classes.items():
        if concrete and has_tests(name):
            result[modes[name]].add(name)
    return result


def main(argv):
    root = argv[1] if len(argv) > 1 else "."
    for mode, fixtures in derive(root).items():
        print(f"{mode} ({len(fixtures)}): {', '.join(sorted(fixtures))}")


if __name__ == "__main__":
    main(sys.argv)
