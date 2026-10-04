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
    /// Pins Utility.GetDeltaTime()'s UpdateMode branch at timeScale 0: fades progress under UnscaledTime and
    /// freeze under Normal. Each test alone passes if the branch is hardcoded either way; only the pair
    /// proves it. Both check two call sites with their own GetDeltaTime() call - a clip FadeIn (Fader) and
    /// SetMasterVolume's coroutine.
    /// <para>
    /// No local teardown: the base fixture restores timeScale and drains the master fade. Not gated on
    /// RequireRealtimeAudioClock: every value asserted here runs on the frame clock.
    /// </para>
    /// </summary>
    public class UpdateModeClockTests : BroAudioTestFixture
    {
        // Wide thresholds: a live Lerp-plus-ease read is not sample-accurate.
        private const float NearSilenceThreshold = 0.15f;
        private const float NearTargetThreshold = 0.95f;

        // One duration for both ramps (started in the same frame), so one wait covers both. The clip
        // outlasts the longest wait (3s), so its natural end never resets _clipVolume mid-assertion.
        private const float FadeDuration = 1f;
        private const float ClipLength = 6f;

        // -20dB: far beyond mixer round-trip noise.
        private const float MasterFadeTargetVolume = 0.1f;

        [UnityTest]
        public IEnumerator Fade_WithUnscaledTimeModeAndPausedGame_StillProgressesToCompletion()
        {
            SoundManager.Instance.Setting.UpdateMode = AudioMixerUpdateMode.UnscaledTime;
            Time.timeScale = 0f;

            // Zero fade bypasses GetDeltaTime, so this lands despite the pause.
            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float baselineDb),
                "Master mixer parameter should be readable once SoundManager has bootstrapped.");
            Assert.AreEqual(0f, baselineDb, DecibelTolerance, "Master should already read full volume (0dB) after the explicit reset above.");

            AudioEntity entity = NewEntity("PausedFadeInSfx", BroAudioType.SFX, NewClip(ClipLength));
            entity.Clips[0].FadeIn = FadeDuration;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            BroAudio.SetVolume(MasterFadeTargetVolume, FadeDuration);

            yield return WaitForPlaybackStart(player);
            Assert.Less(player.GetVolume(), NearSilenceThreshold,
                "Clip fade-in should start near silence regardless of UpdateMode.");

            // Frames still step at timeScale 0 (only WaitForSeconds would hang), so a working UnscaledTime
            // branch finishes in about the fade's own duration; 1.5s of slack for frame-time noise.
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
                "that a fix to FaderModule alone would not touch",
                fadeTimeout);
        }

        [UnityTest]
        public IEnumerator Fade_WithNormalModeAndPausedGame_FreezesAndNeverProgresses()
        {
            // Explicit, though Normal is already the factory value.
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

            // Well past the fade's duration, so an unfrozen fade would be long complete. Must be realtime:
            // WaitForSeconds never returns at timeScale 0.
            yield return new WaitForSecondsRealtime(FadeDuration * 2f + 1f);

            // Exact equality holds: a frozen Fader recomputes the same Lerp at ease(0) = 0 every frame.
            Assert.AreEqual(clipVolumeAtStart, player.GetVolume(), 0.001f,
                "Clip fade-in should not have progressed at all while the game is paused under Normal " +
                "UpdateMode - a regression that returned unscaled time unconditionally would show movement here.");

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float masterDbAfterWait));
            Assert.AreEqual(masterDbAtStart, masterDbAfterWait, DecibelTolerance,
                "SoundManager's master-volume ramp should not have progressed either while paused under " +
                "Normal UpdateMode - this call site has its own Utility.GetDeltaTime() call " +
                "that a fix to FaderModule alone would not touch.");

            // Vacuous alone: read together with the UnscaledTime test above (see the class summary).
        }
    }
}