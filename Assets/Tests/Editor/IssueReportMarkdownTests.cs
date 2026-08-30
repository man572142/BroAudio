using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.Networking;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure string-composition tests for IssueReportMarkdown - no ScriptableObjects, no disk I/O, no GUI.
    /// No fixture behavior is exercised here, but every test still derives from BroEditorTestFixture per the
    /// suite's contract (see EditorUtilityPureTests).
    /// <para>
    /// IssueReportMarkdown is <c>internal static</c> and this assembly has no InternalsVisibleTo (same
    /// reflection pattern as AudioMathTests in the runtime suite), so its public static methods are invoked
    /// via reflection rather than by direct reference.
    /// </para>
    /// </summary>
    public class IssueReportMarkdownTests : BroEditorTestFixture
    {
        private static readonly Type _markdownType =
            typeof(EditorSetting).Assembly.GetType("Ami.BroAudio.Editor.IssueReportMarkdown");

        private static string ComposeTitle(IssueReportDraft draft)
        {
            Assert.IsNotNull(_markdownType, "IssueReportMarkdown type not found via reflection - was it renamed or moved?");
            MethodInfo method = _markdownType.GetMethod("ComposeTitle", BindingFlags.Public | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { draft });
        }

        private static string BuildGitHubIssueURL(string title)
        {
            Assert.IsNotNull(_markdownType, "IssueReportMarkdown type not found via reflection - was it renamed or moved?");
            MethodInfo method = _markdownType.GetMethod("BuildGitHubIssueURL", BindingFlags.Public | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { title });
        }

        [TestCase(IssueType.None, "Unspecified")]
        [TestCase(IssueType.Editor, "Editor")]
        [TestCase(IssueType.PlayMode, "Play Mode")]
        [TestCase(IssueType.Build, "Build")]
        public void ComposeTitle_PrefixesTheTitleWithTheIssueTypeLabel(IssueType type, string expectedLabel)
        {
            var draft = new IssueReportDraft { Type = type, Title = "Everything is silent" };

            string title = ComposeTitle(draft);

            Assert.AreEqual($"[{expectedLabel}] Everything is silent", title);
        }

        [Test]
        public void BuildGitHubIssueURL_EscapesSpacesHashAndAmpersandInTheTitle()
        {
            const string RawTitle = "Bug: Audio & Volume #123 broken";
            string prefix = $"{InfoEditorWindow.GitURL}/issues/new?title=";
            const string Suffix = "&labels=bug";

            string url = BuildGitHubIssueURL(RawTitle);

            Assert.IsTrue(url.StartsWith(prefix), $"URL did not start with the expected GitHub issues prefix. Got: {url}");
            Assert.IsTrue(url.EndsWith(Suffix), $"URL did not end with the expected labels suffix. Got: {url}");

            string encodedTitle = url.Substring(prefix.Length, url.Length - prefix.Length - Suffix.Length);
            Assert.IsFalse(encodedTitle.Contains(" "), $"Encoded title still contains a literal space, which would break the URL: {encodedTitle}");
            Assert.IsFalse(encodedTitle.Contains("#"), $"Encoded title still contains a literal '#', which would truncate the URL at a fragment: {encodedTitle}");
            Assert.IsFalse(encodedTitle.Contains("&"), $"Encoded title still contains a literal '&', which would corrupt the query string: {encodedTitle}");
            Assert.AreEqual(RawTitle, UnityWebRequest.UnEscapeURL(encodedTitle), "Escaped title did not round-trip back to the original via UnEscapeURL.");
        }
    }
}