using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// <c>BroAudio.OnBGMChanged</c> across a <c>CurrentBGMPlayer</c> change. See Docs/Tests/inventory/time-dependent.md.
    /// </summary>
    public class BGMChangedEventTests : BroAudioTestFixture
    {
        [UnityTest]
        [Category("Finding_11")]
        public IEnumerator OnBGMChanged_WhenANewBGMReplacesTheCurrentOne_ReportsTheNewPlayer()
        {
            // Pins TEST_FINDINGS #11. Poll for the arrival, not an exact count: both events land in one frame,
            // so an == on the count is never observed.
            List<IAudioPlayer> received = new List<IAudioPlayer>();
            SubscribeBgmChanged(p => received.Add(p));

            SoundID firstId = NewSound("EventBgmA", BroAudioType.Music, NewClip(4f));
            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);

            yield return WaitUntilOrTimeout(() => received.Count >= 1,
                "OnBGMChanged to fire when the first BGM becomes current", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(firstId, received[0].ID, "The first event carries the incoming BGM player.");

            SoundID secondId = NewSound("EventBgmB", BroAudioType.Music, NewClip(4f));
            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate);

            yield return WaitUntilOrTimeout(
                () => received.Exists(p => p != null && p.ID.Equals(secondId)),
                "OnBGMChanged to report the second BGM player", HandoverWaitSeconds);

            Assert.IsTrue(received.Exists(p => p == null),
                "A null argument is raised as the outgoing BGM clears.");
        }
    }
}
