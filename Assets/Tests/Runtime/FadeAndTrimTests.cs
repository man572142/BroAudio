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
    /// Fade-in/fade-out (clip setting, explicit one-shot override, custom ease), Stop()/Pause() falling back to
    /// the clip's authored FadeOut, clip StartPosition/EndPosition trims, and the Stop() re-entrancy guards. See
    /// Docs/inventory/time-dependent.md.
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
        // Mathf.Lerp + an easing curve is not sample-accurate, and GetVolume() is a live fade read - use a
        // wide, meaning-carrying threshold ("basically at target") rather than an exact value.
        private const float NearTargetThreshold = 0.95f;

        [UnityTest]
        public IEnumerator Play_WithClipFadeIn_RampsVolumeUpFromSilence()
        {
            // The near-silence check below runs on the frame IsPlaying is first observed true, which can already
            // be a slow frame into the fade, so the fade must be long enough that one frame can't push the read
            // past AuthoredProduct's near-silence band.
            //
            // ClipVolume/MasterVolume are authored off their shared default of 1 (TestAudioLibrary.CreateEntityWithVolume)
            // so the ramp's target is the composed product, not full volume - this is also the suite's only test
            // proving the fade-in ramp shape (monotonic, multi-frame via OnUpdate) and the authored composition
            // target land on the same player at once.
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

            // Would this pass if SetupClipVolume's multiplication were deleted, leaving the fade's target at full
            // volume (1) or at either single factor (0.4 or 0.5) alone? No - only the real product (0.2) satisfies
            // both this assertion and the WaitUntilOrTimeout above.
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

            // The default fade-out ease is OutSine (RuntimeSetting.FactorySettings.DefaultFadeOutEase - the
            // base fixture resets RuntimeSetting to its factory values before every test), i.e. volume =
            // 1 - sin(t/T * pi/2), which crosses the 0.5 poll threshold exactly a third of the way in
            // (sin(pi/6) = 0.5). A 1.5s fade on a 3s clip puts that crossing 0.5s into the ramp with a full
            // second of audio still to play when the assertions below run.
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

            yield return WaitForRecycle(player,
                "playback to end once the fade-out completes", fadeOut + 1f);
        }

        [UnityTest]
        public IEnumerator Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting()
        {
            // A 4s override on a 5s clip, with both halves decided at observationTime.
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
            // this read sits 2.7s clear of the point where it could start failing on a correct build. A hitch
            // only ever makes WaitForSeconds overshoot, and overshoot is the safe direction for the other side
            // of the window: the further past 0.15s this lands, the more certain a regression that used the
            // clip setting reads at target.
            yield return new WaitForSeconds(observationTime);
            Assert.Less(firstPlayer.GetVolume(), NearTargetThreshold,
                "The explicit fadeIn override should still be ramping well past the clip's own (shorter) FadeIn duration - FadeData.cs's one-shot Next override should have taken priority over the clip setting.");

            firstPlayer.Stop(FadeData.Immediate);
            yield return WaitForRecycle(firstPlayer, "the first player to stop");

            IAudioPlayer secondPlayer = BroAudio.Play(id);
            yield return WaitForPlaybackStart(secondPlayer, "second playback to start");

            // The override was consumed by TryGetOrConsumeOverride during the first play (FadeData.cs); this
            // play should fall back to only the clip's own short FadeIn. Poll for the target rather than
            // sampling at a fixed instant: the clip's own fade is at target at ~0.15s, so observationTime
            // leaves ~1s for frame-clock lag, while a leaked 4s override would not get there for ~3.93s and
            // so still times out by 2.7s.
            yield return WaitUntilOrTimeout(() => secondPlayer.GetVolume() >= NearTargetThreshold,
                "the second play to reach target volume on the clip's own short FadeIn - timing out here means the one-shot override leaked into a later play",
                observationTime);
        }

        // A custom ease has to change the shape of the fade, not merely let it finish: each half samples its fade
        // a third of the way in, where the chosen ease and the factory default sit far apart. Fade-in: OutCubic
        // reads 1 - (2/3)^3 = 0.70 there, the factory InCubic 0.04. Fade-out: InCubic keeps 1 - (1/3)^3 = 0.96,
        // the factory OutSine 0.5. Frame-clock slop moves the sample to anywhere between roughly 0.22 and 0.44 of
        // the fade, which still leaves each threshold well clear of both curves.
        // <para>
        // Both fades are explicit overrides - Play(id, fadeIn) and Stop(fadeOut) - because that is where these
        // setters apply: PlaybackPreference.TryGetOrConsumeOverride hands back the setter's ease only with a
        // pending or base override, and otherwise keeps the factory ease even when the clip's own FadeIn or
        // FadeOut is what runs.
        // </para>
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
            // Must be set in the same frame Play() was enqueued - before SoundManager.LateUpdate drains the
            // queue and PlayControl reads _fadeInData - mirroring the deferred-application pattern already
            // characterized for SetPitch in VolumePitchMixerTests.SetPitch_BeforePlaybackStarts_DefersFadeRatherThanSnapping.
            player.SetFadeInEase(Ease.OutCubic);

            yield return WaitForPlaybackStart(player);
            yield return new WaitForSeconds(SampleSeconds);
            Assert.Greater(player.GetVolume(), FadeInFloor,
                "A third of the way into an OutCubic fade-in the volume should already be well up - a read near 0 means the factory InCubic ran instead.");
            // All the way to the target, so the fade-out below starts from full volume and its sample reads
            // against the plain curve.
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

        // The counterpart of the test above, on the fades a plain Play()/Stop() actually runs: the clip's own
        // authored FadeIn and FadeOut. PlaybackPreference.TryGetOrConsumeOverride starts from the ease it is
        // handed - RuntimeSetting's DefaultFadeInEase / DefaultFadeOutEase - and only swaps in the FadeData's
        // ease (the one SetFadeInEase / SetFadeOutEase write) when an override or a base fade is pending. With
        // neither, the setters are silently ignored. Same sample points and curves as above, mirrored: a third of
        // the way in, the factory InCubic fade-in reads 0.04 where the requested OutCubic would read 0.70, and the
        // factory OutSine fade-out reads 0.5 where the requested InCubic would keep 0.96. Across the frame-clock
        // slop (roughly 0.22 to 0.44 of the fade) the factory fade-in stays under 0.09 and the factory fade-out
        // under 0.67, while the requested curves stay above 0.53 and 0.91, so each ceiling separates them.
        // <para>
        // Characterizes TEST_FINDINGS #70: both ceilings pin the ignored setter. A fix that makes the setters
        // shape the clip's own fades turns both asserts red.
        // </para>
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

        /// <summary>
        /// The clip's authored FadeOut for the Stop()/Pause() tests below. Sampled a second in, the factory
        /// fade-out ease (OutSine) reads 0.5 - a second clear of both ends of the fade.
        /// </summary>
        private const float AuthoredFadeOutSeconds = 3f;

        // Stop() with no argument resolves FadeData.UseClipSetting: StopControl finds no override, so
        // TryGetFadeOut falls back to the clip's authored FadeOut. Every other Stop pin in the suite passes a
        // fade explicitly, which never reaches that fallback.
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

        // Pause() takes the same fallback as Stop(): StopControl runs the clip's authored FadeOut before it
        // reaches AudioSource.Pause(). UnPause(0f) then brings the player back at full volume from where it froze.
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

        // StopControl's don't-double-fade branch: when Stop() arrives while the clip's own end-of-clip fade-out
        // is already running, it waits that fade out instead of starting a fresh one. So playback ends when the
        // natural fade would have, not a whole FadeOut after the Stop call.
        // <para>
        // A 6s FadeOut on a 7s clip opens the natural fade 1s in; Stop() lands 3s into it. Waiting the running
        // fade out ends playback ~3s after the call; a restarted fade would take the full 6s. The budget sits
        // 1.5s from each, and the check a second after the call rules out a Stop that cut the fade short.
        // </para>
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
            // timeSamples keeps advancing at clip.frequency/sec between AudioSource.Play() and this read,
            // and WaitUntilOrTimeout only guarantees IsPlaying became true at some point in the past frame.
            // 0.25s of slack is still decisive against a 0.5s StartPosition - a regression to 0 would be off
            // by 0.5s, not 0.25s.
            int tolerance = Utility.GetSample(clip.frequency, 0.25f);

            Assert.LessOrEqual(Mathf.Abs(actualSample - expectedSample), tolerance,
                $"AudioSource.timeSamples right after playback starts ({actualSample}) should be near StartPosition * frequency ({expectedSample}), not 0.");
        }

        [UnityTest]
        public IEnumerator Play_WithClipEndPosition_EndsPlaybackBeforeClipLength()
        {
            // This measures a DSP interval with a per-frame poll, which one frame of a decoupled DSP clock
            // would overshoot by seconds.
            yield return RequireRealtimeAudioClock();

            // 4s clip trimmed by 1.5s, so the measured duration is 2.5s and the tolerance below is a fifth of it.
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
            // The DSP-scheduled end (Utility.GetPlayableDuration, computed once at play start) is exact, but
            // our own dspStart sample and the per-frame !IsActive poll both land a frame or two off the true
            // boundary, and one slow frame makes each of those a third of a second. Still decisive: a
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

            // A 4s second fade and a 2.2s deadline keep both outcomes more than a second clear of the deadline:
            // the original 0.8s fade ends ~0.85s after it started, while an un-ignored 4s ramp would not end
            // until ~4.05s. A timeout means either the original fade stalled or the guard regressed - not
            // proof of either on its own, hence the hedged message.
            // Don't sample the ramp mid-flight instead: a restarted fade inherits the volume already reached
            // (Fader.SetTarget re-bases _origin on Current) rather than starting from 1, so a mid-flight
            // threshold would depend on that moving origin and on which frame reads it.
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

            // Unlike a second non-immediate Stop, an explicit FadeData.Immediate (0f) passes the IsStopping
            // guard (`!Mathf.Approximately(overrideFade, FadeData.Immediate)` is false for 0f), so
            // RestartCoroutine replaces the in-flight StopControl and the new one ends playback with no fade
            // instead of waiting out the original 3s ramp. The fader's own coroutine is stopped one step
            // later, by EndPlaying's ResetVolume completing it.
            player.Stop(FadeData.Immediate);

            // A zero-length fade makes PlaybackPreference.TryGetFadeOut return false, so StopControl skips the
            // fade block entirely and reaches EndPlaying before StartCoroutine even returns; the whole 1.2s
            // deadline is slack. The 3s ramp it pre-empted would not have finished until ~3.05s, which is
            // 1.85s past the deadline.
            yield return WaitForRecycle(player,
                "the immediate Stop to end playback promptly, well before the original 3s fade-out would have finished", 1.2f);
        }
    }
}