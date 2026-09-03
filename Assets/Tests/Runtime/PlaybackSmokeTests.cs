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
            yield return WaitForPlaybackStart(player, "the queued player to start playing");
            Assert.AreSame(clip, player.AudioSource.clip, "The AudioSource should be playing the entity's clip.");
            Assert.AreEqual(id, player.ID);
        }

        [UnityTest]
        public IEnumerator Stop_AfterPlaying_DeactivatesThePlayer()
        {
            SoundID id = NewSound("StopSfx", BroAudioType.SFX, NewClip(2f));

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            BroAudio.Stop(id, 0f);

            yield return WaitForRecycle(player, "the player to become inactive after Stop");
            Assert.IsFalse(player.IsPlaying);
        }
    }
}
