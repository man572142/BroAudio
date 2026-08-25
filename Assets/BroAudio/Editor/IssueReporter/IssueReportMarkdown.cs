using System.Collections.Generic;
using System.Text;
using Ami.BroAudio.Data;
using UnityEditor;
using UnityEngine.Networking;

namespace Ami.BroAudio.Editor
{
    internal static class IssueReportMarkdown
    {
        public static string ComposeTitle(IssueReportDraft draft)
        {
            return $"[{GetIssueTypeLabel(draft.Type)}] {draft.Title}";
        }

        public static string Compose(IssueReportDraft draft, List<AudioEntity> problemSoundEntities, string collectedComponentMarkdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {ComposeTitle(draft)}");
            sb.AppendLine();

            sb.AppendLine("## What happened");
            sb.AppendLine(draft.Description);
            sb.AppendLine();

            sb.AppendLine("## What I expected");
            sb.AppendLine(draft.Expectation);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(draft.ConsoleOutput))
            {
                sb.AppendLine("## Console output");
                sb.AppendLine("```");
                sb.AppendLine(draft.ConsoleOutput);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            AppendSoundsSection(sb, problemSoundEntities);
            AppendUsageSection(sb, draft, collectedComponentMarkdown);

            sb.AppendLine("## Environment");
            sb.Append(IssueEnvironmentSnapshot.Compose(draft.Type));

            return sb.ToString();
        }

        public static string BuildGitHubIssueURL(string title)
        {
            string encodedTitle = UnityWebRequest.EscapeURL(title);
            return $"{InfoEditorWindow.GitURL}/issues/new?title={encodedTitle}&labels=bug";
        }

        private static string GetIssueTypeLabel(IssueType type) => type switch
        {
            IssueType.Editor => "Editor",
            IssueType.PlayMode => "Play Mode",
            IssueType.Build => "Build",
            _ => "Unspecified",
        };

        private static void AppendSoundsSection(StringBuilder sb, List<AudioEntity> entities)
        {
            if (entities == null || entities.Count == 0)
            {
                return;
            }

            sb.AppendLine("## Sounds involved");
            sb.AppendLine();
            foreach (var entity in entities)
            {
                if (!entity)
                {
                    sb.AppendLine("*(unassigned SoundID)*");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"<details><summary>{entity.Name}</summary>");
                sb.AppendLine();
                sb.AppendLine("| Setting | Value |");
                sb.AppendLine("|---|---|");
                using (var so = new SerializedObject(entity))
                {
                    IssueReportCollector.AppendSerializedObjectAsMarkdownTable(sb, so, nameof(AudioEntity.Clips));
                    sb.AppendLine();
                    AppendClips(sb, so);
                }
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        private static void AppendClips(StringBuilder sb, SerializedObject entitySO)
        {
            var clipsProp = entitySO.FindProperty(nameof(AudioEntity.Clips));
            if (clipsProp == null || clipsProp.arraySize == 0)
            {
                sb.AppendLine("*(no clips)*");
                return;
            }

            sb.AppendLine("**Clips**");
            sb.AppendLine();
            sb.AppendLine("| Clip | Volume | Delay | Start | End | Fade In | Fade Out |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            for (int i = 0; i < clipsProp.arraySize; i++)
            {
                var clipProp = clipsProp.GetArrayElementAtIndex(i);
                var audioClipProp = clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClip);
                string clipName = audioClipProp != null && audioClipProp.objectReferenceValue ? audioClipProp.objectReferenceValue.name : "(missing)";
                float volume = clipProp.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue;
                float delay = clipProp.FindPropertyRelative(nameof(BroAudioClip.Delay)).floatValue;
                float start = clipProp.FindPropertyRelative(nameof(BroAudioClip.StartPosition)).floatValue;
                float end = clipProp.FindPropertyRelative(nameof(BroAudioClip.EndPosition)).floatValue;
                float fadeIn = clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeIn)).floatValue;
                float fadeOut = clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeOut)).floatValue;
                sb.AppendLine($"| {clipName} | {volume:0.###} | {delay:0.###} | {start:0.###} | {end:0.###} | {fadeIn:0.###} | {fadeOut:0.###} |");
            }
        }

        private static void AppendUsageSection(StringBuilder sb, IssueReportDraft draft, string collectedComponentMarkdown)
        {
            sb.AppendLine("## How it's used");
            sb.AppendLine($"**Integration:** {draft.Integration}");
            sb.AppendLine($"**Target:** {(draft.TargetObject ? draft.TargetObject.name : "(none)")}");
            sb.AppendLine();

            if (draft.Integration == IntegrationStyle.BroAudioComponent)
            {
                if (!string.IsNullOrEmpty(collectedComponentMarkdown))
                {
                    sb.AppendLine("<details><summary>Collected settings</summary>");
                    sb.AppendLine();
                    sb.AppendLine(collectedComponentMarkdown);
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }
            }
            else if (!string.IsNullOrWhiteSpace(draft.ScriptCollection))
            {
                sb.AppendLine(draft.ScriptCollection);
                sb.AppendLine();
            }
        }
    }
}
