using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// The link between <c>Docs/TEST_FINDINGS.md</c> and the tests that pin its findings, checked in both
    /// directions so neither side can drift silently.
    /// <para>
    /// The convention it enforces: a test that pins finding N carries <c>[Category("Finding_N")]</c>, so
    /// starting work on that finding is <c>-testCategory Finding_N</c> rather than a grep through free-text
    /// comments. A finding that is deliberately left unpinned says so on its own <c>Status:</c> line with an
    /// explicit "Not pinned" note, which is what this check accepts in place of a test - there is no exception
    /// list here to add a finding to, because a list of exceptions is the thing that goes stale.
    /// </para>
    /// <para>
    /// Both test assemblies are read by reflection rather than by parsing source, so a category that a
    /// <c>#if</c> compiled out is correctly seen as absent, and a category only counts when it sits on a test
    /// that will actually run: a <c>[Test]</c>-family method, not a helper, and neither it nor its fixture
    /// <c>[Ignore]</c>d or <c>[Explicit]</c>. <c>EditorTests.asmdef</c> already references the PlayMode
    /// <c>Tests</c> assembly, which is what makes one EditMode fixture able to see both.
    /// </para>
    /// <para>
    /// A pin that is compiled out is not the same as a missing pin. #14's only pin needs
    /// <c>PACKAGE_ADDRESSABLES</c>, and the ones for #45 and #48 need <c>!UNITY_WEBGL</c>, so in a project
    /// without Addressables, or on a WebGL build target, those findings have no pin in the compiled assemblies
    /// through no fault of the ledger. The sources under <c>Assets/Tests/</c> are therefore scanned too, for
    /// each <c>[Category("Finding_N")]</c> and the <c>#if</c> conditions around it, and a finding whose pin's
    /// condition is false in this compilation is accepted as gated out. The gate is read from the code itself,
    /// never from a list: move the pin out of its <c>#if</c> and the finding is held to the ordinary rule again.
    /// A condition naming a symbol this fixture cannot evaluate is a failure, not a pass.
    /// </para>
    /// <para>
    /// Reads files it never writes: the two markdown ledgers, the test sources, and the assemblies' metadata.
    /// No fixture behavior is exercised, but every test still derives from BroEditorTestFixture per the
    /// suite's contract (see EditorUtilityPureTests).
    /// </para>
    /// </summary>
    public class FindingCoverageTests : BroEditorTestFixture
    {
        /// <summary>
        /// The prefix every pinning category carries, as in <c>[Category("Finding_14")]</c>. Don't use '-': NUnit
        /// rejects a category containing ',', '!', '+' or '-' and fails the test before its body runs.
        /// </summary>
        public const string CategoryPrefix = "Finding_";

        /// <summary>Repo-relative path of the open ledger this fixture reconciles against.</summary>
        private const string FindingsDocRelativePath = "Docs/TEST_FINDINGS.md";

        /// <summary>Repo-relative path of the closed ledger, read only to catch number collisions.</summary>
        private const string FixedDocRelativePath = "Docs/FIXED_ISSUES.md";

        /// <summary>Project-relative folder scanned for <c>#if</c>-gated pins.</summary>
        private const string TestSourcesRelativePath = "Assets/Tests";

        /// <summary>
        /// Floors for the non-vacuity guard. Deliberately far below the real counts: they exist to catch a
        /// parser or a reflection walk that came back with nothing, not to be a second inventory that has to
        /// be edited whenever a finding is added or fixed.
        /// </summary>
        private const int MinimumFindings = 20;
        private const int MinimumPinnedFindings = 20;
        private const int MinimumFixedIssues = 5;

        /// <summary>A section header, e.g. "## 14. The addressable unload setting ...", in either ledger.</summary>
        private static readonly Regex HeadingPattern = new Regex(@"^##[ \t]+(\d+)\.[ \t]*(.*)$");

        /// <summary>
        /// A summary-table row, e.g. "| 14 | Addressables | ... | Open, characterized |". Only rows whose first
        /// cell is a bare number, so the header and the |---| separator never match.
        /// </summary>
        private static readonly Regex TableRowPattern = new Regex(@"^\|[ \t]*(\d+)[ \t]*\|(.*)\|[ \t]*$");

        /// <summary>
        /// The line that opens a section's status paragraph: "Status: ...", optionally bolded as "**Status:**".
        /// The paragraph runs to the next blank line or heading.
        /// </summary>
        private static readonly Regex StatusLinePattern = new Regex(@"^[ \t]*(\*\*)?Status:");

        /// <summary>
        /// The explicit "this one has no pinning test, on purpose" note: the phrase "Not pinned" opening a
        /// sentence, or lowercase inside one ("Deliberately not pinned: ..."). Narrow on purpose - #39's
        /// "Neither is pinned by a test", which talks about two smaller bugs inside a finding that IS pinned,
        /// must not match, so this asks for the literal phrase rather than for any mention of pinning. It is
        /// only honored inside the Status paragraph, so a passing mention elsewhere in a section cannot
        /// excuse a finding by accident.
        /// </summary>
        private static readonly Regex NotPinnedPattern = new Regex(@"\b[Nn]ot pinned\b");

        /// <summary>Splits "Finding_14" into its number. Anything else under the prefix is malformed.</summary>
        private static readonly Regex CategoryPattern = new Regex(@"^" + CategoryPrefix + @"(\d+)$");

        /// <summary>A pinning category as written in source, e.g. <c>[Category("Finding_14")]</c>.</summary>
        private static readonly Regex SourceCategoryPattern = new Regex(@"Category\(\s*""" + CategoryPrefix + @"(\d+)""\s*\)");

        /// <summary>A preprocessor directive this scan has to follow.</summary>
        private static readonly Regex DirectivePattern = new Regex(@"^[ \t]*#[ \t]*(if|elif|else|endif)\b(.*)$");

        /// <summary>One "## N." section of the open ledger.</summary>
        private readonly struct Finding
        {
            public readonly int Number;
            public readonly string Title;

            /// <summary>True when the section's Status paragraph carries the explicit "Not pinned" note.</summary>
            public readonly bool DeclaredUnpinned;

            /// <summary>True when "Not pinned" appears in the section but outside its Status paragraph.</summary>
            public readonly bool MentionsNotPinnedElsewhere;

            public Finding(int number, string title, bool declaredUnpinned, bool mentionsNotPinnedElsewhere)
            {
                Number = number;
                Title = title;
                DeclaredUnpinned = declaredUnpinned;
                MentionsNotPinnedElsewhere = mentionsNotPinnedElsewhere;
            }

            public override string ToString() => "#" + Number + " (" + Title + ")";
        }

        /// <summary>One <c>[Category("Finding_N")]</c> found on a runnable test, with where it was found.</summary>
        private readonly struct Marker
        {
            public readonly string Category;
            public readonly string Owner;

            public Marker(string category, string owner)
            {
                Category = category;
                Owner = owner;
            }
        }

        /// <summary>One <c>[Category("Finding_N")]</c> found in source, with the #if conditions around it.</summary>
        private readonly struct SourcePin
        {
            public readonly int Number;
            public readonly string Location;

            /// <summary>The conjunction of every enclosing #if/#elif/#else branch, or "" when ungated.</summary>
            public readonly string Condition;

            public SourcePin(int number, string location, string condition)
            {
                Number = number;
                Location = location;
                Condition = condition;
            }
        }

        private static string RepoRoot => Path.GetDirectoryName(Application.dataPath) ?? string.Empty;

        private static string FindingsDocPath => Path.Combine(RepoRoot, FindingsDocRelativePath);

        private static string FixedDocPath => Path.Combine(RepoRoot, FixedDocRelativePath);

        private static string[] ReadDoc(string path, string relativePath)
        {
            Assert.IsTrue(File.Exists(path),
                "Could not find " + relativePath + " at '" + path + "'. This fixture reconciles the ledgers " +
                "against the suite's Finding_N categories and cannot run without it.");
            return File.ReadAllLines(path);
        }

        private static List<Finding> ReadFindings()
        {
            var findings = new List<Finding>();
            int number = 0;
            string title = null;
            bool unpinned = false;
            bool elsewhere = false;
            bool inStatus = false;

            foreach (string line in ReadDoc(FindingsDocPath, FindingsDocRelativePath))
            {
                Match heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    if (title != null)
                    {
                        findings.Add(new Finding(number, title, unpinned, elsewhere));
                    }
                    number = int.Parse(heading.Groups[1].Value);
                    title = heading.Groups[2].Value.Trim();
                    unpinned = false;
                    elsewhere = false;
                    inStatus = false;
                    continue;
                }

                if (title == null)
                {
                    continue;
                }

                if (StatusLinePattern.IsMatch(line))
                {
                    inStatus = true;
                }
                else if (line.Trim().Length == 0)
                {
                    inStatus = false;
                }

                if (NotPinnedPattern.IsMatch(line))
                {
                    if (inStatus)
                    {
                        unpinned = true;
                    }
                    else
                    {
                        elsewhere = true;
                    }
                }
            }

            if (title != null)
            {
                findings.Add(new Finding(number, title, unpinned, elsewhere));
            }
            return findings;
        }

        /// <summary>The summary table at the top of the open ledger: number to its Status cell, in row order.</summary>
        private static List<KeyValuePair<int, string>> ReadSummaryTable()
        {
            var rows = new List<KeyValuePair<int, string>>();
            foreach (string line in ReadDoc(FindingsDocPath, FindingsDocRelativePath))
            {
                if (HeadingPattern.IsMatch(line))
                {
                    break; // the table sits above the first section; a later table belongs to a finding's prose
                }
                Match row = TableRowPattern.Match(line);
                if (!row.Success)
                {
                    continue;
                }
                string[] cells = row.Groups[2].Value.Split('|');
                rows.Add(new KeyValuePair<int, string>(int.Parse(row.Groups[1].Value), cells[cells.Length - 1].Trim()));
            }
            return rows;
        }

        /// <summary>Every number the closed ledger records, from its section headings and its summary table.</summary>
        private static HashSet<int> ReadFixedNumbers()
        {
            var numbers = new HashSet<int>();
            bool pastTable = false;
            foreach (string line in ReadDoc(FixedDocPath, FixedDocRelativePath))
            {
                Match heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    numbers.Add(int.Parse(heading.Groups[1].Value));
                    pastTable = true; // as in the open ledger, only the table above the first section is the summary
                    continue;
                }
                Match row = TableRowPattern.Match(line);
                if (row.Success && !pastTable)
                {
                    numbers.Add(int.Parse(row.Groups[1].Value));
                }
            }
            return numbers;
        }

        /// <summary>Every Finding_* category carried by a runnable test, across both assemblies.</summary>
        private static List<Marker> ReadMarkers()
        {
            var markers = new List<Marker>();
            foreach (Assembly assembly in TestAssemblies())
            {
                CollectMarkers(assembly, markers);
            }
            return markers;
        }

        private static IEnumerable<Assembly> TestAssemblies()
        {
            yield return typeof(FindingCoverageTests).Assembly;                      // EditorTests (EditMode)
            yield return typeof(global::Ami.BroAudio.Tests.BroAudioTestFixture).Assembly; // Tests (PlayMode)
        }

        private static readonly Type[] TestMethodAttributes =
        {
            typeof(TestAttribute),
            typeof(TestCaseAttribute),
            typeof(TestCaseSourceAttribute),
            typeof(TheoryAttribute),
            typeof(UnityTestAttribute),
        };

        private static bool IsSkipped(MemberInfo member) =>
            member.IsDefined(typeof(IgnoreAttribute), true) || member.IsDefined(typeof(ExplicitAttribute), true);

        /// <summary>A method the runner will execute: a [Test]-family attribute, and not [Ignore]d or [Explicit].</summary>
        private static bool IsRunnableTest(MethodInfo method) =>
            TestMethodAttributes.Any(attribute => method.IsDefined(attribute, true)) && !IsSkipped(method);

        private static void CollectMarkers(Assembly assembly, List<Marker> markers)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                // A half-loadable assembly still tells us about the types that did load; losing the rest
                // silently would let this check pass while covering less than it claims, so surface it.
                types = exception.Types.Where(t => t != null).ToArray();
                Assert.Fail("Could not fully load the test assembly '" + assembly.GetName().Name +
                    "'. Finding_N categories in the types that failed to load would be invisible here.");
            }

            const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (Type type in types)
            {
                // An [Ignore]d or [Explicit] fixture runs none of its tests, however they are tagged; an abstract
                // one runs only through a concrete subclass, whose own walk below sees the inherited methods.
                if (type.IsAbstract || IsSkipped(type))
                {
                    continue;
                }

                // Walk inherited methods too, so a tag on a base-class test counts for each concrete fixture.
                MethodInfo[] runnable = HierarchyOf(type)
                    .SelectMany(t => t.GetMethods(MemberFlags))
                    .Where(IsRunnableTest)
                    .ToArray();
                if (runnable.Length == 0)
                {
                    continue;
                }

                // A category on the fixture itself applies to every test it runs.
                foreach (string category in CategoriesOn(type))
                {
                    markers.Add(new Marker(category, type.Name));
                }

                foreach (MethodInfo method in runnable)
                {
                    foreach (string category in CategoriesOn(method))
                    {
                        markers.Add(new Marker(category, type.Name + "." + method.Name));
                    }
                }
            }
        }

        private static IEnumerable<Type> HierarchyOf(Type type)
        {
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                yield return current;
            }
        }

        private static IEnumerable<string> CategoriesOn(MemberInfo member)
        {
            foreach (object attribute in member.GetCustomAttributes(typeof(CategoryAttribute), false))
            {
                string name = ((CategoryAttribute)attribute).Name;
                if (!string.IsNullOrEmpty(name) && name.StartsWith(CategoryPrefix, StringComparison.Ordinal))
                {
                    yield return name;
                }
            }
        }

        #region Source scan for #if-gated pins
        /// <summary>
        /// Every <c>[Category("Finding_N")]</c> in the test sources, with the conjunction of the preprocessor
        /// branches around it. Comments are stripped first, so a category quoted in a doc comment (this
        /// file's own class summary has one) is not a pin.
        /// </summary>
        private static List<SourcePin> ReadSourcePins()
        {
            string root = Path.Combine(RepoRoot, TestSourcesRelativePath);
            Assert.IsTrue(Directory.Exists(root), "Could not find the test sources at '" + root + "'.");

            var pins = new List<SourcePin>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Each open #if contributes (conditions of the branches already passed, condition of this one).
                var stack = new List<List<string>>();
                bool inBlockComment = false;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComments(lines[i], ref inBlockComment);
                    Match directive = DirectivePattern.Match(code);
                    if (directive.Success)
                    {
                        string expression = directive.Groups[2].Value.Trim();
                        switch (directive.Groups[1].Value)
                        {
                            case "if":
                                stack.Add(new List<string> { "(" + expression + ")" });
                                break;
                            case "elif":
                                if (stack.Count > 0)
                                {
                                    stack[stack.Count - 1].Add("(" + expression + ")");
                                }
                                break;
                            case "else":
                                if (stack.Count > 0)
                                {
                                    stack[stack.Count - 1].Add("(true)");
                                }
                                break;
                            case "endif":
                                if (stack.Count > 0)
                                {
                                    stack.RemoveAt(stack.Count - 1);
                                }
                                break;
                        }
                        continue;
                    }

                    foreach (Match category in SourceCategoryPattern.Matches(code))
                    {
                        string location = Path.GetFileName(file) + ":" + (i + 1);
                        pins.Add(new SourcePin(int.Parse(category.Groups[1].Value), location, ConditionOf(stack)));
                    }
                }
            }
            return pins;
        }

        /// <summary>The active branch of each open #if: every earlier branch false, this one true.</summary>
        private static string ConditionOf(List<List<string>> stack)
        {
            var terms = new List<string>();
            foreach (List<string> branches in stack)
            {
                for (int b = 0; b < branches.Count - 1; b++)
                {
                    terms.Add("!" + branches[b]);
                }
                terms.Add(branches[branches.Count - 1]);
            }
            return string.Join(" && ", terms.ToArray());
        }

        private static string StripComments(string line, ref bool inBlockComment)
        {
            // Good enough for this suite's sources, not a C# lexer: it follows regular and verbatim strings
            // and char literals only far enough that a "//" or "/*" inside one is not taken for a comment.
            var result = new System.Text.StringBuilder(line.Length);
            bool inString = false;
            bool verbatim = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                char next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }
                if (inString)
                {
                    result.Append(c);
                    if (c == '\\' && !verbatim)
                    {
                        result.Append(next);
                        i++;
                    }
                    else if (c == '"' && verbatim && next == '"')
                    {
                        result.Append(next); // a verbatim string's doubled quote is one literal quote
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '/' && next == '/')
                {
                    break;
                }
                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }
                if (c == '\'')
                {
                    // A char literal is copied whole, so the quote in '"' cannot open a string.
                    int close = line.IndexOf('\'', i + (next == '\\' ? 3 : 2));
                    int end = close < 0 ? line.Length - 1 : close;
                    result.Append(line, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    verbatim = (i > 0 && line[i - 1] == '@') || (i > 1 && line[i - 1] == '$' && line[i - 2] == '@');
                }
                result.Append(c);
            }
            return result.ToString();
        }

        /// <summary>
        /// Whether a preprocessor symbol is defined for the test assemblies in this compilation, or null when
        /// this fixture cannot tell. Both test assemblies raise <c>PACKAGE_*</c> from the same
        /// <c>versionDefines</c> as <c>EditorTests</c>, and the rest are project- or target-wide, so what this
        /// assembly sees is what the pin's assembly saw. Add a symbol here when a pin is gated on a new one.
        /// </summary>
        private static bool? IsSymbolDefined(string symbol)
        {
            switch (symbol)
            {
                case "true": return true;
                case "false": return false;
                case "PACKAGE_ADDRESSABLES":
#if PACKAGE_ADDRESSABLES
                    return true;
#else
                    return false;
#endif
                case "PACKAGE_LOCALIZATION":
#if PACKAGE_LOCALIZATION
                    return true;
#else
                    return false;
#endif
                case "UNITY_WEBGL":
#if UNITY_WEBGL
                    return true;
#else
                    return false;
#endif
                case "UNITY_EDITOR":
#if UNITY_EDITOR
                    return true;
#else
                    return false;
#endif
                case "UNITY_INCLUDE_TESTS":
#if UNITY_INCLUDE_TESTS
                    return true;
#else
                    return false;
#endif
                case "BroAudio_InitManually":
#if BroAudio_InitManually
                    return true;
#else
                    return false;
#endif
                case "BroAudio_DevOnly":
#if BroAudio_DevOnly
                    return true;
#else
                    return false;
#endif
                default:
                    return null;
            }
        }

        /// <summary>
        /// Evaluates a preprocessor condition built from symbols, <c>!</c>, <c>&amp;&amp;</c>, <c>||</c> and
        /// parentheses. Anything else, or a symbol <see cref="IsSymbolDefined"/> cannot answer, fails the test
        /// rather than guessing - a guess would be an unreviewed exception.
        /// </summary>
        private sealed class ConditionEvaluator
        {
            private static readonly Regex TokenPattern = new Regex(@"\s*(&&|\|\||!|\(|\)|[A-Za-z_][A-Za-z0-9_]*)");
            private readonly List<string> _tokens = new List<string>();
            private readonly string _source;
            private int _position;

            private ConditionEvaluator(string source)
            {
                _source = source;
                int index = 0;
                while (index < source.Length)
                {
                    if (char.IsWhiteSpace(source[index]))
                    {
                        index++;
                        continue;
                    }
                    Match token = TokenPattern.Match(source, index);
                    if (!token.Success || token.Index != index)
                    {
                        Assert.Fail("Cannot parse the preprocessor condition '" + source + "' at '" +
                            source.Substring(index) + "'. Teach FindingCoverageTests.ConditionEvaluator the syntax.");
                    }
                    _tokens.Add(token.Groups[1].Value);
                    index += token.Length;
                }
            }

            public static bool Evaluate(string condition)
            {
                if (string.IsNullOrEmpty(condition))
                {
                    return true;
                }
                var evaluator = new ConditionEvaluator(condition);
                bool result = evaluator.ParseOr();
                if (evaluator._position != evaluator._tokens.Count)
                {
                    Assert.Fail("Trailing tokens in the preprocessor condition '" + condition + "'.");
                }
                return result;
            }

            private string Peek() => _position < _tokens.Count ? _tokens[_position] : null;

            private bool ParseOr()
            {
                bool value = ParseAnd();
                while (Peek() == "||")
                {
                    _position++;
                    bool right = ParseAnd();
                    value = value || right;
                }
                return value;
            }

            private bool ParseAnd()
            {
                bool value = ParseUnary();
                while (Peek() == "&&")
                {
                    _position++;
                    bool right = ParseUnary();
                    value = value && right;
                }
                return value;
            }

            private bool ParseUnary()
            {
                string token = Peek();
                if (token == "!")
                {
                    _position++;
                    return !ParseUnary();
                }
                if (token == "(")
                {
                    _position++;
                    bool value = ParseOr();
                    if (Peek() != ")")
                    {
                        Assert.Fail("Unbalanced parentheses in the preprocessor condition '" + _source + "'.");
                    }
                    _position++;
                    return value;
                }
                if (token == null || token == ")" || token == "&&" || token == "||")
                {
                    Assert.Fail("Malformed preprocessor condition '" + _source + "'.");
                }
                _position++;
                bool? defined = IsSymbolDefined(token);
                if (!defined.HasValue)
                {
                    Assert.Fail("A Finding_N pin is gated on the symbol '" + token + "', which FindingCoverageTests " +
                        "cannot evaluate. Add it to IsSymbolDefined so the gate is checked rather than trusted.");
                }
                return defined.Value;
            }
        }
        #endregion

        private static string Join(IEnumerable<string> items) => string.Join(", ", items.ToArray());

        [Test]
        public void TheFindingsDocumentAndBothTestAssemblies_AreActuallyRead()
        {
            // Non-vacuity. Every other test in this fixture compares two sets, and two empty sets agree
            // perfectly - a parser that matched no heading, or a reflection walk that saw no category,
            // would report the suite fully reconciled while checking nothing at all.
            List<Finding> findings = ReadFindings();
            Assert.GreaterOrEqual(findings.Count, MinimumFindings,
                "Only " + findings.Count + " finding(s) parsed out of " + FindingsDocRelativePath + ". The " +
                "section headings are expected to read '## <number>. <title>'; if that shape changed, " +
                "HeadingPattern in this fixture has to change with it - otherwise the reconciliation below " +
                "compares two empty sets and passes vacuously.");

            Assert.GreaterOrEqual(ReadSummaryTable().Count, MinimumFindings,
                "The summary table at the top of " + FindingsDocRelativePath + " parsed to almost nothing. Rows " +
                "are expected to read '| <number> | ... | <status> |'; if that shape changed, TableRowPattern has " +
                "to change with it.");

            Assert.GreaterOrEqual(ReadFixedNumbers().Count, MinimumFixedIssues,
                "Almost nothing parsed out of " + FixedDocRelativePath + ", so the collision check against it " +
                "would pass vacuously.");

            Assert.GreaterOrEqual(ReadSourcePins().Select(pin => pin.Number).Distinct().Count(), MinimumPinnedFindings,
                "The source scan of " + TestSourcesRelativePath + " found almost no " + CategoryPrefix + "N " +
                "categories, so a gated pin would never be recognised.");

            foreach (Assembly assembly in TestAssemblies())
            {
                var perAssembly = new List<Marker>();
                CollectMarkers(assembly, perAssembly);
                Assert.IsNotEmpty(perAssembly,
                    "No " + CategoryPrefix + "* category was found on a runnable test in the '" +
                    assembly.GetName().Name + "' assembly. Both assemblies pin findings, so an empty one means " +
                    "the assembly did not build, or was not reachable from here, and its findings would read as " +
                    "unpinned below.");
            }

            int pinned = ReadMarkers()
                .Select(marker => marker.Category)
                .Distinct()
                .Count();
            Assert.GreaterOrEqual(pinned, MinimumPinnedFindings,
                "Only " + pinned + " distinct " + CategoryPrefix + "N categories were found across both test " +
                "assemblies, which is fewer than this suite is known to carry. Something is compiled out.");
        }

        [Test]
        public void EveryOpenFinding_IsEitherPinnedByATaggedTest_OrExplicitlyNotedAsUnpinned()
        {
            List<Finding> findings = ReadFindings();
            var pinned = new HashSet<string>(ReadMarkers().Select(marker => marker.Category));
            List<SourcePin> sourcePins = ReadSourcePins();

            var unaccounted = new List<string>();
            foreach (Finding finding in findings)
            {
                if (finding.DeclaredUnpinned || pinned.Contains(CategoryPrefix + finding.Number))
                {
                    continue;
                }

                // Accepted when a pin exists in source but its #if is false in this compilation. A pin whose
                // condition is true yet is absent from the assemblies is not excused: it was compiled in and
                // still does not count, so it is [Ignore]d, [Explicit], or not on a test method.
                SourcePin[] gatedOut = sourcePins
                    .Where(pin => pin.Number == finding.Number && !ConditionEvaluator.Evaluate(pin.Condition))
                    .ToArray();
                if (gatedOut.Length > 0)
                {
                    TestContext.WriteLine(finding + " is pinned only behind a condition false in this compilation: " +
                        Join(gatedOut.Select(pin => pin.Location + " (#if " + pin.Condition + ")")) + ".");
                    continue;
                }

                string entry = finding.ToString();
                if (sourcePins.Any(pin => pin.Number == finding.Number))
                {
                    entry += " [tagged in source, but not on a runnable test: it is a helper, or [Ignore]d or [Explicit]]";
                }
                if (finding.MentionsNotPinnedElsewhere)
                {
                    entry += " [says \"not pinned\", but not on its Status line]";
                }
                unaccounted.Add(entry);
            }

            Assert.IsEmpty(unaccounted,
                "Finding(s) with neither a runnable pinning test nor an explicit note: " + Join(unaccounted) + ". " +
                "Either tag the test that pins it with [Category(\"" + CategoryPrefix + "N\")], or say so on the " +
                "Status line of the finding's own section in " + FindingsDocRelativePath + " with an explicit " +
                "\"Not pinned\" note and the reason. Do not add an exception list to this test.");
        }

        [Test]
        public void EveryFindingCategory_NamesAFindingTheDocumentRecords()
        {
            var recorded = new HashSet<int>(ReadFindings().Select(finding => finding.Number));
            HashSet<int> fixedNumbers = ReadFixedNumbers();

            var malformed = new List<string>();
            var orphaned = new List<string>();
            foreach (Marker marker in ReadMarkers())
            {
                Match match = CategoryPattern.Match(marker.Category);
                if (!match.Success)
                {
                    malformed.Add(marker.Owner + " -> \"" + marker.Category + "\"");
                    continue;
                }
                int number = int.Parse(match.Groups[1].Value);
                if (!recorded.Contains(number))
                {
                    orphaned.Add(marker.Owner + " -> " + marker.Category +
                        (fixedNumbers.Contains(number) ? " (recorded as fixed in " + FixedDocRelativePath + ")" : ""));
                }
            }

            Assert.IsEmpty(malformed,
                "Categor(ies) under the '" + CategoryPrefix + "' prefix that are not '" + CategoryPrefix +
                "<number>': " + Join(malformed) + ". The tooling selects a finding's tests with " +
                "-testCategory " + CategoryPrefix + "N, so the number has to be the whole suffix.");

            Assert.IsEmpty(orphaned,
                "Test(s) tagged with a finding " + FindingsDocRelativePath + " does not record: " +
                Join(orphaned) + ". Either the finding was fixed and moved to FIXED_ISSUES.md - in which " +
                "case the category goes with it, since this file is the open ledger - or the number is a typo.");
        }

        [Test]
        public void NoFindingNotedAsUnpinned_AlsoCarriesATaggedTest()
        {
            var pinned = new HashSet<string>(ReadMarkers().Select(marker => marker.Category));
            var inSource = new HashSet<int>(ReadSourcePins().Select(pin => pin.Number));

            var contradictions = ReadFindings()
                .Where(finding => finding.DeclaredUnpinned &&
                    (pinned.Contains(CategoryPrefix + finding.Number) || inSource.Contains(finding.Number)))
                .Select(finding => finding.ToString())
                .ToList();

            Assert.IsEmpty(contradictions,
                "Finding(s) whose Status line says they are not pinned, yet a test carries their category: " +
                Join(contradictions) + ". One of the two is now wrong: if the finding did get pinned, drop " +
                "the \"Not pinned\" note and give it a Status line naming the test; if the test does not " +
                "actually pin it, drop the category.");
        }

        [Test]
        public void NoNumber_IsBothOpenAndFixed_OrRecordedTwice()
        {
            List<Finding> findings = ReadFindings();
            HashSet<int> fixedNumbers = ReadFixedNumbers();

            var duplicated = findings
                .GroupBy(finding => finding.Number)
                .Where(group => group.Count() > 1)
                .Select(group => "#" + group.Key)
                .ToList();
            Assert.IsEmpty(duplicated,
                "Number(s) with more than one section in " + FindingsDocRelativePath + ": " + Join(duplicated) +
                ". A Finding_N category could then pin either one.");

            var collisions = findings
                .Where(finding => fixedNumbers.Contains(finding.Number))
                .Select(finding => finding.ToString())
                .ToList();
            Assert.IsEmpty(collisions,
                "Finding(s) recorded as open in " + FindingsDocRelativePath + " under a number " +
                FixedDocRelativePath + " also uses: " + Join(collisions) + ". Numbers are shared across both " +
                "ledgers - a finding that was fixed moves, it is not copied, and a new finding takes a fresh number.");
        }

        [Test]
        public void TheSummaryTable_MatchesTheSections()
        {
            List<Finding> findings = ReadFindings();
            List<KeyValuePair<int, string>> rows = ReadSummaryTable();
            var sections = new HashSet<int>(findings.Select(finding => finding.Number));
            var rowNumbers = new HashSet<int>(rows.Select(row => row.Key));

            var duplicatedRows = rows
                .GroupBy(row => row.Key)
                .Where(group => group.Count() > 1)
                .Select(group => "#" + group.Key)
                .ToList();
            var missingRows = sections.Where(n => !rowNumbers.Contains(n)).OrderBy(n => n).Select(n => "#" + n).ToList();
            var rowsWithoutSection = rowNumbers.Where(n => !sections.Contains(n)).OrderBy(n => n).Select(n => "#" + n).ToList();

            Assert.IsEmpty(duplicatedRows, "Summary-table row(s) listed twice: " + Join(duplicatedRows) + ".");
            Assert.IsEmpty(missingRows,
                "Finding(s) with a section but no summary-table row in " + FindingsDocRelativePath + ": " +
                Join(missingRows) + ".");
            Assert.IsEmpty(rowsWithoutSection,
                "Summary-table row(s) with no '## N.' section in " + FindingsDocRelativePath + ": " +
                Join(rowsWithoutSection) + ". A fixed finding's row moves to " + FixedDocRelativePath + " with its section.");

            // The table's Status cell is what a reader skims, so it must not call an unpinned finding
            // characterized-by-a-test, nor a pinned one unpinned.
            var byNumber = findings.GroupBy(finding => finding.Number).ToDictionary(group => group.Key, group => group.First());
            var mismatched = new List<string>();
            foreach (KeyValuePair<int, string> row in rows)
            {
                Finding finding;
                if (!byNumber.TryGetValue(row.Key, out finding))
                {
                    continue;
                }
                bool rowSaysUnpinned = row.Value.IndexOf("not pinned", StringComparison.OrdinalIgnoreCase) >= 0;
                if (rowSaysUnpinned != finding.DeclaredUnpinned)
                {
                    mismatched.Add(finding + ": table says '" + row.Value + "', Status line " +
                        (finding.DeclaredUnpinned ? "says Not pinned" : "does not say Not pinned"));
                }
            }
            Assert.IsEmpty(mismatched,
                "Summary-table Status cell(s) that disagree with the finding's own Status line on whether it is " +
                "pinned: " + Join(mismatched) + ". A finding whose Status line says \"Not pinned\" says so in its " +
                "table row too, and no other row does.");
        }
    }
}