using System.Collections;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// <c>RuntimeSetting.AlwaysPlayMusicAsBGM</c>: a Music Play() is auto-wrapped with AsBGM()+SetTransition.
    /// See Docs/inventory/time-dependent.md.
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
    }
}
