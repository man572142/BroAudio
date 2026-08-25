using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    public enum IssueType { None = 0, Editor, PlayMode, Build }
    public enum IntegrationStyle { BroAudioComponent = 0, Script }

    [Serializable]
    public class IssueReportDraft
    {
        public IssueType Type = IssueType.None;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string Expectation = string.Empty;
        public List<SoundID> ProblemSounds = new List<SoundID>();
        public UnityEngine.Object TargetObject = null;
        public IntegrationStyle Integration = IntegrationStyle.BroAudioComponent;
        public string ScriptCollection = string.Empty;
        public string ConsoleOutput = string.Empty;

        public static class NameOf
        {
            public const string Type = nameof(Type);
            public const string Title = nameof(Title);
            public const string Description = nameof(Description);
            public const string Expectation = nameof(Expectation);
            public const string ProblemSounds = nameof(ProblemSounds);
            public const string TargetObject = nameof(TargetObject);
            public const string Integration = nameof(Integration);
            public const string ScriptCollection = nameof(ScriptCollection);
            public const string ConsoleOutput = nameof(ConsoleOutput);
        }
    }
}
