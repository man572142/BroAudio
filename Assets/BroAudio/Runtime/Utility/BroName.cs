namespace Ami.BroAudio.Tools
{
	public static class BroName
	{
        public const string AudioPlayerPrefabName = "AudioPlayer";
        public const string Demo = "Demo";

        #region File Path
        public const string ResourcesFolder = "Resources";
        public const string EditorFolder = "Editor";
        public const string CoreDataName = "BroAudioData";
        public const string InstructionFileName = "BroInstruction";
		public const string RuntimeSettingName = "BroRuntimeSetting";
        public const string EditorSettingName = "BroEditorSetting";
        public const string GlobalPlaybackGroupName = "GlobalPlaybackGroup";
		public const string EditorAudioMixerName = "EditorBroAudioMixer";
        public const string MainAssetPath = "Assets/BroAudio";
        #endregion

        public const string CombFilteringTimeName = "Time To Prevent Comb Filtering";

        #region MenuItem
        public const string MenuItem_BroAudio = "Tools/BroAudio/";
		public const string MenuItem_BroAudio_Window = "Window/Audio/BroAudio/";
		public const string MenuItem_LibraryManager = "Library Manager";
		public const string MenuItem_ClipEditor = "Audio Clip Editor";
		public const string MenuItem_EffectEditor = "Audio Effect Editor";
		public const string MenuItem_Preferences = "Preferences";
		public const string MenuItem_Info = "Info";
        public const string MenuItem_SetupWizard = "Setup Wizard";
        public const string MenuItem_IssueReport = "Report an Issue";
		#endregion

		#region Audio Mixer
		public const string MixerName = "BroAudioMixer";
		public const string MasterTrackName = "Master";
		public const string GenericTrackName = "Track";
		public const string MainTrackName = "Main";
		public const string MainDominatedTrackName = "Main_Dominated";
		public const string EffectTrackName = "Effect";
		public const string DominatorTrackName = "Dominator";
		#endregion

		#region Exposed Parameters Name
		public const string EffectParaNameSuffix = "_Effect";
		public const string PitchParaNameSuffix = "_Pitch";
		public const string LowPassParaNameSuffix = "_LowPass";
		public const string HighPassParaNameSuffix = "_HighPass";

        public const string LowPassParaName = EffectTrackName + LowPassParaNameSuffix;
        public const string HighPassParaName = EffectTrackName + HighPassParaNameSuffix;

        public const string Dominator_LowPassParaName = MainTrackName + LowPassParaNameSuffix;
		public const string Dominator_HighPassParaName = MainTrackName + HighPassParaNameSuffix;
        #endregion
    }
}