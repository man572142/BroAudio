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
    /// Edges of otherwise-covered behaviors: the far side of the comb-filtering window, a pause that outlasts
    /// what was left of the clip, a looping entity with a clip Delay, and the one place BroAudio drives
    /// <c>AudioSource.volume</c> at all - a player with no mixer track.
    /// </summary>
    public class PlaybackEdgeCaseTests : BroAudioTestFixture
    {
        /// <summary>Creates a tracked entity wired to the given group, as PlaybackGroupTests does.</summary>
        private SoundID NewGroupedSound(DefaultPlaybackGroup group, string name, float clipSeconds)
        {
            AudioEntity entity = NewEntity(name, BroAudioType.SFX, NewClip(clipSeconds, name + "Clip"));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.Group, group);
            return IdOf(entity);
        }

        #region Comb-filtering window expiry
        /// <summary>
        /// Seconds. Wide, so the in-window control play below cannot drift out of it on a stalled frame.
        /// </summary>
        private const float CombWindowSeconds = 3f;

        /// <summary>How far past the window the second attempt is made - clear of the boundary on any frame.</summary>
        private const int CombWindowOvershootMilliseconds = 500;

        // PlaybackGroupTests pins the rejection inside the window; this pins the other side of it.
        // DefaultPlaybackGroup compares TimeExtension.UnscaledCurrentFrameBeganTime (frame-start unscaled time,
        // in ms) against the previous player's PlaybackStartingTime, so the wait below polls exactly that clock.
        // The first player is paused rather than left playing: a paused player stays active and stays the
        // _combFilteringPreventer entry for its ID, so neither a clip ending early (a fast DSP clock) nor the
        // recycle that would clear the entry can be what lets the later play through. The control play in the
        // same paused state is what shows it is the elapsed time, and nothing else, that changes the verdict.
        [UnityTest]
        public IEnumerator Play_SameIdAfterTheCombFilteringWindowExpires_IsAcceptedAgain()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: CombWindowSeconds);
            SoundID id = NewGroupedSound(group, "CombExpirySfx", 4f);

            IAudioPlayer first = BroAudio.Play(id);
            yield return WaitForPlaybackStart(first, "the first play to start so PlaybackStartingTime is recorded");
            first.Pause(0f);
            yield return WaitFrames(2);

            AudioPlayer firstInstance = InstanceOf(first);
            int startedAtMs = firstInstance.PlaybackStartingTime;
            Assert.Greater(startedAtMs, 0, "Precondition: the first play must have recorded its start time.");
            Assert.IsTrue(SoundManager.Instance.TryGetPreviousPlayerFromCombFilteringPreventer(id, out AudioPlayer tracked) && tracked == firstInstance,
                "Precondition: the paused first player must still be the comb-filtering reference for this ID.");

            Assume.That(TimeExtension.UnscaledCurrentFrameBeganTime - startedAtMs, Is.LessThan(TimeExtension.SecToMs(CombWindowSeconds)),
                "A stalled frame already carried the control play past the window, so the in-window half cannot be staged.");
            IAudioPlayer control = BroAudio.Play(id);
            Assert.IsFalse(control.IsActive, "Control: a same-ID play inside the window is rejected.");

            int acceptAfterMs = startedAtMs + TimeExtension.SecToMs(CombWindowSeconds) + CombWindowOvershootMilliseconds;
            yield return WaitUntilOrTimeout(() => TimeExtension.UnscaledCurrentFrameBeganTime >= acceptAfterMs,
                "the unscaled frame clock to pass the comb-filtering window", CombWindowSeconds + DefaultPlaybackWaitSeconds);

            Assert.IsTrue(SoundManager.Instance.TryGetPreviousPlayerFromCombFilteringPreventer(id, out tracked) && tracked == firstInstance,
                "Precondition: the reference player is unchanged, so only the elapsed time differs from the control.");
            IAudioPlayer later = BroAudio.Play(id);
            Assert.IsTrue(later.IsActive, "Once the window has passed, the same ID is accepted again.");
        }
        #endregion

        #region Pause longer than the remaining clip
        // PlayControl waits on `dspTime < _playbackEndDspTime`, an absolute DSP time fixed at the start. A pause
        // replaces that coroutine with StopControl, and the resume's ResolveScheduledTiming slides the end time
        // by the whole pause (then RecalculateScheduledEndTime re-derives it from the playhead). So a pause
        // longer than what was left of the clip still resumes from the paused sample and plays the remainder -
        // the stale end time, long past by then, would otherwise end the player in the frame it resumed.
        [UnityTest]
        public IEnumerator Pause_LongerThanTheRemainingClip_ResumesFromThePausedSampleAndPlaysTheRemainder()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 2.5f;
            SoundID id = NewSound("LongPauseSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitDspSeconds(0.5);

            player.Pause(0f);
            yield return null;
            Assert.IsFalse(player.IsPlaying, "Precondition: a zero-fade pause freezes the source at once.");
            float pausedAt = player.AudioSource.time;
            double remaining = ClipSeconds - pausedAt;

            yield return WaitDspSeconds(remaining + 1.0);

            Assert.IsTrue(player.IsActive, "A paused player outlives its clip's original end time.");
            Assert.IsFalse(player.IsPlaying, "It stays paused for as long as the caller leaves it paused.");
            Assert.AreEqual(pausedAt, player.AudioSource.time, 0.01f, "The playhead does not move while paused.");

            player.UnPause(0f);
            yield return WaitForPlaybackStart(player, "the long-paused player to resume");
            yield return WaitDspSeconds(0.5);

            Assert.IsTrue(player.IsPlaying,
                "The remainder is still playing half a second after the resume - the end time moved with the pause.");
            Assert.Greater(player.AudioSource.time, pausedAt + 0.25f, "Playback resumed from the paused sample and advanced.");

            yield return WaitForRecycle(player, "the remainder to finish and the player to recycle",
                (float)remaining + DefaultPlaybackWaitSeconds);
        }
        #endregion

        #region Loop with clip Delay
        // SetClipDelayIfNotScheduled only applies clip.Delay while _pref.ScheduledStartTime is still 0. The first
        // iteration gets it; ScheduleNextPlayback then hands the next player a ScheduledStartTime of the seam, so
        // the delay is not applied again - it is a one-off pre-roll, not a gap between iterations.
        // (A delayed first iteration also skips the loop warm-up: ResolveScheduledTiming only adds it when
        // ScheduledStartTime is still 0.)
        [UnityTest]
        public IEnumerator Loop_WithAClipDelay_DelaysOnlyTheFirstIterationAndNotEachSeam()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 3f;
            const float DelaySeconds = 2f;
            AudioEntity entity = NewEntity("LoopDelaySfx", BroAudioType.SFX, NewClip(ClipSeconds));
            entity.Clips[0].Delay = DelaySeconds;
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player, "the delayed first iteration to be scheduled (isPlaying flips at PlayScheduled)");
            AudioPlayer firstIteration = InstanceOf(player);

            yield return WaitDspSeconds(DelaySeconds * 0.5f);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Halfway through clip.Delay, the first iteration must still be held.");

            yield return WaitUntilOrTimeout(() => InstanceOf(player) != firstIteration,
                "the handle to hand over to the second iteration at the first seam", DelaySeconds + ClipSeconds + HandoverWaitSeconds);

            // With the delay re-applied at the seam, the second iteration would still be held at sample 0 here.
            yield return WaitDspSeconds(1.0);
            Assert.Greater(player.AudioSource.time, 0.5f,
                "characterizes: the second iteration starts at the seam - clip.Delay is not re-applied per loop.");
        }
        #endregion

#if !UNITY_WEBGL
        #region AudioSource.volume
        // AudioPlayer.UpdateVolume writes the linear product clip * track * type as decibels to its mixer track,
        // and only falls back to AudioSource.volume - linearly, clamped to [0, 1] - when the player holds no
        // track (TrySetMixerDecibelVolume fails). A player past the Dominator pool's capacity is the cheapest way
        // to get one without a track (DominatorTrackRoutingTests.AsDominator_BeyondThePoolCapacity_PlaysUnroutedAndWarns).
        // So the same SetVolume(0.5) leaves a routed player's AudioSource.volume at 1 and puts exactly 0.5 - not a
        // decibel value, not a curve - on the unrouted one: the engine applies AudioSource.volume linearly.
        [UnityTest]
        public IEnumerator SetVolume_LeavesARoutedPlayersAudioSourceVolumeAtFull_ButAnUnroutedPlayerTakesTheLinearValue()
        {
            const float TargetVolume = 0.5f;

            IAudioPlayer routed = BroAudio.Play(NewSound("RoutedVolumeSfx", BroAudioType.SFX, NewClip(4f)));

            int capacity = SoundManager.Instance.AudioMixer.FindMatchingGroups(BroName.DominatorTrackName).Length;
            Assert.Greater(capacity, 0, "Precondition: the mixer must have Dominator groups.");
            var dominators = new List<IAudioPlayer>();
            for (int i = 0; i <= capacity; i++)
            {
                IAudioPlayer dominator = BroAudio.Play(NewSound($"UnroutedVolumeSfx{i}", BroAudioType.SFX, NewClip(4f)));
                dominator.AsDominator(); // same frame as Play, so SetupAudioTrack sees IsDominator
                dominators.Add(dominator);
            }
            IAudioPlayer unrouted = dominators[capacity];

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            yield return WaitForPlaybackStart(routed, "the routed player to start");
            yield return WaitForPlaybackStart(unrouted, "the dominator past capacity to start");

            Assert.IsNotNull(routed.AudioSource.outputAudioMixerGroup, "Precondition: the SFX player holds a generic track.");
            Assert.IsNull(unrouted.AudioSource.outputAudioMixerGroup, "Precondition: the dominator past capacity holds no track.");

            routed.SetVolume(TargetVolume, 0f);
            unrouted.SetVolume(TargetVolume, 0f);
            yield return WaitFrames(1);

            Assert.AreEqual(TargetVolume, routed.GetVolume(), LinearTolerance);
            Assert.AreEqual(AudioConstant.FullVolume, routed.AudioSource.volume, LinearTolerance,
                "A routed player's loudness lives on its mixer track; its AudioSource.volume is never written.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(routed.AudioSource.outputAudioMixerGroup.name, out float trackDb));
            Assert.AreEqual(TargetVolume.ToDecibel(), trackDb, DecibelTolerance, "The routed player's volume is on its track, in decibels.");

            Assert.AreEqual(TargetVolume, unrouted.GetVolume(), LinearTolerance);
            Assert.AreEqual(TargetVolume, unrouted.AudioSource.volume, LinearTolerance,
                "An unrouted player's AudioSource.volume carries the linear product itself, not its decibel value.");
        }
        #endregion
#endif
    }
}