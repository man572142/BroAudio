using UnityEngine;
using Ami.Extension;
using Ami.BroAudio.Runtime;
using System;
using UnityEngine.Audio;

namespace Ami.BroAudio.Data
{
#if BroAudio_DevOnly
    [CreateAssetMenu(menuName = nameof(BroAudio) + "/Runtime Setting", fileName = Tools.BroName.RuntimeSettingName)]
#endif
    public class RuntimeSetting : ScriptableObject
    {
        [Obsolete("This feature has been moved to " + nameof(PlaybackGroup)), HideInInspector]
        public float CombFilteringPreventionInSeconds = FactorySettings.CombFilteringPreventionInSeconds;
        public bool LogAccessRecycledPlayerWarning = true;
        public Ease DefaultFadeInEase = FactorySettings.DefaultFadeInEase;
        public Ease DefaultFadeOutEase = FactorySettings.DefaultFadeOutEase;
        public Ease SeamlessFadeInEase = FactorySettings.SeamlessFadeInEase;
        public Ease SeamlessFadeOutEase = FactorySettings.SeamlessFadeOutEase;
        public FilterSlope AudioFilterSlope = FactorySettings.AudioFilterSlope;
        public AudioMixerUpdateMode UpdateMode = FactorySettings.UpdateMode;

        public int DefaultAudioPlayerPoolSize = FactorySettings.DefaultAudioPlayerPoolSize;

        public bool AlwaysPlayMusicAsBGM = true;
        public Transition DefaultBGMTransition = FactorySettings.DefaultBGMTransition;
        public float DefaultBGMTransitionTime = FactorySettings.DefaultBGMTransitionTime;
        
        public LoopType DefaultChainedPlayModeLoop = FactorySettings.DefaultChainedPlayModeLoop;
        public float DefaultChainedPlayModeTransitionTime = FactorySettings.DefaultChainedPlayModeSeamlessTransitionTime;

        public PlaybackGroup GlobalPlaybackGroup = null;

        public bool AutomaticallyLoadAddressableAudioClips = FactorySettings.AutomaticallyLoadAddressableAudioClips;
        public float AutomaticallyUnloadUnusedAddressableAudioClipsAfter = FactorySettings.AutomaticallyUnloadUnusedAddressableAudioClipsAfter;
        public LogType AddressablesNonPreloadedLogLevel = FactorySettings.AddressablesNonPreloadedLogLevel;

#if UNITY_EDITOR
        public void ResetToFactorySettings()
        {
            DefaultFadeInEase = FactorySettings.DefaultFadeInEase;
            DefaultFadeOutEase = FactorySettings.DefaultFadeOutEase;
            SeamlessFadeInEase = FactorySettings.SeamlessFadeInEase;
            SeamlessFadeOutEase = FactorySettings.SeamlessFadeOutEase;
            AudioFilterSlope = FactorySettings.AudioFilterSlope;
            DefaultAudioPlayerPoolSize = FactorySettings.DefaultAudioPlayerPoolSize;
            AlwaysPlayMusicAsBGM = true;
            DefaultBGMTransition = FactorySettings.DefaultBGMTransition;
            DefaultBGMTransitionTime = FactorySettings.DefaultBGMTransitionTime;
            DefaultChainedPlayModeLoop = FactorySettings.DefaultChainedPlayModeLoop;
            DefaultChainedPlayModeTransitionTime = FactorySettings.DefaultChainedPlayModeSeamlessTransitionTime;

            AutomaticallyLoadAddressableAudioClips = FactorySettings.AutomaticallyLoadAddressableAudioClips;
            AutomaticallyUnloadUnusedAddressableAudioClipsAfter = FactorySettings.AutomaticallyUnloadUnusedAddressableAudioClipsAfter;
        }
#endif
        public class FactorySettings
        {
            public const float CombFilteringPreventionInSeconds = 0.04f;
            public const Ease DefaultFadeInEase = Ease.InCubic;
            public const Ease DefaultFadeOutEase = Ease.OutSine;
            public const Ease SeamlessFadeInEase = Ease.OutCubic;
            public const Ease SeamlessFadeOutEase = Ease.OutSine;
            public const FilterSlope AudioFilterSlope = FilterSlope.FourPole;
            public const AudioMixerUpdateMode UpdateMode = AudioMixerUpdateMode.Normal;

            public const int DefaultAudioPlayerPoolSize = 5;
            public const Transition DefaultBGMTransition = Transition.CrossFade;
            public const float DefaultBGMTransitionTime = 2f;

            public const LoopType DefaultChainedPlayModeLoop = LoopType.SeamlessLoop;
            public const float DefaultChainedPlayModeSeamlessTransitionTime = 0.1f;

            public const bool AutomaticallyLoadAddressableAudioClips = false;
            public const float AutomaticallyUnloadUnusedAddressableAudioClipsAfter = 60f;
            public const LogType AddressablesNonPreloadedLogLevel = LogType.Error;
        }
    }
}