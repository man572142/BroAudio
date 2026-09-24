using System;
using System.Reflection;
using NUnit.Framework;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure string-composition tests for IssueReportMarkdown - no ScriptableObjects, no disk I/O, no GUI.
    /// No fixture behavior is exercised here, but every test still derives from BroEditorTestFixture per the
    /// suite's contract (see EditorUtilityPureTests).
    /// <para>
    /// IssueReportMarkdown is <c>internal static</c> and this assembly has no InternalsVisibleTo, so its
    /// public static methods are invoked through <see cref="EditorReflected"/> rather than by direct reference.
    /// </para>
    /// </summary>
    public class IssueReportMarkdownTests : BroEditorTestFixture
    {
        private static Type MarkdownType =>
            EditorReflected.ResolveType(typeof(EditorSetting).Assembly, EditorReflected.IssueReportMarkdown.TypeName);

        private static string ComposeTitle(IssueReportDraft draft)
        {
            MethodInfo method = EditorReflected.StaticMethod(MarkdownType, EditorReflected.IssueReportMarkdown.ComposeTitle);
            return (string)method.Invoke(null, new object[] { draft });
        }

        private static string BuildGitHubIssueURL(string title)
        {
            MethodInfo method = EditorReflected.StaticMethod(MarkdownType, EditorReflected.IssueReportMarkdown.BuildGitHubIssueURL);
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
            // Decoded the way the receiving end reads a query string (form encoding: '+' is a space, then
            // percent-decoding), with the BCL rather than UnityWebRequest.UnEscapeURL - the inverse of the very
            // encoder production calls would share its mistakes and round-trip them away.
            string decoded = Uri.UnescapeDataString(encodedTitle.Replace('+', ' '));
            Assert.AreEqual(RawTitle, decoded, $"The escaped title does not decode back to the original. Encoded: {encodedTitle}");
        }
    }
}