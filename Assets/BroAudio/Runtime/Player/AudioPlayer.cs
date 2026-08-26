using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;
using Ami.BroAudio.Data;
using Ami.Extension;
using static Ami.BroAudio.Tools.BroName;

namespace Ami.BroAudio.Runtime
{
    [RequireComponent(typeof(AudioSource)), AddComponentMenu("")]
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>
    {
        [SerializeField] private AudioSource AudioSource = null;

        private IBroAudioClip _clip;
        private List<AudioPlayerDecorator> _decorators = null;
        private string _sendParaName = null;
        private string _currTrackName = null;

        private IDisposable _proxy = null;
        private AudioFilterReader _audioFilterReader = null;
        
        private struct AddedEffect
        {
            public Component Component;
            public IAudioEffectModifier Modifier;
        }
        private List<AddedEffect> _addedEffects = null;

        public SoundID ID { get; private set; } = SoundID.Invalid;

        public bool IsActive => ID.IsValid();
        public bool IsPlaying => AudioSource.isPlaying;
        public Vector3 PlayingPosition => _pref.Position;
        public bool IsStopping { get; private set; }
        public EffectType CurrentActiveTrackEffects { get; private set; } = EffectType.None;
        public bool IsUsingTrackEffect => CurrentActiveTrackEffects != EffectType.None;
        public bool IsDominator => HasDecoratorOf<DominatorPlayer>();
        public bool IsBGM => HasDecoratorOf<MusicPlayer>();
        public IBroAudioClip CurrentPlayingClip => _clip;
        IAudioSourceProxy IAudioPlayer.AudioSource
        {
            get
            {
                if(!IsActive)
                {
                    Debug.LogError(Utility.LogTitle +
                        "The audio player is not playing! Please consider accessing the AudioSource via OnStart() or OnUpdate() methods.");
                    return Empty.AudioSource;
                }
                _proxy ??= new AudioSourceProxy(AudioSource);
                return _proxy as IAudioSourceProxy;
            }
        }

        private AudioTrackType TrackType { get; set; } = AudioTrackType.Generic;
        private AudioMixerGroup AudioTrack 
        {
            set
            {
                AudioSource.outputAudioMixerGroup = value;
                if(value == null)
                {
                    _currTrackName = null;
                    _sendParaName = null;
                }
            }
        }

        private string VolumeParaName => IsUsingTrackEffect ? GetSendParaName() : GetCurrentTrackName();

        IAudioMixerPool Mixer => SoundManager.Instance;

        private bool TryGetMixerAndTrack(out AudioMixer mixer, out AudioMixerGroup track)
        {
            track = AudioSource.outputAudioMixerGroup;
            mixer = track?.audioMixer;
            return !ReferenceEquals(mixer, null) && !ReferenceEquals(track, null);
        }

        protected virtual void Awake()
        {
# pragma warning disable UNT0023
            AudioSource ??= GetComponent<AudioSource>(); 
# pragma warning restore UNT0023
            InitVolumeModule();
        }

        private void Update()
        {
            if(!IsActive)
            {
                return;
            }

            if(_pref.HasFollowTarget(out var target))
            {
                transform.position = target.position;
            }

#if !UNITY_WEBGL
            MonitorVirtualTrack();
#endif
        }

        private void SetSpatial(PlaybackPreference pref)
        {
            SpatialSetting setting = pref.Entity.SpatialSetting;
            SetSpatialBlend();

            if (setting == null)
            {
                return;
            }

            AudioSource.panStereo = setting.StereoPan;
            AudioSource.dopplerLevel = setting.DopplerLevel;
            AudioSource.minDistance = setting.MinDistance;
            AudioSource.maxDistance = setting.MaxDistance;

            AudioSource.SetCustomCurveOrResetDefault(setting.ReverbZoneMix, AudioSourceCurveType.ReverbZoneMix);
            AudioSource.SetCustomCurveOrResetDefault(setting.Spread, AudioSourceCurveType.Spread);

            AudioSource.rolloffMode = setting.RolloffMode;
            if (setting.RolloffMode == AudioRolloffMode.Custom)
            {
                AudioSource.SetCustomCurve(AudioSourceCurveType.CustomRolloff, setting.CustomRolloff);
            }

            if (setting.HasLowPassFilter && setting.LowpassLevelCustomCurve != null && 
                _addedEffects == null) // addedEffect might be transferred from the previous player, so we don't need to set it here.'
            {
                this.AddLowPassEffect(x => x.customCutoffCurve = setting.LowpassLevelCustomCurve);
            }

            void SetSpatialBlend()
            {
                if (pref.HasFollowTarget(out var followTarget))
                {
                    transform.position = followTarget.position;
                    SetTo3D();
                }
                else if (pref.HasSpecifiedPosition(out var position))
                {
                    transform.position = position;
                    SetTo3D();
                }
                // The log is unnecessary and may cause misunderstandings, as the Play method already provides clear summaries.
                //else if (setting != null && !setting.SpatialBlend.IsDefaultCurve(AudioConstant.SpatialBlend_2D) && pref.Entity is IEntityIdentity entity)
                //{
                //	Debug.LogWarning(Utility.LogTitle + $"You've set a non-2D SpatialBlend for :{entity.Name}, but didn't specify a position or a follow target when playing it");
                //}
            }

            void SetTo3D()
            {
                if (setting != null && !setting.SpatialBlend.IsDefaultCurve(AudioConstant.SpatialBlend_2D))
                {
                    // Don't use SetCustomCurveOrResetDefault, it will set to 2D if isDefaultCurve.
                    AudioSource.SetCustomCurve(AudioSourceCurveType.SpatialBlend, setting.SpatialBlend);
                }
                else
                {
                    // force to 3D if it's played with a position or a follow target, even if it has no custom curve. 
                    AudioSource.spatialBlend = AudioConstant.SpatialBlend_3D;
                }
            }
        }

        private void ResetSpatial()
        {
            AudioSource.spatialBlend = AudioConstant.SpatialBlend_2D;
            transform.position = Vector3.zero;

            AudioSource.panStereo = AudioConstant.DefaultPanStereo;
            AudioSource.dopplerLevel = AudioConstant.DefaultDoppler;
            AudioSource.minDistance = AudioConstant.AttenuationMinDistance;
            AudioSource.maxDistance = AudioConstant.AttenuationMaxDistance;
            AudioSource.reverbZoneMix = AudioConstant.DefaultReverZoneMix;
            AudioSource.spread = AudioConstant.DefaultSpread;
            AudioSource.rolloffMode = AudioConstant.DefaultRolloffMode;
        }

        IAudioPlayer IAudioPlayer.SetVelocity(int velocity)
        {
            _pref.SetVelocity(velocity);
            return this;
        }

        IAudioPlayer IAudioPlayer.SetSequenceId(string sequenceId)
        {
            _pref.SequenceId = sequenceId;
            return this;
        }

        public void SetTrackEffect(EffectType effect, SetEffectMode mode)
        {
            if(!ID.IsValid() || (effect == EffectType.None && mode != SetEffectMode.Override) 
                || !TryGetMixerAndTrack(out var mixer, out _) || !TryGetMixerDecibelVolume(out float mixerDecibelVolume))
            {
                return;
            }

            bool oldUsingEffectState = IsUsingTrackEffect;
            switch (mode)
            {
                case SetEffectMode.Add:
                    CurrentActiveTrackEffects |= effect;
                    break;
                case SetEffectMode.Remove:
                    CurrentActiveTrackEffects &= ~effect;
                    break;
                case SetEffectMode.Override:
                    CurrentActiveTrackEffects = effect;
                    break;
            }
            bool newUsingEffectState = IsUsingTrackEffect;
            if (oldUsingEffectState != newUsingEffectState)
            {
                string from = IsUsingTrackEffect ? GetCurrentTrackName() : GetSendParaName();
                string to = IsUsingTrackEffect ? GetSendParaName() : GetCurrentTrackName();
                mixer.ChangeChannel(from, to, mixerDecibelVolume);
            }
        }

        private void ResetEffect()
        {
            if(IsUsingTrackEffect && TryGetMixerAndTrack(out var mixer, out _))
            {
                mixer.SafeSetFloat(GetSendParaName(), AudioConstant.MinDecibelVolume);
            }
            CurrentActiveTrackEffects = EffectType.None;
        }

        private string GetSendParaName()
        {
            if(IsUsingTrackEffect)
            {
                _sendParaName ??= GetCurrentTrackName() + EffectParaNameSuffix;
            }
            return _sendParaName ?? string.Empty;
        }

        private string GetCurrentTrackName()
        {
            _currTrackName ??= AudioSource.outputAudioMixerGroup.name;
            return _currTrackName ?? string.Empty;
        }

        IMusicPlayer IMusicDecoratable.AsBGM()
        {
            return Utility.GetOrCreateDecorator(ref _decorators, () => new MusicPlayer(this));
        }

#if !UNITY_WEBGL
        IPlayerEffect IEffectDecoratable.AsDominator()
        {
            return Utility.GetOrCreateDecorator(ref _decorators, () => new DominatorPlayer(this));
        }
#endif

        public void GetOutputData(float[] samples, int channels) => AudioSource.GetOutputData(samples, channels);
        public void GetSpectrumData(float[] samples, int channels, FFTWindow window) => AudioSource.GetSpectrumData(samples, channels, window);
        IAudioPlayer IAudioPlayer.OnAudioFilterRead(Action<float[], int> onAudioFilterRead)
        {
            if (!_audioFilterReader)
            {
                _audioFilterReader = gameObject.AddComponent<AudioFilterReader>();
            }
            _audioFilterReader.OnTriggerAudioFilterRead = onAudioFilterRead;
            return this;
        }

        IAudioPlayer IAudioPlayer.AddAudioEffect<T, TProxy>(Action<TProxy> onSet)
        {
            if (!IsActive)
            {
                Debug.LogError(Utility.LogTitle + $"Cannot add {typeof(T).Name} to inactive audio player!");
                return this;
            }

            if (_addedEffects != null && _addedEffects.Any(x => x.Component is T))
            {
                Debug.LogWarning(Utility.LogTitle + $"Effect {typeof(T).Name} already exists!", this);
                return this;
            }

            T component = gameObject.AddComponent<T>();
            var modifier = Utility.CreateAudioEffectProxy(component);

            _addedEffects ??= new List<AddedEffect>();
            _addedEffects.Add(new AddedEffect { Component = component, Modifier = modifier });

            onSet?.Invoke(modifier as TProxy);
            return this;
        }

        IAudioPlayer IAudioPlayer.RemoveAudioEffect<T>()
        {
            if (!IsActive)
            {
                Debug.LogError(Utility.LogTitle + $"Cannot remove {typeof(T).Name} from inactive audio player!");
                return this;
            }

            if (_addedEffects == null || _addedEffects.Count == 0)
            {
                Debug.LogWarning(Utility.LogTitle + $"No effects to remove from audio player!");
                return this;
            }

            for (int i = _addedEffects.Count - 1; i >= 0; i--)
            {
                var effect = _addedEffects[i];
                if (effect.Component is T)
                {
                    if (effect.Component != null)
                    {
                        Destroy(effect.Component);
                    }
                    _addedEffects.RemoveAt(i);
                    break; // Remove only the first matching effect
                }
            }
            return this;
        }

        internal bool TransferOnUpdates(out Delegate[] onUpdateDelegates)
        {
            onUpdateDelegates = null;
            if (_onUpdate != null)
            {
                onUpdateDelegates = _onUpdate.GetInvocationList();
                _onUpdate = null;
            }
            return onUpdateDelegates != null;
        }

        internal bool TransferOnEnds(out Delegate[] onEndDelegates) 
        {
            onEndDelegates = null;
            if(_onEnd != null)
            {
                onEndDelegates = _onEnd.GetInvocationList();
                _onEnd = null;
            }
            return onEndDelegates != null;
        }

        internal bool TransferOnPauses(out Delegate[] onPauseDelegates)
        {
            onPauseDelegates = null;
            if (_onPaused != null)
            {
                onPauseDelegates = _onPaused.GetInvocationList();
                _onPaused = null;
            }
            return onPauseDelegates != null;
        }

        internal bool TransferDecorators(out IReadOnlyList<AudioPlayerDecorator> decorators)
        {
            decorators = _decorators;
            _decorators = null;
            return decorators != null;
        }

        internal void SetDecorators(IReadOnlyList<AudioPlayerDecorator> decorators)
        {
            _decorators = decorators as List<AudioPlayerDecorator>;
        }

        internal void TransferAddedEffectComponents(AudioPlayer newInstance)
        {
            newInstance.SetAddedEffectComponents(_addedEffects);
        }

        private void SetAddedEffectComponents(IReadOnlyList<AddedEffect> previousPlayerEffects)
        {
            if (previousPlayerEffects == null)
            {
                return;
            }
            var go = gameObject;
            for (int i = 0; i < previousPlayerEffects.Count; i++)
            {
                var copiedEffect = previousPlayerEffects[i];
                var newComponent = go.AddComponent(Utility.GetFilterTypeFromProxy(copiedEffect.Modifier));
                copiedEffect.Modifier.TransferValueTo(newComponent as Behaviour);
                copiedEffect.Component = newComponent;
                _addedEffects ??= new List<AddedEffect>();
                _addedEffects.Add(copiedEffect);
            }
        }

        internal bool TryGetDecorator<T>(out T decorator) where T : AudioPlayerDecorator
        {
            decorator = null;
            if (_decorators != null)
            {
                return _decorators.TryGetDecorator<T>(out decorator);
            }
            return false;
        }

        private bool HasDecoratorOf<T>() where T : AudioPlayerDecorator
        {
            return _decorators != null && _decorators.TryGetDecorator<T>(out _);
        }
    }
}