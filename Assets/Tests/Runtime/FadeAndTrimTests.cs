using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fades (clip setting, one-shot override, custom ease), Stop()/Pause() falling back to the clip's authored
    /// FadeOut, StartPosition/EndPosition trims, and the Stop() re-entrancy guards.
    /// <para>
    /// Two clocks: fade progress is frame-clocked, but the natural-end fade's start gate is DSP-clocked
    /// (Stop(fadeOut) has no gate). Poll for transitions and ranges, not exact counts (Docs/inventory/time-dependent.md).
    /// </para>
    /// </summary>
    public class FadeAndTrimTests : BroAudioTestFixture
    {
        // Lerp + easing is not sample-accurate: a wide "basically at target" threshold, not an exact value.
        private const float NearTargetThreshold = 0.95f;

        [UnityTest]
        public IEnumerator Play_WithClipFadeIn_RampsVolumeUpFromSilence()
        {
            // Long fade: the near-silence read lands on the first IsPlaying frame, which can be a slow frame in.
            // Clip/master volumes are off their default 1 so the ramp's target is their product, not full volume.
            const float fadeIn = 1.2f;
            const float ClipVolume = 0.4f;
            const float MasterVolume = 0.5f;
            const float AuthoredProduct = ClipVolume * MasterVolume; // 0.2
            AudioClip clip = NewClip(3f);
            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("FadeInSfx", BroAudioType.SFX, ClipVolume, MasterVolume, clip);
            Track(entity);
            entity.Clips[0].FadeIn = fadeIn;
            SoundID id = IdOf(entity);

            List<float> samples = new List<float>();
            IAudioPlayer player = BroAudio.Play(id);
            player.OnUpdate(p => samples.Add(p.GetVolume()));

            yield return WaitForPlaybackStart(player);

            // SetupClipVolume snaps _clipVolume.Current to 0 before the fade-in coroutine starts ramping it up.
            Assert.Less(player.GetVolume(), AuthoredProduct * 0.5f, "Volume should start near silence when the clip has a FadeIn.");

            yield return WaitUntilOrTimeout(() => Mathf.Abs(player.GetVolume() - AuthoredProduct) < LinearTolerance,
                "the clip's own fade-in to reach the authored clip*master target", fadeIn + 1f);

            // 0.2 differs from 1, 0.4 and 0.5, so a fade targeting full volume or one factor alone fails.
            Assert.AreEqual(AuthoredProduct, player.GetVolume(), LinearTolerance,
                "A fade-in must land on clip.Volume * entity.MasterVolume, not on full volume or either factor alone.");

            Assert.GreaterOrEqual(samples.Count, 2, "OnUpdate should have fired on multiple frames during the fade-in.");
            for (int i = 1; i < samples.Count; i++)
            {
                // Tiny negative tolerance absorbs float noise from Mathf.Lerp/easing, not a real decrease.
                Assert.GreaterOrEqual(samples[i], samples[i - 1] - 0.001f,
                    $"Fade-in volume should be monotonically non-decreasing across frames (sample {i}: {samples[i]} < {samples[i - 1]}).");
            }
            Assert.Greater(samples[samples.Count - 1], samples[0], "Volume should have increased overall across the fade-in.");
        }

        [UnityTest]
        public IEnumerator Play_WithClipFadeOut_RampsVolumeDownBeforeNaturalEnd()
        {
            // The IsPlaying assertion below reads a DSP-driven voice at an instant the frame-clocked ramp
            // chooses, so the two clocks have to run at the same rate.
            yield return RequireRealtimeAudioClock();

            // The factory OutSine fade-out crosses 0.5 a third of the way in: 0.5s into this 1.5s ramp, with a
            // full second of the 3s clip still to play.
            const float clipLength = 3f;
            const float fadeOut = 1.5f;
            AudioClip clip = NewClip(clipLength);
            AudioEntity entity = NewEntity("FadeOutSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeOut = fadeOut;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(2);
            Assert.GreaterOrEqual(player.GetVolume(), NearTargetThreshold,
                "With no FadeIn set, volume should already be at full target once playback starts.");

            // Poll rather than compute the DSP gate. IsActive is required: a recycled handle's GetVolume() reads 0,
            // so a natural end with no ramp would satisfy the poll. IsActive/IsPlaying don't log the recycled warning.
            yield return WaitUntilOrTimeout(() => player.IsActive && player.GetVolume() < 0.5f,
                "the natural-end fade-out to ramp a still-live player below half volume (timing out here means no such drop was ever seen while the player was active, which includes playback simply ending with no ramp at all)",
                clipLength + 1f);
            Assert.IsTrue(player.IsPlaying,
                "The drop below half volume must be observed while the player is still playing, not after playback has already ended.");

            yield return WaitForRecycle(player,
                "playback to end once the fade-out completes", fadeOut + 1f);
        }

        [UnityTest]
        public IEnumerator Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting()
        {
            // The factory InCubic (t^3) crosses NearTargetThreshold at 98.3% of a fade: the 4s override not
            // before ~3.93s, the clip's own 0.15s fade at once.
            const float clipFadeIn = 0.15f;
            const float overrideFadeIn = 4f;
            const float observationTime = 1.2f;
            AudioClip clip = NewClip(5f);
            AudioEntity entity = NewEntity("FadeOverrideSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = clipFadeIn;
            SoundID id = IdOf(entity);

            IAudioPlayer firstPlayer = BroAudio.Play(id, overrideFadeIn);
            yield return WaitForPlaybackStart(firstPlayer, "first playback to start");

            // 2.7s clear of failing on a correct build; a hitch only overshoots, which is the safe direction.
            yield return new WaitForSeconds(observationTime);
            Assert.Less(firstPlayer.GetVolume(), NearTargetThreshold,
                "The explicit fadeIn override should still be ramping well past the clip's own (shorter) FadeIn duration - FadeData.cs's one-shot Next override should have taken priority over the clip setting.");

            firstPlayer.Stop(FadeData.Immediate);
            yield return WaitForRecycle(firstPlayer, "the first player to stop");

            IAudioPlayer secondPlayer = BroAudio.Play(id);
            yield return WaitForPlaybackStart(secondPlayer, "second playback to start");

            // Poll, not a fixed instant: the clip's fade is at target by ~0.15s, leaving ~1s for frame-clock lag,
            // while a leaked 4s override still times out by 2.7s.
            yield return WaitUntilOrTimeout(() => secondPlayer.GetVolume() >= NearTargetThreshold,
                "the second play to reach target volume on the clip's own short FadeIn - timing out here means the one-shot override leaked into a later play",
                observationTime);
        }

        // Each half samples a third of the way in, where the eases sit far apart: fade-in OutCubic 0.70 vs factory
        // InCubic 0.04; fade-out InCubic 0.96 vs factory OutSine 0.5. Frame slop moves the sample to ~0.22-0.44 of
        // the fade, still clear of both curves. Both fades are explicit overrides, the only fades the setters
        // shape (TEST_FINDINGS #70).
        [UnityTest]
        public IEnumerator SetFadeInEase_AndSetFadeOutEase_ShapeExplicitFades()
        {
            // Sampled on the frame clock inside a 10s voice, which a decoupled DSP clock could end first.
            yield return RequireRealtimeAudioClock();

            const float FadeSeconds = 3f;
            const float SampleSeconds = 1f;
            const float FadeInFloor = 0.3f;
            const float FadeOutFloor = 0.8f;
            SoundID id = NewSound("EaseSfx", BroAudioType.SFX, NewClip(10f));

            IAudioPlayer player = BroAudio.Play(id, FadeSeconds);
            // Same frame as Play(), before SoundManager.LateUpdate drains the queue and PlayControl reads _fadeInData.
            player.SetFadeInEase(Ease.OutCubic);

            yield return WaitForPlaybackStart(player);
            yield return new WaitForSeconds(SampleSeconds);
            Assert.Greater(player.GetVolume(), FadeInFloor,
                "A third of the way into an OutCubic fade-in the volume should already be well up - a read near 0 means the factory InCubic ran instead.");
            // To full target, so the fade-out sample reads against the plain curve.
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the custom-eased fade-in to reach its target", FadeSeconds + 1f);

            player.SetFadeOutEase(Ease.InCubic);
            player.Stop(FadeSeconds);
            yield return new WaitForSeconds(SampleSeconds);
            Assert.IsTrue(player.IsActive, "The 3s fade-out should still be in flight a second in.");
            Assert.Greater(player.GetVolume(), FadeOutFloor,
                "A third of the way into an InCubic fade-out the volume should barely have dropped - a read near 0.5 means the factory OutSine ran instead.");
            yield return WaitForRecycle(player,
                "the custom-eased fade-out to complete", FadeSeconds + 1f);
        }

        // Pins TEST_FINDINGS #70 on the clip's own authored fades; a fix turns both asserts red. Same sample points
        // as above: across the frame slop the factory curves stay under 0.09 (in) and 0.67 (out), the requested
        // ones above 0.53 and 0.91, so each ceiling separates them.
        [UnityTest]
        [Category("Finding_70")]
        public IEnumerator SetFadeInEase_AndSetFadeOutEase_DoNotShapeTheClipsOwnAuthoredFades()
        {
            // Sampled on the frame clock inside a 10s voice, which a decoupled DSP clock could end first.
            yield return RequireRealtimeAudioClock();

            const float AuthoredFadeSeconds = 3f;
            const float SampleSeconds = 1f;
            const float FadeInCeiling = 0.3f;
            const float FadeOutCeiling = 0.8f;
            AudioEntity entity = NewEntity("AuthoredEaseSfx", BroAudioType.SFX, NewClip(AuthoredFadeClipSeconds));
            entity.Clips[0].FadeIn = AuthoredFadeSeconds;
            entity.Clips[0].FadeOut = AuthoredFadeSeconds;

            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            // Same frame as Play(), before SoundManager.LateUpdate starts PlayControl - as in the test above.
            player.SetFadeInEase(Ease.OutCubic);

            yield return WaitForPlaybackStart(player);
            yield return new WaitForSeconds(SampleSeconds);
            Assert.Less(player.GetVolume(), FadeInCeiling,
                "characterizes: a third of the way into the clip's own FadeIn the volume is still near 0 - the factory " +
                "InCubic ran, not the OutCubic SetFadeInEase asked for.");
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the clip's own fade-in to reach its target", AuthoredFadeSeconds + 1f);

            player.SetFadeOutEase(Ease.InCubic);
            player.Stop();
            yield return new WaitForSeconds(SampleSeconds);
            Assert.IsTrue(player.IsActive, "The clip's 3s FadeOut should still be in flight a second in.");
            Assert.Less(player.GetVolume(), FadeOutCeiling,
                "characterizes: a third of the way into the clip's own FadeOut the volume has already dropped to about " +
                "half - the factory OutSine ran, not the InCubic SetFadeOutEase asked for.");
            yield return WaitForRecycle(player,
                "the clip's own fade-out to complete", AuthoredFadeSeconds + 1f);
        }

        /// <summary>A clip long enough that no authored-fade test below reaches its natural end by accident.</summary>
        private const float AuthoredFadeClipSeconds = 10f;

        /// <summary>Sampled a second in, the factory OutSine reads 0.5, a second clear of both ends of the fade.</summary>
        private const float AuthoredFadeOutSeconds = 3f;

        // Only an argument-less Stop() reaches the fallback to the clip's authored FadeOut; an explicit fade skips it.
        [UnityTest]
        public IEnumerator Stop_WithoutAFade_FadesOutOverTheClipsAuthoredFadeOut()
        {
            // The voice has to outlast the frame-clocked fade, and a decoupled DSP clock can end it first.
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("AuthoredStopFadeSfx", BroAudioType.SFX, NewClip(AuthoredFadeClipSeconds));
            entity.Clips[0].FadeOut = AuthoredFadeOutSeconds;
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            yield return WaitForPlaybackStart(player);

            player.Stop();
            yield return new WaitForSeconds(1f);

            Assert.IsTrue(player.IsActive && player.IsPlaying,
                "1s into the clip's 3s FadeOut the player should still be playing - Stop() must fade, not cut.");
            float midFade = player.GetVolume();
            Assert.Greater(midFade, 0.05f, "1s into a 3s fade-out the voice should still be audible.");
            Assert.Less(midFade, NearTargetThreshold, "1s into a 3s fade-out the voice should already be well below full volume.");

            yield return WaitForRecycle(player,
                "Stop() to end playback once the clip's authored FadeOut completes", AuthoredFadeOutSeconds + 1f);
        }

        // Pause() takes the same authored-FadeOut fallback before it reaches AudioSource.Pause().
        [UnityTest]
        public IEnumerator Pause_WithoutAFade_FadesOutOverTheClipsAuthoredFadeOutThenFreezes()
        {
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("AuthoredPauseFadeSfx", BroAudioType.SFX, NewClip(AuthoredFadeClipSeconds));
            entity.Clips[0].FadeOut = AuthoredFadeOutSeconds;
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            yield return WaitForPlaybackStart(player);

            player.Pause();
            yield return new WaitForSeconds(1f);

            Assert.IsTrue(player.IsPlaying,
                "1s into the clip's 3s FadeOut the source should still be playing - Pause() must fade before it pauses.");
            float midFade = player.GetVolume();
            Assert.Greater(midFade, 0.05f, "1s into a 3s fade-out the voice should still be audible.");
            Assert.Less(midFade, NearTargetThreshold, "1s into a 3s fade-out the voice should already be well below full volume.");

            yield return WaitUntilOrTimeout(() => !player.IsPlaying,
                "the authored fade-out to finish and the pause to take effect", AuthoredFadeOutSeconds + 1f);
            Assert.IsTrue(player.IsActive, "A faded pause leaves the player active, not recycled.");

            int pausedAt = player.AudioSource.timeSamples;
            Assert.Greater(pausedAt, 0, "Precondition: the playhead must have moved before the pause.");
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(pausedAt, player.AudioSource.timeSamples, "The paused playhead must not advance.");

            player.UnPause(0f);
            yield return WaitForPlaybackStart(player, "the player to resume");
            Assert.GreaterOrEqual(player.AudioSource.timeSamples, pausedAt, "The resume must continue from where the pause froze it.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "UnPause(0f) must bring the player back at full volume, not at the silence the pause fade ended on.");
        }

        // A 6s FadeOut on a 7s clip opens the natural fade 1s in; Stop() lands 3s into it. Waiting it out ends
        // playback ~3s after the call, a restarted fade 6s; the budget sits 1.5s from each. The check a second
        // after the call rules out a Stop that cut the fade short.
        [UnityTest]
        public IEnumerator Stop_WhileTheClipsOwnFadeOutRuns_WaitsItOutInsteadOfRestartingIt()
        {
            // The natural fade's start is DSP-gated and its progress frame-clocked; the two must run together.
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 7f;
            const float ClipFadeOutSeconds = 6f;
            const double StopIntoFadeSeconds = 3.0;
            const float RecycleBudgetSeconds = 4.5f;
            AudioEntity entity = NewEntity("DoubleFadeGuardSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            entity.Clips[0].FadeOut = ClipFadeOutSeconds;

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);
            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire", DefaultPlaybackWaitSeconds);

            double stopAtDsp = startDsp.Value + (ClipSeconds - ClipFadeOutSeconds) + StopIntoFadeSeconds;
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= stopAtDsp,
                "the dsp clock to reach a point well inside the clip's own fade-out", ClipSeconds);
            Assert.Less(player.GetVolume(), NearTargetThreshold,
                "Precondition: the clip's own fade-out must already be running when Stop() is called.");

            player.Stop();

            yield return new WaitForSeconds(1f);
            Assert.IsTrue(player.IsActive && player.IsPlaying,
                "A second after Stop() the running fade-out still has ~2s to go - Stop() must not cut it short.");

            yield return WaitForRecycle(player,
                "playback to end when the already-running fade-out does (~3s after Stop), not a fresh 6s fade later",
                RecycleBudgetSeconds);
        }

        [UnityTest]
        public IEnumerator Play_WithClipStartPosition_BeginsPlaybackPartwayIntoClip()
        {
            const float startPosition = 0.5f;
            AudioClip clip = NewClip(2f);
            AudioEntity entity = NewEntity("StartPosSfx", BroAudioType.SFX, clip);
            entity.Clips[0].StartPosition = startPosition;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            int expectedSample = Utility.GetSample(clip.frequency, startPosition);
            int actualSample = player.AudioSource.timeSamples;
            // timeSamples advances between Play() and this read; 0.25s slack still separates a regression to 0.
            int tolerance = Utility.GetSample(clip.frequency, 0.25f);

            Assert.LessOrEqual(Mathf.Abs(actualSample - expectedSample), tolerance,
                $"AudioSource.timeSamples right after playback starts ({actualSample}) should be near StartPosition * frequency ({expectedSample}), not 0.");
        }

        [UnityTest]
        public IEnumerator Play_WithClipEndPosition_EndsPlaybackBeforeClipLength()
        {
            // A per-frame poll of a DSP interval: one frame of a decoupled DSP clock overshoots by seconds.
            yield return RequireRealtimeAudioClock();

            const float clipLength = 4f;
            const float endPosition = 1.5f;
            AudioClip clip = NewClip(clipLength);
            AudioEntity entity = NewEntity("EndPosSfx", BroAudioType.SFX, clip);
            entity.Clips[0].EndPosition = endPosition;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            double dspStart = AudioSettings.dspTime;

            yield return WaitForRecycle(player,
                "playback to end before the clip's full length because of the EndPosition trim", clipLength + 1f);
            double elapsed = AudioSettings.dspTime - dspStart;
            const float expectedDuration = clipLength - endPosition;

            Assert.Less(elapsed, clipLength - 0.5, "EndPosition should make playback end well before the clip's full length.");
            // dspStart and the !IsActive poll each land a frame or two off, up to ~1/3s on a slow frame. Ignoring
            // EndPosition would measure 4s, three tolerances past 2.5s.
            Assert.AreEqual(expectedDuration, elapsed, 0.5,
                "Measured playback duration should match clip length minus EndPosition.");
        }

        [UnityTest]
        public IEnumerator Stop_SecondNonImmediateCall_WhileFadeOutInFlight_IsIgnored()
        {
            // 6s clip so the voice outlasts both outcomes below, including the regressed one at ~4.05s.
            SoundID id = NewSound("StopGuardSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.Stop(0.8f); // Starts an 0.8s fade-out immediately - Stop has no DSP wait gate.
            yield return WaitFrames(3);
            Assert.IsTrue(player.IsActive, "The 0.8s fade-out should still be in flight.");

            // The IsStopping guard should drop this; a regression starts a fresh 4s fade.
            player.Stop(4f);

            // The 0.8s fade ends ~0.85s, an un-ignored 4s ramp ~4.05s: both over a second from the 2.2s deadline.
            // Don't sample mid-ramp instead: a restarted fade re-bases on the current volume (Fader.SetTarget),
            // so a threshold would depend on which frame reads it.
            yield return WaitForRecycle(player,
                "the original 0.8s fade-out to finish (a timeout here does not by itself prove the second Stop(4f) call went through)", 2.2f);
        }

        [UnityTest]
        public IEnumerator Stop_WithImmediateFade_PassesGuardAndEndsPromptly()
        {
            // 5s clip so the voice outlasts the 3s ramp a regression would run to completion.
            SoundID id = NewSound("StopImmediateSfx", BroAudioType.SFX, NewClip(5f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.Stop(3f); // Starts a 3s fade-out.
            yield return WaitFrames(3);
            Assert.IsTrue(player.IsActive, "The 3s fade-out should still be in flight.");

            // FadeData.Immediate passes the IsStopping guard and replaces the in-flight StopControl with no fade.
            player.Stop(FadeData.Immediate);

            // The pre-empted 3s ramp would end ~3.05s, 1.85s past the deadline.
            yield return WaitForRecycle(player,
                "the immediate Stop to end playback promptly, well before the original 3s fade-out would have finished", 1.2f);
        }
    }
}