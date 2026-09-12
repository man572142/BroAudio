using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Utility.GetDeltaTime() (Utility.cs:44-52) is the single clock behind every fade, pitch tween,
    /// scheduled start and effect automation in this codebase: it reads RuntimeSetting.UpdateMode and
    /// returns Time.unscaledDeltaTime for AudioMixerUpdateMode.UnscaledTime, Time.deltaTime otherwise.
    /// UnscaledTime exists so a fade can keep running while the game is paused (Time.timeScale == 0);
    /// before this file, that branch appeared only in comments (three callers name it) and in zero
    /// assertions - deleting it silently freezes every fade in a paused game.
    /// <para>
    /// The discriminating scenario is Time.timeScale == 0: a fade must still progress under
    /// AudioMixerUpdateMode.UnscaledTime and must NOT progress under the factory-default
    /// AudioMixerUpdateMode.Normal (RuntimeSetting.FactorySettings.UpdateMode, RuntimeSetting.cs:66).
    /// Neither half proves the branch alone - a GetDeltaTime that always returned unscaled time would
    /// still pass the UnscaledTime half, and one that always returned scaled time would still pass the
    /// Normal half - so both tests below assert their pair of outcomes and must be read together.
    /// </para>
    /// <para>
    /// Both tests exercise two independent call sites so this isn't just pinning FaderModule: the clip's
    /// own FadeIn (Fader.Update, FaderModule.cs:116, observed through IAudioPlayer.GetVolume() exactly as
    /// FadeAndTrimTests does) and SoundManager's master-volume ramp (SetMasterVolume's own local
    /// coroutine - not the Fader class at all - SoundManager.cs:260, the non-WebGL branch that is the one
    /// actually compiled into an Editor/PlayMode run; its WebGL-only twin at :216 is `#if UNITY_WEBGL`'d
    /// out here and untested by this file). Observed through the mixer's exposed Master parameter exactly
    /// as VolumePitchMixerTests does. A regression that hardcoded the wrong Time.*deltaTime at only one of
    /// these two call sites would be invisible to a test that checked just the other.
    /// </para>
    /// <para>
    /// Deliberately NOT gated on RequireRealtimeAudioClock: that gate exists for state that rides the DSP
    /// clock (BroAudioTestFixture.WaitDspSeconds's own doc comment says so explicitly), and every value
    /// asserted here - Fader.Current and the mixer's Master float - is written from the frame clock
    /// (Utility.GetDeltaTime()) by a plain per-frame coroutine, never from AudioSettings.dspTime. Gating
    /// this file on the DSP-clock check would only add an unrelated Assert.Ignore path.
    /// </para>
    /// </summary>
    public class UpdateModeClockTests : BroAudioTestFixture
    {
        // Same wide, meaning-carrying thresholds FadeAndTrimTests uses for a live Fader read rather than
        // exact values - Mathf.Lerp plus an easing curve is not sample-accurate.
        private const float NearSilenceThreshold = 0.15f;
        private const float NearTargetThreshold = 0.95f;

        // One duration shared by the clip's FadeIn and the master-volume fade so a single wait window
        // below covers both ramps. A 6s clip comfortably outlasts every window used in this file (the
        // widest is the frozen test's 3s realtime wait), so natural playback end never intervenes and
        // resets _clipVolume out from under the assertion.
        private const float FadeDuration = 1f;
        private const float ClipLength = 6f;

        // -20dB (Mathf.Log10(0.1) * 20 = -20), a large, unambiguous drop from the 0dB baseline (full
        // volume) established below - nowhere near mixer round-trip noise.
        private const float MasterFadeTargetVolume = 0.1f;

        // Drain budget for a master fade left in flight by these tests (see the teardown below). The
        // longest one this file can leave is FadeDuration, and it only has to run down once timeScale is
        // restored, so 5s is several times the worst case - wide enough that a slow frame cannot trip it,
        // tight enough to fail loudly rather than hang the run if a fade never stops.
        private const float MasterDrainTimeout = 5f;

        // Consecutive identical readings that count as "no coroutine is writing this any more". One frame
        // is not enough: a fade's own ease can land two adjacent frames on the same float near the end of
        // its curve, which would read as settled while the ramp is still going.
        private const int SteadyFrameCount = 5;

        /// <summary>
        /// Time.timeScale is global process state BroAudioTestFixture does not touch (its JSON
        /// snapshot/restore only covers the RuntimeSetting asset, which does include UpdateMode - a plain
        /// public field JsonUtility serializes like any other). A test here that pauses the game must
        /// restore timeScale itself or every later PlayMode test in the run hangs on its first
        /// WaitForSeconds. Declared here as its own [UnityTearDown] rather than inside the test bodies:
        /// NUnit runs a derived class's [UnityTearDown] before the base class's (confirmed by the
        /// identical pattern in AudioEffectTests.AudioEffectTearDown, whose own doc comment says so), and
        /// unconditionally - a failed assertion above cannot skip it.
        /// </summary>
        [UnityTearDown]
        public IEnumerator RestoreTimeScaleAndDrainTheMasterFade()
        {
            Time.timeScale = 1f;
            yield return null;

            // Restoring timeScale is not enough on its own: a master fade this fixture froze outlives the
            // fixture's own reset and resumes the moment the line above unpauses it.
            //
            // BroAudioTearDown resets with BroAudio.SetVolume(FullVolume, 0f), and that cannot cancel a
            // running master fade. SetMasterVolume (SoundManager.cs:234-250) only calls RestartCoroutine -
            // the one path that stops the previous coroutine - on its `fadeTime != 0f` branch; the zero
            // branch just writes the parameter once and leaves the coroutine running. And a fade frozen at
            // timeScale 0 never even gets that far: with GetDeltaTime pinned at 0 the coroutine rewrites
            // Master with its *starting* value every frame, so Master still reads exactly full volume and
            // the `currentVol == targetVol` early return (:238) skips the write entirely.
            //
            // The stale coroutine therefore survived into the next fixture and kept moving Master while
            // that fixture asserted on it - which is exactly how this file first turned
            // VolumePitchMixerTests.SetVolume_Master_WritesDirectlyToMixerAndNeverEntersLinearProduct red
            // (recorded as finding #51). Draining it here is the fixture's own mess to clean up.
            //
            // Waiting for the reading to stop moving, rather than for a particular value, is deliberate: a
            // live fade rewrites Master every frame (:253-263), so a steady reading is the observable end
            // of the coroutine whether it completed, was never started, or is still mid-ramp - and no
            // branch of this file has to predict which.
            float deadline = Time.realtimeSinceStartup + MasterDrainTimeout;
            float lastDb = float.MinValue;
            int steadyFrames = 0;
            while (steadyFrames < SteadyFrameCount)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline,
                    "Timed out waiting for the master volume to stop being written by an in-flight fade. " +
                    "Something is still ramping it, and leaving it running would corrupt every later fixture.");

                yield return null;
                if (!SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float db))
                {
                    continue;
                }

                steadyFrames = Mathf.Approximately(db, lastDb) ? steadyFrames + 1 : 0;
                lastDb = db;
            }
        }

        [UnityTest]
        public IEnumerator Fade_WithUnscaledTimeModeAndPausedGame_StillProgressesToCompletion()
        {
            SoundManager.Instance.Setting.UpdateMode = AudioMixerUpdateMode.UnscaledTime;
            Time.timeScale = 0f;

            // Known baseline before measuring: fadeTime 0 takes SetMasterVolume's immediate SafeSetFloat
            // branch (SoundManager.cs:247-249), which isn't gated by GetDeltaTime at all, so this lands
            // regardless of the pause above.
            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float baselineDb),
                "Master mixer parameter should be readable once SoundManager has bootstrapped.");
            Assert.AreEqual(0f, baselineDb, DecibelTolerance, "Master should already read full volume (0dB) after the explicit reset above.");

            AudioEntity entity = NewEntity("PausedFadeInSfx", BroAudioType.SFX, NewClip(ClipLength));
            entity.Clips[0].FadeIn = FadeDuration;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            // Started in the same frame as Play(): both ramps begin at effectively the same real time, so
            // one wait window below can cover either.
            BroAudio.SetVolume(MasterFadeTargetVolume, FadeDuration);

            yield return WaitForPlaybackStart(player);
            // SetupClipVolume snaps _clipVolume.Current to 0 before the fade-in coroutine starts ramping
            // it up (same mechanism FadeAndTrimTests.Play_WithClipFadeIn_RampsVolumeUpFromSilence checks).
            Assert.Less(player.GetVolume(), NearSilenceThreshold,
                "Clip fade-in should start near silence regardless of UpdateMode.");

            // fadeDuration + 1.5s: real (unscaled) frame time keeps advancing every rendered frame even
            // though Time.timeScale is 0 (yield return null still steps a frame - only WaitForSeconds
            // would hang), so a working UnscaledTime branch finishes this 1s fade in about one real
            // second regardless of the pause. 1.5s of slack absorbs test-runner frame-time noise the same
            // way FadeAndTrimTests pads its own fade waits.
            const float fadeTimeout = FadeDuration + 1.5f;
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= NearTargetThreshold,
                "the clip's own fade-in to reach target while the game is paused under UnscaledTime - " +
                "timing out here means Utility.GetDeltaTime() froze it, i.e. the UnscaledTime branch is gone or unreachable",
                fadeTimeout);

            float targetDb = MasterFadeTargetVolume.ToDecibel();
            yield return WaitUntilOrTimeout(
                () => SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float db) && db <= targetDb + DecibelTolerance,
                "SoundManager's master-volume ramp (a separate coroutine from FaderModule) to reach target " +
                "while paused under UnscaledTime - this call site has its own Utility.GetDeltaTime() call " +
                "(SoundManager.cs:260) that a fix to FaderModule alone would not touch",
                fadeTimeout);
        }

        [UnityTest]
        public IEnumerator Fade_WithNormalModeAndPausedGame_FreezesAndNeverProgresses()
        {
            // Explicit even though Normal is RuntimeSetting.FactorySettings.UpdateMode (RuntimeSetting.cs:66,
            // already the ambient value here) - documents intent rather than relying on that default holding.
            SoundManager.Instance.Setting.UpdateMode = AudioMixerUpdateMode.Normal;
            Time.timeScale = 0f;

            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float baselineDb),
                "Master mixer parameter should be readable once SoundManager has bootstrapped.");
            Assert.AreEqual(0f, baselineDb, DecibelTolerance, "Master should already read full volume (0dB) after the explicit reset above.");

            AudioEntity entity = NewEntity("FrozenFadeInSfx", BroAudioType.SFX, NewClip(ClipLength));
            entity.Clips[0].FadeIn = FadeDuration;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            BroAudio.SetVolume(MasterFadeTargetVolume, FadeDuration);

            yield return WaitForPlaybackStart(player);
            float clipVolumeAtStart = player.GetVolume();
            Assert.Less(clipVolumeAtStart, NearSilenceThreshold,
                "Clip fade-in should start near silence regardless of UpdateMode.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float masterDbAtStart));

            // A REALTIME interval comfortably longer than the 1s fade: fadeDuration * 2 + 1s = 3s. With
            // Time.timeScale == 0, Time.deltaTime is 0 on every frame (well-established Unity pause
            // behaviour, not something this codebase controls), so Utility.GetDeltaTime() under Normal
            // mode should accumulate nothing for the whole 3s and both ramps should still read exactly
            // their starting value - if this is wrong, a 1s fade would instead be long complete by the
            // time this wait returns. Must be WaitForSecondsRealtime, not WaitForSeconds: the latter never
            // returns while timeScale is 0 (it is itself scaled-time-driven), which is exactly the trap
            // this suite's own fixture warns about.
            yield return new WaitForSecondsRealtime(FadeDuration * 2f + 1f);

            // Fader.Update(), when _elapsedTime never advances, keeps recomputing
            // Mathf.Lerp(origin, target, ease(0 / fadeTime)) every frame - the same value each time, since
            // every standard Ease.*(0) is 0 (EaseExtension.cs) - so an exact-equality check (not just "below
            // NearSilenceThreshold") is the tightest, most direct proof that literally zero progress was made.
            Assert.AreEqual(clipVolumeAtStart, player.GetVolume(), 0.001f,
                "Clip fade-in should not have progressed at all while the game is paused under Normal " +
                "UpdateMode - a regression that returned unscaled time unconditionally would show movement here.");

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float masterDbAfterWait));
            Assert.AreEqual(masterDbAtStart, masterDbAfterWait, DecibelTolerance,
                "SoundManager's master-volume ramp should not have progressed either while paused under " +
                "Normal UpdateMode - this call site has its own Utility.GetDeltaTime() call (SoundManager.cs:260) " +
                "that a fix to FaderModule alone would not touch.");

            // Non-vacuity, read together with Fade_WithUnscaledTimeModeAndPausedGame_StillProgressesToCompletion
            // above: this test alone would still pass if GetDeltaTime() always returned Time.deltaTime
            // regardless of UpdateMode (Normal already expects that value), just as the UnscaledTime test
            // alone would still pass if GetDeltaTime() always returned Time.unscaledDeltaTime. Only the pair,
            // sharing the same paused-game setup and differing solely in UpdateMode, proves the branch in
            // Utility.GetDeltaTime() actually reads RuntimeSetting.UpdateMode rather than ignoring it.
        }
    }
}