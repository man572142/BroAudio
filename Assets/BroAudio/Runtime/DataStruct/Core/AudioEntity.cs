using System.Collections.Generic;
using Ami.BroAudio.Runtime;
using Ami.BroAudio;
using UnityEngine;
using Ami.Extension;

namespace Ami.BroAudio.Data
{
    [System.Serializable]
    public partial class AudioEntity : ScriptableObject, IAudioEntity, IReadOnlyAudioEntity
    {
        public string Name => name;

        [System.Obsolete("IDs are superceded by direct references", true)]
        [field: SerializeField] 
        public int ID { get; private set; }

        [SerializeField] private MulticlipsPlayMode MulticlipsPlayMode = MulticlipsPlayMode.Single;
        [SerializeField] private PlaybackGroup _group;

        private PlaybackGroup _upperGroup => AudioAsset != null ? AudioAsset.PlaybackGroup : null;

        public BroAudioClip[] Clips;

        [field: SerializeField] public AudioAsset AudioAsset { get; private set; }
        [field: SerializeField] public BroAudioType AudioType { get; private set; }
        [field: SerializeField] public float MasterVolume { get; private set; }
        [field: SerializeField] public bool Loop { get; private set; }
        [field: SerializeField] public bool SeamlessLoop { get; private set; }
        [field: SerializeField] public float TransitionTime { get; private set; }
        [field: SerializeField] public SpatialSetting SpatialSetting { get; private set; }
        [field: SerializeField] public int Priority { get; private set; }
        [field: SerializeField] public float Pitch { get; private set; }
        [field: SerializeField] public float PitchRandomRange { get; private set; }
        [field: SerializeField] public float VolumeRandomRange { get; private set; }
        [field: SerializeField] public RandomFlag RandomFlags { get; private set; }
        [field: SerializeField] public AudioEntityFlag Flags { get; private set; }
        public PlaybackGroup PlaybackGroup => _group ? _group : _upperGroup;

        IReadOnlyList<IBroAudioClip> IReadOnlyAudioEntity.Clips => Clips;
        private IClipSelectionStrategy _clipSelectionStrategy = null;

        public IBroAudioClip PickNewClip() => PickNewClip(context: 0, out _);
        public IBroAudioClip PickNewClip(ClipSelectionContext context) => PickNewClip(context, out _);
        public IBroAudioClip PickNewClip(ClipSelectionContext context, out int index)
        {
            switch (MulticlipsPlayMode)
            {
                case MulticlipsPlayMode.Single: EnsureClipSelectionStrategy<SingleClipStrategy>(); break;
                case MulticlipsPlayMode.Sequence: EnsureClipSelectionStrategy<SequenceClipStrategy>(); break;
                case MulticlipsPlayMode.Random: EnsureClipSelectionStrategy<RandomClipStrategy>(); break;
                case MulticlipsPlayMode.Shuffle: EnsureClipSelectionStrategy<ShuffleClipStrategy>(); break;
                case MulticlipsPlayMode.Chained: EnsureClipSelectionStrategy<ChainedClipStrategy>(); break;
                case MulticlipsPlayMode.Velocity: EnsureClipSelectionStrategy<VelocityClipStrategy>(); break;
#if PACKAGE_LOCALIZATION
                case MulticlipsPlayMode.Localization:
                    EnsureClipSelectionStrategy<LocalizationClipStrategy>();
                    var id = new SoundID(this);
                    ((LocalizationClipStrategy)_clipSelectionStrategy).Inject(
                        _localizedAudio,
                        Name,
                        () => SoundManager.HasInstance && SoundManager.Instance.TryGetCachedLocalizedClip(id, out var c) ? c : null);
                    break;
#endif
                default:
                    Debug.LogError(Utility.LogTitle + $"Invalid play mode: {MulticlipsPlayMode}");
                    EnsureClipSelectionStrategy<SingleClipStrategy>(); 
                    break;
            }
            context.EntityName = Name;
            return _clipSelectionStrategy.SelectClip(Clips, context, out index);
        }

        private void EnsureClipSelectionStrategy<T>() where T : IClipSelectionStrategy, new()
        {
            if (_clipSelectionStrategy == null || _clipSelectionStrategy.GetType() != typeof(T))
            {
                _clipSelectionStrategy = new T();
            }
        }

        public bool Validate() => Utility.Validate(Name, Clips);
        public MulticlipsPlayMode PlayMode => MulticlipsPlayMode;

        public float GetMasterVolume() => GetRandomValue(MasterVolume, RandomFlag.Volume);
        public float GetPitch() => GetRandomValue(Pitch, RandomFlag.Pitch);

        public float GetRandomValue(float baseValue, RandomFlag flag)
        {
            if(!RandomFlags.Contains(flag))
            {
                return baseValue;
            }

            float range = flag switch 
            { 
                RandomFlag.Pitch => PitchRandomRange,
                RandomFlag.Volume => VolumeRandomRange,
                _ => throw new System.InvalidOperationException(),
            };

            return GetRandomValue(baseValue, range);
        }

        public static float GetRandomValue(float baseValue, float range)
        {
            float half = range * 0.5f;
            return baseValue + Random.Range(-half, half);
        }
        
        public bool HasLoop(out LoopType loopType, out float transitionTime)
        {
            return HasLoop(out loopType, out transitionTime,
                SoundManager.Instance.Setting.DefaultChainedPlayModeLoop,
                SoundManager.Instance.Setting.DefaultChainedPlayModeTransitionTime);
        }

        public bool HasLoop(out LoopType loopType, out float transitionTime, LoopType chainedDefaultLoop, float chainedDefaultTransitionTime)
        {
            loopType = LoopType.None;
            transitionTime = 0f;
            if (Loop)
            {
                loopType = LoopType.Loop;
            }
            else if (SeamlessLoop)
            {
                loopType = LoopType.SeamlessLoop;
                transitionTime = TransitionTime;
            }
            else if (MulticlipsPlayMode == MulticlipsPlayMode.Chained)
            {
                loopType = chainedDefaultLoop;
                transitionTime = loopType != LoopType.None ? chainedDefaultTransitionTime : 0f;
            }
            return loopType != LoopType.None;
        }

        public void ResetMultiClipStrategy()
        {
            if (_clipSelectionStrategy != null)
            {
                _clipSelectionStrategy.Reset();
            }
        }

        public void ResetMultiClipStrategy(string sequenceId)
        {
            if (_clipSelectionStrategy is SequenceClipStrategy sequenceStrategy)
            {
                sequenceStrategy.Reset(sequenceId);
            }
        }

        public override string ToString()
        {
            return Name;
        }

#if UNITY_EDITOR

        public static class EditorPropertyName
		{
            public static string MulticlipsPlayMode => nameof(AudioEntity.MulticlipsPlayMode);
            public static string PlaybackGroup => nameof(AudioEntity._group);
            public static string SeamlessType => nameof(AudioEntity.SeamlessTransitionType);
            public static string TransitionTempo => nameof(AudioEntity.TransitionTempo);
            public static string SnapToFullVolume => nameof(AudioEntity.SnapToFullVolume);
        }

        [SerializeField] private SeamlessType SeamlessTransitionType = SeamlessType.ClipSetting;
        [SerializeField] private TempoTransition TransitionTempo = default;
        [SerializeField] private bool SnapToFullVolume = false;

        public void SetAudioAsset(AudioAsset asset)
        {
            AudioAsset = asset;
        }
#endif

        [System.Obsolete("Only for conversion")]
        internal static AudioEntity ConvertLegacy(AudioEntity_LEGACY legacy, AudioAsset asset)
        {
            var entity = ScriptableObject.CreateInstance<AudioEntity>();

            entity._group = legacy._group;
            entity.AudioAsset = asset;
            entity.MulticlipsPlayMode = legacy.MulticlipsPlayMode;

            entity.ID = legacy.ID;
            entity.name = legacy.Name;
            entity.Clips = legacy.Clips;
            entity.MasterVolume = legacy.MasterVolume;
            entity.Loop = legacy.Loop;
            entity.SeamlessLoop = legacy.SeamlessLoop;
            entity.TransitionTime = legacy.TransitionTime;
            entity.SpatialSetting = legacy.SpatialSetting;
            entity.Priority = legacy.Priority;
            entity.Pitch = legacy.Pitch;
            entity.PitchRandomRange = legacy.PitchRandomRange;
            entity.VolumeRandomRange = legacy.VolumeRandomRange;
            entity.RandomFlags = legacy.RandomFlags;

            entity.AudioType = Utility.GetAudioType(legacy.ID);

#if UNITY_EDITOR
            entity.SeamlessTransitionType = legacy.SeamlessTransitionType;
            entity.TransitionTempo = legacy.TransitionTempo;
            entity.SnapToFullVolume = legacy.SnapToFullVolume;
#endif

#if PACKAGE_ADDRESSABLES
            entity.UseAddressables = legacy.UseAddressables;
#endif

            return entity;
        }

        public static AudioEntity CreateNewInstance(AudioAsset asset, string name, BroAudioType audioType)
        {
            var entity = ScriptableObject.CreateInstance<AudioEntity>();
            entity.name = name;
            entity.AudioAsset = asset;
            entity.AudioType = audioType;
            entity.MasterVolume = AudioConstant.FullVolume;
            entity.Pitch = AudioConstant.DefaultPitch;
            entity.Priority = AudioConstant.DefaultPriority;
            return entity;
        }
    }
}