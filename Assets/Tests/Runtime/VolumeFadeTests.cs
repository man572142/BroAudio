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
    /// Inventory 1.6 (Docs/inventory/volume-mixer.md, rows "fades run in linear space" and "fade direction
    /// picks ease ... re-anchors mid-fade", both listed deferred): the *timed* half of the SetVolume API.
    /// Every other SetVolume call in the suite passes fadeTime 0 and so only ever exercises
    /// <c>AudioPlayer.Volume.cs: SetVolumeInternal</c>'s <c>module.Complete(vol)</c> branch; the master fade
    /// (a separate coroutine in SoundManager, ramping in dB space) is timed in UpdateModeClockTests, but the
    /// Fader-driven per-type / per-SoundID / per-handle ramps were not timed anywhere.
    /// <para>
    /// The three entry points funnel into one method with a different Fader each:
    /// <c>BroAudio.SetVolume(BroAudioType,...)</c> -> <c>SoundManager.SetVolume(vol, type, fadeTime)</c> ->
    /// <c>player.SetAudioTypeVolume</c> -> <c>SetVolumeInternal(_audioTypeVolume,...)</c>, while both
    /// <c>BroAudio.SetVolume(SoundID,...)</c> (via SoundManager's active-player loop) and the handle
    /// overload (via AudioPlayerInstanceWrapper) reach <c>SetVolumeInternal(_trackVolume,...)</c>. Hence two
    /// ramp tests, not three: the second drives both trackVolume entry points at the same player, and spends
    /// the second one on a mid-flight reversal rather than on repeating the first one's ramp.
    /// </para>
    /// <para>
    /// Timing. Fader progress accumulates <c>Utility.GetDeltaTime()</c>, i.e. the same (capped) frame clock
    /// <see cref="WaitForSeconds"/> runs on, so a WaitForSeconds interval and a fade's elapsed time advance
    /// together - unlike the DSP clock, which is why every test here also needs the voice itself to be
    /// realtime (<see cref="BroAudioTestFixture.RequireRealtimeAudioClock"/>): with no output device the clip
    /// would end, and the player recycle, between two frames of a multi-second ramp. Every sample window
    /// below is at least 1s clear of the point where it could start failing, per the fixture's timing rule.
    /// </para>
    /// <para>
    /// Ease. With no BroRuntimeSetting asset in the project the factory defaults apply
    /// (RuntimeSetting.FactorySettings): <c>DefaultFadeInEase</c> = InCubic (t^3), <c>DefaultFadeOutEase</c> =
    /// OutSine (sin(t*pi/2)). <c>SetVolumeInternal</c> picks between them by direction
    /// (<c>module.Current &lt; vol</c>) at the moment of the call, and the numbers in each test's comments are
    /// derived from the resulting curve. The two curves are shaped very differently near their start - OutSine
    /// moves fastest there, InCubic barely moves at all - so a "strictly between start and target" sample is
    /// taken on the fade-*down* legs, and the fade-*up* legs are pinned by their (deterministic) origin plus a
    /// "still short of half way" read instead. Every Ease is monotonic on [0,1] and <c>Mathf.Lerp</c> clamps,
    /// so a ramp can never overshoot either end whichever curve is configured - only the *rate* is
    /// ease-dependent.
    /// </para>
    /// </summary>
    public class VolumeFadeTests : BroAudioTestFixture
    {
        /// <summary>A clip long enough to outlive every ramp below, with seconds to spare for a slow frame clock.</summary>
        private const float ClipLength = 15f;

        /// <summary>
        /// Fade-down window. A 5s fade from 1 to 0.2 under OutSine sits at Lerp(1, 0.2, sin(0.4*pi/2)) = 0.530
        /// when sampled 2s in. The bounds below are the same curve solved for time: it only reaches 0.9 after
        /// 0.4s and only falls to 0.25 after 3.88s, so this sample has 1.6s of slack on one side and 1.9s on
        /// the other - well past the fixture's "one slow frame is 0.333s" allowance - while still failing
        /// decisively if the fade were ignored (the read would be 0.2, the target) or never started (1).
        /// </summary>
        private const float FadeDownDuration = 5f;
        private const float MidFadeSampleTime = 2f;
        private const float FadedTargetVolume = 0.2f;
        private const float MidFadeFloor = 0.25f;
        private const float MidFadeCeiling = 0.9f;

        /// <summary>
        /// 0.2 linear is 20*Log10(0.2) = -13.98dB. TrySetMixerDecibelVolume clamps to [-80, 20], so the plain
        /// conversion passes through untouched. Written as a literal rather than via ToDecibel() so the
        /// expectation cannot move with the code under test.
        /// </summary>
        private const float FadedTargetDecibel = -13.98f;

        /// <summary>Polls this close to the target before the exact-value assertion; Fader.Complete then lands it exactly.</summary>
        private const float ArrivalEpsilon = 0.001f;

        /// <summary>How long an arrival poll may outlast its own fade before it counts as "never arrived".</summary>
        private const float ArrivalSlack = 1.5f;

        // Characterizes TEST_FINDINGS #57: SetPlaybackPrefByType runs before the active-player loop and
        // takes no fadeTime, so the stored per-type volume jumps straight to the target while every live
        // player of that type is handed a fadeTime-long Fader ramp. Asserted in the frame of the call.
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

            // Read with no yield in between, so these two are statements about the call itself rather than
            // about timing. SetPlaybackPrefByType writes the stored per-type pref synchronously and unfaded,
            // while the live player's Fader has only run its first pass - which writes Current = _origin
            // exactly, because every Ease is 0 at 0. That split (the pref snaps, live players ramp) is the
            // characterized behavior, not an accident of when this test looks.
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

            // What the listener actually hears, and the one assertion here that needs no timing margin at
            // all: Fader.Update assigns Current and calls UpdateVolume in the same statement pair, so the
            // exposed parameter and GetVolume() always come from the same pass, whichever order the two
            // coroutines ran in this frame. Mid-ramp they must therefore satisfy the plain dB definition -
            // 20*Log10 of the linear product, computed here rather than through the ToDecibel() under test.
            // This is what distinguishes the real behavior (fade in linear space, re-converted every frame)
            // from a ramp interpolated in dB space, and from one that only writes the mixer at completion
            // (which would still read 0dB here).
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

            // Self-sufficient restore: a per-type volume is stored in SoundManager and would otherwise
            // attenuate every later SFX player in the run. Zero fade lands it immediately - and, unlike the
            // master fade (TEST_FINDINGS #51), a zero-fade SetVolume on a Fader does cancel a running ramp,
            // because SetVolumeInternal's else branch goes through Fader.Complete, which stops the coroutine.
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

            // Entry point 1: SoundManager loops its active players and calls IVolumeSettable.SetVolume on
            // every one whose ID matches. Same window and reasoning as the per-type test above - a different
            // Fader (_trackVolume) driven through a different call path.
            BroAudio.SetVolume(id, FadedTargetVolume, FadeDownDuration);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "The per-SoundID fade should leave the player at its origin in the frame it was requested, not snap it to the target.");

            yield return new WaitForSeconds(MidFadeSampleTime);

            float midVolume = player.GetVolume();
            Assert.Greater(midVolume, MidFadeFloor,
                $"Mid-fade the player should still be above its target ({FadedTargetVolume}) - a read at or below it means the ramp had already finished, i.e. fadeTime was not honoured.");
            Assert.Less(midVolume, MidFadeCeiling,
                "Mid-fade the player should already be well below full volume - a read near 1 means the per-SoundID fade never started.");

            // Entry point 2: the handle overload, on the same _trackVolume fader, reversing the ramp while it
            // is still in flight. Fader.SetTarget re-bases _origin on Current, so the new ramp starts from
            // wherever the old one had reached - it does not rewind to the old origin (1) and does not snap to
            // the new target. Both of those are what this same-frame read rules out.
            const float fadeUpDuration = 3f;
            const float fadeUpTarget = 0.8f;
            player.SetVolume(fadeUpTarget, fadeUpDuration);
            Assert.AreEqual(midVolume, player.GetVolume(), LinearTolerance,
                "Reversing a fade mid-flight should re-anchor on the level reached so far - neither rewinding to the previous origin nor snapping to the new target.");

            // Now fading up, so SetVolumeInternal picks FadeInEase (InCubic), which crosses the half-way
            // point between the two ends only at t^3 = 0.5, i.e. 79% of the way through - 2.38s into this 3s
            // fade. A read two frames in therefore has ~1.7s of slack before it could fail on a correct
            // build (two frames is at most 0.667s of fade progress, since Fader accumulates capped delta
            // time), whatever level the reversal happened to start from, while a regression that ignored
            // fadeTime would already read the target and fail immediately.
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

            // A 6s clip fade-in on a 15s clip, interrupted half way. The fade-in ease is InCubic, so at t=0.5
            // the level is 0.5^3 = 0.125 of target: unambiguously moving, and far enough below full volume that
            // "ramped down from full" and "ramped down from here" cannot be confused. The bounds are the same
            // curve solved for time - it passes 0.01 at 1.29s and 0.5 at 4.76s - so this sample sits ~1.7s
            // clear of either.
            const float clipFadeIn = 6f;
            const float interruptAfter = 3f;
            const float stopFadeOut = 2f;
            const float stillRampingFloor = 0.01f;
            const float stillRampingCeiling = 0.5f;

            // The fade-out's own first frames, solved the same way: OutSine is at its steepest at the start,
            // so one capped frame (0.333s of a 2s fade) already takes it to 74% of where it started and three
            // (1s) to 29%. A ramp that genuinely starts at the interrupted level therefore cannot be observed
            // below a quarter of it, while one that collapsed to near-silence and then crawled back would be.
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

            // No yield between the read and the call: StopControl runs synchronously up to its first yield,
            // and an explicit fadeOut argument is a pending override, which is what makes it take the branch
            // that calls Fader.SetTarget (the other branch - waiting out a fade that is already heading down -
            // needs IsFadingOut, and this one is fading *in*). SetTarget re-bases _origin on Current there and
            // Fade's first pass writes Current back out unchanged, so this reads the level the fade-out
            // starts from, in the same frame.
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

            // The contract, now measured frame by frame rather than in the single frame above: the fade-out
            // ramps 0.125 -> 0 rather than re-running the full 1 -> 0 curve. A regression that re-anchored on
            // the fader's original value, or completed the fade-in first, would show up here as a jump to (or
            // toward) full volume.
            Assert.LessOrEqual(peakAfterStop, levelAtStop + LinearTolerance,
                $"Stopping mid fade-in must ramp down from the level reached so far ({levelAtStop}), never rise back toward full volume first.");
            Assert.Greater(peakAfterStop, levelAtStop * startsFromCurrentLevelFraction,
                "The fade-out must also be audible from that level on the way down, not drop to near-silence first and ramp the remainder.");

            // ...and it is a ramp, not a cut: both a fade-out that collapsed to Complete(0) and one that
            // decided its origin and target were equal (Fader.Update's Mathf.Approximately early-out) would
            // end playback within a frame or two and observe no intermediate volumes at all.
            Assert.GreaterOrEqual(intermediateSamples, 2,
                "The Stop fade-out should pass through intermediate volumes on its way down, not cut straight to silence.");
            Assert.Greater(stopDuration, 1f,
                $"The {stopFadeOut}s fade-out should take its stated time even though it starts from a quiet mid-fade-in level - it re-anchors its origin, it does not shorten.");
        }
    }
}