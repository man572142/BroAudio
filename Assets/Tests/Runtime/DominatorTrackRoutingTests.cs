using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Which mixer track a dominator lands on. AudioPlayer.SetupAudioTrack reads IsDominator (decorator already
    /// attached?) once, when SoundManager.LateUpdate drains the queue, and that read fixes routing for the
    /// player's life.
    /// </summary>
    public class DominatorTrackRoutingTests : BroAudioTestFixture
    {
        // Pins TEST_FINDINGS #42, correct-routing half: AsDominator() chained in Play()'s frame.
        [UnityTest]
        [Category("Finding_42")]
        public IEnumerator Play_AsDominatorInTheSameFrame_RoutesToADominatorTrackAndDucksTheMainTrack()
        {
            const float othersVolume = 0.2f;
            SoundID dominatorId = NewSound("SameFrameDominatorSfx", BroAudioType.SFX, NewClip(4f));

            // Chained before the queue drains, so IsDominator is true by the time SetupAudioTrack runs.
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            yield return WaitForPlaybackStart(dominatorPlayer, "the dominator to start playing");

            StringAssert.StartsWith(BroName.DominatorTrackName, dominatorPlayer.AudioSource.outputAudioMixerGroup.name,
                "A player decorated as a dominator before the queue drained must be routed to a pooled Dominator track, " +
                "not to a generic Track* group under Main - otherwise it is filtered by its own QuietOthers/LowPassOthers.");

            // Non-zero fade on purpose: a zero fade applies synchronously, before SwitchMainTrackMode(true)
            // overwrites Main_Dominated with FullDecibelVolume (TEST_FINDINGS #43).
            dominator.QuietOthers(othersVolume, 0.1f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float v);
                return Mathf.Abs(v - othersVolume.ToDecibel()) < DecibelTolerance;
            }, "Main_Dominated to reach the requested others-volume in decibels", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float mainWhileDominating));
            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainWhileDominating, DecibelTolerance,
                "While a dominator is active the plain Main channel is muted outright and everything audible is " +
                "routed through Main_Dominated, which carries the ducked level.");

            // Stop ends TweakTrackParameter's .While(), which restores Main; teardown doesn't, and a muted Main
            // would silence every later test.
            dominatorPlayer.Stop(0f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Main to return to full volume once the dominator stops", RampConvergenceWaitSeconds);
        }

        // Pins TEST_FINDINGS #42.
        [UnityTest]
        [Category("Finding_42")]
        public IEnumerator Play_ThenAsDominatorAfterPlaybackStarted_StaysOnAGenericTrack()
        {
            SoundID lateId = NewSound("LateDominatorSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer latePlayer = BroAudio.Play(lateId);
            yield return WaitForPlaybackStart(latePlayer, "the player to start playing");

            StringAssert.StartsWith(BroName.GenericTrackName, latePlayer.AudioSource.outputAudioMixerGroup.name,
                "Precondition: an undecorated player starts on a pooled generic track.");

            latePlayer.AsDominator();
            yield return WaitFrames(2);

            StringAssert.StartsWith(BroName.GenericTrackName, latePlayer.AudioSource.outputAudioMixerGroup.name,
                "Decorating after play started must leave the player on its generic track - TrackType is only " +
                "consulted by SetupAudioTrack, which has already run. Nothing re-routes it.");
        }

        // Pins TEST_FINDINGS #44.
        [UnityTest]
        [Category("Finding_44")]
        public IEnumerator Play_LoopingDominator_KeepsDuckingAcrossASeamButTheIncomingPlayerTakesAGenericTrack()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 1f;
            const float OthersVolume = 0.2f;
            AudioEntity entity = NewEntity("LoopingDominatorSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            // Same-frame chain: the only way even the first player reaches a Dominator track.
            IAudioPlayer player = BroAudio.Play(id);
            IPlayerEffect dominator = player.AsDominator();
            yield return WaitForPlaybackStart(player, "the looping dominator to start playing");

            AudioPlayer firstInstance = InstanceOf(player);
            Assert.IsTrue(firstInstance, "Precondition: the handle should resolve to a live player.");
            StringAssert.StartsWith(BroName.DominatorTrackName, player.AudioSource.outputAudioMixerGroup.name,
                "Precondition: the first player of a same-frame dominator is routed to a pooled Dominator track.");

            // Non-zero fade: TEST_FINDINGS #43.
            dominator.QuietOthers(OthersVolume, 0.1f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float v);
                return Mathf.Abs(v - OthersVolume.ToDecibel()) < DecibelTolerance;
            }, "Main_Dominated to reach the requested others-volume in decibels, well before the first seam", DefaultPlaybackWaitSeconds);

            // UpdateInstance re-points the handle, so resolving to a different AudioPlayer is the seam.
            yield return WaitUntilOrTimeout(() =>
            {
                AudioPlayer current = InstanceOf(player);
                return current && current != firstInstance;
            }, "the loop to hand over to a second player, with the handle following it", ClipSeconds + 4f);

            // Nothing below yields: every assertion has to describe the same moment, just past the seam.
            // MonitorVirtualTrack can hand a Generic track back after half a second of virtualization,
            // and the next seam is only one clip away.
            Assert.IsTrue(player.IsPlaying, "The looping sound must still be audible on the incoming player.");

            // The list moves off the outgoing player first, so its Recycle() can't recycle the decorator.
            List<AudioPlayerDecorator> decorators = GetDecorators(player);
            Assert.IsNotNull(decorators, "The decorator list must have been transferred to the incoming player.");
            Assert.IsTrue(decorators.Exists(d => d is DominatorPlayer),
                "The DominatorPlayer decorator must survive the handover - it is the caller's only dominator handle.");

            StringAssert.StartsWith(BroName.GenericTrackName, player.AudioSource.outputAudioMixerGroup.name,
                "A looping dominator's incoming player takes a generic track: the decorator is transferred at " +
                "BeginHandover, which runs after that player's SetupAudioTrack has already chosen a track.");

            // The decorator is re-pointed before the outgoing player recycles, so TweakTrackParameter's .While()
            // never sees false and the duck survives the seam.
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float mainAfterSeam));
            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainAfterSeam, DecibelTolerance,
                "The dominator's own duck must still be in effect after the seam.");
            SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedAfterSeam);
            Assert.AreEqual(OthersVolume.ToDecibel(), dominatedAfterSeam, DecibelTolerance,
                "Main_Dominated must still carry the ducked level - which is now the level the loop plays at.");

            // Restore Main, as in the same-frame test.
            player.Stop(0f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Main to return to full volume once the looping dominator stops", RampConvergenceWaitSeconds);
        }

        // Capacity is the mixer's Dominator group count, read rather than assumed.
        [UnityTest]
        public IEnumerator AsDominator_BeyondThePoolCapacity_PlaysUnroutedAndWarns()
        {
            int capacity = SoundManager.Instance.AudioMixer.FindMatchingGroups(BroName.DominatorTrackName).Length;
            Assert.Greater(capacity, 0, "Precondition: the mixer must have Dominator groups.");

            var players = new List<IAudioPlayer>();
            for (int i = 0; i <= capacity; i++)
            {
                IAudioPlayer player = BroAudio.Play(NewSound($"PoolDominatorSfx{i}", BroAudioType.SFX, NewClip(4f)));
                player.AsDominator(); // same frame as Play, so SetupAudioTrack sees IsDominator
                players.Add(player);
            }

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            foreach (IAudioPlayer player in players)
            {
                yield return WaitForPlaybackStart(player, "every dominator, including the one past capacity, to start");
            }

            var groupNames = new HashSet<string>();
            for (int i = 0; i < capacity; i++)
            {
                AssertHoldsDistinctDominatorTrack(players[i], groupNames);
            }
            Assert.AreEqual(capacity, groupNames.Count, "The dominators within capacity should each hold a distinct Dominator track.");

            IAudioPlayer overflow = players[capacity];
            Assert.IsFalse(overflow.AudioSource.outputAudioMixerGroup,
                "characterizes: the dominator past capacity gets no mixer group - not a generic track.");
            Assert.IsTrue(overflow.IsPlaying, "The unrouted dominator still plays rather than being rejected.");
        }

        private static void AssertHoldsDistinctDominatorTrack(IAudioPlayer player, HashSet<string> names)
        {
            Assert.IsTrue(player.AudioSource.outputAudioMixerGroup, "A dominator within capacity must be routed.");
            string name = player.AudioSource.outputAudioMixerGroup.name;
            StringAssert.StartsWith(BroName.DominatorTrackName, name, "A dominator within capacity must hold a Dominator track.");
            names.Add(name);
        }

        private static List<AudioPlayerDecorator> GetDecorators(IAudioPlayer player)
            => TestAudioLibrary.GetPrivateField<List<AudioPlayerDecorator>>(InstanceOf(player), TestAudioLibrary.Reflected.AudioPlayer.Decorators);
    }
}
