using System;
using System.Collections;
using Ami.Extension;
using UnityEngine;
using UnityEngine.Audio;

namespace Ami.BroAudio.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>, IAudioBus
    {
        public const float DefaultClipVolume = 0f;
        public const float DefaultTrackVolume = AudioConstant.FullVolume;
        public const float UnSetMixerDecibelVolume = float.MinValue;

        /// <summary>
        /// The playback volume of an audio track, it will be set by SetVolume() function.
        /// </summary>
        private Fader _trackVolume = null;

        /// <summary>
        /// The playback volume of an audio clip, it's determined by the clip's property settings, such as Fade In/Out.   
        /// </summary>
        private Fader _clipVolume = null;

        /// <summary>
        /// The playback volume of the audio type this player belongs to, this is used when being set by SetVolume(BroAudioType)
        /// </summary>
        private Fader _audioTypeVolume = null;

        private float _mixerDecibelVolume = UnSetMixerDecibelVolume;
        private bool _isWaitingForMixerInitialization = false;

        /// <summary>
        /// Try get the final decibel volume that is set in the mixer.
        /// </summary>
        private bool TryGetMixerDecibelVolume(out float vol)
        {
            if (Mathf.Approximately(_mixerDecibelVolume, UnSetMixerDecibelVolume) && TryGetMixerAndTrack(out var mixer, out _)
                && mixer.SafeGetFloat(VolumeParaName, out float currentVol))
            {
                _mixerDecibelVolume = currentVol;
            }
            vol = _mixerDecibelVolume;
            return vol > UnSetMixerDecibelVolume;
        }

        /// <summary>
        /// Set the final decibel volume that is set in the mixer.
        /// </summary>
        private bool TrySetMixerDecibelVolume(float vol)
        {
            if (!TryGetMixerAndTrack(out var mixer, out _))
            {
                return false;
            }

            _mixerDecibelVolume = vol.ClampDecibel(true);

            if (Mixer.WaitForAudioMixerInitialization != null)
            {
                if (!_isWaitingForMixerInitialization)
                {
                    _isWaitingForMixerInitialization = true;
                    StartCoroutine(DelaySetMixerVolume());
                }
                return true;
            }

            return mixer.SafeSetFloat(VolumeParaName, _mixerDecibelVolume);
        }

        private IEnumerator DelaySetMixerVolume()
        {
            yield return Mixer.WaitForAudioMixerInitialization;
            if (TryGetMixerAndTrack(out var mixer, out _))
            {
                mixer.SafeSetFloat(VolumeParaName, _mixerDecibelVolume);
            }
            _isWaitingForMixerInitialization = false;
        }

        private void InitVolumeModule()
        {
            _trackVolume = new Fader(DefaultTrackVolume, this);
            _clipVolume = new Fader(DefaultClipVolume, this);
            _audioTypeVolume = new Fader(DefaultTrackVolume, this);
        }

        public void UpdateVolume(bool forceUpdate = false)
        {
            if (!HasStartedPlaying && !forceUpdate)
            {
                return;
            }
#if UNITY_WEBGL
            UpdateWebGLVolume();
#else
            float vol = _clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current;
            if (!TrySetMixerDecibelVolume(vol.ToDecibel()))
            {
                AudioSource.volume = vol.ClampNormalize();
            }
#endif
        }

        IAudioPlayer IVolumeSettable.SetVolume(float vol, float fadeTime)
        {
            SetVolumeInternal(_trackVolume, vol, fadeTime);
            return this;
        }

        float IAudioPlayer.GetVolume()
        {
            return _clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current;
        }

        public void SetAudioTypeVolume(float vol, float fadeTime)
        {
            SetVolumeInternal(_audioTypeVolume, vol, fadeTime);
        }

        private void SetVolumeInternal(Fader module, float vol, float fadeTime)
        {
            module.SetTarget(vol);
            if (fadeTime > 0f)
            {
                Ease ease = module.Current < vol ? SoundManager.FadeInEase : SoundManager.FadeOutEase;
                module.Fade(fadeTime, ease);
            }
            else
            {
                module.Complete(vol);
            }
        }

        /// <summary>
        /// Tracks are pooled and shared across audio types, so a track returned at this player's level
        /// gives its next borrower a blip at our volume until that player's own UpdateVolume lands.
        /// </summary>
        private void SilenceTrackBeforeReturn(AudioMixer mixer)
        {
            mixer.SafeSetFloat(GetCurrentTrackName(), AudioConstant.MinDecibelVolume);
            if (IsUsingTrackEffect)
            {
                mixer.SafeSetFloat(GetSendParaName(), AudioConstant.MinDecibelVolume);
            }
            _mixerDecibelVolume = UnSetMixerDecibelVolume; // this player no longer owns the value on that track
        }

        private void ResetVolume()
        {
            _clipVolume.Complete(DefaultClipVolume, false);
            _trackVolume.Complete(DefaultTrackVolume, false);
            _audioTypeVolume.Complete(DefaultTrackVolume, false);
            UpdateVolume();
        }

#if UNITY_WEBGL
        public void UpdateWebGLVolume()
        {
            float masterVolume = SoundManager.Instance.WebGLMasterVolume;
            AudioSource.volume = AudioExtension.ClampNormalize(_clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current * masterVolume);
        }
#endif
    }
}