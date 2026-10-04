using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The loop-seam exception to <see cref="BroAudio.OnBGMChanged"/>, and what <see cref="StopMode.Mute"/>'s
    /// "until it's played (unmuted) again" amounts to in practice.
    /// </summary>
    public class BGMEdgeCaseTests : BroAudioTestFixture
    {
        // A handover is not a BGM change, so no event; CurrentBGMPlayer must still follow it, or a later
        // transition would stop the wrong iteration.
        [UnityTest]
        public IEnumerator OnBGMChanged_AcrossALoopingBGMsHandoverSeam_DoesNotFire_ButCurrentBGMPlayerFollowsTheHandover()
        {
            yield return RequireRealtimeAudioClock();

            List<IAudioPlayer> received = new List<IAudioPlayer>();
            SubscribeBgmChanged(p => received.Add(p));

            AudioEntity entity = NewEntity("LoopingBgm", BroAudioType.Music, NewClip(1f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            player.AsBGM().SetTransition(Transition.Immediate);

            yield return WaitUntilOrTimeout(() => received.Count >= 1,
                "OnBGMChanged to fire when the looping BGM becomes current", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(1, received.Count, "Becoming the first BGM raises the event once.");
            Assert.IsNotNull(received[0]);
            Assert.AreEqual(id, received[0].ID, "The event carries the looping BGM.");

            AudioPlayer firstIteration = InstanceOf(player);
            Assert.AreSame(firstIteration, MusicPlayer.CurrentBGMPlayer, "Precondition: the first iteration is the current BGM.");

            yield return WaitUntilOrTimeout(() => InstanceOf(player) != firstIteration,
                "the looping BGM to hand over to its second iteration", HandoverWaitSeconds);
            yield return WaitFrames(3); // the outgoing player's EndPlaying/Recycle runs in the frame of the handover

            Assert.AreEqual(1, received.Count,
                "characterizes: a loop handover re-points the BGM without raising OnBGMChanged - no null, no repeat.");
            Assert.AreSame(InstanceOf(player), MusicPlayer.CurrentBGMPlayer,
                "CurrentBGMPlayer follows the handover to the iteration that is now playing.");
        }

        // Pins TEST_FINDINGS #67 (the replay half).
        [UnityTest]
        [Category("Finding_67")]
        public IEnumerator StopModeMute_PlayingTheSameSoundAgainStartsANewPlayerAndLeavesTheMutedOneRunningSilently()
        {
            const float MutedThreshold = 0.05f;
            SoundID firstId = NewSound("MuteReplayBgmA", BroAudioType.Music, NewClip(6f));
            SoundID secondId = NewSound("MuteReplayBgmB", BroAudioType.Music, NewClip(6f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(first, "the first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate, StopMode.Mute);
            yield return WaitForPlaybackStart(second, "the incoming BGM to start");
            yield return WaitUntilOrTimeout(() => first.GetVolume() < MutedThreshold,
                "the outgoing BGM to be muted", DefaultPlaybackWaitSeconds);
            AudioPlayer mutedInstance = InstanceOf(first);

            IAudioPlayer replay = BroAudio.Play(firstId);
            replay.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(replay, "the replayed BGM to start");

            Assert.AreNotSame(mutedInstance, InstanceOf(replay),
                "characterizes: playing the muted sound again takes a new player - it does not resume the muted one.");
            Assert.AreEqual(1f, replay.GetVolume(), LinearTolerance, "The new player starts at full volume.");
            Assert.IsTrue(first.IsActive && first.IsPlaying, "The muted player is still running in the background.");
            Assert.Less(first.GetVolume(), MutedThreshold, "characterizes: and it is still muted - nothing unmuted it.");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            first.UnPause(0f);
            yield return WaitFrames(1);
            Assert.Less(first.GetVolume(), MutedThreshold, "characterizes: UnPause only resumes a Pause, so it cannot unmute either.");
        }

        // Pins TEST_FINDINGS #67 (the leak half); a fix that ends a muted player at its clip's end turns the
        // IsActive assert red. The closing explicit Stop frees the stranded player.
        [UnityTest]
        [Category("Finding_67")]
        public IEnumerator StopModeMute_TheMutedPlayerIsNeverRecycledWhenItsClipEnds_OnlyAnExplicitStopFreesIt()
        {
            yield return RequireRealtimeAudioClock();

            const float MutedClipSeconds = 1.5f;
            const float MutedThreshold = 0.05f;
            SoundID firstId = NewSound("MuteOutlivesBgmA", BroAudioType.Music, NewClip(MutedClipSeconds));
            SoundID secondId = NewSound("MuteOutlivesBgmB", BroAudioType.Music, NewClip(6f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(first, "the first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate, StopMode.Mute);
            yield return WaitForPlaybackStart(second, "the incoming BGM to start");
            Assert.Less(first.GetVolume(), MutedThreshold, "Precondition: an Immediate Mute transition mutes the outgoing BGM at once.");

            yield return WaitUntilOrTimeout(() => !first.IsPlaying,
                "the muted BGM's clip to run out", MutedClipSeconds + DefaultPlaybackWaitSeconds);
            // A live PlayControl would have ended and recycled the player within a frame of the clip's end.
            yield return WaitFrames(5);

            Assert.IsTrue(first.IsActive,
                "characterizes: the muted player outlives its clip - it is never recycled on its own and keeps its pool slot.");

            first.Stop(0f);
            yield return WaitForRecycle(first, "an explicit Stop to recycle the stranded muted player");
        }
    }
}