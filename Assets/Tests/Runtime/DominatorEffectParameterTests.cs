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
    /// <summary>
    /// Runtime-only characterization: DominatorPlayer.LowPassOthers/HighPassOthers/
    /// QuietOthers write the dedicated <c>BroName.Dominator_*ParaName</c> mixer parameters ("Main_LowPass" /
    /// "Main_HighPass"), a completely separate exposed surface from the <c>BroName.*ParaName</c> ("Effect_*")
    /// parameters that <see cref="BroAudio.SetEffect"/> uses. The two effect paths never touch the same
    /// mixer parameter, and their invalid-input guards differ in log level.
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

            // characterizes: a dominator writes BroName.Dominator_LowPassParaName ("Main_LowPass"), a
            // completely separate exposed parameter from BroName.LowPassParaName ("Effect_LowPass") that
            // BroAudio.SetEffect uses. The two effect paths never touch the same mixer parameter.
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
        /// Stops the dominator and checks that its filter reverts on its own: DominatorPlayer chains its effect
        /// with .While(PlayerIsPlaying), so once the player is recycled TweakTrackParameter tweaks the Main_*
        /// parameter back to its default and SwitchMainTrackMode(false) puts Main back at full volume. Nothing
        /// else resets these parameters - the base fixture's teardown leaves them to this same automation -
        /// so a broken revert would silently filter or mute every later test in the run.
        /// <para>
        /// The revert is observed rather than assumed, and on failure this puts the parameters back itself
        /// before reporting, like QuietOthers_WithZeroFadeTime_*, so one broken revert fails one test. The
        /// filter's reset fade is the zero fade the call was made with, so the budget only has to outlast the
        /// frame the .While() notices the stop.
        /// </para>
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

            // characterizes: this is NOT silent. AudioExtension.IsValidFrequency itself calls Debug.LogError
            // (with the standard Utility.LogTitle prefix, per Docs/FIXED_ISSUES.md #15) before
            // DominatorPlayer.LowPassOthers even reaches SetAllEffectExceptDominator. The log's TYPE and
            // BroAudio's own tag are the contract (Docs/GOAL.md anti-goal: "Asserting on log text"); the
            // parameter staying put is what actually proves the guard rejected the call.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            dominator.LowPassOthers(0f, 0f);
            yield return WaitFrames(2);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float after);
            Assert.AreEqual(before, after, "An invalid frequency must leave the mixer parameter untouched.");

            // Contrast: QuietOthers' own range guard also logs with the Utility.LogTitle prefix, but at
            // Warning instead of Error — the two "invalid input" guards still differ in log level, which is
            // what LogType.Warning vs LogType.Error above and below actually pins.
            // Both reads are asserted to resolve: an unexposed parameter would hand back 0 twice and make the
            // comparison below pass without the guard doing anything.
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float quietBefore),
                "Precondition: " + BroName.MainDominatedTrackName + " must be an exposed mixer parameter for this check to mean anything.");
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            dominator.QuietOthers(0f, 0f);
            yield return WaitFrames(2);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float quietAfter));
            Assert.AreEqual(quietBefore, quietAfter, "An invalid othersVol must leave the mixer parameter untouched.");
        }

        // Characterizes TEST_FINDINGS #43: QuietOthers with a zero fade time mutes Main and never ducks
        // Main_Dominated, so everything else keeps playing at full volume.
        //
        // EffectAutomationHelper.SetEffectTrackParameter starts TweakTrackParameter, and with fadeTime 0 the
        // whole coroutine drains synchronously inside StartCoroutine (Docs/FIXED_ISSUES.md #17): Tweak writes
        // the ducked level to Main_Dominated, and the coroutine's tail runs SwitchMainTrackMode(false).
        // SetEffectTrackParameter then runs SwitchMainTrackMode(true), whose ChangeChannel(Main ->
        // Main_Dominated, FullDecibelVolume) mutes Main and overwrites Main_Dominated with 0dB. The
        // `.While(PlayerIsPlaying)` that DominatorPlayer chains does not write the ducked level back.
        //
        // Stopping the dominator is then observed rather than assumed: a Main left muted would silence every
        // later test in the run, so the test puts both parameters back itself before reporting that.
        [UnityTest]
        [Category("Finding_43")]
        public IEnumerator QuietOthers_WithZeroFadeTime_MutesMainAndLeavesMainDominatedAtFullVolume()
        {
            const float OthersVolume = 0.2f;
            float requestedDuckDb = OthersVolume.ToDecibel();
            AudioMixer mixer = SoundManager.Instance.AudioMixer;

            // Main reading full volume means no earlier dominator's TweakTrackParameter is still parked on its
            // .While(): SwitchMainTrackMode(false) is that coroutine's last statement. The Volume tweaker is
            // shared for the whole run, and a still-tweaking one would take SetEffectTrackParameter's
            // IsTweaking/isMoreIntense branch instead of the path under test.
            yield return WaitUntilOrTimeout(() => IsAtFullVolume(mixer, BroName.MainTrackName),
                "Precondition: Main to read full volume, i.e. no dominator effect still active from an earlier test", DefaultPlaybackWaitSeconds);

            SoundID dominatorId = NewSound("ZeroFadeQuietOthersSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            // Same frame as Play, so the dominator is routed to a Dominator track and is not ducked by
            // itself (TEST_FINDINGS #42). The mixer parameters asserted below do not depend on the routing.
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

        private static bool IsAtFullVolume(AudioMixer mixer, string parameterName)
            => mixer.GetFloat(parameterName, out float db) && Mathf.Abs(db - AudioConstant.FullDecibelVolume) < DecibelTolerance;
    }
}
