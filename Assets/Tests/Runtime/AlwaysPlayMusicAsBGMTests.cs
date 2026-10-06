using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// <c>RuntimeSetting.AlwaysPlayMusicAsBGM</c>: a Music Play() is auto-wrapped with AsBGM()+SetTransition.
    /// See Docs/Tests/inventory/time-dependent.md.
    /// </summary>
    public class AlwaysPlayMusicAsBGMTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Enabled_AutoTransitionsUnrelatedMusicPlaysWithoutExplicitAsBGM()
        {
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = true;
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            // Must outlast the recycle wait, or the clip's natural end would pass with the feature deleted.
            const float FirstClipSeconds = 9f;
            SoundID firstId = NewSound("AutoBgmA", BroAudioType.Music, NewClip(FirstClipSeconds));
            SoundID secondId = NewSound("AutoBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId); // never calls AsBGM() explicitly
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId); // also never calls AsBGM() explicitly

            // Derived from the clip: the bound is only decisive while well under FirstClipSeconds.
            yield return WaitForRecycle(first,
                "the first Music player to be auto-transitioned off by SoundManager's implicit AsBGM()+SetTransition",
                FirstClipSeconds / 3f);
            yield return WaitForPlaybackStart(second, "the second Music player to take over as BGM");
        }

        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Disabled_MusicPlaysOverlapFreelyWithoutTransition()
        {
            // Immediate is load-bearing: the default CrossFade keeps a fading-out player IsPlaying for 2s,
            // so a deleted guard would pass the window below.
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = false;
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            SoundID firstId = NewSound("NoBgmA", BroAudioType.Music, NewClip(9f));
            SoundID secondId = NewSound("NoBgmB", BroAudioType.Music, NewClip(9f));

            IAudioPlayer first = BroAudio.Play(firstId);
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            yield return WaitForPlaybackStart(second, "second Music play to start");

            // Every frame of the window, not one sample; the window must stay well inside both clips' length.
            float deadline = Time.realtimeSinceStartup + 1.5f;
            while (Time.realtimeSinceStartup < deadline)
            {
                Assert.IsTrue(first.IsPlaying, "With AlwaysPlayMusicAsBGM off, the first Music player must keep playing - no auto-transition should have stopped it.");
                Assert.IsTrue(second.IsPlaying, "The second Music player must be playing concurrently, not sequenced after the first.");
                yield return null;
            }
        }

        /// <summary>
        /// Hand-copied from RuntimeSetting.FactorySettings.DefaultBGMTransitionTime rather than read from it, so a
        /// changed factory value fails here: it changes what every user hears between two Music plays.
        /// </summary>
        private const float FactoryCrossFadeSeconds = 2f;

        // Runs at factory settings on purpose: BGM tests that set DefaultBGMTransition to Immediate cannot see this path.
        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_FactoryDefaults_CrossFadesConsecutiveMusicOverTwoSeconds()
        {
            // No setting is written: Setup's factory values are the subject.
            RuntimeSetting setting = SoundManager.Instance.Setting;
            Assert.IsTrue(setting.AlwaysPlayMusicAsBGM, "Precondition: AlwaysPlayMusicAsBGM is on by factory default.");
            Assert.AreEqual(Transition.CrossFade, setting.DefaultBGMTransition, "Precondition: the factory auto-BGM transition is a CrossFade.");
            Assert.AreEqual(FactoryCrossFadeSeconds, setting.DefaultBGMTransitionTime, "Precondition: the factory auto-BGM transition lasts 2s.");

            // The fades run on the frame clock, but the 9s clips must not reach their natural end on a decoupled DSP clock.
            yield return RequireRealtimeAudioClock();

            // Outlasts the crossfade and every wait below, so only the transition can end the first player.
            const float ClipSeconds = 9f;
            SoundID firstId = NewSound("FactoryAutoBgmA", BroAudioType.Music, NewClip(ClipSeconds));
            SoundID secondId = NewSound("FactoryAutoBgmB", BroAudioType.Music, NewClip(ClipSeconds));

            IAudioPlayer first = BroAudio.Play(firstId); // never calls AsBGM() or SetTransition explicitly
            yield return WaitForPlaybackStart(first, "first Music play to start");
            Assert.AreEqual(AudioConstant.FullVolume, first.GetVolume(), LinearTolerance,
                "Precondition: the first Music play has no prior BGM to transition from, so it must start at full volume.");

            IAudioPlayer second = BroAudio.Play(secondId); // neither does this one
            yield return WaitUntilOrTimeout(() => first.IsPlaying && second.IsPlaying,
                "both Music players to be playing at once as the factory CrossFade opens (a timeout means the transition is sequential)",
                DefaultPlaybackWaitSeconds);
            float openedAt = Time.realtimeSinceStartup;

            // Hand-derived from the factory eases at the 1s mark of a 2s fade: the outgoing OutSine fade-out
            // reads about 0.29 and the incoming InCubic fade-in about 0.13. A dropped time falls back to the
            // code-built clip's zero fades - a hard cut - and a hard-coded Immediate/OnlyFadeIn cuts too.
            yield return new WaitForSeconds(1f);
            Assert.IsTrue(first.IsActive && first.IsPlaying,
                "1s into the factory 2s CrossFade the outgoing Music should still be playing - it must be faded, not cut.");
            float outgoingMidFade = first.GetVolume();
            Assert.Greater(outgoingMidFade, 0.05f, "1s into the factory 2s CrossFade the outgoing Music should still be audible.");
            Assert.Less(outgoingMidFade, 0.95f, "1s into the factory 2s CrossFade the outgoing Music should already be well below full volume.");
            Assert.Less(second.GetVolume(), 0.5f, "1s into the factory 2s CrossFade the incoming Music should still be well short of full volume.");

            // Bounded from the opening, not from now: the outgoing player ends about 2s in, so a 3s deadline keeps
            // the decisive window 1s wide on each side and fails a crossfade left running or never ended.
            yield return WaitForRecycle(first,
                "the outgoing Music to end once the factory CrossFade's fade-out completes",
                openedAt + FactoryCrossFadeSeconds + 1f - Time.realtimeSinceStartup);
            Assert.IsTrue(second.IsActive && second.IsPlaying, "Ending the outgoing Music must leave the incoming one playing.");
            yield return WaitUntilOrTimeout(() => second.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the incoming Music's fade-in to reach full volume alongside the outgoing fade-out", DefaultPlaybackWaitSeconds);
        }
    }
}
