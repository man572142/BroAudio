using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.Extension;
using UnityEngine;

namespace Ami.BroAudio.Runtime
{
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>
    {
        /// <summary>
        /// Pitch value without any fading process
        /// </summary>
        private float? TargetPitch { get; set; }

        private Coroutine _pitchCoroutine;
        // Fade requested before SetInitialPitch has run; consumed there to anchor the fade.
        private float _pendingPitchFadeTime;

        IAudioPlayer IAudioPlayer.SetPitch(float pitch, float fadeTime)
        {
            TargetPitch = pitch;
            pitch = Mathf.Clamp(pitch, AudioConstant.MinAudioSourcePitch, AudioConstant.MaxAudioSourcePitch);
            if (fadeTime > 0f)
            {
                // Before SetInitialPitch runs, the source pitch isn't the entity pitch yet, so defer.
                if (HasStartedPlaying || AudioSource.isPlaying)
                {
                    _pendingPitchFadeTime = 0f;
                    this.RestartCoroutine(PitchControl(pitch, fadeTime), ref _pitchCoroutine);
                }
                else
                {
                    _pendingPitchFadeTime = fadeTime;
                }
            }
            else
            {
                _pendingPitchFadeTime = 0f;
                AudioSource.pitch = pitch;
                RecalculateScheduledEndTime();
            }
            return this;
        }

        private void SetInitialPitch(IAudioEntity entity, IAudioPlaybackPref audioTypePlaybackPref)
        {
            if (_pendingPitchFadeTime > 0f && TargetPitch.HasValue)
            {
                // Fade from the entity's base pitch to StaticPitch (the pending target) instead of snapping.
                AudioSource.pitch = GetBasePitch(entity, audioTypePlaybackPref);
                float target = Mathf.Clamp(TargetPitch.Value, AudioConstant.MinAudioSourcePitch, AudioConstant.MaxAudioSourcePitch);
                this.RestartCoroutine(PitchControl(target, _pendingPitchFadeTime), ref _pitchCoroutine);
                _pendingPitchFadeTime = 0f;
                return;
            }

            if (TargetPitch.HasValue)
            {
                // An explicit SetPitch() overrides the entity's pitch randomization — use the value verbatim.
                AudioSource.pitch = Mathf.Clamp(TargetPitch.Value, AudioConstant.MinAudioSourcePitch, AudioConstant.MaxAudioSourcePitch);
            }
            else
            {
                AudioSource.pitch = GetBasePitch(entity, audioTypePlaybackPref);
            }
        }

        private float GetBasePitch(IAudioEntity entity, IAudioPlaybackPref audioTypePlaybackPref)
        {
            if (!Mathf.Approximately(audioTypePlaybackPref.Pitch, AudioConstant.DefaultPitch))
            {
                return entity.GetRandomValue(audioTypePlaybackPref.Pitch, RandomFlag.Pitch);
            }
            return entity.GetPitch();
        }

        private IEnumerator PitchControl(float targetPitch, float fadeTime)
        {
            float startPitch = AudioSource.pitch;
            float currentTime = 0f;
            while (currentTime < fadeTime)
            {
                currentTime += Utility.GetDeltaTime();
                AudioSource.pitch = Mathf.Lerp(startPitch, targetPitch, (currentTime / fadeTime).SetEase(Ease.Linear));
                RecalculateScheduledEndTime();
                yield return null;
            }
        }

        private void ResetPitch()
        {
            TargetPitch = null;
            AudioSource.pitch = AudioConstant.DefaultPitch;
            _pendingPitchFadeTime = 0f;
        }
    } 
}