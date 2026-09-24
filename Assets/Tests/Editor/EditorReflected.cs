using System;
using System.Reflection;
using Ami.BroAudio.Runtime;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// The EditMode suite's counterpart of <c>TestAudioLibrary.Reflected</c>: every non-public member this
    /// assembly reaches by name, in one place to update on a rename, and one lookup family that fails with a
    /// <see cref="BroAudioException"/> naming the exact member. <c>TestAudioLibrary.Reflected</c> lives in the
    /// runtime test assembly, which cannot name Editor-only types and only resolves private INSTANCE members,
    /// so the Editor-only and static/constructor cases live here. A private instance field on an Editor type
    /// still goes through <c>TestAudioLibrary.Reflected.Field</c>, with its name kept here.
    /// </summary>
    public static class EditorReflected
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Names on <c>Ami.Extension.AudioClipEditingHelper</c>.</summary>
        public static class AudioClipEditingHelper
        {
            public const string SampleDatas = "_sampleDatas";
        }

        /// <summary>Names on <c>Ami.BroAudio.Editor.EditorSetting</c>.</summary>
        public static class EditorSetting
        {
            /// <summary>Private const prefix of the EditorPrefs key behind <c>LastEditAudioAsset</c>.</summary>
            public const string LastEditAudioAssetPrefsKey = "LastEditAudioAssetPrefsKey";
        }

        /// <summary>Names on <c>Ami.BroAudio.Editor.IssueReportWindow</c>.</summary>
        public static class IssueReportWindow
        {
            /// <summary>Private const prefix of the EditorPrefs key the Save dialog remembers its folder under.</summary>
            public const string LastSaveDirectoryPrefKey = "LastSaveDirectoryPrefKey";
        }

        /// <summary>Names on the <c>internal static</c> <c>Ami.BroAudio.Editor.IssueReportMarkdown</c>.</summary>
        public static class IssueReportMarkdown
        {
            public const string TypeName = "Ami.BroAudio.Editor.IssueReportMarkdown";
            public const string ComposeTitle = "ComposeTitle";
            public const string BuildGitHubIssueURL = "BuildGitHubIssueURL";
        }

        /// <summary>Resolves a type by full name from the given assembly (for internal types).</summary>
        public static Type ResolveType(Assembly assembly, string fullName)
        {
            Type type = assembly.GetType(fullName);
            if (type == null)
            {
                throw new BroAudioException($"Reflection: type {fullName} could not be resolved in {assembly.GetName().Name}. " +
                    "Renamed or moved? Update EditorReflected and its caller.");
            }
            return type;
        }

        /// <summary>Resolves a static method of any visibility.</summary>
        public static MethodInfo StaticMethod(Type type, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, AnyStatic);
            if (method == null)
            {
                throw new BroAudioException($"Reflection: static {type.Name}.{methodName} could not be resolved. " +
                    "Renamed or moved? Update EditorReflected and its caller.");
            }
            return method;
        }

        /// <summary>Resolves a non-public instance constructor by its exact parameter list.</summary>
        public static ConstructorInfo Constructor(Type type, params Type[] parameterTypes)
        {
            ConstructorInfo ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, parameterTypes, null);
            if (ctor == null)
            {
                string signature = string.Join(", ", Array.ConvertAll(parameterTypes, t => t.Name));
                throw new BroAudioException($"Reflection: constructor {type.Name}({signature}) could not be resolved. " +
                    "Signature changed? Update EditorReflected and its caller.");
            }
            return ctor;
        }

        /// <summary>Reads a string <c>const</c> of any visibility.</summary>
        public static string StringConstant(Type type, string constName)
        {
            FieldInfo field = type.GetField(constName, AnyStatic);
            if (field == null || !field.IsLiteral || !(field.GetRawConstantValue() is string value))
            {
                throw new BroAudioException($"Reflection: string const {type.Name}.{constName} could not be resolved. " +
                    "Renamed, moved or no longer a const? Update EditorReflected and its caller.");
            }
            return value;
        }
    }
}