using System;
using System.Collections.Generic;
using System.Text;
using Ami.BroAudio.Data;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    internal static class IssueEnvironmentSnapshot
    {
        public static string Compose(IssueType issueType)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Unity | {Application.unityVersion} |");
            sb.AppendLine($"| BroAudio | {BroVersion.Version} (codebase {BroVersion.CodeBaseVersion}) |");
            sb.AppendLine($"| Platform | {Application.platform} |");
            sb.AppendLine($"| Build Target | {EditorUserBuildSettings.activeBuildTarget} |");

            if (issueType == IssueType.Build)
            {
                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
                var namedBuildTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
                sb.AppendLine($"| Scripting Backend | {PlayerSettings.GetScriptingBackend(namedBuildTarget)} |");
            }

            sb.AppendLine($"| Install Source | {GetInstallSource()} |");
            sb.AppendLine($"| Defines | {GetDefines()} |");

            AppendAudioConfig(sb);
            AppendRuntimeSettingDiff(sb);

            return sb.ToString();
        }

        private static string GetInstallSource()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Assets/BroAudio/package.json");
            return packageInfo != null ? $"UPM ({packageInfo.source})" : "Unity Asset Store (.unitypackage)";
        }

        private static string GetDefines()
        {
            var defines = new List<string>();
#if PACKAGE_ADDRESSABLES
            defines.Add("PACKAGE_ADDRESSABLES");
#endif
#if PACKAGE_LOCALIZATION
            defines.Add("PACKAGE_LOCALIZATION");
#endif
#if BroAudio_InitManually
            defines.Add("BroAudio_InitManually");
#endif
#if BroAudio_DevOnly
            defines.Add("BroAudio_DevOnly");
#endif
            return defines.Count > 0 ? string.Join(", ", defines) : "(none)";
        }

        private static void AppendAudioConfig(StringBuilder sb)
        {
            var config = AudioSettings.GetConfiguration();
            sb.AppendLine($"| Sample Rate | {config.sampleRate} |");
            sb.AppendLine($"| DSP Buffer Size | {config.dspBufferSize} |");
            sb.AppendLine($"| Speaker Mode | {config.speakerMode} |");
            sb.AppendLine($"| Real Voices | {config.numRealVoices} |");
            sb.AppendLine($"| Virtual Voices | {config.numVirtualVoices} |");
        }

        private static void AppendRuntimeSettingDiff(StringBuilder sb)
        {
            RuntimeSetting current = BroEditorUtility.RuntimeSetting;
            if (!current)
            {
                return;
            }

            RuntimeSetting pristine = ScriptableObject.CreateInstance<RuntimeSetting>();
            pristine.ResetToFactorySettings();

            try
            {
                var currentSO = new SerializedObject(current);
                var pristineSO = new SerializedObject(pristine);

                var diffLines = new List<string>();
                using (var iterator = currentSO.GetIterator())
                {
                    bool enterChildren = true;
                    while (iterator.Next(enterChildren))
                    {
                        enterChildren = true;

                        if (iterator.name.StartsWith("m_", StringComparison.Ordinal) || iterator.propertyType == SerializedPropertyType.Generic)
                        {
                            continue;
                        }

                        var pristineProp = pristineSO.FindProperty(iterator.propertyPath);
                        if (pristineProp == null || PropertiesEqual(iterator, pristineProp))
                        {
                            continue;
                        }

                        diffLines.Add($"| {iterator.displayName} | {IssueReportCollector.GetPropertyValueString(iterator)} |");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("**Non-default settings**");
                sb.AppendLine();
                if (diffLines.Count == 0)
                {
                    sb.AppendLine("*(all factory defaults)*");
                }
                else
                {
                    sb.AppendLine("| Setting | Value |");
                    sb.AppendLine("|---|---|");
                    foreach (var line in diffLines)
                    {
                        sb.AppendLine(line);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(pristine);
            }
        }

        private static bool PropertiesEqual(SerializedProperty a, SerializedProperty b)
        {
            if (a.propertyType != b.propertyType)
            {
                return false;
            }

            switch (a.propertyType)
            {
                case SerializedPropertyType.Integer: return a.intValue == b.intValue;
                case SerializedPropertyType.Boolean: return a.boolValue == b.boolValue;
                case SerializedPropertyType.Float: return Mathf.Approximately(a.floatValue, b.floatValue);
                case SerializedPropertyType.String: return a.stringValue == b.stringValue;
                case SerializedPropertyType.Enum: return a.enumValueIndex == b.enumValueIndex;
                case SerializedPropertyType.ObjectReference: return a.objectReferenceValue == b.objectReferenceValue;
                case SerializedPropertyType.Color: return a.colorValue == b.colorValue;
                case SerializedPropertyType.Vector2: return a.vector2Value == b.vector2Value;
                case SerializedPropertyType.Vector3: return a.vector3Value == b.vector3Value;
                default: return true; // unhandled types are never reported as changed
            }
        }
    }
}
