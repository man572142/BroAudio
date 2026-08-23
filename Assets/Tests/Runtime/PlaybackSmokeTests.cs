using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>Proves the harness: a code-built entity plays a real clip through the public API.</summary>
    public class PlaybackSmokeTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator Play_AfterQueueIsDrained_PlaysTheEntitysClip()
        {
            AudioClip clip = NewClip(2f, "SmokeClip");
            SoundID id = NewSound("SmokeSfx", BroAudioType.SFX, clip);

            IAudioPlayer player = BroAudio.Play(id);

            Assert.IsTrue(player.IsActive, "Play should return an active player immediately.");
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "the queued player to start playing", 2f);
            Assert.AreSame(clip, player.AudioSource.clip, "The AudioSource should be playing the entity's clip.");
            Assert.AreEqual(id, player.ID);
        }

        [UnityTest]
        public IEnumerator Stop_AfterPlaying_DeactivatesThePlayer()
        {
            SoundID id = NewSound("StopSfx", BroAudioType.SFX, NewClip(2f));

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            BroAudio.Stop(id, 0f);

            yield return WaitUntilOrTimeout(() => !player.IsActive, "the player to become inactive after Stop", 2f);
            Assert.IsFalse(player.IsPlaying);
        }
    }
}
