using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ami.BroAudio.Data;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    // Walks a user-nominated Target Object (never the project) to collect BroAudio component
    // settings and SoundID-bearing script references. See ADR-0002.
    internal static class IssueReportCollector
    {
        private const int MaxPropertyDepth = 10;
        private const int MaxIterations = 10000;
        private const string PackageSourcePrefix = "Assets/BroAudio";

        public readonly struct SoundIDFieldReference
        {
            public readonly UnityEngine.Object Owner;
            public readonly string FieldName;
            public readonly AudioEntity Entity;

            public SoundIDFieldReference(UnityEngine.Object owner, string fieldName, AudioEntity entity)
            {
                Owner = owner;
                FieldName = fieldName;
                Entity = entity;
            }
        }

        public static IEnumerable<UnityEngine.Object> ResolveTargets(UnityEngine.Object targetObject)
        {
            if(targetObject is Component component)
            {
                yield return component;
            }

            if (targetObject is GameObject go)
            {
                foreach (var comp in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (comp)
                    {
                        yield return comp;
                    }
                }
            }
            else if (targetObject is ScriptableObject so)
            {
                yield return so;
            }
        }

        // Only reports actual BroAudioComponents attached to the target — never the user's own
        // scripts — and only those wired to one of the selected Problem Sounds.
        public static string CollectComponentSettings(UnityEngine.Object targetObject, IReadOnlyList<AudioEntity> problemSounds)
        {
            if (!targetObject)
            {
                return null;
            }

            var sb = new StringBuilder();
            bool foundAny = false;

            foreach (var obj in ResolveTargets(targetObject))
            {
                Type type = obj.GetType();
                if (IsBroAudioComponent(type) && MatchesProblemSounds(obj, problemSounds))
                {
                    foundAny = true;
                    AppendBroAudioComponentSettings(sb, obj, type);
                }
            }

            return foundAny ? sb.ToString() : null;
        }

        private static bool IsBroAudioComponent(Type type)
        {
            return type == typeof(SoundSource) || type == typeof(SoundVolume) || type == typeof(SpectrumAnalyzer);
        }

        private static bool MatchesProblemSounds(UnityEngine.Object obj, IReadOnlyList<AudioEntity> problemSounds)
        {
            if (problemSounds == null || problemSounds.Count == 0)
            {
                return true;
            }

            foreach (var field in FindSoundIDFieldsOn(obj))
            {
                if (field.Entity && problemSounds.Contains(field.Entity))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AppendBroAudioComponentSettings(StringBuilder sb, UnityEngine.Object obj, Type type)
        {
            sb.AppendLine($"<details><summary>{type.Name} ({GetHierarchyPath(obj)})</summary>");
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|---|---|");
            using (var so = new SerializedObject(obj))
            {
                AppendSerializedObjectAsMarkdownTable(sb, so);
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        private static string GetHierarchyPath(UnityEngine.Object obj)
        {
            if (obj is Component component)
            {
                Transform t = component.transform;
                string path = t.name;
                while (t.parent != null)
                {
                    t = t.parent;
                    path = t.name + "/" + path;
                }
                return path;
            }
            return obj.name;
        }

        public static List<SoundIDFieldReference> FindSoundIDFieldsOn(UnityEngine.Object obj)
        {
            var results = new List<SoundIDFieldReference>();
            if (!obj)
            {
                return results;
            }

            using (var so = new SerializedObject(obj))
            using (var iterator = so.GetIterator())
            {
                bool enterChildren = true;
                int iterations = 0;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = iterator.depth < MaxPropertyDepth;

                    if (++iterations > MaxIterations)
                    {
                        Debug.LogWarning(Utility.LogTitle + $"Skipped scanning \"{obj.name}\" for SoundID fields: property iteration exceeded {MaxIterations}, possible circular reference.");
                        break;
                    }

                    if (iterator.propertyType == SerializedPropertyType.Generic && iterator.type.Contains(nameof(SoundID)))
                    {
                        var entityProp = iterator.FindPropertyRelative(SoundID.NameOf.Entity);
                        var entity = entityProp?.objectReferenceValue as AudioEntity;
                        results.Add(new SoundIDFieldReference(obj, iterator.name, entity));
                        enterChildren = false;
                    }
                }
            }

            return results;
        }

        // Excludes BroAudio's own components (SoundSource/SoundVolume/SpectrumAnalyzer) — those are
        // covered separately by CollectComponentSettings — and only reports the user's scripts.
        // noneMatched is the "wrong object dragged" signal from ADR-0002: Problem Sounds was
        // non-empty but none of them are referenced on the Target Object.
        public static string CollectScriptReferences(UnityEngine.Object targetObject, IReadOnlyList<AudioEntity> problemSounds, out bool noneMatched)
        {
            noneMatched = false;
            if (!targetObject)
            {
                return string.Empty;
            }

            var allReferences = new List<SoundIDFieldReference>();
            foreach (var obj in ResolveTargets(targetObject))
            {
                if (IsBroAudioComponent(obj.GetType()))
                {
                    continue;
                }
                allReferences.AddRange(FindSoundIDFieldsOn(obj));
            }

            List<SoundIDFieldReference> matched;
            bool isFiltered = problemSounds != null && problemSounds.Count > 0;
            if (isFiltered)
            {
                matched = allReferences.FindAll(r => r.Entity && problemSounds.Contains(r.Entity));
                if (matched.Count == 0)
                {
                    noneMatched = true;
                    return string.Empty;
                }
            }
            else
            {
                matched = allReferences;
            }

            if (matched.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var fieldLabelsByPath = new Dictionary<string, List<string>>();
            var noScriptReferences = new List<SoundIDFieldReference>();
            foreach (var reference in matched)
            {
                GroupScriptSource(reference, fieldLabelsByPath, noScriptReferences);
            }

            foreach (var reference in noScriptReferences)
            {
                sb.AppendLine($"- `{reference.Owner.GetType().Name}.{reference.FieldName}` (no backing script found)");
            }
            foreach (var (path, fieldLabels) in fieldLabelsByPath)
            {
                AppendScriptSource(sb, path, fieldLabels);
            }
            return sb.ToString();
        }

        // Groups references by their backing script path so a script with multiple SoundID
        // fields is printed once, listing every matched field instead of only the first.
        private static void GroupScriptSource(SoundIDFieldReference reference, Dictionary<string, List<string>> fieldLabelsByPath, List<SoundIDFieldReference> noScriptReferences)
        {
            MonoScript script = reference.Owner switch
            {
                MonoBehaviour mono => MonoScript.FromMonoBehaviour(mono),
                ScriptableObject scriptableObject => MonoScript.FromScriptableObject(scriptableObject),
                _ => null,
            };

            if (!script)
            {
                noScriptReferences.Add(reference);
                return;
            }

            string path = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            string ownerLabel = $"{reference.Owner.GetType().Name}.{reference.FieldName}";
            if (!fieldLabelsByPath.TryGetValue(path, out var fieldLabels))
            {
                fieldLabels = new List<string>();
                fieldLabelsByPath[path] = fieldLabels;
            }
            fieldLabels.Add(ownerLabel);
        }

        private static void AppendScriptSource(StringBuilder sb, string path, List<string> fieldLabels)
        {
            string fieldList = string.Join(", ", fieldLabels.ConvertAll(label => $"`{label}`"));
            if (!File.Exists(path))
            {
                sb.AppendLine($"- {fieldList} (declared in {path}, file not found)");
                return;
            }

            string content = File.ReadAllText(path);
            sb.AppendLine($"`{Path.GetFileName(path)}` — SoundID field(s): {fieldList}");
            sb.AppendLine("```csharp");
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        internal static void AppendSerializedObjectAsMarkdownTable(StringBuilder sb, SerializedObject so, params string[] skipTopLevelNames)
        {
            using (var iterator = so.GetIterator())
            {
                bool enterChildren = true;
                int iterations = 0;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = iterator.depth < MaxPropertyDepth;

                    if (++iterations > MaxIterations)
                    {
                        sb.AppendLine($"*(property iteration exceeded {MaxIterations}, possible circular reference — truncated)*");
                        break;
                    }

                    if (iterator.name.StartsWith("m_", StringComparison.Ordinal))
                    {
                        enterChildren = false;
                        continue;
                    }

                    if (iterator.depth == 0 && skipTopLevelNames != null && Array.IndexOf(skipTopLevelNames, iterator.name) >= 0)
                    {
                        enterChildren = false;
                        continue;
                    }

                    if (iterator.propertyType == SerializedPropertyType.Generic)
                    {
                        continue;
                    }

                    string value = GetPropertyValueString(iterator);
                    if (value != null)
                    {
                        sb.AppendLine($"| {iterator.displayName} | {EscapeMarkdownTableCell(value)} |");
                    }
                }
            }
        }

        internal static string GetPropertyValueString(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("0.###");
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Enum:
                    return prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    var objRef = prop.objectReferenceValue;
                    if (!objRef)
                    {
                        return "None";
                    }
                    return objRef is PlaybackGroup pg ? $"{pg.name} ({pg.GetType().Name})" : objRef.name;
                case SerializedPropertyType.Color: return prop.colorValue.ToString();
                case SerializedPropertyType.Vector2: return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return prop.vector3Value.ToString();
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                default: return null;
            }
        }

        private static string EscapeMarkdownTableCell(string value)
        {
            return value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", string.Empty);
        }
    }
}
