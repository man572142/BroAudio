using System.Collections;
using System.Text.RegularExpressions;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using NUnit.Framework;
using UnityEngine;
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
    }
}
