using System;
using System.Collections;
using Ami.BroAudio.Data;
using Ami.Extension;
using UnityEngine;
using static Ami.BroAudio.Utility;

namespace Ami.BroAudio.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>
    {
        public delegate AudioPlayer HandoverPlayerFactory(PlaybackHandoverData handover);

        public HandoverPlayerFactory RequestNextPlayer;

        private PlaybackPreference _pref;
        private StopMode _stopMode;
        private Coroutine _playbackControlCoroutine = null;
        private Coroutine _handoverScheduleCoroutine = null;
        private AudioPlayer _nextPlayer;

        private event Action<SoundID> _onEnd = null;
        private event Action<IAudioPlayer> _onUpdate = null;
        private event Action<IAudioPlayer> _onStart = null;
        private event Action<IAudioPlayer> _onPaused = null;

        public int PlaybackStartingTime { get; private set; }
        public bool HasStartedPlaying => PlaybackStartingTime > 0;
        private bool IsPausedBeforeStart => _stopMode == StopMode.Pause && !HasStartedPlaying;

        public void SetPlaybackData(SoundID id, PlaybackPreference pref, IBroAudioClip clip = null)
        {
            ID = id;
            _pref = pref;
            _clip = clip;
        }

        public void Play()
        {
            if (IsStopping || IsPausedBeforeStart || _pref.ScheduledStartTime > 0 || !ID.IsValid())
            {
                return;
            }

            PlayInternal();
        }

        private void PlayInternal()
        {
            if (!ValidatePlayback(ID.IsValid() && _pref.Entity != null, $"Cannot play audio. Invalid ID:{ID} or Entity is null.") ||
                !ValidatePlayback(SoundManager.Instance.TryGetAudioTypePref(ID.ToAudioType(), out var audioTypePref), $"Cannot play audio. Failed to get audio type preference for {ID.ToAudioType()}.") ||
                !ValidatePlayback(!HasStartedPlaying || _stopMode == StopMode.Pause, "Audio Player wasn't cleaned up correctly"))
            {
                EndPlaying();
                return;
            }

            try
            {
                this.RestartCoroutine(PlayControl(audioTypePref), ref _playbackControlCoroutine);
            }
            catch (Exception ex)
            {
                EndPlaying();
                Debug.LogException(ex);
            }
        }

        private IEnumerator PlayControl(IAudioPlaybackPref audioTypePref)
        {
            if (!Mathf.Approximately(audioTypePref.Volume, DefaultTrackVolume) && !_audioTypeVolume.IsFading)
            {
                _audioTypeVolume.Complete(audioTypePref.Volume, false);
            }
            _clip ??= _pref.PickNewClip();
            if (_clip == null)
            {
                EndPlaying();
                yield break;
            }

            SetClipDelayIfNotScheduled();
#if PACKAGE_ADDRESSABLES
            if (_clip is BroAudioClip broAudioClip && broAudioClip.IsAddressablesAvailable() && !broAudioClip.IsLoaded)
            {
                if (!SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips)
                {
                    LogNotPreloadedMessage(broAudioClip);
                }
                yield return WaitForAddressablesToLoad(broAudioClip);
            }
#endif

            var audioClip = _clip.GetAudioClip();
            if (!ValidatePlayback(audioClip != null, $"Audio Clip is not assigned in {ID}"))
            {
                EndPlaying();
                yield break;
            }

            // Skip full setup on resume: the source is frozen mid-clip and already configured; re-running would leak the held track and rewind the playhead.
            bool isResuming = _stopMode == StopMode.Pause && HasStartedPlaying;
            bool isMusicPlayer = _decorators.TryGetDecorator<MusicPlayer>(out var musicPlayer);
            if (isResuming)
            {
                RebaseScheduleAfterPause();
            }
            else
            {
                AudioSource.clip = audioClip;
                AudioSource.priority = _pref.Entity.Priority;
                AudioSource.timeSamples = GetSample(audioClip.frequency, _clip.StartPosition);
                SetInitialPitch(_pref.Entity, audioTypePref);
                SetSpatial(_pref);
                SetupAudioTrack(audioTypePref);
            }
            
            bool clipVolumeReady = !isMusicPlayer || !musicPlayer.NeedTransition;
            if (clipVolumeReady)
            {
                SetupClipVolume();
            }

            ResolveScheduledTiming(isResuming, out double endDspTime);
            _playbackEndDspTime = endDspTime;

            if (!isResuming)
            {
                SchedulePlayback();
                if (_secondsUntilScheduledStart > 0)
                {
                    yield return WaitForScheduledStartTime();
                }
            }

            if (isMusicPlayer)
            {
                AudioSource.reverbZoneMix = 0f;
                AudioSource.priority = AudioConstant.HighestPriority;
                musicPlayer.DoTransition(ref _pref);
                if (!clipVolumeReady)
                {
                    SetupClipVolume();
                }
                while (musicPlayer.IsWaitingForTransition)
                {
                    yield return null;
                }
            }

            // On resume, start is past — UnPause (via StartPlaying) restores playback without rewinding; re-arm the rebased end time after.
            if (isResuming || _pref.ScheduledStartTime <= 0)
            {
                StartPlaying();
            }

            if (isResuming)
            {
                // The rebased end time is provisional (slid by the pause duration); now that the source is
                // un-paused, derive the exact end from the playhead — this also covers SetPitch while paused.
                RecalculateScheduledEndTime();
                ScheduleEndTime();
            }

            if (!HasStartedPlaying)
            {
                PlaybackStartingTime = TimeExtension.UnscaledCurrentFrameBeganTime;
                _onStart?.Invoke(this);
                _onStart = null;
                _onUpdate?.Invoke(this);
            }
            
            if (_pref.TryGetFadeIn(_clip.FadeIn, out var fadeIn, out var fadeInEase))
            {
                _clipVolume.Fade(fadeIn, fadeInEase, _onUpdate, (IAudioPlayer)this);
                while (_clipVolume.IsFading)
                {
                    yield return null;
                }
            }

            if (_pref.HasLoop() && _pref.ChainedModeStage != PlaybackStage.End)
            {
                if (_pref.IsLoop(LoopType.SeamlessLoop))
                {
                    _pref.ApplySeamlessFade();
                }

                this.RestartCoroutine(ScheduleNextPlayback(), ref _handoverScheduleCoroutine);
            }

            // Re-read _playbackEndDspTime every frame from here on — a mid-play pitch change recomputes it.
            if (_pref.TryGetFadeOut(_clip.FadeOut, out float fadeOut, out var fadeOutEase))
            {
                while (AudioSettings.dspTime < _playbackEndDspTime - fadeOut)
                {
                    yield return null;
                    _onUpdate?.Invoke(this);
                }

                if (_pref.IsLoop(LoopType.SeamlessLoop))
                {
                    BeginHandover();
                }

                _clipVolume.SetTarget(0f);
                _clipVolume.Fade(fadeOut, fadeOutEase, _onUpdate, (IAudioPlayer)this);
                while (_clipVolume.IsFading)
                {
                    yield return null;
                }

                if (_pref.IsLoop(LoopType.Loop))
                {
                    BeginHandover();
                }
            }
            else
            {
                while (AudioSettings.dspTime < _playbackEndDspTime)
                {
                    yield return null;
                    _onUpdate?.Invoke(this);
                }

                BeginHandover();
            }
            EndPlaying();
        }

        private void StartPlaying()
        {
            switch (_stopMode)
            {
                case StopMode.Pause when HasStartedPlaying:
                    AudioSource.UnPause();
                    break;
                case StopMode.Mute when AudioSource.isPlaying:
                    break;
                case StopMode.Stop:
                case StopMode.Pause:
                case StopMode.Mute:
                    AudioSource.Play();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            _stopMode = default;
        }

        private void SetupAudioTrack(IAudioPlaybackPref audioTypePref)
        {
            if (IsDominator)
            {
                TrackType = AudioTrackType.Dominator;
            }

#if !UNITY_WEBGL
            AudioTrack = Mixer.GetTrack(TrackType);
            // Claim the borrowed track at our own level before anything can start the source: SetupClipVolume
            // is the next write, and it's deferred past a music transition (and past PlayScheduled).
            UpdateVolume(true);
#endif

            if (!IsDominator)
            {
                SetTrackEffect(audioTypePref.EffectType, SetEffectMode.Add);
            }
        }
        
        private void SetupClipVolume()
        {
            float targetClipVolume = _clip.Volume * _pref.Entity.GetMasterVolume();
            _clipVolume.Complete(_pref.HasFadeIn(_clip.FadeIn) ? 0f : targetClipVolume);
            _clipVolume.SetTarget(targetClipVolume);
            UpdateVolume(true);
        }

        private void ResolveScheduledTiming(bool isResuming, out double endDspTime)
        {
            if (isResuming)
            {
                // Slide the stored end time forward by the pause duration; also covers one-shots where ScheduledEndTime stays 0 and would be wrongly recomputed as a fresh duration.
                endDspTime = _playbackEndDspTime + (AudioSettings.dspTime - _pauseDspTime);
                return;
            }

            bool isFirstLoopIteration = _pref.HasLoop() && _pref.ScheduledStartTime <= 0;
            double warmUpTime = isFirstLoopIteration ? SoundManager.Instance.ScheduledPlaybackWarmUpTime : 0d;
            double startBaseTime = _pref.ScheduledStartTime > 0 ? _pref.ScheduledStartTime : AudioSettings.dspTime + warmUpTime;
            double pitchAdjustedDuration = PitchAdjusted(_clip.GetPlayableDuration(), AudioSource.pitch);
            bool isDerivedFromClip = _pref.ScheduledEndTime <= 0;
            endDspTime = isDerivedFromClip ? startBaseTime + pitchAdjustedDuration : _pref.ScheduledEndTime;
            if (isFirstLoopIteration)
            {
                _pref.ScheduledStartTime = startBaseTime;
                _pref.ScheduledEndTime = endDspTime;
            }
            else if (_pref.ScheduledStartTime > 0 && _pref.ScheduledEndTime <= 0)
            {
                _pref.ScheduledEndTime = endDspTime;
            }
            if (isDerivedFromClip)
            {
                _isEndTimeDerivedFromClip = true;
            }
        }

        private IEnumerator ScheduleNextPlayback(bool isEnd = false)
        {
            var newPref = _pref;
            if (newPref.IsChainedMode())
            {
                newPref.ChainedModeStage = isEnd ? PlaybackStage.End : PlaybackStage.Loop;
            }

            // Consume the fade-out override on the copied pref up front (so it isn't consumed twice),
            // but bake the handover times only after the wait — a mid-play pitch change moves the seam.
            double seamlessFadeOut = 0d;
            if (!isEnd && _pref.IsLoop(LoopType.SeamlessLoop) && newPref.TryGetFadeOut(_clip.FadeOut, out float fadeOut, out _))
            {
                seamlessFadeOut = fadeOut;
            }

            bool changePerLoop = _pref.Entity.Flags.Contains(AudioEntityFlag.ChangeClipPerLoop);
            bool needNewClip = changePerLoop
                            || (_pref.IsChainedMode() && (isEnd || _pref.ChainedModeStage == PlaybackStage.Start));

            if (!isEnd)
            {
                double warmUpTime = SoundManager.Instance.ScheduledPlaybackWarmUpTime;
                while (AudioSettings.dspTime < _playbackEndDspTime - seamlessFadeOut - warmUpTime)
                {
                    yield return null;
                }
            }

            double rawStart = (isEnd ? AudioSettings.dspTime : _playbackEndDspTime) - seamlessFadeOut;
            newPref.ScheduledStartTime = System.Math.Max(rawStart, AudioSettings.dspTime);
            // Reset to 0 so the receiver derives the end from its own resolved pitch, avoiding both
            // mid-fade bake mismatch and per-iteration pitch randomization disagreement.
            newPref.ScheduledEndTime = 0;

            var handover = new PlaybackHandoverData()
            {
                ID = ID,
                Pref = newPref,
                Clip = needNewClip ? null : _clip,
                TrackEffect = CurrentActiveTrackEffects,
                TrackVolume = _trackVolume.Target,
                Pitch = TargetPitch ?? AudioConstant.DefaultPitch,
            };

            if (_trackVolume.IsFading)
            {
                handover.TrackVolumeCurrent = _trackVolume.Current;
                handover.TrackVolumeRemaining = _trackVolume.RemainingTime;
                handover.TrackVolumeEase = _trackVolume.Ease;
            }
            
            _nextPlayer?.Stop(FadeData.Immediate, StopMode.Stop, null);
            _nextPlayer = RequestNextPlayer?.Invoke(handover);
        }

        private void BeginHandover(bool isEnd = false)
        {
            if (!CanHandover(isEnd) || _instanceWrapper == null || _nextPlayer == null)
            {
                return;
            }

            _instanceWrapper.UpdateInstance(_nextPlayer);
            _nextPlayer.SetInstanceWrapper(_instanceWrapper);
            _instanceWrapper = null;
            _nextPlayer = null;
        }

        private bool CanHandover(bool isEnd = false) =>
            isEnd ? _pref.CanHandoverToEnd() : _pref.CanHandoverToLoop();

        private static double PitchAdjusted(double duration, float pitch) =>
            Mathf.Approximately(pitch, 0f) ? duration : duration / pitch;

        internal void ReceiveHandover(PlaybackHandoverData handover)
        {
            SetPlaybackData(handover.ID, handover.Pref, handover.Clip);
            // Handover end times always come from a clip-duration computation (or 0 when a new clip is needed).
            _isEndTimeDerivedFromClip = true;
            if (handover.TrackVolumeRemaining > 0f)
            {
                _trackVolume.Resume(handover.TrackVolumeCurrent, handover.TrackVolume);
                _trackVolume.Fade(handover.TrackVolumeRemaining, handover.TrackVolumeEase);
            }
            else
            {
                _trackVolume.Complete(handover.TrackVolume);
            }
            this.SetPitch(handover.Pitch);
            PlayInternal();
#if !UNITY_WEBGL
            SetTrackEffect(handover.TrackEffect, SetEffectMode.Override);
#endif
        }

        private static bool ValidatePlayback(bool condition, string message, UnityEngine.Object context = null)
        {
            if (!condition)
            {
                Debug.LogError(LogTitle + message, context);
            }
            return condition;
        }

        #region Stop Overloads
        void IAudioStoppable.Pause()
            => this.Pause(FadeData.UseClipSetting);
        void IAudioStoppable.Pause(float fadeOut)
            => Stop(fadeOut, StopMode.Pause, null);
        void IAudioStoppable.UnPause()
            => this.UnPause(FadeData.UseClipSetting);
        void IAudioStoppable.UnPause(float fadeIn)
        {
            if (_stopMode != StopMode.Pause)
            {
                Debug.LogWarning(LogTitle + $"Cannot UnPause: The player is not paused. Sound:{ID}", this);
                return;
            }
            _pref.SetNextFadeIn(fadeIn);
            PlayInternal();
        }
        void IAudioStoppable.Stop()
            => this.Stop(FadeData.UseClipSetting);
        void IAudioStoppable.Stop(float fadeOut)
            => this.Stop(fadeOut, null);
        void IAudioStoppable.Stop(Action onFinished)
            => this.Stop(FadeData.UseClipSetting, onFinished);
        void IAudioStoppable.Stop(float fadeOut, Action onFinished)
            => Stop(fadeOut, StopMode.Stop, onFinished);
        #endregion
        public void Stop(float overrideFade, StopMode stopMode, Action onFinished)
        {
            if (IsStopping && !Mathf.Approximately(overrideFade, FadeData.Immediate))
            {
                return;
            }

            bool isPlaying = AudioSource.isPlaying;
            if (stopMode == StopMode.Pause && !isPlaying)
            {
                bool hasPaused = _stopMode == StopMode.Pause;
                _stopMode = StopMode.Pause;
                _pauseDspTime = AudioSettings.dspTime;
                if (!hasPaused)
                {
                    _onPaused?.Invoke(this);
                }
                return;
            }

            if (!ID.IsValid() || !isPlaying)
            {
                onFinished?.Invoke();
                EndPlaying();
                return;
            }

            this.RestartCoroutine(StopControl(overrideFade, stopMode, onFinished), ref _playbackControlCoroutine);
        }

        private IEnumerator StopControl(float overrideFade, StopMode stopMode, Action onFinished)
        {
            _stopMode = stopMode;
            IsStopping = true;
            _pref.SetNextFadeOut(overrideFade);

            bool didHandoverToEnd = false;
            if (_instanceWrapper != null && _pref.CanHandoverToEnd())
            {
                this.RestartCoroutine(ScheduleNextPlayback(isEnd: true), ref _handoverScheduleCoroutine);  // Start the next playback immediately
                BeginHandover(isEnd: true);
                // BeginHandover only nulls _instanceWrapper on success; the next player now owns onUpdate dispatch.
                didHandoverToEnd = _instanceWrapper == null;
            }
            else
            {
                // Stop pre-empts any scheduled loop handover; cancel the schedule and
                // discard a pre-spawned next player so no unwanted iteration starts.
                this.SafeStopCoroutine(_handoverScheduleCoroutine);
                _handoverScheduleCoroutine = null;
                if(_nextPlayer != null)
                {
                    _nextPlayer.Stop(FadeData.Immediate, StopMode.Stop, null);
                    _nextPlayer = null;
                }
            }

            #region FadeOut
            bool hasExplicitOverride = _pref.HasFadeOutOverride;
            if (_pref.TryGetFadeOut(_clip.FadeOut, out float fadeOut, out var fadeOutEase))
            {
                // After end-handover, the in-flight Fade captured the old player's _onUpdate before
                // BeginHandover ran. Restart it with a null callback to keep the ce15a806 invariant.
                if (_clipVolume.IsFadingOut && !hasExplicitOverride && !didHandoverToEnd)
                {
                    while (_clipVolume.IsFading)
                    {
                        yield return null;
                        if (!IsActive)
                        {
                            yield break;
                        }
                    }
                }
                else
                {
                    _clipVolume.SetTarget(0f);
                    _clipVolume.Fade(fadeOut, fadeOutEase, didHandoverToEnd ? null : _onUpdate, (IAudioPlayer)this);
                    while (_clipVolume.IsFading)
                    {
                        yield return null;
                    }
                }
            }
            #endregion
            switch (stopMode)
            {
                case StopMode.Stop:
                    EndPlaying();
                    break;
                case StopMode.Pause:
                    // Snapshot freeze time so resume can slide the dsp schedule forward by the pause duration.
                    _pauseDspTime = AudioSettings.dspTime;
                    AudioSource.Pause();
                    _onPaused?.Invoke(this);
                    break;
                case StopMode.Mute:
                    this.SetVolume(0f);
                    break;
            }
            IsStopping = false;
            onFinished?.Invoke();
        }

        private void EndPlaying()
        {
            if (!IsActive)
            {
                return;
            }

            PlaybackStartingTime = 0;
            _pauseDspTime = 0d;
            _playbackEndDspTime = 0d;
            _isEndTimeDerivedFromClip = false;
            _stopMode = default;
            _pref = default;
            IsStopping = false;
            ResetVolume();
            ResetPitch();

            AudioSource.Stop();
            AudioSource.clip = null;
            _clip = null;
            ResetSpatial();
            ResetEffect();
            
            _onEnd?.Invoke(ID);
            _onEnd = null;

            Recycle();
        }

        public IAudioPlayer OnEnd(Action<SoundID> onEnd)
        {
            _onEnd -= onEnd;
            _onEnd += onEnd;
            return this;
        }

        public IAudioPlayer OnUpdate(Action<IAudioPlayer> onUpdate)
        {
            _onUpdate -= onUpdate;
            _onUpdate += onUpdate;
            return this;
        }

        public IAudioPlayer OnStart(Action<IAudioPlayer> onStart)
        {
            _onStart -= onStart;
            _onStart += onStart;
            return this;
        }

        public IAudioPlayer OnPause(Action<IAudioPlayer> onPause)
        {
            _onPaused -= onPause;
            _onPaused += onPause;
            return this;
        }

        public IAudioPlayer SetFadeInEase(Ease ease)
        {
            _pref.SetFadeInEase(ease);
            return this;
        }

        public IAudioPlayer SetFadeOutEase(Ease ease)
        {
            _pref.SetFadeOutEase(ease);
            return this;
        }

#if PACKAGE_ADDRESSABLES
        private void LogNotPreloadedMessage(BroAudioClip broAudioClip)
        {
            var logMessage = broAudioClip.IsLoading
                ? LogTitle +
                  $"Entity: '{ID}' is still loading. You should wait for it to finish before playback, or it <b>may have caused a playback delay</b>."
                : LogTitle +
                  $"Entity: '{ID}' is marked as Addressables but was not preloaded — it will be loaded on demand and <b>may have caused a playback delay</b>.\n" +
                  $"Call BroAudio.{nameof(BroAudio.LoadAssetAsync)}() before playback, or enable <b>Automatically Load Addressable Audio Clips</b> in Preferences to suppress this error.";

            Log(logMessage, SoundManager.Instance.Setting.AddressablesNonPreloadedLogLevel);
        }

        private IEnumerator WaitForAddressablesToLoad(BroAudioClip broAudioClip)
        {
            if (broAudioClip.IsLoading)
            {
                // Wait for the existing loading operation to complete
                yield return broAudioClip.GetCurrentOperationHandle();
            }
            else
            {
                // Start loading and wait for it no matter what the user has set
                yield return broAudioClip.LoadAssetAsync();

                var loadedAudioClip = _clip.GetAudioClip();
                if (loadedAudioClip == null)
                {
                    Debug.LogError(LogTitle + $"Failed to load addressable audio clip for {ID}");
                }
            }

            // Update tracking when playback starts
            SoundManager.Instance.UpdateLoadedEntityLastPlayedTime(ID);
        }
#endif
    }
}
