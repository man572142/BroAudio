using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
#if !UNITY_WEBGL
    /// <summary>
    /// Dominator effects write their own <c>Main_*</c> mixer parameters, never the <c>Effect_*</c> ones
    /// <see cref="BroAudio.SetEffect"/> uses, and their invalid-input guards differ in log level.
    /// </summary>
    public class DominatorEffectParameterTests : BroAudioTestFixture
    {
        /// <summary>Hz. A cutoff this close to its default is back at its default.</summary>
        private const float FrequencyTolerance = 1f;

        [UnityTest]
        public IEnumerator LowPassOthers_MovesDominatorLowPassParameter_LeavesEffectLowPassParameterUntouched()
        {
            SoundID dominatorId = NewSound("DominatorLowPassSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float effectLowPassBefore);

            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            dominator.LowPassOthers(2000f, 0f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float v);
                return Mathf.Approximately(v, 2000f);
            }, "Main_LowPass to reach the requested frequency", DefaultPlaybackWaitSeconds);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float effectLowPassAfter);
            Assert.AreEqual(effectLowPassBefore, effectLowPassAfter,
                "LowPassOthers must not move Effect_LowPass — that parameter belongs to BroAudio.SetEffect.");

            yield return StopAndAssertRevert(dominatorPlayer, BroName.Dominator_LowPassParaName, AudioConstant.MaxFrequency);
        }

        [UnityTest]
        public IEnumerator HighPassOthers_MovesDominatorHighPassParameter_LeavesEffectHighPassParameterUntouched()
        {
            SoundID dominatorId = NewSound("DominatorHighPassSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float effectHighPassBefore);

            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            dominator.HighPassOthers(5000f, 0f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_HighPassParaName, out float v);
                return Mathf.Approximately(v, 5000f);
            }, "Main_HighPass to reach the requested frequency", DefaultPlaybackWaitSeconds);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float effectHighPassAfter);
            Assert.AreEqual(effectHighPassBefore, effectHighPassAfter,
                "HighPassOthers must not move Effect_HighPass — that parameter belongs to BroAudio.SetEffect.");

            yield return StopAndAssertRevert(dominatorPlayer, BroName.Dominator_HighPassParaName, AudioConstant.MinFrequency);
        }

        /// <summary>
        /// Nothing else resets these parameters, so a broken revert would filter or mute every later test:
        /// on failure this restores them itself before reporting, so one broken revert fails one test.
        /// </summary>
        private static IEnumerator StopAndAssertRevert(IAudioPlayer dominatorPlayer, string parameterName, float defaultValue)
        {
            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            dominatorPlayer.Stop(0f);

            float deadline = Time.realtimeSinceStartup + DefaultPlaybackWaitSeconds;
            while (!(IsAt(mixer, parameterName, defaultValue) && IsAtFullVolume(mixer, BroName.MainTrackName))
                   && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            bool filterReverted = IsAt(mixer, parameterName, defaultValue);
            bool mainRecovered = IsAtFullVolume(mixer, BroName.MainTrackName);
            mixer.GetFloat(parameterName, out float filterAfterStop);
            mixer.GetFloat(BroName.MainTrackName, out float mainAfterStop);
            if (!filterReverted)
            {
                mixer.SafeSetFloat(parameterName, defaultValue);
                mixer.SafeSetFloat(parameterName + "2", defaultValue);
            }
            if (!mainRecovered)
            {
                mixer.SafeSetFloat(BroName.MainTrackName, AudioConstant.FullDecibelVolume);
                mixer.SafeSetFloat(BroName.MainDominatedTrackName, AudioConstant.MinDecibelVolume);
            }

            Assert.IsTrue(filterReverted,
                $"{parameterName} must return to its default {defaultValue:F0}Hz once the dominator stops; it read " +
                $"{filterAfterStop:F0}Hz after {DefaultPlaybackWaitSeconds}s (the test restored it so later tests are unaffected).");
            Assert.IsTrue(mainRecovered,
                $"Main must return to full volume once the dominator stops; it read {mainAfterStop:F2}dB after " +
                $"{DefaultPlaybackWaitSeconds}s (the test restored it so later tests are unaffected).");
        }

        private static bool IsAt(AudioMixer mixer, string parameterName, float value)
            => mixer.GetFloat(parameterName, out float current) && Mathf.Abs(current - value) <= FrequencyTolerance;

        [UnityTest]
        public IEnumerator LowPassOthers_InvalidFrequency_LogsErrorAndLeavesParameterUnchanged_UnlikeQuietOthersWarning()
        {
            SoundID dominatorId = NewSound("InvalidFreqDominatorSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float before);
            IPlayerEffect dominator = dominatorPlayer.AsDominator();

            // Pin the log's type and tag, never its text; the unmoved parameter proves the rejection.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            dominator.LowPassOthers(0f, 0f);
            yield return WaitFrames(2);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float after);
            Assert.AreEqual(before, after, "An invalid frequency must leave the mixer parameter untouched.");

            // Contrast: QuietOthers' guard logs at Warning. Both reads must resolve: an unexposed parameter
            // reads 0 twice and passes vacuously.
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float quietBefore),
                "Precondition: " + BroName.MainDominatedTrackName + " must be an exposed mixer parameter for this check to mean anything.");
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            dominator.QuietOthers(0f, 0f);
            yield return WaitFrames(2);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float quietAfter));
            Assert.AreEqual(quietBefore, quietAfter, "An invalid othersVol must leave the mixer parameter untouched.");
        }

        // Pins TEST_FINDINGS #43. A Main left muted would silence every later test, so the stop is observed
        // and the parameters restored before reporting.
        [UnityTest]
        [Category("Finding_43")]
        public IEnumerator QuietOthers_WithZeroFadeTime_MutesMainAndLeavesMainDominatedAtFullVolume()
        {
            const float OthersVolume = 0.2f;
            float requestedDuckDb = OthersVolume.ToDecibel();
            AudioMixer mixer = SoundManager.Instance.AudioMixer;

            // The Volume tweaker is shared for the run; one still tweaking from an earlier dominator would take
            // a different branch than the path under test. Main at full volume means none is.
            yield return WaitUntilOrTimeout(() => IsAtFullVolume(mixer, BroName.MainTrackName),
                "Precondition: Main to read full volume, i.e. no dominator effect still active from an earlier test", DefaultPlaybackWaitSeconds);

            SoundID dominatorId = NewSound("ZeroFadeQuietOthersSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            // Same frame as Play, so the dominator isn't ducked by itself (TEST_FINDINGS #42).
            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            dominator.QuietOthers(OthersVolume, 0f);
            yield return WaitFrames(2);
            mixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedSettled);
            mixer.GetFloat(BroName.MainTrackName, out float mainSettled);
            string observed = $"Observed Main_Dominated {dominatedSettled:F2}dB and Main {mainSettled:F2}dB two frames after " +
                              $"QuietOthers({OthersVolume}, 0f); the requested duck is {requestedDuckDb:F2}dB.";

            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainSettled, DecibelTolerance,
                $"While a dominator is active Main is muted outright and everything else plays through Main_Dominated. {observed}");
            Assert.AreEqual(AudioConstant.FullDecibelVolume, dominatedSettled, DecibelTolerance,
                $"characterizes: with a zero fade the duck never lands - SwitchMainTrackMode(true) leaves Main_Dominated at full volume. {observed}");

            dominatorPlayer.Stop(0f);
            float deadline = Time.realtimeSinceStartup + RampConvergenceWaitSeconds;
            while (!IsAtFullVolume(mixer, BroName.MainTrackName) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            bool mainRecovered = IsAtFullVolume(mixer, BroName.MainTrackName);
            mixer.GetFloat(BroName.MainTrackName, out float mainAfterStop);
            if (!mainRecovered)
            {
                mixer.SafeSetFloat(BroName.MainTrackName, AudioConstant.FullDecibelVolume);
                mixer.SafeSetFloat(BroName.MainDominatedTrackName, AudioConstant.MinDecibelVolume);
            }
            Assert.IsTrue(mainRecovered,
                $"Main must return to full volume once the zero-fade dominator stops; it read {mainAfterStop:F2}dB " +
                $"after {RampConvergenceWaitSeconds}s (the test restored it so later tests are unaffected).");
        }

        // Two dominators share one Volume tweaker. Effect.CompareTo ranks the higher (lighter) duck as the more
        // intense (AudioMathTests pins that), so the deeper duck goes first and the lighter one restarts over it.
        // Non-zero fades: a zero fade takes the TEST_FINDINGS #43 path.
        [UnityTest]
        public IEnumerator QuietOthers_TwoOverlappingDominators_KeepMainDuckedUntilTheLastOneStops()
        {
            yield return RequireRealtimeAudioClock();

            const float DeepOthersVolume = 0.2f;
            const float DeepDuckDb = -13.98f; // 20 * log10(0.2)
            const float LightOthersVolume = 0.5f;
            const float LightDuckDb = -6.02f; // 20 * log10(0.5)
            const float FadeSeconds = 0.1f;
            const float HoldSeconds = 1.5f;
            AudioMixer mixer = SoundManager.Instance.AudioMixer;

            // The Volume tweaker is shared for the run; one still tweaking from an earlier dominator would take
            // a different branch than the path under test. Main at full volume means none is.
            yield return WaitUntilOrTimeout(() => IsAtFullVolume(mixer, BroName.MainTrackName),
                "Precondition: Main to read full volume, i.e. no dominator effect still active from an earlier test", DefaultPlaybackWaitSeconds);

            // Each AsDominator() in its Play()'s frame, so neither dominator is ducked by itself (TEST_FINDINGS #42).
            SoundID deepId = NewSound("DeepDuckDominatorSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer deepPlayer = BroAudio.Play(deepId);
            IPlayerEffect deepDominator = deepPlayer.AsDominator();
            yield return WaitForPlaybackStart(deepPlayer, "the deep-ducking dominator to start playing");
            deepDominator.QuietOthers(DeepOthersVolume, FadeSeconds);
            yield return WaitUntilOrTimeout(() => IsAtDecibel(mixer, BroName.MainDominatedTrackName, DeepDuckDb),
                "Main_Dominated to reach the first dominator's duck", DefaultPlaybackWaitSeconds);

            SoundID lightId = NewSound("LightDuckDominatorSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer lightPlayer = BroAudio.Play(lightId);
            IPlayerEffect lightDominator = lightPlayer.AsDominator();
            yield return WaitForPlaybackStart(lightPlayer, "the light-ducking dominator to start playing");
            // characterizes: the lighter duck wins while both play, though the deep dominator asked for more.
            lightDominator.QuietOthers(LightOthersVolume, FadeSeconds);
            yield return WaitUntilOrTimeout(() => IsAtDecibel(mixer, BroName.MainDominatedTrackName, LightDuckDb),
                "the lighter duck, ranked more intense, to take over Main_Dominated while both play", DefaultPlaybackWaitSeconds);

            lightPlayer.Stop(0f);
            // Back down to the queued deep duck, never up past the light one: a reset to full volume or a switch
            // back to Main on the way, even for a frame, reads above it.
            float highestDominated = float.MinValue;
            float highestMain = float.MinValue;
            yield return WaitUntilOrTimeout(() =>
            {
                mixer.GetFloat(BroName.MainDominatedTrackName, out float dominated);
                mixer.GetFloat(BroName.MainTrackName, out float main);
                highestDominated = Mathf.Max(highestDominated, dominated);
                highestMain = Mathf.Max(highestMain, main);
                return Mathf.Abs(dominated - DeepDuckDb) < DecibelTolerance;
            }, "Main_Dominated to return to the still-playing dominator's deeper duck once the lighter one stops", DefaultPlaybackWaitSeconds);
            Assert.LessOrEqual(highestDominated, LightDuckDb + DecibelTolerance,
                $"The first stop must hand over to the queued duck without lifting it (Main_Dominated read up to {highestDominated:F2}dB).");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, highestMain, DecibelTolerance,
                "Main must stay muted when one of two dominators stops: the other still plays.");

            // Every frame of the window is read, so a lift in any one of them fails the hold.
            float holdEnd = Time.time + HoldSeconds;
            int samples = 0;
            float lowestDominated = float.MaxValue;
            highestDominated = float.MinValue;
            highestMain = float.MinValue;
            while (Time.time < holdEnd)
            {
                Assert.IsTrue(mixer.GetFloat(BroName.MainDominatedTrackName, out float dominated));
                Assert.IsTrue(mixer.GetFloat(BroName.MainTrackName, out float main));
                lowestDominated = Mathf.Min(lowestDominated, dominated);
                highestDominated = Mathf.Max(highestDominated, dominated);
                highestMain = Mathf.Max(highestMain, main);
                samples++;
                yield return null;
            }
            string observed = $"Observed Main_Dominated {lowestDominated:F2}..{highestDominated:F2}dB and Main up to {highestMain:F2}dB over {samples} frames.";
            Assert.Greater(samples, 0, "The hold window should have been sampled at least once.");
            Assert.AreEqual(DeepDuckDb, lowestDominated, DecibelTolerance, $"The remaining dominator's duck should hold while it plays. {observed}");
            Assert.AreEqual(DeepDuckDb, highestDominated, DecibelTolerance, $"The remaining dominator's duck should hold while it plays. {observed}");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, highestMain, DecibelTolerance, $"Main should stay muted while a dominator still plays. {observed}");

            deepPlayer.Stop(0f);
            float deadline = Time.realtimeSinceStartup + RampConvergenceWaitSeconds;
            while (!IsAtFullVolume(mixer, BroName.MainTrackName) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            bool mainRecovered = IsAtFullVolume(mixer, BroName.MainTrackName);
            mixer.GetFloat(BroName.MainTrackName, out float mainAfterStop);
            mixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedAfterStop);
            if (!mainRecovered)
            {
                mixer.SafeSetFloat(BroName.MainTrackName, AudioConstant.FullDecibelVolume);
                mixer.SafeSetFloat(BroName.MainDominatedTrackName, AudioConstant.MinDecibelVolume);
            }
            Assert.IsTrue(mainRecovered,
                $"Main must return to full volume once the last dominator stops; it read {mainAfterStop:F2}dB " +
                $"after {RampConvergenceWaitSeconds}s (the test restored it so later tests are unaffected).");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, dominatedAfterStop, DecibelTolerance,
                "Once the last dominator stops, Main_Dominated should be muted again.");
        }

        private static bool IsAtDecibel(AudioMixer mixer, string parameterName, float db)
            => mixer.GetFloat(parameterName, out float current) && Mathf.Abs(current - db) < DecibelTolerance;

        private static bool IsAtFullVolume(AudioMixer mixer, string parameterName)
            => mixer.GetFloat(parameterName, out float db) && Mathf.Abs(db - AudioConstant.FullDecibelVolume) < DecibelTolerance;
    }
#endif
}
