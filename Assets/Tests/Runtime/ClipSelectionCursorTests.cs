using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Runtime-only characterization for inventory 3.5: clip-selection state lives on the
    /// <see cref="AudioEntity"/>, not on the player. The pure per-strategy behavior already lives in
    /// ClipSelectionTests.cs (EditMode) — this file only covers what needs a live SoundManager: the shared
    /// cursor across concurrent plays, its explicit reset, and the SetVelocity/SetSequenceId wiring that has
    /// to land before SoundManager.LateUpdate drains the queued Play() and calls PickNewClip - the same
    /// same-frame seam VolumePitchMixerTests exercises for SetPitch.
    /// </summary>
    public class ClipSelectionCursorTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator Play_SameSequenceEntityPlayedTwice_AdvancesSharedCursor_AndResetRestartsAtClipZero()
        {
            AudioClip clip0 = NewClip(3f, "SeqClip0");
            AudioClip clip1 = NewClip(3f, "SeqClip1");
            AudioClip clip2 = NewClip(3f, "SeqClip2");
            AudioEntity entity = NewEntity("SeqSfx", BroAudioType.SFX, clip0, clip1, clip2);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            SoundID id = IdOf(entity);

            IAudioPlayer player1 = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player1, "the first play to start");
            Assert.AreEqual("SeqClip0", player1.AudioSource.clip.name);

            // characterizes: AudioEntity._clipSelectionStrategy is one field shared by every Play() on this
            // SoundID — a second concurrent play advances the same cursor rather than starting its own at 0.
            IAudioPlayer player2 = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player2, "the second play to start");
            Assert.AreEqual("SeqClip1", player2.AudioSource.clip.name,
                "The second concurrent play should advance the same shared Sequence cursor, not restart at clip 0.");

            Assert.AreNotSame(player1.AudioSource, player2.AudioSource, "The two plays should be on different pooled players.");
            Assert.IsTrue(player1.IsPlaying, "The first player should still be playing concurrently with the second.");

            // The state is never reset automatically between plays — only this explicit call clears it.
            BroAudio.ResetMultiClipStrategy(id);

            IAudioPlayer third = BroAudio.Play(id);
            yield return WaitForPlaybackStart(third, "the third play to start");
            Assert.AreEqual("SeqClip0", third.AudioSource.clip.name,
                "ResetMultiClipStrategy is the only thing that clears the shared cursor.");
        }

        [UnityTest]
        public IEnumerator SetVelocity_CalledBeforeQueueDrains_SelectsTheVelocityMatchedClip()
        {
            AudioClip low = NewClip(3f, "LowVelocityClip");
            AudioClip mid = NewClip(3f, "MidVelocityClip");
            AudioClip high = NewClip(3f, "HighVelocityClip");
            AudioEntity entity = NewEntity("VelocityWiringSfx", BroAudioType.SFX, low, mid, high);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Velocity);
            entity.Clips[0].Weight = 0;
            entity.Clips[1].Weight = 40;
            entity.Clips[2].Weight = 80;
            SoundID id = IdOf(entity);

            // Play only enqueues; SetVelocity lands in the same frame, before the clip is picked.
            IAudioPlayer player = BroAudio.Play(id);
            player.SetVelocity(50);

            yield return WaitForPlaybackStart(player);
            Assert.AreSame(mid, player.AudioSource.clip,
                "SetVelocity(50) called in the same frame as Play() must still steer PickNewClip to the clip at the 40 threshold.");
        }

        [UnityTest]
        public IEnumerator SetSequenceId_WithDifferentIds_AdvancesEachNamedCursorIndependently()
        {
            AudioClip clip0 = NewClip(3f, "SeqIdClip0");
            AudioClip clip1 = NewClip(3f, "SeqIdClip1");
            AudioClip clip2 = NewClip(3f, "SeqIdClip2");
            AudioEntity entity = NewEntity("SequenceWiringSfx", BroAudioType.SFX, clip0, clip1, clip2);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            SoundID id = IdOf(entity);

            IAudioPlayer firstA = BroAudio.Play(id);
            firstA.SetSequenceId("a");
            yield return WaitForPlaybackStart(firstA, "the first 'a' play to start");
            Assert.AreSame(clip0, firstA.AudioSource.clip, "The 'a' cursor's first pick should be index 0.");

            IAudioPlayer secondA = BroAudio.Play(id);
            secondA.SetSequenceId("a");
            yield return WaitForPlaybackStart(secondA, "the second 'a' play to start");
            Assert.AreSame(clip1, secondA.AudioSource.clip, "The 'a' cursor should have advanced to index 1.");

            // characterizes: a named sequence id gets its own cursor, unlike the default shared one that
            // Play_SameSequenceEntityPlayedTwice_AdvancesSharedCursor_AndResetRestartsAtClipZero pins down above.
            IAudioPlayer firstB = BroAudio.Play(id);
            firstB.SetSequenceId("b");
            yield return WaitForPlaybackStart(firstB, "the first 'b' play to start");
            Assert.AreSame(clip0, firstB.AudioSource.clip,
                "A different sequence id must start fresh at index 0, unaffected by 'a' already sitting at index 1.");
        }
    }
}
