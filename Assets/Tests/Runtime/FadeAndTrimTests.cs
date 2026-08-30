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
    /// Inventory 2.4, 2.6, 2.10 (Docs/inventory/time-dependent.md): fade-in/fade-out (clip setting, explicit
    /// one-shot override, custom ease), clip StartPosition/EndPosition trims, and the Stop() re-entrancy guard.
    /// <para>
    /// Two clocks are mixed throughout this file, per the inventory doc: fade *progress* runs on the frame
    /// clock (Fader.Update accumulates Utility.GetDeltaTime()), while the wait-to-start-fading gate on a
    /// natural clip end is DSP-clocked (PlayControl: `while (dspTime < _playbackEndDspTime - fadeOut)`).
    /// A Stop(fadeOut) has no such gate - its fade starts immediately. Assertions here poll for state
    /// transitions and ranges rather than exact sample/frame counts, per that guidance.
    /// </para>
    /// </summary>
    public class FadeAndTrimTests : BroAudioTestFixture
    {
        // Mathf.Lerp + an easing curve is not sample-accurate, and GetVolume() is a live fade read - use
        // wide, meaning-carrying thresholds ("basically silent" / "basically at target") rather than exact values.
        private const float NearSilenceThreshold = 0.15f;
        private const float NearTargetThreshold = 0.95f;

        [UnityTest]
        public IEnumerator Play_WithClipFadeIn_RampsVolumeUpFromSilence()
        {
            const float fadeIn = 0.4f;
            AudioClip clip = NewClip(1.5f);
            AudioEntity entity = NewEntity("FadeInSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = fadeIn;
            SoundID id = IdOf(entity);

            List<float> samples = new List<float>();
            IAudioPlayer player = BroAudio.Play(id);
            player.OnUpdate(p => samples.Add(p.GetVolume()));

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            // SetupClipVolume snaps _clipVolume.Current to 0 before the fade-in coroutine starts ramping it up.
            Assert.Less(player.GetVolume(), NearSilenceThreshold, "Volume should start near silence when the clip has a FadeIn.");

            yield return WaitUntilOrTimeout(() => player.GetVolume() >= NearTargetThreshold,
                "the clip's own fade-in to reach full target volume", fadeIn + 1f);

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
            const float clipLength = 1.6f;
            const float fadeOut = 0.5f;
            AudioClip clip = NewClip(clipLength);
            AudioEntity entity = NewEntity("FadeOutSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeOut = fadeOut;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            yield return WaitFrames(2);
            Assert.GreaterOrEqual(player.GetVolume(), NearTargetThreshold,
                "With no FadeIn set, volume should already be at full target once playback starts.");

            // The wait-to-start-fading gate (dspTime < end - fadeOut) is DSP-clocked; the ramp itself runs on
            // the frame clock. Poll for the drop rather than computing the exact DSP gate boundary.
            yield return WaitUntilOrTimeout(() => player.GetVolume() < 0.5f,
                "the natural-end fade-out to begin ramping the volume down", clipLength + 0.3f);
            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end once the fade-out completes", fadeOut + 1f);
        }

        [UnityTest]
        public IEnumerator Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting()
        {
            const float clipFadeIn = 0.15f;
            const float overrideFadeIn = 0.6f;
            AudioClip clip = NewClip(1.2f);
            AudioEntity entity = NewEntity("FadeOverrideSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = clipFadeIn;
            SoundID id = IdOf(entity);

            IAudioPlayer firstPlayer = BroAudio.Play(id, overrideFadeIn);
            yield return WaitUntilOrTimeout(() => firstPlayer.IsPlaying, "first playback to start", 2f);

            // By now the clip's own 0.15s fade would already be done; the 0.6s explicit override should not be.
            yield return WaitDspSeconds(clipFadeIn + 0.1);
            Assert.Less(firstPlayer.GetVolume(), NearTargetThreshold,
                "The explicit fadeIn override should still be ramping well past the clip's own (shorter) FadeIn duration - FadeData.cs's one-shot Next override should have taken priority over the clip setting.");

            firstPlayer.Stop(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !firstPlayer.IsActive, "the first player to stop", 2f);

            IAudioPlayer secondPlayer = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => secondPlayer.IsPlaying, "second playback to start", 2f);

            // The override was consumed by TryGetOrConsumeOverride during the first play (FadeData.cs); this
            // play should fall back to only the clip's own short FadeIn.
            yield return WaitDspSeconds(clipFadeIn + 0.1);
            Assert.GreaterOrEqual(secondPlayer.GetVolume(), NearTargetThreshold,
                "Without a fresh override, the second play should already be at target using the clip's own short FadeIn - the one-shot override must not leak into a later play.");
        }

        [UnityTest]
        public IEnumerator SetFadeInEase_AndSetFadeOutEase_StillReachTargetAndComplete()
        {
            AudioClip clip = NewClip(2f);
            AudioEntity entity = NewEntity("EaseSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = 0.4f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            // Must be set in the same frame Play() was enqueued - before SoundManager.LateUpdate drains the
            // queue and PlayControl reads _fadeInData - mirroring the deferred-application pattern already
            // characterized for SetPitch in VolumePitchMixerTests.SetPitch_BeforePlaybackStarts_DefersFadeRatherThanSnapping.
            player.SetFadeInEase(Ease.OutCubic);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= NearTargetThreshold,
                "a fade-in with a custom ease to still reach its target", 1.5f);

            player.SetFadeOutEase(Ease.InCubic);
            player.Stop(0.4f);
            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "a fade-out with a custom ease to still complete", 1.5f);
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
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            int expectedSample = Utility.GetSample(clip.frequency, startPosition);
            int actualSample = player.AudioSource.timeSamples;
            // 0.1s of slack: timeSamples keeps advancing at clip.frequency/sec between AudioSource.Play() and
            // this read, and WaitUntilOrTimeout only guarantees IsPlaying became true at some point in the past frame.
            int tolerance = Utility.GetSample(clip.frequency, 0.1f);

            Assert.LessOrEqual(Mathf.Abs(actualSample - expectedSample), tolerance,
                $"AudioSource.timeSamples right after playback starts ({actualSample}) should be near StartPosition * frequency ({expectedSample}), not 0.");
        }

        [UnityTest]
        public IEnumerator Play_WithClipEndPosition_EndsPlaybackBeforeClipLength()
        {
            const float clipLength = 2f;
            const float endPosition = 0.8f;
            AudioClip clip = NewClip(clipLength);
            AudioEntity entity = NewEntity("EndPosSfx", BroAudioType.SFX, clip);
            entity.Clips[0].EndPosition = endPosition;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            double dspStart = AudioSettings.dspTime;

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end before the clip's full length because of the EndPosition trim", clipLength + 1f);
            double elapsed = AudioSettings.dspTime - dspStart;
            const float expectedDuration = clipLength - endPosition;

            Assert.Less(elapsed, clipLength - 0.3, "EndPosition should make playback end well before the clip's full length.");
            // 0.3s tolerance: the DSP-scheduled end (Utility.GetPlayableDuration, computed once at play start)
            // is exact, but our own dspStart sample and the !IsActive poll both land a frame or two off the
            // true boundary.
            Assert.AreEqual(expectedDuration, elapsed, 0.3,
                "Measured playback duration should match clip length minus EndPosition.");
        }

        [UnityTest]
        public IEnumerator Stop_SecondNonImmediateCall_WhileFadeOutInFlight_IsIgnored()
        {
            SoundID id = NewSound("StopGuardSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            player.Stop(0.8f); // Starts an 0.8s fade-out immediately - Stop has no DSP wait gate.
            yield return WaitFrames(3);
            Assert.IsTrue(player.IsActive, "The 0.8s fade-out should still be in flight.");

            // AudioPlayer.Playback.cs's Stop() guard: `if (IsStopping && !Mathf.Approximately(overrideFade,
            // FadeData.Immediate)) return;`. While a fade-out is already in flight, a second non-immediate
            // Stop is silently dropped rather than restarting RestartCoroutine on a fresh ramp. If this ever
            // regresses, a fresh 2s fade would begin here and the wait below would time out.
            player.Stop(2f);

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "the ORIGINAL 0.8s fade-out to finish on its own schedule (the second Stop(2f) call should have been ignored)", 1.3f);
        }

        [UnityTest]
        public IEnumerator Stop_WithImmediateFade_PassesGuardAndEndsPromptly()
        {
            SoundID id = NewSound("StopImmediateSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            player.Stop(1f); // Starts a 1s fade-out.
            yield return WaitFrames(3);
            Assert.IsTrue(player.IsActive, "The 1s fade-out should still be in flight.");

            // Unlike a second non-immediate Stop, an explicit FadeData.Immediate (0f) passes the IsStopping
            // guard (`!Mathf.Approximately(overrideFade, FadeData.Immediate)` is false for 0f), so
            // RestartCoroutine cancels the in-flight fade and StopControl ends playback with no fade instead
            // of waiting out the original 1s ramp.
            player.Stop(FadeData.Immediate);

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "the immediate Stop to end playback promptly, well before the original 1s fade-out would have finished", 0.3f);
        }
    }
}