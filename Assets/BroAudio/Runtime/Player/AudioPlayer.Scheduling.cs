using UnityEngine;
using Ami.Extension;
using System.Collections;

namespace Ami.BroAudio.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public partial class AudioPlayer : MonoBehaviour, IAudioPlayer, IPlayable, IRecyclable<AudioPlayer>
    {
        private double _secondsUntilScheduledStart;
        private double _pauseDspTime;
        private double _playbackEndDspTime;
        // True when _pref.ScheduledEndTime was computed from the clip's duration (and may therefore be
        // rescaled on pitch change); false when the caller chose an absolute time via ISchedulable.SetScheduledEndTime.
        private bool _isEndTimeDerivedFromClip;

        private void SchedulePlayback()
        {
            if (_pref.ScheduledStartTime > 0d) // Scheduled has higher priority than clip.delay
            {
                var dspTime = AudioSettings.dspTime;
                AudioSource.PlayScheduled(System.Math.Max(_pref.ScheduledStartTime, dspTime));
                _secondsUntilScheduledStart = _pref.ScheduledStartTime - dspTime;
            }

            ScheduleEndTime();
        }

        private void ScheduleEndTime()
        {
            if (_pref.ScheduledEndTime > 0d)
            {
                AudioSource.SetScheduledEndTime(_pref.ScheduledEndTime);
            }
        }

        // Slide dsp-time schedule forward by the pause duration; caller re-arms the end via ScheduleEndTime after UnPause (PlayScheduled would restart the source).
        // The end-time slide is provisional: once the source is un-paused, RecalculateScheduledEndTime derives the exact
        // end from the playhead, which also absorbs any SetPitch issued while paused. The slide only survives in the
        // paths the recompute declines to touch (explicit end time, pitch <= 0).
        private void RebaseScheduleAfterPause()
        {
            double pauseDuration = AudioSettings.dspTime - _pauseDspTime;
            if (_pref.ScheduledStartTime > 0)
            {
                _pref.ScheduledStartTime += pauseDuration;
            }
            if (_pref.ScheduledEndTime > 0)
            {
                _pref.ScheduledEndTime += pauseDuration;
            }
        }

        // Re-derives the scheduled end from the actual playhead so it stays correct after mid-play pitch changes.
        // AudioSource.timeSamples reflects real progress regardless of pitch history, so this works after any
        // number of pitch changes (including mid-fade) without integrating pitch over time.
        private void RecalculateScheduledEndTime()
        {
            if (_clip == null || _playbackEndDspTime <= 0d || _stopMode == StopMode.Pause)
            {
                return;
            }

            if (_pref.ScheduledEndTime > 0d && !_isEndTimeDerivedFromClip)
            {
                // ISchedulable.SetScheduledEndTime is an absolute-time contract; pitch changes must not move it.
                return;
            }

            var audioClip = AudioSource.clip;
            if (!audioClip)
            {
                return;
            }

            float pitch = AudioSource.pitch;
            if (Mathf.Approximately(pitch, 0f))
            {
                // The playhead is frozen; PitchAdjusted treats ~0 as unscaled, so leave the schedule as-is.
                return;
            }

            if (pitch < 0f)
            {
                // Reverse playback moves away from the end position, so "time remaining" is undefined.
                // Push the hardware stop past any possible stop moment (there's no API to clear it) and let
                // the frame-based checks end playback; sample-accurate looping requires pitch > 0.
                if (_pref.ScheduledEndTime > 0d && AudioSource.isPlaying)
                {
                    AudioSource.SetScheduledEndTime(_playbackEndDspTime + audioClip.length + 1d);
                }
                return;
            }

            double dspTime = AudioSettings.dspTime;
            double newEndDspTime;
            if (_pref.ScheduledStartTime > dspTime)
            {
                // Still inside the warm-up window; the playhead hasn't started moving yet.
                newEndDspTime = _pref.ScheduledStartTime + PitchAdjusted(_clip.GetPlayableDuration(), pitch);
            }
            else
            {
                int endSample = audioClip.samples - audioClip.GetTimeSample(_clip.EndPosition);
                int remainingSamples = endSample - AudioSource.timeSamples;
                if (remainingSamples <= 0)
                {
                    // Playhead has reached the end position; the existing scheduled stop is about to fire.
                    return;
                }
                double remainingSeconds = remainingSamples / (double)audioClip.frequency / pitch;
                newEndDspTime = dspTime + remainingSeconds;
            }

            double delta = newEndDspTime - _playbackEndDspTime;
            _playbackEndDspTime = newEndDspTime;
            if (_pref.ScheduledEndTime > 0d)
            {
                _pref.ScheduledEndTime = newEndDspTime;
                if (AudioSource.isPlaying)
                {
                    AudioSource.SetScheduledEndTime(newEndDspTime);
                }
            }
            _nextPlayer?.ShiftScheduledTimes(delta);
        }

        // Slides this player's whole schedule by the given amount. Used to propagate a recomputed loop seam
        // to a pre-spawned next player, whose times were derived from the previous player's old end time.
        private void ShiftScheduledTimes(double delta)
        {
            if (delta == 0d)
            {
                return;
            }

            if (_pref.ScheduledStartTime > 0d)
            {
                _pref.ScheduledStartTime += delta;
                _secondsUntilScheduledStart += delta;
                if (AudioSource.isPlaying && _pref.ScheduledStartTime > AudioSettings.dspTime)
                {
                    AudioSource.SetScheduledStartTime(_pref.ScheduledStartTime);
                }
            }
            if (_pref.ScheduledEndTime > 0d)
            {
                _pref.ScheduledEndTime += delta;
                if (AudioSource.isPlaying)
                {
                    AudioSource.SetScheduledEndTime(_pref.ScheduledEndTime);
                }
            }
            if (_playbackEndDspTime > 0d)
            {
                _playbackEndDspTime += delta;
            }
        }

        IAudioPlayer ISchedulable.SetScheduledStartTime(double dspTime)
        {
            if (_pref.ScheduledStartTime > 0d)
            {
                // Recalculate the time when WaitForScheduledStartTime() is already running
                _secondsUntilScheduledStart += dspTime - _pref.ScheduledStartTime;
            }
            _pref.ScheduledStartTime = dspTime;

            // isPlaying will return true once it's scheduled, even if it's not actually playing
            if (AudioSource.isPlaying)
            {
                // If this is called after the audio is already playing, it pauses until the given dspTime.
                // Intentionally kept as a feature.
                AudioSource.SetScheduledStartTime(dspTime);
            }
            else
            {
                PlayInternal();
            }
            return this;
        }

        IAudioPlayer ISchedulable.SetScheduledEndTime(double dspTime)
        {
            _pref.ScheduledEndTime = dspTime;
            _isEndTimeDerivedFromClip = false;
            // Keep PlayControl's own end-wait in sync with the explicit end so it (not a separate isPlaying poll)
            // is the single authority that ends playback, avoiding a double EndPlaying.
            _playbackEndDspTime = dspTime;

            if (AudioSource.isPlaying)
            {
                AudioSource.SetScheduledEndTime(dspTime);
            }
            return this;
        }

        IAudioPlayer ISchedulable.SetDelay(float delay)
        {
            return this.SetScheduledStartTime(AudioSettings.dspTime + delay);
        }

        private void SetClipDelayIfNotScheduled()
        {
            if (_pref.ScheduledStartTime <= 0 && _clip != null && _clip.Delay > 0)
            {
                _pref.ScheduledStartTime = AudioSettings.dspTime + _clip.Delay;
            }
        }

        private IEnumerator WaitForScheduledStartTime()
        {
            while (_secondsUntilScheduledStart > 0)
            {
                yield return null;
                _secondsUntilScheduledStart -= Utility.GetDeltaTime();
            }
        }
    }
}