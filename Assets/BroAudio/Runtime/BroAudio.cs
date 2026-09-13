using UnityEngine;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Data;
using System;
using System.Collections.Generic;

#if PACKAGE_ADDRESSABLES || PACKAGE_LOCALIZATION
using UnityEngine.ResourceManagement.AsyncOperations;
#endif

namespace Ami.BroAudio
{
    public static class BroAudio
    {
#if BroAudio_InitManually
        public static void Init()
        {
            SoundManager.Init();
        }
#endif
        
        public static event Action<IAudioPlayer> OnBGMChanged
        {
            add => MusicPlayer.OnBGMChanged += value;
            remove => MusicPlayer.OnBGMChanged -= value;
        }

        // Release verbs (Stop/Pause/SetVolume/SetPitch) may be called from OnDestroy/OnApplicationQuit,
        // when SoundManager can already be torn down due to non-deterministic destruction order. Treat
        // "nothing to release" as a silent no-op rather than throwing.
        internal static SoundManager Manager => SoundManager.HasInstance ? SoundManager.Instance : null;

        #region Play
        /// <summary>
        /// Plays audio globally (2D)
        /// </summary>
        /// <param name="playableValidator">Overrides the validation rule of the <see cref="PlaybackGroup"/></param>
        public static IAudioPlayer Play(SoundID id, IPlayableValidator playableValidator = null)
            => Play(id, FadeData.UseClipSetting, playableValidator);

        /// <param name="fadeIn">Time in seconds to fade in at start</param>
        /// <inheritdoc cref="Play(SoundID, IPlayableValidator)"/>
        public static IAudioPlayer Play(SoundID id, float fadeIn, IPlayableValidator playableValidator = null)
            => SoundManager.Instance.Play(id, fadeIn, playableValidator);

        /// <summary>
        /// Plays audio in 3D space at the given position
        /// </summary>
        /// <param name="playableValidator">Overrides the validation rule of the <see cref="PlaybackGroup"/></param>
        public static IAudioPlayer Play(SoundID id, Vector3 position, IPlayableValidator playableValidator = null)
            => Play(id, position, FadeData.UseClipSetting, playableValidator);
        
        /// <param name="fadeIn">Time in seconds to fade in at start</param>
        /// <inheritdoc cref="Play(SoundID, Vector3, IPlayableValidator)"/>
        public static IAudioPlayer Play(SoundID id, Vector3 position, float fadeIn, IPlayableValidator playableValidator = null)
            => SoundManager.Instance.Play(id, position, fadeIn, playableValidator);

        /// <summary>
        /// Plays audio in 3D space and keeps it following the target continuously
        /// </summary>
        /// <param name="playableValidator">Overrides the validation rule of the <see cref="PlaybackGroup"/></param>
        public static IAudioPlayer Play(SoundID id, Transform followTarget, IPlayableValidator playableValidator = null)
            => Play(id, followTarget, FadeData.UseClipSetting, playableValidator);
        
        /// <param name="fadeIn">Time in seconds to fade in at start</param>
        /// <inheritdoc cref="Play(SoundID, Transform, IPlayableValidator)"/>
        public static IAudioPlayer Play(SoundID id, Transform followTarget, float fadeIn, IPlayableValidator playableValidator = null)
            => SoundManager.Instance.Play(id, followTarget, fadeIn, playableValidator);
        #endregion

        #region Stop
        /// <summary>
        /// Stop playing all audio that match the given audio type
        /// </summary>
        public static void Stop(BroAudioType audioType)
            => Manager?.Stop(audioType);

        /// <summary>
        /// Stop playing all audio that match the given audio type
        /// </summary>
        /// <param name="fadeOut">Duration in seconds to fade out. Set this value to override the LibraryManager's setting</param>
        public static void Stop(BroAudioType audioType, float fadeOut)
            => Manager?.Stop(audioType, fadeOut);

        /// <summary>
        /// Stop playing audio
        /// </summary>
        public static void Stop(SoundID id)
            => Manager?.Stop(id);

        /// <summary>
        /// Stop playing audio
        /// </summary>
        /// <param name="fadeOut">Duration in seconds to fade out. Set this value to override the LibraryManager's setting</param>
        public static void Stop(SoundID id, float fadeOut)
            => Manager?.Stop(id,fadeOut);
        #endregion

        #region Pause
        /// <summary>
        /// Pause audio
        /// </summary>
        public static void Pause(SoundID id)
            => Manager?.Pause(id, true);

        /// <summary>
        /// Pause audio
        /// </summary>
        /// <param name="fadeOut">Duration in seconds to fade out. Set this value to override the LibraryManager's setting</param>
        public static void Pause(SoundID id, float fadeOut)
            => Manager?.Pause(id, fadeOut, true);

        /// <summary>
        /// Resume paused audio
        /// </summary>
        public static void UnPause(SoundID id)
            => Manager?.Pause(id, false);

        /// <summary>
        /// Resume paused audio
        /// </summary>
        /// <param name="fadeIn">Duration in seconds to fade in. Set this value to override the LibraryManager's setting</param>
        public static void UnPause(SoundID id, float fadeIn)
            => Manager?.Pause(id, fadeIn, false);

        /// <summary>
        /// Pause all audio that matches the given audio type
        /// </summary>
        public static void Pause(BroAudioType audioType)
            => Manager?.Pause(audioType, true);

        /// <summary>
        /// Pause all audio that matches the given audio type
        /// </summary>
        /// <param name="fadeOut">Duration in seconds to fade out. Set this value to override the LibraryManager's setting</param>
        public static void Pause(BroAudioType audioType, float fadeOut)
            => Manager?.Pause(audioType, fadeOut, true);

        /// <summary>
        /// Resume all audio that matches the given audio type
        /// </summary>
        public static void UnPause(BroAudioType audioType)
            => Manager?.Pause(audioType, false);

        /// <summary>
        /// Resume all audio that matches the given audio type
        /// </summary>
        /// <param name="fadeIn">Duration in seconds to fade in. Set this value to override the LibraryManager's setting</param>
        public static void UnPause(BroAudioType audioType, float fadeIn)
            => Manager?.Pause(audioType, fadeIn, false);

        #endregion

        #region Volume
        /// <summary>
        /// Set the master volume
        /// </summary>
        /// <param name="vol">Accepts values from 0 to 10, default is 1</param>
        public static void SetVolume(float vol, float fadeTime = BroAdvice.FadeTime_Immediate)
            => SetVolume(BroAudioType.All, vol, fadeTime);

        /// <summary>
        /// Set the volume of the given audio type
        /// </summary>
        /// <param name="vol">Accepts values from 0 to 10, default is 1</param>
        /// <param name="fadeTime">Duration in seconds to fade from current to target</param>
        public static void SetVolume(BroAudioType audioType, float vol, float fadeTime = BroAdvice.FadeTime_Immediate)
            => Manager?.SetVolume(vol, audioType, fadeTime);

        /// <summary>
        /// Set the audio volume
        /// </summary>
        /// <param name="vol">Accepts values from 0 to 10, default is 1</param>
        public static void SetVolume(SoundID id, float vol)
            => SetVolume(id, vol, BroAdvice.FadeTime_Immediate);

        /// <summary>
        /// Set the audio volume
        /// </summary>
        /// <param name="vol">Accepts values from 0 to 10, default is 1</param>
        /// <param name="fadeTime">Duration in seconds to fade from current to target</param>
        public static void SetVolume(SoundID id, float vol, float fadeTime)
            => Manager?.SetVolume(id, vol, fadeTime);
        #endregion

        #region Pitch

        /// <summary>
        /// Set the audio pitch immediately
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        public static void SetPitch(SoundID id, float pitch)
            => Manager?.SetPitch(id, pitch, BroAdvice.FadeTime_Immediate);

        /// <summary>
        /// Set the audio pitch
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        /// <param name="fadeTime">Duration in seconds to fade from current to target</param>
        public static void SetPitch(SoundID id, float pitch, float fadeTime)
            => Manager?.SetPitch(id, pitch, fadeTime);

        /// <summary>
        /// Set the pitch of all audio immediately
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        public static void SetPitch(float pitch)
            => Manager?.SetPitch(pitch, BroAudioType.All, BroAdvice.FadeTime_Immediate);

        /// <summary>
        /// Deprecated: The argument order is changed, please use <see cref="SetPitch(BroAudioType, float)"></see> instead
        /// </summary>
        [Obsolete]
        public static void SetPitch(float pitch, BroAudioType audioType)
            => Manager?.SetPitch(pitch, audioType, BroAdvice.FadeTime_Immediate);


        /// <summary>
        /// Set the pitch of the given audio type immediately
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        public static void SetPitch(BroAudioType audioType, float pitch)
            => Manager?.SetPitch(pitch, audioType, BroAdvice.FadeTime_Immediate);

        /// <summary>
        /// Set the pitch of all audio
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        /// <param name="fadeTime">Duration in seconds to fade from current to target</param>
        public static void SetPitch(float pitch, float fadeTime)
            => Manager?.SetPitch(pitch, BroAudioType.All, fadeTime);

        /// <summary>
        /// Deprecated: The argument order is changed, please use <see cref="SetPitch(BroAudioType, float, float)"></see> instead
        /// </summary>
        [Obsolete]
        public static void SetPitch(float pitch, BroAudioType audioType, float fadeTime)
            => Manager?.SetPitch(pitch, audioType, fadeTime);

        /// <summary>
        /// Set the pitch of the given audio type
        /// </summary>
        /// <param name="pitch">values between -3 to 3, default is 1</param>
        /// <param name="fadeTime">Duration in seconds to fade from current to target</param>
        public static void SetPitch(BroAudioType audioType, float pitch, float fadeTime)
            => Manager?.SetPitch(pitch, audioType, fadeTime);
        #endregion

        #region Reset MultiClips Data
        public static void ResetMultiClipStrategy(SoundID id)
        {
            if (id.Entity != null)
            {
                id.Entity.ResetMultiClipStrategy();
            }
        }

        /// <summary>
        /// Resets the sequence state for a specific named sequence instance
        /// </summary>
        public static void ResetMultiClipStrategy(SoundID id, string sequenceId)
        {
            if (id.Entity != null)
            {
                id.Entity.ResetMultiClipStrategy(sequenceId);
            }
        }
        #endregion

        /// <summary>
        /// Checks if the sound is playing anywhere
        /// </summary>
        public static bool HasAnyPlayingInstances(SoundID id)
            => SoundManager.Instance.HasAnyPlayingInstances(id);

        /// <summary>
        /// Get the read-only information of an audio entity
        /// </summary>
        public static bool TryGetEntityInfo(SoundID id, out IReadOnlyAudioEntity entityInfo)
        {
            entityInfo = null;
            if (SoundManager.Instance.TryGetEntity(id, out var entity, false))
            {
                entityInfo = entity as IReadOnlyAudioEntity;
                return entityInfo != null;
            }
            return false;
        }

#if !UNITY_WEBGL
        #region Effect
        /// <summary>
        /// Set effect for all audio
        /// </summary>
        public static IAutoResetWaitable SetEffect(Effect effect) 
            => SoundManager.Instance.SetEffect(effect);

        /// <summary>
        /// Set effect for all audio that matches the given audio type
        /// </summary>
        public static IAutoResetWaitable SetEffect(Effect effect, BroAudioType audioType)
            => SoundManager.Instance.SetEffect(audioType,effect);
        #endregion
#endif

#if PACKAGE_ADDRESSABLES || PACKAGE_LOCALIZATION
        public static bool IsLoaded(SoundID id)
            => SoundManager.Instance.IsLoaded(id);

        public static bool IsLoaded(SoundID id, int clipIndex)
            => SoundManager.Instance.IsLoaded(id, clipIndex);

        /// <summary>
        /// Loads all the audio clips in the entity
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static AsyncOperationHandle<IList<AudioClip>> LoadAllAssetsAsync(SoundID id)
            => SoundManager.Instance.LoadAllAssetsAsync(id);

        /// <summary>
        /// Loads the first audio clip in the entity, or the active locale's clip in Localization mode.
        /// </summary>
        public static AsyncOperationHandle<AudioClip> LoadAssetAsync(SoundID id)
            => LoadAssetAsync(id, 0);

        /// <summary>
        /// Loads the audio clip in the entity's clip list by index.
        /// In Localization mode, <paramref name="clipIndex"/> is ignored and the active locale's clip is loaded.
        /// </summary>
        public static AsyncOperationHandle<AudioClip> LoadAssetAsync(SoundID id, int clipIndex)
            => SoundManager.Instance.LoadAssetAsync(id, clipIndex);

        /// <summary>
        /// Releases all the audio clips in the entity
        /// </summary>
        /// <param name="id"></param>
        public static void ReleaseAllAssets(SoundID id)
            => Manager?.ReleaseAllAssets(id);

        /// <summary>
        /// Releases the first audio clip in the entity, or the active locale's clip in Localization mode.
        /// </summary>
        /// <param name="id"></param>
        public static void ReleaseAsset(SoundID id)
            => Manager?.ReleaseAsset(id, 0);

        /// <summary>
        /// Releases the audio clip in the entity's clip list by index.
        /// In Localization mode, <paramref name="clipIndex"/> is ignored and the active locale's clip is released.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="clipIndex"></param>
        public static void ReleaseAsset(SoundID id, int clipIndex)
            => Manager?.ReleaseAsset(id, clipIndex);
#endif

#if PACKAGE_LOCALIZATION
        /// <summary>
        /// Subscribes to localized clip changes for a Localization-mode entity, identified by <see cref="SoundID"/>.
        /// Uses <c>LocalizedAsset&lt;T&gt;.AssetChanged</c> under the hood, so behavior matches Unity Localization's standard asset-change notification.
        /// </summary>
        public static void SubscribeLocalizedAudioChanged(SoundID id, Action<SoundID> handler)
            => Manager?.SubscribeLocalizedAudioChanged(id, handler);

        /// <summary>
        /// Removes a handler previously registered with <see cref="SubscribeLocalizedAudioChanged"/>.
        /// Unsubscribing the last handler for an <paramref name="id"/> causes Unity Localization to release the
        /// underlying Addressables handle automatically.
        /// </summary>
        public static void UnsubscribeLocalizedAudioChanged(SoundID id, Action<SoundID> handler)
            => Manager?.UnsubscribeLocalizedAudioChanged(id, handler);
        
        /// <summary>
        /// <see cref="Action{T}"/>-compatible adapter for hooking <see cref="SoundID.LocalizedAudioChanged"/>:
        /// <c>id.LocalizedAudioChanged += BroAudio.PlayOnLocalizedAudioChanged;</c>
        /// </summary>
        public static Action<SoundID> PlayOnLocalizedAudioChanged => _playOnLocalizedAudioChanged ??= (id => Play(id)) ;
        private static Action<SoundID> _playOnLocalizedAudioChanged;
#endif
    }
}
