using System.Collections;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory slice 2.8: <c>RuntimeSetting.AlwaysPlayMusicAsBGM</c> (default true) - a Music-typed
    /// Play() is auto-wrapped with AsBGM()+SetTransition even when the caller never calls AsBGM()
    /// themselves. See Docs/inventory/time-dependent.md.
    /// </summary>
    public class AlwaysPlayMusicAsBGMTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Enabled_AutoTransitionsUnrelatedMusicPlaysWithoutExplicitAsBGM()
        {
            // fixture restores RuntimeSetting in TearDown - Immediate keeps this deterministic and fast.
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = true;
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            // The first clip's length is load-bearing: at 2s it used to reach its own natural end within
            // the 2s deactivation wait below, so the assertion passed even with the auto-BGM feature
            // deleted. 9s puts the clip's natural end far outside the 3s wait, so only the auto-transition
            // can explain the first player deactivating.
            SoundID firstId = NewSound("AutoBgmA", BroAudioType.Music, NewClip(9f));
            SoundID secondId = NewSound("AutoBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId); // never calls AsBGM() explicitly
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId); // also never calls AsBGM() explicitly

            // 3s is well under the 9s clip length, so this stays discriminating; the transition itself is
            // Immediate, so 3s is a generous CI-safe margin rather than a tight bound on the transition.
            yield return WaitForRecycle(first,
                "the first Music player to be auto-transitioned off by SoundManager's implicit AsBGM()+SetTransition", 3f);
            yield return WaitForPlaybackStart(second, "the second Music player to take over as BGM");
        }

        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Disabled_MusicPlaysOverlapFreelyWithoutTransition()
        {
            // fixture restores RuntimeSetting in TearDown. Pinning the transition is what makes the
            // negative assertion decisive, and it is the mirror image of the Enabled twin's reason for
            // doing the same: left at the factory default of a 2s CrossFade, the auto-BGM path stops the
            // outgoing player by fading it out, and a fade-out keeps AudioSource.isPlaying - hence
            // IAudioPlayer.IsPlaying - true for those 2s. Deleting the Setting.AlwaysPlayMusicAsBGM guard
            // in SoundManager.PlayerToPlay would then be indistinguishable from the feature working.
            // With Immediate, that same mutation ends the first player within a frame or two of the
            // second starting (Transition.Immediate forces fadeOut to 0 in MusicPlayer.StopCurrentPlayer).
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = false;
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            // 9s clips, as in the Enabled twin: the observation window has to sit far inside both clips'
            // natural length, or a clip simply reaching its own end could stand in for the auto-transition.
            SoundID firstId = NewSound("NoBgmA", BroAudioType.Music, NewClip(9f));
            SoundID secondId = NewSound("NoBgmB", BroAudioType.Music, NewClip(9f));

            IAudioPlayer first = BroAudio.Play(firstId);
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            yield return WaitForPlaybackStart(second, "second Music play to start");

            // Watch continuously instead of sampling once: every frame of a 1.5s window must show both
            // players audible. That is a full second wider than the couple of frames an auto-transition
            // needs to end the first player, and still ~7s short of either clip's natural end.
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
