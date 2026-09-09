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
            // 1.2s fade (was 0.4s): the near-silence check below runs on the same frame IsPlaying is
            // observed true, which can already be a frame or more into the fade - at 0.4s a single capped
            // hitch frame (Time.maximumDeltaTime ~0.333s) was enough to push the read past NearSilenceThreshold.
            const float fadeIn = 1.2f;
            AudioClip clip = NewClip(3f);
            AudioEntity entity = NewEntity("FadeInSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = fadeIn;
            SoundID id = IdOf(entity);

            List<float> samples = new List<float>();
            IAudioPlayer player = BroAudio.Play(id);
            player.OnUpdate(p => samples.Add(p.GetVolume()));

            yield return WaitForPlaybackStart(player);

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
            // The IsPlaying assertion below straddles both clocks: it reads a DSP-driven voice at an instant
            // the frame-clocked ramp chooses, so the two have to run at the same rate. On a machine with no
            // audio output device the voice is long finished by the time the ramp crosses the threshold, and
            // this test would go red where the suite deliberately stays green.
            yield return RequireRealtimeAudioClock();

            // 3s clip with a 1.5s fade-out (was 1.6s / 0.5s). The default fade-out ease is OutSine
            // (RuntimeSetting.FactorySettings.DefaultFadeOutEase - no BroRuntimeSetting asset ships in this
            // project, so the factory values apply), i.e. volume = 1 - sin(t/T * pi/2), which crosses the 0.5
            // poll threshold exactly a third of the way in (sin(pi/6) = 0.5). At 1.5s that is 0.5s into the
            // ramp with a full second of audio still to play when the assertions below run; at the old 0.5s
            // fade the crossing sat 0.17s in with only 0.33s of audio left - one capped hitch frame
            // (Time.maximumDeltaTime ~0.333s).
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

            // The wait-to-start-fading gate (dspTime < end - fadeOut) is DSP-clocked; the ramp itself runs on
            // the frame clock. Poll for the drop rather than computing the exact DSP gate boundary.
            // IsActive is part of the condition because AudioPlayerInstanceWrapper's IAudioPlayer.GetVolume()
            // returns 0f for a recycled instance: without it the poll is satisfied by the natural end itself,
            // which is the one outcome this test must not accept as evidence of a ramp. IsActive and
            // IsPlaying both resolve through IsAvailable(false), so neither logs the recycled-player warning.
            yield return WaitUntilOrTimeout(() => player.IsActive && player.GetVolume() < 0.5f,
                "the natural-end fade-out to ramp a still-live player below half volume (timing out here means no such drop was ever seen while the player was active, which includes playback simply ending with no ramp at all)",
                clipLength + 1f);
            Assert.IsTrue(player.IsPlaying,
                "The drop below half volume must be observed while the player is still playing, not after playback has already ended.");

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end once the fade-out completes", fadeOut + 1f);
        }

        [UnityTest]
        public IEnumerator Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting()
        {
            // A 4s override on a 5s clip (was 0.6s on 1.2s), with both halves decided at observationTime.
            // The default fade-in ease is InCubic (RuntimeSetting.FactorySettings.DefaultFadeInEase), i.e.
            // volume = t^3, so a fade only crosses NearTargetThreshold at 98.3% of its length (0.95^(1/3)):
            // the override cannot reach target before ~3.93s, while the clip's own 0.15s fade is there in
            // 0.15s.
            const float clipFadeIn = 0.15f;
            const float overrideFadeIn = 4f;
            const float observationTime = 1.2f;
            AudioClip clip = NewClip(5f);
            AudioEntity entity = NewEntity("FadeOverrideSfx", BroAudioType.SFX, clip);
            entity.Clips[0].FadeIn = clipFadeIn;
            SoundID id = IdOf(entity);

            IAudioPlayer firstPlayer = BroAudio.Play(id, overrideFadeIn);
            yield return WaitForPlaybackStart(firstPlayer, "first playback to start");

            // By now the clip's own 0.15s fade would long be done; the 4s explicit override needs ~3.93s, so
            // this read sits 2.7s clear of the point where it could start failing on a correct build (was
            // 0.25s against a 0.6s override, whose ramp crossed at 0.59s - 0.34s of slack). A hitch only ever
            // makes WaitForSeconds overshoot, and overshoot is the safe direction for the other side of the
            // window: the further past 0.15s this lands, the more certain a regression that used the clip
            // setting reads at target.
            yield return new WaitForSeconds(observationTime);
            Assert.Less(firstPlayer.GetVolume(), NearTargetThreshold,
                "The explicit fadeIn override should still be ramping well past the clip's own (shorter) FadeIn duration - FadeData.cs's one-shot Next override should have taken priority over the clip setting.");

            firstPlayer.Stop(FadeData.Immediate);
            yield return WaitForRecycle(firstPlayer, "the first player to stop");

            IAudioPlayer secondPlayer = BroAudio.Play(id);
            yield return WaitForPlaybackStart(secondPlayer, "second playback to start");

            // The override was consumed by TryGetOrConsumeOverride during the first play (FadeData.cs); this
            // play should fall back to only the clip's own short FadeIn. Poll for the target instead of
            // sampling at a fixed instant (was a bare 0.25s wait then an assert - 0.10s of slack over a 0.15s
            // frame-clock fade): the clip's own fade is at target at ~0.15s, so observationTime leaves ~1s
            // for frame-clock lag, while a leaked 4s override would not get there for ~3.93s and so still
            // times out by 2.7s.
            yield return WaitUntilOrTimeout(() => secondPlayer.GetVolume() >= NearTargetThreshold,
                "the second play to reach target volume on the clip's own short FadeIn - timing out here means the one-shot override leaked into a later play",
                observationTime);
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

            yield return WaitForPlaybackStart(player);
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
            yield return WaitForPlaybackStart(player);

            int expectedSample = Utility.GetSample(clip.frequency, startPosition);
            int actualSample = player.AudioSource.timeSamples;
            // 0.25s of slack (was 0.1s, thinner than a single capped hitch frame at ~0.333s):
            // timeSamples keeps advancing at clip.frequency/sec between AudioSource.Play() and this read,
            // and WaitUntilOrTimeout only guarantees IsPlaying became true at some point in the past frame.
            // Still decisive against a 0.5s StartPosition - a regression to 0 would be off by 0.5s, not 0.25s.
            int tolerance = Utility.GetSample(clip.frequency, 0.25f);

            Assert.LessOrEqual(Mathf.Abs(actualSample - expectedSample), tolerance,
                $"AudioSource.timeSamples right after playback starts ({actualSample}) should be near StartPosition * frequency ({expectedSample}), not 0.");
        }

        [UnityTest]
        public IEnumerator Play_WithClipEndPosition_EndsPlaybackBeforeClipLength()
        {
            // This measures a DSP interval with a per-frame poll, so the two clocks have to run at the same
            // rate: on a machine with no audio output device a single frame carries the DSP clock seconds
            // past the scheduled end, and the elapsed reading blows through the tolerance below. Pre-existing
            // rather than introduced by the widened window, but red where the suite means to stay green.
            yield return RequireRealtimeAudioClock();

            // 4s clip trimmed by 1.5s (was 2s trimmed by 0.8s), so the measured duration is 2.5s and the
            // widened tolerance below is a fifth of it rather than a quarter.
            const float clipLength = 4f;
            const float endPosition = 1.5f;
            AudioClip clip = NewClip(clipLength);
            AudioEntity entity = NewEntity("EndPosSfx", BroAudioType.SFX, clip);
            entity.Clips[0].EndPosition = endPosition;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            double dspStart = AudioSettings.dspTime;

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end before the clip's full length because of the EndPosition trim", clipLength + 1f);
            double elapsed = AudioSettings.dspTime - dspStart;
            const float expectedDuration = clipLength - endPosition;

            Assert.Less(elapsed, clipLength - 0.5, "EndPosition should make playback end well before the clip's full length.");
            // 0.5s tolerance (was 0.3s, under a single capped hitch frame at Time.maximumDeltaTime ~0.333s):
            // the DSP-scheduled end (Utility.GetPlayableDuration, computed once at play start) is exact, but
            // our own dspStart sample and the per-frame !IsActive poll both land a frame or two off the true
            // boundary, and one hitch frame makes each of those a third of a second. Still decisive: a
            // regression that ignored EndPosition entirely would measure 4s - three whole tolerances past
            // the 2.5s expected here.
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

            // AudioPlayer.Playback.cs's Stop() guard: `if (IsStopping && !Mathf.Approximately(overrideFade,
            // FadeData.Immediate)) return;`. While a fade-out is already in flight, a second non-immediate
            // Stop is silently dropped rather than restarting RestartCoroutine on a fresh ramp. If this ever
            // regresses, a fresh 4s fade would begin here and the wait below would time out.
            player.Stop(4f);

            // A 4s second fade and a 2.2s deadline (was 2s and 1.8s, which left only 0.2s between the
            // deadline and where a regressed ramp lands). The two outcomes are now more than a second clear
            // of the deadline in both directions: the original 0.8s fade ends ~0.85s after it started,
            // leaving ~1.35s for hitch frames and frame-clock lag, while an un-ignored 4s ramp would not end
            // until ~4.05s, 1.85s past the deadline. A timeout still means either the original fade stalled
            // or the guard regressed - not proof of either on its own, hence the hedged message.
            // Sampling the ramp mid-flight was considered and rejected in favour of this state transition:
            // a restarted fade inherits the volume already reached (Fader.SetTarget re-bases _origin on
            // Current, ~0.9 after the three frames above) rather than starting from 1, so a mid-flight
            // threshold has to be derived from that moving origin and read on a particular frame, while
            // !IsActive is a one-way transition with more than a second of margin on either side.
            yield return WaitUntilOrTimeout(() => !player.IsActive,
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

            // Unlike a second non-immediate Stop, an explicit FadeData.Immediate (0f) passes the IsStopping
            // guard (`!Mathf.Approximately(overrideFade, FadeData.Immediate)` is false for 0f), so
            // RestartCoroutine replaces the in-flight StopControl and the new one ends playback with no fade
            // instead of waiting out the original 3s ramp. The fader's own coroutine is stopped one step
            // later, by EndPlaying's ResetVolume completing it.
            player.Stop(FadeData.Immediate);

            // A 3s original fade and a 1.2s deadline (was 1s and 0.3s - under one capped hitch frame at
            // Time.maximumDeltaTime ~0.333s). A zero-length fade makes PlaybackPreference.TryGetFadeOut
            // return false, so StopControl skips the fade block entirely and reaches EndPlaying before
            // StartCoroutine even returns; the whole deadline is slack. The ramp it pre-empted would not
            // have finished until ~3.05s, which is 1.85s past the deadline.
            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "the immediate Stop to end playback promptly, well before the original 3s fade-out would have finished", 1.2f);
        }
    }
}