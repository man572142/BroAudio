using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using Ami.BroAudio.Data;
using Ami.Extension;
using System;
using static Ami.BroAudio.Utility;
using static Ami.BroAudio.Tools.BroName;

namespace Ami.BroAudio.Runtime
{
    [DisallowMultipleComponent, AddComponentMenu("")]
    public partial class SoundManager : MonoBehaviour, IAudioMixerPool
    {
#if !BroAudio_InitManually
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        public static void Init()
        {
            var prefab = Resources.Load(nameof(SoundManager)) as GameObject;
            if (prefab == null)
            {
                Debug.LogError(LogTitle + $"Initialize failed, please check {nameof(SoundManager)}.prefab in your Resources folder!");
                return;
            }

            prefab.SetActive(false);
            GameObject objectInstance = Instantiate(prefab);
            if (objectInstance.TryGetComponent(out SoundManager manager))
            {
                _instance = manager;
            }
            else
            {
                Debug.LogError(LogTitle + $"Initialize failed ,please add {nameof(SoundManager)} component to {nameof(SoundManager)}.prefab");
                return;
            }

            DontDestroyOnLoad(objectInstance);
            objectInstance.SetActive(true);
        }

        private static SoundManager _instance;
        public static SoundManager Instance 
        { 
            get 
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    return null;
                }
#endif
                return !HasInstance ? throw new BroAudioException("Bro Audio is not initialized! Please call <b>BroAudio.Init()</b> first when using manual initialization.") : _instance;
            } 
        }
        
        public static bool HasInstance => _instance != null;

        [SerializeField] AudioPlayer _audioPlayerPrefab = null;
        private AudioPlayerObjectPool _audioPlayerPool = null;
        private ObjectPool<AudioMixerGroup> _audioTrackPool = null;
        private ObjectPool<AudioMixerGroup> _dominatorTrackPool = null;

        [SerializeField] AudioMixer _broAudioMixer = null;

        [System.Obsolete("Only for backwards compatibility")]
        [SerializeField] BroAudioData _data = null;
        
        private Dictionary<BroAudioType, AudioTypePlaybackPreference> _audioTypePref = new Dictionary<BroAudioType, AudioTypePlaybackPreference>();
        private EffectAutomationHelper _automationHelper = null;
        private EffectAutomationHelper _dominatorAutomationHelper = null;
        private Dictionary<SoundID, AudioPlayer> _combFilteringPreventer = null;
        private Coroutine _masterVolumeCoroutine;
        private WaitForEndOfFrame _waitForAudioMixerParametersAvailable = new WaitForEndOfFrame();

        // Tracking for loaded addressable entities
        private Dictionary<SoundID, double> _loadedEntityLastPlayedTime = new Dictionary<SoundID, double>();
#if PACKAGE_ADDRESSABLES
        private Coroutine _addressableCleanupCoroutine = null;
#endif

#if UNITY_WEBGL
        public float WebGLMasterVolume { get; private set; } = AudioConstant.FullVolume;
#endif

        internal double ScheduledPlaybackWarmUpTime { get; private set; } = AudioConstant.MixerWarmUpTime;
        public AudioMixer AudioMixer => _broAudioMixer;
        WaitForEndOfFrame IAudioMixerPool.WaitForAudioMixerInitialization => _waitForAudioMixerParametersAvailable;

        private void Awake()
        {
            const string nullRefLog = LogTitle + "Please assign {{0}} in " + nameof(SoundManager) + ".prefab";
            if(!_broAudioMixer)
            {
                Debug.LogError(string.Format(nullRefLog, MixerName));
                return;
            }
            else if(!_audioPlayerPrefab)
            {
                Debug.LogError(string.Format(nullRefLog, AudioPlayerPrefabName));
                return;
            }

            CacheScheduledPlaybackWarmUpTime();
            _audioPlayerPool = new AudioPlayerObjectPool(_audioPlayerPrefab, transform, Setting.DefaultAudioPlayerPoolSize);
            AudioMixerGroup[] mixerGroups = _broAudioMixer.FindMatchingGroups(GenericTrackName);
            AudioMixerGroup[] dominatorGroups = _broAudioMixer.FindMatchingGroups(DominatorTrackName);
            
            _audioTrackPool = new AudioTrackObjectPool(mixerGroups);
            _dominatorTrackPool = new AudioTrackObjectPool(dominatorGroups, true);

            InitBank();
            _automationHelper = new EffectAutomationHelper(this, _broAudioMixer);
            _dominatorAutomationHelper = new EffectAutomationHelper(this, _broAudioMixer);

#if PACKAGE_ADDRESSABLES
            // Start the coroutine to cleanup unused loaded addressable entities
            _addressableCleanupCoroutine = StartCoroutine(AddressableCleanupRoutine());
#endif
        }

        private void Start()
        {
            _waitForAudioMixerParametersAvailable = null;
        }

        private void CacheScheduledPlaybackWarmUpTime()
        {
            AudioSettings.GetDSPBufferSize(out int bufferLength, out int numBuffers);
            int outputSampleRate = AudioSettings.outputSampleRate;
            if (bufferLength <= 0 || numBuffers <= 0 || outputSampleRate <= 0)
            {
                ScheduledPlaybackWarmUpTime = AudioConstant.MixerWarmUpTime;
                return;
            }

            double outputLatency = (double)bufferLength * numBuffers / outputSampleRate;
            // This handover path wakes on frames rather than DSP ticks, so keep the historical fallback as a floor.
            ScheduledPlaybackWarmUpTime = Math.Max(outputLatency, AudioConstant.MixerWarmUpTime);
        }

        private void OnDestroy()
        {
            MusicPlayer.CleanUp();

#if PACKAGE_ADDRESSABLES
            // Stop the cleanup coroutine
            if (_addressableCleanupCoroutine != null)
            {
                StopCoroutine(_addressableCleanupCoroutine);
                _addressableCleanupCoroutine = null;
            }
#endif

#if PACKAGE_LOCALIZATION
            ReleaseAllLocalizationPreloads();
#endif
        }

        #region InitBank
        private void InitBank()
        {
            ForeachConcreteAudioType(new PlaybackPrefInitializer() { AudioTypePref = _audioTypePref });
        }
        #endregion

        #region Volume
        public void SetVolume(float vol, BroAudioType targetType, float fadeTime)
        {
            targetType = targetType.ConvertEverythingFlag();
            if (targetType == BroAudioType.None)
            {
                Debug.LogWarning(LogTitle + $"SetVolume with {targetType} is meaningless");
                return;
            }

            if (targetType == BroAudioType.All)
            {
                SetMasterVolume(vol,fadeTime);
                return;
            }
            SetPlaybackPrefByType(targetType, vol , AudioTypePlaybackPreference.OnSetVolume);
            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && targetType.Contains(player.ID.ToAudioType()))
                {
                    player.SetAudioTypeVolume(vol, fadeTime);
                }
            }
        }

        private void SetMasterVolume(float targetVol, float fadeTime)
        {
#if UNITY_WEBGL
            if (!Mathf.Approximately(WebGLMasterVolume, targetVol))
            {
                if (fadeTime > 0f)
                {
                    this.RestartCoroutine(SetMasterVolume(WebGLMasterVolume, targetVol, fadeTime), ref _masterVolumeCoroutine);
                }
                else
                {
                    SetWebGLMaster(targetVol);
                }
            }

            IEnumerator SetMasterVolume(float currentVol, float targetVol, float fadeTime)
            {
                Ease ease = currentVol < targetVol ? FadeInEase : FadeOutEase;
                float currentTime = 0f;
                while (currentTime < fadeTime)
                {
                    yield return null;
                    currentTime += Utility.GetDeltaTime();
                    float vol = Mathf.Lerp(currentVol, targetVol, (currentTime / fadeTime).SetEase(ease));
                    SetWebGLMaster(vol);
                }
            }

            void SetWebGLMaster(float targetVol)
            {
                WebGLMasterVolume = targetVol.ClampNormalize();

                foreach (var player in GetCurrentAudioPlayers())
                {
                    if (player.IsActive)
                    {
                        player.UpdateWebGLVolume();
                    }
                }
            }
#else
            targetVol = targetVol.ToDecibel();
            if(_broAudioMixer.SafeGetFloat(MasterTrackName,out float currentVol))
            {
                if (currentVol == targetVol)
                {
                    return;
                }

                if(fadeTime != 0f)
                {
                    this.RestartCoroutine(SetMasterVolume(currentVol, targetVol, fadeTime), ref _masterVolumeCoroutine);
                }
                else
                {
                    _broAudioMixer.SafeSetFloat(MasterTrackName, targetVol);
                }
            }

            IEnumerator SetMasterVolume(float currentVol, float targetVol, float fadeTime)
            {
                Ease ease = currentVol < targetVol ? FadeInEase : FadeOutEase;
                float currentTime = 0f;
                while (currentTime < fadeTime)
                {
                    yield return null;
                    currentTime += Utility.GetDeltaTime();
                    float vol = Mathf.Lerp(currentVol, targetVol, (currentTime / fadeTime).SetEase(ease));
                    _broAudioMixer.SafeSetFloat(MasterTrackName, vol);
                }
            }
#endif
        }

        public void SetVolume(SoundID id, float vol, float fadeTime)
        {
            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && player.ID.Equals(id))
                {
                    player.SetVolume(vol, fadeTime);
                }
            }
        }
        #endregion

        #region Effect
        public IAutoResetWaitable SetEffect(Effect effect)
        {
            return SetEffect(BroAudioType.All,effect);
        }

        public IAutoResetWaitable SetEffect(BroAudioType targetType, Effect effect)
        {
            targetType = targetType.ConvertEverythingFlag();
            SetEffectMode mode = SetEffectMode.Add;
            if(effect.Type == EffectType.None)
            {
                mode = SetEffectMode.Override;
            }
            else if(effect.IsDefault())
            {
                mode = SetEffectMode.Remove;
            }

            Action<EffectType> onResetEffect = null;
            EffectAutomationHelper automationHelper = null;
            if (effect.IsDominator)
            {
                automationHelper = _dominatorAutomationHelper;
            }
            else
            {
                if (mode == SetEffectMode.Remove)
                {
                    // wait for reset tweaking of the previous effect
                    onResetEffect = (resetType) => SetPlayerEffect(targetType, resetType, SetEffectMode.Remove);
                }
                else
                {
                    SetPlayerEffect(targetType, effect.Type, mode);
                }
                automationHelper = _automationHelper;
            }

            automationHelper.SetEffectTrackParameter(effect, onResetEffect);
            return automationHelper;
        }

        private void SetPlayerEffect(BroAudioType targetType, EffectType effectType,SetEffectMode mode)
        {
            var parameter = new AudioTypePlaybackPreference.SetEffectParameter() { EffectType = effectType, Mode = mode};
            SetPlaybackPrefByType(targetType, parameter, AudioTypePlaybackPreference.OnSetEffect);

            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && targetType.Contains(player.ID.ToAudioType()) && !player.IsDominator)
                {
                    player.SetTrackEffect(effectType, mode);
                }
            }
        }
        #endregion

        public void SetPitch(SoundID id, float pitch, float fadeTime)
        {
            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && player.ID.Equals(id))
                {
                    player.SetPitch(pitch, fadeTime);
                }
            }
        }
        
        public void SetPitch(float pitch, BroAudioType targetType, float fadeTime)
        {
            targetType = targetType.ConvertEverythingFlag();
            if (targetType == BroAudioType.None)
            {
                Debug.LogWarning(LogTitle + $"SetPitch with {targetType} is meaningless");
                return;
            }

            SetPlaybackPrefByType(targetType, pitch, AudioTypePlaybackPreference.OnSetpitch);
            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && targetType.Contains(player.ID.ToAudioType()))
                {
                    player.SetPitch(pitch, fadeTime);
                }
            }
        }

        public bool TryGetAudioTypePref(BroAudioType audioType, out IAudioPlaybackPref result)
        {
            result = null;
            if (_audioTypePref.TryGetValue(audioType, out var typePref))
            {
                result = typePref;
            }
            return result != null;
        }

        public bool HasAnyPlayingInstances(SoundID id)
        {
            foreach (var player in GetCurrentAudioPlayers())
            {
                if (player.IsActive && player.ID.Equals(id) && player.IsPlaying)
                {
                    return true;
                }
            }
            return false;
        }

        AudioMixerGroup IAudioMixerPool.GetTrack(AudioTrackType trackType) => trackType switch
        {
            AudioTrackType.Generic => _audioTrackPool.Extract(),
            AudioTrackType.Dominator => _dominatorTrackPool.Extract(),
            _ => null,
        };

        void IAudioMixerPool.ReturnTrack(AudioTrackType trackType, AudioMixerGroup track)
        {
            switch (trackType)
            {
                case AudioTrackType.Generic:
                    _audioTrackPool.Recycle(track);
                    break;
                case AudioTrackType.Dominator:
                    _dominatorTrackPool.Recycle(track);
                    break;
            }
        }

        // For those which may be played in the future.
        private void SetPlaybackPrefByType<TParameter>(BroAudioType targetType, TParameter parameter, Action<AudioTypePlaybackPreference, TParameter> onModifyPref) where TParameter : struct
        {
            var setter = new PlaybackPrefSetter<TParameter>()
            {
                TargetType = targetType,
                AudioTypePref = _audioTypePref,
                OnModifyPref = onModifyPref,
                Parameter = parameter,
            };

            ForeachConcreteAudioType(setter);
        }

        #region Audio Player
        void IAudioMixerPool.ReturnPlayer(AudioPlayer player)
        {
            RemoveFromPreventer(player);
            _audioPlayerPool.Recycle(player);
        }

        internal IReadOnlyList<AudioPlayer> GetCurrentAudioPlayers()
        {
            return _audioPlayerPool.GetCurrentAudioPlayers();
        }
        #endregion
        
#if PACKAGE_ADDRESSABLES
        #region Addressable Cleanup
        private WaitForSecondsRealtime _addressableCleanupInterval = null;
        private readonly List<SoundID> _addressableCleanupEntitiesList = new List<SoundID>();
        private IEnumerator AddressableCleanupRoutine()
        {
            if (_addressableCleanupInterval == null)
            {
                _addressableCleanupInterval = new WaitForSecondsRealtime(Mathf.Clamp(Setting.AutomaticallyUnloadUnusedAddressableAudioClipsAfter, 1f, 5f));
            }

            while (true)
            {
                yield return _addressableCleanupInterval;

                if (!Setting.AutomaticallyLoadAddressableAudioClips)
                {
                    continue;
                }

                double currentTime = Time.unscaledTimeAsDouble;
                _addressableCleanupEntitiesList.AddRange(_loadedEntityLastPlayedTime.Keys);

                foreach (var id in _addressableCleanupEntitiesList)
                {
                    if (!_loadedEntityLastPlayedTime.TryGetValue(id, out var lastPlayedTime))
                    {
                        continue;
                    }

                    // Check if this entity is currently being played
                    if (!HasAnyPlayingInstances(id))
                    {
                        // If not playing and hasn't been played for 60 seconds, unload it
                        if (currentTime - lastPlayedTime > 60.0)
                        {
                            UnloadAddressableEntity(id);
                            _loadedEntityLastPlayedTime.Remove(id);
                        }
                    }
                    else
                    {
                        // Update the last played time since it's currently playing
                        _loadedEntityLastPlayedTime[id] = currentTime;
                    }
                }

                // Remove unloaded entities from tracking
                _addressableCleanupEntitiesList.Clear();
            }
        }

        private void UnloadAddressableEntity(SoundID id)
        {
            if (TryGetEntity(id, out var entity) && entity is AudioEntity audioEntity && audioEntity.UseAddressables)
            {
                audioEntity.ReleaseAllAssets();
            }
        }

        public void UpdateLoadedEntityLastPlayedTime(SoundID id)
        {
            // Don't track if we're not meant to automatically load
            // It's on the dev to manage their own memory
            if (Setting.AutomaticallyLoadAddressableAudioClips)
            {
                if (_loadedEntityLastPlayedTime.ContainsKey(id))
                {
                    _loadedEntityLastPlayedTime[id] = Time.unscaledTimeAsDouble;
                }
            }
        }
        #endregion
#endif

        [System.Obsolete("Only for backwards compatibility")]
        public bool TryConvertIdToEntity(int id, out AudioEntity entity)
        {
            if (id == 0 || id == -1)
            {
                entity = null;
                return false;
            }

            if (_data == null)
            {
                entity = null;
                return false;
            }

            foreach (var asset in _data.Assets)
            {
                if (asset is AudioAsset audioAsset)
                {
                    if (audioAsset.TryGetEntityFromId(id, out entity))
                    {
                        return true;
                    }
                }
            }

            Debug.LogError(LogTitle + $"Can't find entity with id {id}");
            entity = null;
            return false;
        }
    }
}
