#if !UNITY_WEBGL
using UnityEngine.Audio;
using Ami.Extension;

namespace Ami.BroAudio.Runtime
{
    public partial class AudioPlayer
    {
        /// <summary>
        /// How long an <see cref="AudioSource"/> must stay continuously virtual (inaudible)
        /// before its track is released back to the pool, to avoid thrashing on sources that
        /// flicker around the virtualization threshold.
        /// </summary>
        private const float VirtualReleaseGracePeriod = 0.5f;

        private float _virtualElapsed = 0f;
        private bool _trackReleasedByVirtual = false;

        /// <summary>
        /// Frees a Generic track back to the pool while the source is virtualized by Unity,
        /// and re-acquires one (re-applying volume/effect routing) when it becomes audible again.
        /// This increases the effective capacity of the limited track pool.
        /// </summary>
        private void MonitorVirtualTrack()
        {
            if (!HasStartedPlaying || IsStopping || TrackType != AudioTrackType.Generic || !IsPlaying)
            {
                return;
            }

            if (AudioSource.isVirtual)
            {
                if (TryGetMixerAndTrack(out _, out _))
                {
                    _virtualElapsed += Utility.GetDeltaTime();
                    if (_virtualElapsed >= VirtualReleaseGracePeriod)
                    {
                        ReleaseVirtualTrack();
                    }
                }
            }
            else
            {
                _virtualElapsed = 0f;
                if (_trackReleasedByVirtual)
                {
                    ReacquireVirtualTrack();
                }
            }
        }

        private void ReleaseVirtualTrack()
        {
            if (!TryGetMixerAndTrack(out var mixer, out var track))
            {
                return;
            }

            SilenceTrackBeforeReturn(mixer);
            Mixer?.ReturnTrack(AudioTrackType.Generic, track);
            // Once unrouted (no mixer group), the source plays at AudioSource.volume directly to the
            // listener. We normally keep that at full and drive loudness via the track, so preserve
            // the player's real computed level here to avoid a full-volume blip while released.
            AudioSource.volume = (_clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current).ClampNormalize();
            AudioTrack = null; // clears outputAudioMixerGroup and the cached track/send names
            _trackReleasedByVirtual = true;
            _virtualElapsed = 0f;
        }

        private void ReacquireVirtualTrack()
        {
            var track = Mixer.GetTrack(AudioTrackType.Generic);
            if (track == null)
            {
                // Pool still exhausted; stay released and retry next frame. The source remains
                // audible but unrouted, same as the existing pool-exhaustion behavior.
                return;
            }

            AudioTrack = track;
            _trackReleasedByVirtual = false;

            // Hand loudness control back to the mixer track by restoring the source to full volume.
            AudioSource.volume = AudioConstant.FullVolume;

            // _mixerDecibelVolume is UnSet, so UpdateVolume recomputes from the faders and writes
            // to VolumeParaName, which routes to the send param automatically when effects are
            // active, restoring both volume and effect routing on the (possibly different) track.
            UpdateVolume();
        }
    }
}
#endif
