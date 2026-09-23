using System.Collections;
using System.Text.RegularExpressions;
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
        // Anchored on the constant every BroAudio log is tagged with (Utility.LogTitle), not on any one
        // message's wording - the log's TYPE plus this tag is the contract; the sentence is not
        // (Docs/GOAL.md anti-goal: "Asserting on log text"). Regex.Escape because the tag's rich-text markup
        // ("[BroAudio]" among it) contains regex metacharacters.
        private static readonly Regex BroAudioLogPrefix = new Regex(Regex.Escape(Utility.LogTitle));

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
        }

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
            LogAssert.Expect(LogType.Error, BroAudioLogPrefix);
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
            LogAssert.Expect(LogType.Warning, BroAudioLogPrefix);
            dominator.QuietOthers(0f, 0f);
            yield return WaitFrames(2);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float quietAfter));
            Assert.AreEqual(quietBefore, quietAfter, "An invalid othersVol must leave the mixer parameter untouched.");
        }

        // Characterizes TEST_FINDINGS #43: QuietOthers with a zero fade time. The finding predicts the duck
        // is lost; the source says the clobber happens but is undone before QuietOthers returns, and this
        // test asserts that repaired outcome.
        //
        // EffectAutomationHelper.SetEffectTrackParameter starts TweakTrackParameter, and with fadeTime 0 the
        // whole coroutine drains synchronously inside StartCoroutine (Docs/FIXED_ISSUES.md #17): Tweak writes
        // the ducked level to Main_Dominated, the WaitableList empties, and the coroutine's own tail runs
        // SwitchMainTrackMode(false). SetEffectTrackParameter then runs SwitchMainTrackMode(true), whose
        // ChangeChannel(Main -> Main_Dominated, FullDecibelVolume) overwrites Main_Dominated with 0dB. That
        // much of the finding is right.
        //
        // What it leaves out: DominatorPlayer.SetAllEffectExceptDominator always chains
        // `.While(PlayerIsPlaying)`, and DecorateTweakingWaitable's empty-list branch (the FIXED_ISSUES #17
        // re-arm) restarts TweakTrackParameter. The restarted Tweak reads Main_Dominated's current value, which
        // differs from the target, and writes the ducked level again. Only after that does the coroutine park
        // on the WaitWhile. So the duck lands after all, and the final state matches the non-zero-fade case.
        // Nothing on the public surface calls SetEffect with a dominator effect without that `.While`.
        //
        // If this fails with Main_Dominated at ~0dB, the finding's prediction is right after all and the
        // re-arm does not repair the clobber.
        [UnityTest]
        [Category("Finding_43")]
        public IEnumerator QuietOthers_WithZeroFadeTime_StillDucksBecauseTheWhileReArmRewritesMainDominated()
        {
            const float OthersVolume = 0.2f;
            float expectedDuckedDb = OthersVolume.ToDecibel();

            // Main reading full volume means no earlier dominator's TweakTrackParameter is still parked on its
            // .While(): SwitchMainTrackMode(false) is that coroutine's last statement. The Volume tweaker is
            // shared for the whole run, and a still-tweaking one would take SetEffectTrackParameter's
            // IsTweaking/isMoreIntense branch instead of the path under test.
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Precondition: Main to read full volume, i.e. no dominator effect still active from an earlier test", DefaultPlaybackWaitSeconds);

            SoundID dominatorId = NewSound("ZeroFadeQuietOthersSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            // Same frame as Play, so the dominator is routed to a Dominator track and is not ducked by
            // itself (TEST_FINDINGS #42). The mixer parameters asserted below do not depend on the routing.
            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            Assert.IsTrue(mixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedBefore),
                "Precondition: " + BroName.MainDominatedTrackName + " must be an exposed mixer parameter.");
            Assert.IsTrue(mixer.GetFloat(BroName.MainTrackName, out float mainBefore),
                "Precondition: " + BroName.MainTrackName + " must be an exposed mixer parameter.");

            dominator.QuietOthers(OthersVolume, 0f);

            // Read in the same frame the call returned, before anything else can run. Reported, not asserted:
            // it shows which write landed last inside the call. The frame-settled reading below is the
            // outcome the finding is about.
            mixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedOnReturn);
            mixer.GetFloat(BroName.MainTrackName, out float mainOnReturn);

            yield return WaitFrames(2);
            mixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedSettled);
            mixer.GetFloat(BroName.MainTrackName, out float mainSettled);

            string observed = $"Observed Main_Dominated {dominatedBefore:F2}dB before -> {dominatedOnReturn:F2}dB as " +
                              $"QuietOthers returned -> {dominatedSettled:F2}dB two frames later; Main {mainBefore:F2}dB -> " +
                              $"{mainOnReturn:F2}dB -> {mainSettled:F2}dB. Expected ducked level {expectedDuckedDb:F2}dB, " +
                              $"muted Main {AudioConstant.MinDecibelVolume:F2}dB.";

            Assert.AreEqual(expectedDuckedDb, dominatedSettled, DecibelTolerance,
                "characterizes: QuietOthers(vol, 0f) still ducks. SwitchMainTrackMode(true) overwrites the first " +
                "write with 0dB, but the .While() re-arm rewrites the ducked level. A reading near " +
                $"{AudioConstant.FullDecibelVolume:F2}dB would confirm TEST_FINDINGS #43 (nothing ducks). {observed}");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainSettled, DecibelTolerance,
                "While the dominator is active Main is muted outright and everything else plays through " +
                $"Main_Dominated, with a zero fade too. {observed}");

            // A zero-fade duck must still let go: the parked .While() ends when the dominator stops, and the
            // coroutine's SwitchMainTrackMode(false) puts Main back. Asserting it also keeps this test from
            // leaving Main muted for every later test in the run.
            dominatorPlayer.Stop(0f);
            yield return WaitUntilOrTimeout(() =>
            {
                mixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Main to return to full volume once the zero-fade dominator stops", RampConvergenceWaitSeconds);
        }
    }
}
