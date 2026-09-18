using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    /// Runtime-only characterization for inventory 3.6: which mixer track a dominator actually lands on.
    /// AudioPlayer.SetupAudioTrack is the only place TrackType becomes Dominator, and it reads IsDominator -
    /// i.e. whether a DominatorPlayer decorator is already attached - at play time, when
    /// SoundManager.LateUpdate drains the queue. That single read-once moment decides routing for the rest
    /// of the player's life, including across a looping handover seam and past the Dominator pool's capacity.
    /// </summary>
    public class DominatorTrackRoutingTests : BroAudioTestFixture
    {
        // Characterizes TEST_FINDINGS #42: the correct-routing half. AsDominator() must be chained in the
        // same frame as Play() to reach the dominator track at all - every dominator test in
        // DominatorEffectParameterTests decorates *after* WaitForPlaybackStart, so none of them exercises
        // this routing; see Play_ThenAsDominatorAfterPlaybackStarted_StaysOnAGenericTrack below for what
        // those tests actually run.
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

            // QuietOthers writes through EffectAutomationHelper, whose GetEffectParameterName maps a
            // dominator Volume effect to Main_Dominated - never to Main. Main is muted outright:
            // SwitchMainTrackMode(true) does ChangeChannel(Main -> Main_Dominated), and ChangeChannel
            // sets the "from" parameter to MinDecibelVolume and the "to"
            // parameter to the passed target. So the ducked level lands on Main_Dominated, and it is
            // Effect.Value that converts it: for EffectType.Volume the setter stores value.ToDecibel(),
            // so 0.2 becomes ~-13.98dB.
            //
            // A non-zero fade is deliberate. With fadeTime 0 the tween drains synchronously inside
            // StartCoroutine - AudioEffectTests.SetEffect_WithDefaultZeroFade_ThenForSeconds_AutoResetsWithoutThrowing
            // already pins that ("a zero fadeTime still applies the parameter right away"), and the bug it
            // came from is Docs/FIXED_ISSUES.md #17 - that file's numbering, not TEST_FINDINGS'. A zero fade
            // would therefore land the ducked value *before* SetEffectTrackParameter's own
            // SwitchMainTrackMode(true) overwrites Main_Dominated with FullDecibelVolume - see
            // Docs/TEST_FINDINGS.md #43.
            dominator.QuietOthers(othersVolume, 0.1f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float v);
                return Mathf.Abs(v - othersVolume.ToDecibel()) < DecibelTolerance;
            }, "Main_Dominated to reach the requested others-volume in decibels", 2f);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float mainWhileDominating));
            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainWhileDominating, DecibelTolerance,
                "While a dominator is active the plain Main channel is muted outright and everything audible is " +
                "routed through Main_Dominated, which carries the ducked level.");

            // The restore is not teardown's job: TweakTrackParameter calls SwitchMainTrackMode(false) once its
            // .While(PlayerIsPlaying) waitable finishes, which is what returns Main to full volume. Asserting it
            // here also keeps this test from leaving a muted Main behind for every later test in the run.
            dominatorPlayer.Stop(0f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Main to return to full volume once the dominator stops", 3f);
        }

        // Characterizes TEST_FINDINGS #42: AsDominator() after playback has started attaches the decorator but
        // cannot move the player - SetupAudioTrack already ran and already took a generic track from the pool.
        // The player stays under Main, which means it filters and ducks *itself* along with everything else.
        // This is the configuration DominatorEffectParameterTests' LowPass/HighPass tests actually run: they
        // pass in both configurations because they only watch the Main_LowPass/Main_HighPass parameter move,
        // which is true either way.
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

        // Characterizes TEST_FINDINGS #44: 3.6 x 2.2 - a dominator that loops. Decorators reach the incoming
        // player at BeginHandover, after its SetupAudioTrack already took a generic track, so ducking persists
        // across the seam but the dominator ducks itself.
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

            // Chained in the same frame as Play, as in Play_AsDominatorInTheSameFrame_* above - the only way
            // even the first player reaches a Dominator track.
            IAudioPlayer player = BroAudio.Play(id);
            IPlayerEffect dominator = player.AsDominator();
            yield return WaitForPlaybackStart(player, "the looping dominator to start playing");

            AudioPlayer firstInstance = InstanceOf(player);
            Assert.IsNotNull(firstInstance, "Precondition: the handle should resolve to a live player.");
            StringAssert.StartsWith(BroName.DominatorTrackName, player.AudioSource.outputAudioMixerGroup.name,
                "Precondition: the first player of a same-frame dominator is routed to a pooled Dominator track.");

            // Non-zero fade, for the ordering reason spelled out in the same-frame test above (finding #43).
            dominator.QuietOthers(OthersVolume, 0.1f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float v);
                return Mathf.Abs(v - OthersVolume.ToDecibel()) < DecibelTolerance;
            }, "Main_Dominated to reach the requested others-volume in decibels, well before the first seam", 2f);

            // The seam itself. UpdateInstance re-points the caller's wrapper at the incoming player, so the
            // handle resolving to a *different* AudioPlayer is the handover - no dsp arithmetic needed to
            // spot it, unlike the clock-derived seams in LoopHandoverTests.
            yield return WaitUntilOrTimeout(() =>
            {
                AudioPlayer current = InstanceOf(player);
                return current && current != firstInstance;
            }, "the loop to hand over to a second player, with the handle following it", ClipSeconds + 4f);

            // Nothing below yields: every assertion has to describe the same moment, just past the seam.
            // MonitorVirtualTrack can hand a Generic track back after half a second of virtualization,
            // and the next seam is only one clip away.
            Assert.IsTrue(player.IsPlaying, "The looping sound must still be audible on the incoming player.");

            // characterizes: AudioPlayer.TransferDecorators/SetDecorators, driven by
            // AudioPlayerInstanceWrapper.UpdateInstance. The list is moved off the outgoing player
            // first, so its Recycle() no longer sees it and never recycles
            // the decorator out from under the incoming one.
            List<AudioPlayerDecorator> decorators = GetDecorators(player);
            Assert.IsNotNull(decorators, "The decorator list must have been transferred to the incoming player.");
            Assert.IsTrue(decorators.Exists(d => d is DominatorPlayer),
                "The DominatorPlayer decorator must survive the handover - it is the caller's only dominator handle.");

            // characterizes: and yet the routing does not follow the decorator. SetupAudioTrack already ran
            // on this player, one warm-up time before the decorator arrived, and read IsDominator == false.
            StringAssert.StartsWith(BroName.GenericTrackName, player.AudioSource.outputAudioMixerGroup.name,
                "A looping dominator's incoming player takes a generic track: the decorator is transferred at " +
                "BeginHandover, which runs after that player's SetupAudioTrack has already chosen a track.");

            // characterizes: the duck itself is untouched by the seam. DominatorPlayer.PlayerIsPlaying
            // is the decorator's own IsActive,
            // and the decorator is re-pointed at the incoming player before the outgoing one is recycled, so
            // the .While() waitable in TweakTrackParameter never observes
            // a false and never runs its auto-reset. Main stays muted - and now mutes the dominator too.
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float mainAfterSeam));
            Assert.AreEqual(AudioConstant.MinDecibelVolume, mainAfterSeam, DecibelTolerance,
                "The dominator's own duck must still be in effect after the seam.");
            SoundManager.Instance.AudioMixer.GetFloat(BroName.MainDominatedTrackName, out float dominatedAfterSeam);
            Assert.AreEqual(OthersVolume.ToDecibel(), dominatedAfterSeam, DecibelTolerance,
                "Main_Dominated must still carry the ducked level - which is now the level the loop plays at.");

            // Same restore-and-assert tail as the same-frame test: Stop is what ends the .While() waitable,
            // and leaving Main at MinDecibelVolume would mute every later test in the run.
            player.Stop(0f);
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.MainTrackName, out float v);
                return Mathf.Abs(v - AudioConstant.FullDecibelVolume) < DecibelTolerance;
            }, "Main to return to full volume once the looping dominator stops", 3f);
        }

        // AudioTrackObjectPool.CreateObject returns null once every Dominator group is checked out, so the
        // player past the pool's capacity plays with no mixer group at all rather than falling back to a
        // generic track. Pool capacity is the mixer's Dominator group count, read here rather than assumed.
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

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape("used up all the [Dominator] tracks")));
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
            Assert.IsNull(overflow.AudioSource.outputAudioMixerGroup,
                "characterizes: the dominator past capacity gets no mixer group - not a generic track.");
            Assert.IsTrue(overflow.IsPlaying, "The unrouted dominator still plays rather than being rejected.");
        }

        private static void AssertHoldsDistinctDominatorTrack(IAudioPlayer player, HashSet<string> names)
        {
            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "A dominator within capacity must be routed.");
            string name = player.AudioSource.outputAudioMixerGroup.name;
            StringAssert.StartsWith(BroName.DominatorTrackName, name, "A dominator within capacity must hold a Dominator track.");
            names.Add(name);
        }

        private static List<AudioPlayerDecorator> GetDecorators(IAudioPlayer player)
            => TestAudioLibrary.GetPrivateField<List<AudioPlayerDecorator>>(InstanceOf(player), TestAudioLibrary.Reflected.AudioPlayer.Decorators);
    }
}
