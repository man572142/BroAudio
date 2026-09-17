using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// The link between <c>Docs/TEST_FINDINGS.md</c> and the tests that pin its findings, checked in both
    /// directions so neither side can drift silently.
    /// <para>
    /// The convention it enforces: a test that pins finding N carries <c>[Category("Finding_N")]</c>, so
    /// starting work on that finding is <c>-testCategory Finding_N</c> rather than a grep through free-text
    /// comments. A finding that is deliberately left unpinned says so in its own section with an explicit
    /// "Not pinned" note, which is what this check accepts in place of a test - there is no exception list
    /// here to add a finding to, because a list of exceptions is the thing that goes stale.
    /// </para>
    /// <para>
    /// Both test assemblies are read by reflection rather than by parsing source, so a category that a
    /// <c>#if</c> compiled out is correctly seen as absent. <c>EditorTests.asmdef</c> already references the
    /// PlayMode <c>Tests</c> assembly, which is what makes one EditMode fixture able to see both.
    /// </para>
    /// <para>
    /// Reads two files it never writes: the markdown document, and the assemblies' metadata. No fixture
    /// behavior is exercised, but every test still derives from BroEditorTestFixture per the suite's
    /// contract (see EditorUtilityPureTests).
    /// </para>
    /// </summary>
    public class FindingCoverageTests : BroEditorTestFixture
    {
        /// <summary>
        /// The prefix every pinning category carries, as in <c>[Category("Finding_14")]</c>. Don't use '-': NUnit
        /// rejects a category containing ',', '!', '+' or '-' and fails the test before its body runs.
        /// </summary>
        public const string CategoryPrefix = "Finding_";

        /// <summary>Repo-relative path of the document this fixture reconciles against.</summary>
        private const string FindingsDocRelativePath = "Docs/TEST_FINDINGS.md";

        /// <summary>
        /// Floors for the non-vacuity guard. Deliberately far below the real counts: they exist to catch a
        /// parser or a reflection walk that came back with nothing, not to be a second inventory that has to
        /// be edited whenever a finding is added or fixed.
        /// </summary>
        private const int MinimumFindings = 20;
        private const int MinimumPinnedFindings = 20;

        /// <summary>A finding's section header, e.g. "## 14. The addressable unload setting ...".</summary>
        private static readonly Regex HeadingPattern = new Regex(@"^##[ \t]+(\d+)\.[ \t]*(.*)$");

        /// <summary>
        /// The explicit "this one has no pinning test, on purpose" note: the phrase "Not pinned" opening a
        /// sentence, or lowercase inside one ("Deliberately not pinned: ..."). Narrow on purpose - #39's
        /// "Neither is pinned by a test", which talks about two smaller bugs inside a finding that IS pinned,
        /// must not match, so this asks for the literal phrase rather than for any mention of pinning.
        /// </summary>
        private static readonly Regex NotPinnedPattern = new Regex(@"\b[Nn]ot pinned\b");

        /// <summary>Splits "Finding_14" into its number. Anything else under the prefix is malformed.</summary>
        private static readonly Regex CategoryPattern = new Regex(@"^" + CategoryPrefix + @"(\d+)$");

        /// <summary>One "## N." section of the document.</summary>
        private readonly struct Finding
        {
            public readonly int Number;
            public readonly string Title;

            /// <summary>True when the section carries the explicit "Not pinned" note.</summary>
            public readonly bool DeclaredUnpinned;

            public Finding(int number, string title, bool declaredUnpinned)
            {
                Number = number;
                Title = title;
                DeclaredUnpinned = declaredUnpinned;
            }

            public override string ToString() => "#" + Number + " (" + Title + ")";
        }

        /// <summary>One <c>[Category("Finding_N")]</c> found on a test, with where it was found.</summary>
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

        private static string FindingsDocPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, FindingsDocRelativePath);

        private static List<Finding> ReadFindings()
        {
            string path = FindingsDocPath;
            Assert.IsTrue(File.Exists(path),
                "Could not find " + FindingsDocRelativePath + " at '" + path + "'. This fixture reconciles the " +
                "document against the suite's Finding_N categories and cannot run without it.");

            var findings = new List<Finding>();
            int number = 0;
            string title = null;
            bool unpinned = false;

            foreach (string line in File.ReadAllLines(path))
            {
                Match heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    if (title != null)
                    {
                        findings.Add(new Finding(number, title, unpinned));
                    }
                    number = int.Parse(heading.Groups[1].Value);
                    title = heading.Groups[2].Value.Trim();
                    unpinned = false;
                    continue;
                }

                if (title != null && NotPinnedPattern.IsMatch(line))
                {
                    unpinned = true;
                }
            }

            if (title != null)
            {
                findings.Add(new Finding(number, title, unpinned));
            }
            return findings;
        }

        /// <summary>Every Finding_* category carried by a test method or a fixture, across both assemblies.</summary>
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
                foreach (string category in CategoriesOn(type))
                {
                    markers.Add(new Marker(category, type.Name));
                }

                foreach (MethodInfo method in type.GetMethods(MemberFlags))
                {
                    foreach (string category in CategoriesOn(method))
                    {
                        markers.Add(new Marker(category, type.Name + "." + method.Name));
                    }
                }
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

            foreach (Assembly assembly in TestAssemblies())
            {
                var perAssembly = new List<Marker>();
                CollectMarkers(assembly, perAssembly);
                Assert.IsNotEmpty(perAssembly,
                    "No " + CategoryPrefix + "* category was found in the '" + assembly.GetName().Name +
                    "' assembly. Both assemblies pin findings, so an empty one means the assembly did not " +
                    "build, or was not reachable from here, and its findings would read as unpinned below.");
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

            var unaccounted = findings
                .Where(finding => !finding.DeclaredUnpinned && !pinned.Contains(CategoryPrefix + finding.Number))
                .Select(finding => finding.ToString())
                .ToList();

            Assert.IsEmpty(unaccounted,
                "Finding(s) with neither a pinning test nor an explicit note: " + Join(unaccounted) + ". " +
                "Either tag the test that pins it with [Category(\"" + CategoryPrefix + "N\")], or say so in " +
                "the finding's own section in " + FindingsDocRelativePath + " with an explicit \"Not pinned\" " +
                "note and the reason. Do not add an exception list to this test.");
        }

        [Test]
        public void EveryFindingCategory_NamesAFindingTheDocumentRecords()
        {
            var recorded = new HashSet<int>(ReadFindings().Select(finding => finding.Number));

            var malformed = new List<string>();
            var orphaned = new List<string>();
            foreach (Marker marker in ReadMarkers())
            {
                Match match = CategoryPattern.Match(marker.Category);
                if (!match.Success)
                {
                    malformed.Add(marker.Owner + " -> \"" + marker.Category + "\"");
                }
                else if (!recorded.Contains(int.Parse(match.Groups[1].Value)))
                {
                    orphaned.Add(marker.Owner + " -> " + marker.Category);
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

            var contradictions = ReadFindings()
                .Where(finding => finding.DeclaredUnpinned && pinned.Contains(CategoryPrefix + finding.Number))
                .Select(finding => finding.ToString())
                .ToList();

            Assert.IsEmpty(contradictions,
                "Finding(s) whose section says they are not pinned, yet a test carries their category: " +
                Join(contradictions) + ". One of the two is now wrong: if the finding did get pinned, drop " +
                "the \"Not pinned\" note and give it a Status line naming the test; if the test does not " +
                "actually pin it, drop the category.");
        }
    }
}