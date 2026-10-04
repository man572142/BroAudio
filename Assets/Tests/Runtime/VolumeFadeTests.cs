using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins timed SetVolume ramps on both Faders: per-type (_audioTypeVolume), and per-SoundID then per-handle
    /// on one player's _trackVolume (the second spent on a mid-flight reversal), plus Stop during a fade-in.
    /// <para>
    /// Faders and WaitForSeconds share the capped frame clock, but the voice still needs
    /// <see cref="BroAudioTestFixture.RequireRealtimeAudioClock"/> or the clip ends mid-ramp. Windows derive
    /// from the factory eases, picked by direction: fade-in InCubic (t^3) barely moves at first, fade-out
    /// OutSine (sin(t*pi/2)) moves fastest - so fade-down legs get a "strictly between" sample and fade-up
    /// legs a "still short of half way" read.
    /// </para>
    /// </summary>
    public class VolumeFadeTests : BroAudioTestFixture
    {
        /// <summary>A clip long enough to outlive every ramp below, with seconds to spare for a slow frame clock.</summary>
        private const float ClipLength = 15f;

        /// <summary>
        /// Fade-down window: a 5s OutSine fade 1 -> 0.2 reads 0.530 at 2s, and passes 0.9 at 0.4s and 0.25 at
        /// 3.88s - 1.6s / 1.9s of slack. An ignored fade reads 0.2, an unstarted one 1.
        /// </summary>
        private const float FadeDownDuration = 5f;
        private const float MidFadeSampleTime = 2f;
        private const float FadedTargetVolume = 0.2f;
        private const float MidFadeFloor = 0.25f;
        private const float MidFadeCeiling = 0.9f;

        /// <summary>
        /// 20*Log10(0.2), inside the mixer's [-80, 20] clamp. A literal, not ToDecibel(), so the expectation
        /// cannot move with the code under test.
        /// </summary>
        private const float FadedTargetDecibel = -13.98f;

        /// <summary>Polls this close to the target before the exact-value assertion; Fader.Complete then lands it exactly.</summary>
        private const float ArrivalEpsilon = 0.001f;

        /// <summary>How long an arrival poll may outlast its own fade before it counts as "never arrived".</summary>
        private const float ArrivalSlack = 1.5f;

        // Pins TEST_FINDINGS #57: the stored type pref snaps to the target while live players ramp.
        [UnityTest]
        [Category("Finding_57")]
        public IEnumerator SetVolume_ByTypeWithFade_RampsLivePlayerOverDuration()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("TypeFadeSfx", BroAudioType.SFX, NewClip(ClipLength));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "A freshly played default entity should start at full linear volume, which is the origin this ramp is measured from.");
            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "The player must hold a pooled track for its volume parameter to be exposed.");
            string trackParaName = player.AudioSource.outputAudioMixerGroup.name;
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(trackParaName, out float startDecibel));
            Assert.AreEqual(AudioConstant.FullDecibelVolume, startDecibel, DecibelTolerance,
                "Full linear volume is 0dB on the player's own track - the mixer-side origin of the ramp below.");

            BroAudio.SetVolume(BroAudioType.SFX, FadedTargetVolume, FadeDownDuration);

            // No yield: the Fader's first pass writes exactly the origin (every Ease is 0 at 0).
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(FadedTargetVolume, pref.Volume, LinearTolerance,
                "The stored per-type pref should take the target immediately - fadeTime only applies to live players' Faders.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "The live player should still be exactly at its origin in the frame SetVolume was called - a read at the target here means fadeTime was ignored and the volume snapped.");

            yield return new WaitForSeconds(MidFadeSampleTime);

            float midVolume = player.GetVolume();
            Assert.Greater(midVolume, MidFadeFloor,
                $"Mid-fade the live player should still be above its target ({FadedTargetVolume}) - a read at or below it means the ramp had already finished, i.e. fadeTime was not honoured.");
            Assert.Less(midVolume, MidFadeCeiling,
                "Mid-fade the live player should already be well below full volume - a read near 1 means the per-type fade never started.");

            // No timing margin needed: Fader.Update writes Current and the mixer in one pass. Rules out a
            // dB-space ramp and a mixer write only at completion (0dB here). Not via the ToDecibel() under test.
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(trackParaName, out float midDecibel));
            Assert.AreEqual(20f * Mathf.Log10(midVolume), midDecibel, DecibelTolerance,
                "Every frame of the ramp must reach the track's exposed mixer parameter as the decibel conversion of that frame's linear value.");

            yield return WaitUntilOrTimeout(() => player.GetVolume() <= FadedTargetVolume + ArrivalEpsilon,
                "the per-type fade to reach its target volume", FadeDownDuration + ArrivalSlack);
            Assert.IsTrue(player.IsPlaying, "The player must still be live at the end of the ramp - a recycled handle reads 0 volume, which would satisfy the poll above for the wrong reason.");
            Assert.AreEqual(FadedTargetVolume, player.GetVolume(), LinearTolerance,
                "Fader.Complete should land the live player exactly on the target once the fade time elapses.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(trackParaName, out float endDecibel));
            Assert.AreEqual(FadedTargetDecibel, endDecibel, DecibelTolerance,
                "The faded-to value must reach the track's exposed mixer parameter in decibels - GetVolume() alone is only the input to that write.");

            // Unlike the master fade (TEST_FINDINGS #51), a zero-fade SetVolume on a Fader cancels its ramp.
            BroAudio.SetVolume(BroAudioType.SFX, AudioConstant.FullVolume, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance, "The per-type volume should be restored before leaving this test.");
        }

        [UnityTest]
        public IEnumerator SetVolume_BySoundIdThenByHandleWithFade_ReanchorsOnTheCurrentLevelMidFlight()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("TrackFadeSfx", BroAudioType.SFX, NewClip(ClipLength));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "A freshly played default entity should start at full linear volume.");

            // Entry point 1, per SoundID: same window as the per-type test, on _trackVolume.
            BroAudio.SetVolume(id, FadedTargetVolume, FadeDownDuration);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "The per-SoundID fade should leave the player at its origin in the frame it was requested, not snap it to the target.");

            yield return new WaitForSeconds(MidFadeSampleTime);

            float midVolume = player.GetVolume();
            Assert.Greater(midVolume, MidFadeFloor,
                $"Mid-fade the player should still be above its target ({FadedTargetVolume}) - a read at or below it means the ramp had already finished, i.e. fadeTime was not honoured.");
            Assert.Less(midVolume, MidFadeCeiling,
                "Mid-fade the player should already be well below full volume - a read near 1 means the per-SoundID fade never started.");

            // Entry point 2, the handle overload, reversing the ramp in flight on the same Fader.
            const float fadeUpDuration = 3f;
            const float fadeUpTarget = 0.8f;
            player.SetVolume(fadeUpTarget, fadeUpDuration);
            Assert.AreEqual(midVolume, player.GetVolume(), LinearTolerance,
                "Reversing a fade mid-flight should re-anchor on the level reached so far - neither rewinding to the previous origin nor snapping to the new target.");

            // Fading up uses InCubic, which reaches half way only 2.38s into this 3s fade; two capped frames
            // are at most 0.667s, leaving ~1.7s of slack whatever level the reversal started from.
            float halfWayUp = (midVolume + fadeUpTarget) * 0.5f;
            yield return WaitFrames(2);
            Assert.Less(player.GetVolume(), halfWayUp,
                "The handle overload should ramp from the current volume, not snap to the target - IAudioPlayer.SetVolume(vol, fadeTime) reaches the same SetVolumeInternal as the SoundID path.");

            yield return WaitUntilOrTimeout(() => player.GetVolume() >= fadeUpTarget - ArrivalEpsilon,
                "the handle's own fade to reach its target volume", fadeUpDuration + ArrivalSlack);
            Assert.IsTrue(player.IsPlaying, "The player must still be live at the end of the ramp - a recycled handle reads 0 volume, which cannot satisfy the poll above but would break the mixer read below.");
            Assert.AreEqual(fadeUpTarget, player.GetVolume(), LinearTolerance,
                "The handle's fade should land exactly on its target.");

            // 20*Log10(0.8) = -1.94dB, written as a literal for the same reason as FadedTargetDecibel.
            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "The player must still hold a pooled track for its volume parameter to be exposed.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(player.AudioSource.outputAudioMixerGroup.name, out float endDecibel));
            Assert.AreEqual(-1.94f, endDecibel, DecibelTolerance,
                "The faded-to value must reach the track's exposed mixer parameter in decibels.");
        }

        [UnityTest]
        public IEnumerator Stop_WithFade_DuringFadeIn_RampsDownFromCurrentLevelNotFromFull()
        {
            yield return RequireRealtimeAudioClock();

            // A 6s InCubic fade-in interrupted half way reads 0.125, far from full. The curve passes 0.01 at
            // 1.29s and 0.5 at 4.76s, ~1.7s clear of the 3s sample.
            const float clipFadeIn = 6f;
            const float interruptAfter = 3f;
            const float stopFadeOut = 2f;
            const float stillRampingFloor = 0.01f;
            const float stillRampingCeiling = 0.5f;

            // OutSine is steepest at its start: one capped frame of the 2s fade-out leaves 74%, three leave 29%.
            // A ramp truly starting at the interrupted level is never first seen below a quarter of it.
            const float startsFromCurrentLevelFraction = 0.25f;
            const float recycleSlack = 3f;

            AudioClip clip = NewClip(ClipLength);
            AudioEntity entity = NewEntity("StopDuringFadeInSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = clipFadeIn;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return new WaitForSeconds(interruptAfter);

            float levelAtStop = player.GetVolume();
            Assert.Greater(levelAtStop, stillRampingFloor, "The clip's fade-in should have left silence by the time it is interrupted.");
            Assert.Less(levelAtStop, stillRampingCeiling, "The clip's fade-in should still be well short of full volume when it is interrupted.");

            // No yield: StopControl runs synchronously to its first yield, and on a fading-in player an explicit
            // fadeOut takes the Fader.SetTarget branch, so this reads the fade-out's origin in the same frame.
            float stopRealtime = Time.realtimeSinceStartup;
            player.Stop(stopFadeOut);
            Assert.AreEqual(levelAtStop, player.GetVolume(), LinearTolerance,
                "Stopping mid fade-in should re-anchor the fade-out on the level reached so far, in the frame Stop was called.");

            float peakAfterStop = 0f;
            int intermediateSamples = 0;
            float deadline = stopRealtime + stopFadeOut + recycleSlack;
            while (player.IsActive)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline,
                    "Timed out waiting for the Stop fade-out to finish and the player to recycle.");
                yield return null;

                // GetVolume() resolves through IsAvailable(false) on the wrapper, so reading it on the frame
                // the player recycles returns 0f rather than logging the recycled-player warning.
                float volume = player.GetVolume();
                peakAfterStop = Mathf.Max(peakAfterStop, volume);
                if (volume > 0f && volume < levelAtStop - 0.005f)
                {
                    intermediateSamples++;
                }
            }
            float stopDuration = Time.realtimeSinceStartup - stopRealtime;

            // Frame by frame: re-anchoring on the original value, or finishing the fade-in first, would rise.
            Assert.LessOrEqual(peakAfterStop, levelAtStop + LinearTolerance,
                $"Stopping mid fade-in must ramp down from the level reached so far ({levelAtStop}), never rise back toward full volume first.");
            Assert.Greater(peakAfterStop, levelAtStop * startsFromCurrentLevelFraction,
                "The fade-out must also be audible from that level on the way down, not drop to near-silence first and ramp the remainder.");

            // A cut (Complete(0), or Fader.Update's Approximately early-out) shows no intermediate volumes.
            Assert.GreaterOrEqual(intermediateSamples, 2,
                "The Stop fade-out should pass through intermediate volumes on its way down, not cut straight to silence.");
            Assert.Greater(stopDuration, 1f,
                $"The {stopFadeOut}s fade-out should take its stated time even though it starts from a quiet mid-fade-in level - it re-anchors its origin, it does not shorten.");
        }
    }
}