using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Plain, seamless and Chained looping, and pausing mid-handover - all via player handover, never
    /// AudioSource.loop. The handle Play returned surviving every seam is public API (a looping BGM's owner
    /// has no other handle). Tests about which players exist inspect the pool via GetActivePlayers instead.
    /// </summary>
    [Category("Slow")]
    public class LoopHandoverTests : BroAudioTestFixture
    {
        /// <summary>
        /// The players behind BroAudio.HasAnyPlayingInstances(id), returned so a test can inspect or count them.
        /// </summary>
        private static List<AudioPlayer> GetActivePlayers(SoundID id)
        {
            var all = CurrentAudioPlayers();
            var matches = new List<AudioPlayer>();
            foreach (AudioPlayer candidate in all)
            {
                if (candidate.IsActive && candidate.IsPlaying && candidate.ID.Equals(id))
                {
                    matches.Add(candidate);
                }
            }
            return matches;
        }

        private static AudioClip ClipOf(AudioPlayer player) => ((IAudioPlayer)player).AudioSource.clip;

        /// <summary>
        /// Trap: a PlayScheduled-armed player already reports isPlaying, so only an advancing playhead
        /// separates a heard player from a queued one.
        /// </summary>
        private static int PlayheadOf(AudioPlayer player) => ((IAudioPlayer)player).AudioSource.timeSamples;

        /// <summary>
        /// Reads back the crossfade's _clipVolume alone, as long as the test leaves track and type volume at 1.
        /// </summary>
        private static float VolumeOf(AudioPlayer player) => ((IAudioPlayer)player).GetVolume();

        // Volume, OnEnd and Stop on the caller's original handle after two seams. A plain loop on purpose:
        // with no crossfade _clipVolume stays at 1, so GetVolume() reads back the track volume alone.
        [UnityTest]
        public IEnumerator Play_WithPlainLoop_HandleKeepsDrivingTheSoundAcrossTwoSeams()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.4f;
            const float TargetVolume = 0.3f;
            AudioEntity entity = NewEntity("HandoverHandleSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            int onEndCount = 0;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", DefaultPlaybackWaitSeconds);

            Assert.IsFalse(player.AudioSource.loop,
                "characterizes: BroAudio implements looping via player handover, never via AudioSource.loop.");

            player.OnEnd(_ => onEndCount++);
            player.SetVolume(TargetVolume);
            Assert.AreEqual(TargetVolume, player.GetVolume(), LinearTolerance,
                "Precondition: SetVolume must land on the first player before any handover.");

            double secondSeamDsp = startDsp.Value + (ClipSeconds * 2);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= secondSeamDsp + 0.2,
                "the dsp clock to pass two loop seams", HandoverWaitSeconds);

            Assert.IsTrue(player.IsActive,
                "The caller's IAudioPlayer must still be live after two handovers - UpdateInstance re-points " +
                "it at the incoming player, and the owner of a looping sound has no other handle to hold.");
            Assert.AreEqual(TargetVolume, player.GetVolume(), LinearTolerance,
                "The volume set before the first seam must ride across both handovers, via " +
                "PlaybackHandoverData.TrackVolume and ReceiveHandover's _trackVolume.Complete.");
            Assert.AreEqual(0, onEndCount,
                "characterizes: OnEnd is an end-of-sound callback, not a per-iteration one - BeginHandover " +
                "transfers the delegate away before the outgoing player's EndPlaying could invoke it.");

            // A handle stranded on the recycled first player would make this a no-op.
            player.Stop(0f);
            yield return WaitFrames(3);

            Assert.AreEqual(0, GetActivePlayers(id).Count,
                "Stop() on the handle must stop the handed-over player and the one already scheduled behind it.");
            Assert.AreEqual(1, onEndCount,
                "OnEnd must fire exactly once, at the real end of the sound, no matter how many seams it crossed.");
        }

        /// <summary>
        /// DSP time of each seam at which the handle is re-pointed. Polled per frame, so up to a frame late.
        /// </summary>
        private sealed class HandoverRecorder
        {
            private readonly IAudioPlayer _handle;
            private AudioPlayer _current;

            public readonly List<double> SeamDspTimes = new List<double>();

            public HandoverRecorder(IAudioPlayer handle)
            {
                _handle = handle;
                _current = InstanceOf(handle);
            }

            public int Count => SeamDspTimes.Count;

            public void Poll()
            {
                AudioPlayer instance = InstanceOf(_handle);
                if (instance && instance != _current)
                {
                    _current = instance;
                    SeamDspTimes.Add(AudioSettings.dspTime);
                }
            }
        }

        // Pitch below 1 is the case that can truncate: the seam player must derive its end from the carried
        // pitch, or each iteration is cut to the unpitched length. The DSP period between two seams is 2.5s if
        // honoured, 1s if not; the tolerance puts the edge at their midpoint, 0.75s from either.
        [UnityTest]
        public IEnumerator Play_WithPlainLoop_PitchBelowOneAndFollowTargetRideAcrossTwoSeams()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 1f;
            const float Pitch = 0.4f;
            const double PitchedPeriodSeconds = ClipSeconds / Pitch;
            const double PeriodToleranceSeconds = 0.75;
            AudioEntity entity = NewEntity("PitchedLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            Transform target = Track(new GameObject("LoopFollowTarget")).transform;
            target.position = new Vector3(3f, 0f, 0f);

            IAudioPlayer player = BroAudio.Play(id, target);
            yield return WaitForPlaybackStart(player);
            player.SetPitch(Pitch);
            Assert.AreEqual(Pitch, player.AudioSource.pitch, 0.001f, "Precondition: an immediate SetPitch must land on the first player.");

            HandoverRecorder seams = new HandoverRecorder(player);
            float deadline = Time.realtimeSinceStartup + (HandoverWaitSeconds * 2);
            while (seams.Count < 2)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline,
                    $"Timed out after {seams.Count} seam(s): the pitched loop stopped handing over.");
                seams.Poll();
                yield return null;
            }

            Assert.IsTrue(player.IsActive, "The caller's handle must still be live after two handovers.");
            Assert.AreEqual(Pitch, player.AudioSource.pitch, 0.001f,
                "A pitch set mid-play must ride across both seams (PlaybackHandoverData.Pitch) rather than reset to the entity's 1.0.");
            double period = seams.SeamDspTimes[1] - seams.SeamDspTimes[0];
            Assert.AreEqual(PitchedPeriodSeconds, period, PeriodToleranceSeconds,
                $"The seam player must play the whole clip at the carried pitch: one iteration lasts ClipSeconds / Pitch " +
                $"({PitchedPeriodSeconds}s), not the unpitched {ClipSeconds}s - measured {period:F3}s.");

            // A seam player that lost the follow target would stay wherever it was spawned.
            AudioPlayer current = InstanceOf(player);
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, player.AudioSource.spatialBlend, 0.001f,
                "A follow-target play is forced to 3D, and the seam player must be as well.");
            target.position = new Vector3(-4f, 0f, 2f);
            yield return WaitFrames(2);
            Assert.AreSame(current, InstanceOf(player), "Precondition: no seam fell inside the two frames the follow check waits.");
            Assert.Less(Vector3.Distance(target.position, current.transform.position), 0.001f,
                "The seam player must keep following the target the sound was played with.");
        }

        // The 3s fade spans several 0.5s iterations. Halfway, a fade a seam snapped to its target reads 0.2, a
        // dropped one reads 1; the factory OutSine ease puts a carried fade near 0.45, inside the band below.
        [UnityTest]
        public IEnumerator Play_WithPlainLoop_InFlightFadeTrackEffectAndPositionRideAcrossSeams()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.5f;
            const float TargetVolume = 0.2f;
            const float FadeSeconds = 3f;
            const float MidFadeSampleSeconds = FadeSeconds / 2f;
            const float MidFadeFloor = 0.25f;
            const float MidFadeCeiling = 0.9f;
            Vector3 position = new Vector3(5f, 0f, 0f);
            AudioEntity entity = NewEntity("FadingLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

#if !UNITY_WEBGL
            BroAudio.SetEffect(Effect.LowPass(800f), BroAudioType.SFX);
#endif

            IAudioPlayer player = BroAudio.Play(id, position);
            yield return WaitForPlaybackStart(player);
#if !UNITY_WEBGL
            Assert.AreNotEqual(EffectType.None, InstanceOf(player).CurrentActiveTrackEffects & EffectType.LowPass,
                "Precondition: the first player must start routed through the LowPass effect.");
#endif

            player.SetVolume(TargetVolume, FadeSeconds);
            HandoverRecorder seams = new HandoverRecorder(player);
            float fadeStartedAt = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - fadeStartedAt < MidFadeSampleSeconds)
            {
                seams.Poll();
                yield return null;
            }

            float midFade = player.GetVolume();
            Assert.GreaterOrEqual(seams.Count, 1, "Precondition: the fade must have crossed at least one seam by its midpoint.");
            Assert.Greater(midFade, MidFadeFloor,
                $"Halfway through a {FadeSeconds}s fade, across {seams.Count} seam(s), the volume should still be above its {TargetVolume} target - a read at the target means a seam completed the fade early.");
            Assert.Less(midFade, MidFadeCeiling,
                $"Halfway through a {FadeSeconds}s fade, across {seams.Count} seam(s), the volume should be well below full - a read near 1 means a seam dropped the fade.");

            float deadline = Time.realtimeSinceStartup + (FadeSeconds - MidFadeSampleSeconds) + 1.5f;
            while (Mathf.Abs(player.GetVolume() - TargetVolume) > LinearTolerance)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline,
                    $"Timed out waiting for the carried fade to land on {TargetVolume} (last read {player.GetVolume():F3}, {seams.Count} seam(s)) - a fade restarted at each seam never finishes on time.");
                seams.Poll();
                yield return null;
            }

            Assert.GreaterOrEqual(seams.Count, 3, "The fade should have been carried across several seams, not settled on one player.");
            Assert.IsTrue(player.IsActive, "The caller's handle must still be live after the seams.");

            AudioPlayer current = InstanceOf(player);
            Assert.Less(Vector3.Distance(position, current.transform.position), 0.001f,
                "The seam player must play at the position the sound was played at.");
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, player.AudioSource.spatialBlend, 0.001f,
                "A positioned play is forced to 3D, and the seam player must be as well.");
#if !UNITY_WEBGL
            Assert.AreNotEqual(EffectType.None, current.CurrentActiveTrackEffects & EffectType.LowPass,
                "The seam player must still route through the LowPass effect: ReceiveHandover overrides its track " +
                "effects with the ones the outgoing player carried, so a handover that lost them would clear it.");
#endif
        }

        // Trap: two live players is not proof of a crossfade - a plain loop has two for the ~0.1s warm-up before
        // every seam, both reporting isPlaying with flat volumes. Only a crossfade gives a long overlap with an
        // advancing incoming playhead and the two clip volumes moving in opposite directions.
        [UnityTest]
        public IEnumerator Play_WithSeamlessLoop_CrossfadesTwoPlayersAcrossTheSeam()
        {
            yield return RequireRealtimeAudioClock();

            // Must exceed TransitionSeconds * 2: at that length the crossfades abut, two players stay live
            // forever and the single-player tail wait never comes true.
            const float ClipSeconds = 5f;
            const float TransitionSeconds = 2f;
            // A slow frame would have to eat a full second to miss this; the plain-loop ~0.1s overlap never reaches it.
            const double MinOverlapSeconds = TransitionSeconds / 2d;
            // Safe under the factory eases (OutCubic in, OutSine out); a steep enough curve could travel less.
            const float MinVolumeTravel = 0.25f;
            AudioEntity entity = NewEntity("SeamlessLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", DefaultPlaybackWaitSeconds);

            double crossfadeStartDsp = startDsp.Value + ClipSeconds - TransitionSeconds;
            double seamDsp = startDsp.Value + ClipSeconds;
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= crossfadeStartDsp,
                "the dsp clock to reach the start of the crossfade window", HandoverWaitSeconds * 2);

            // Accumulate the overlap every frame, don't sample chosen instants: an overshoot then only trims it.
            double firstOverlapDsp = -1d;
            double lastOverlapDsp = -1d;
            float outgoingVolumeFirst = 0f;
            float outgoingVolumeLast = 0f;
            float incomingVolumeFirst = 0f;
            float incomingVolumeLast = 0f;
            int incomingPlayheadLast = 0;
            float scanDeadline = Time.realtimeSinceStartup + TransitionSeconds + 5f;
            while (AudioSettings.dspTime < seamDsp)
            {
                if (Time.realtimeSinceStartup > scanDeadline)
                {
                    Assert.Fail("Timed out waiting for: the dsp clock to reach the loop seam. This scan has to " +
                                "read state every frame, so it stands in for WaitUntilOrTimeout's stall guard.");
                }

                List<AudioPlayer> active = GetActivePlayers(id);
                if (active.Count == 2)
                {
                    // The outgoing player is the one further into its clip.
                    bool isFirstOutgoing = PlayheadOf(active[0]) > PlayheadOf(active[1]);
                    AudioPlayer outgoing = isFirstOutgoing ? active[0] : active[1];
                    AudioPlayer incoming = isFirstOutgoing ? active[1] : active[0];
                    if (firstOverlapDsp < 0d)
                    {
                        firstOverlapDsp = AudioSettings.dspTime;
                        outgoingVolumeFirst = VolumeOf(outgoing);
                        incomingVolumeFirst = VolumeOf(incoming);
                    }
                    lastOverlapDsp = AudioSettings.dspTime;
                    outgoingVolumeLast = VolumeOf(outgoing);
                    incomingVolumeLast = VolumeOf(incoming);
                    incomingPlayheadLast = PlayheadOf(incoming);
                }
                yield return null;
            }

            Assert.GreaterOrEqual(firstOverlapDsp, 0d,
                "Two players of this sound were never simultaneously active and audible during the transition " +
                "window - nothing was crossfaded at all.");
            Assert.Greater(lastOverlapDsp - firstOverlapDsp, MinOverlapSeconds,
                $"The two players overlapped for only {lastOverlapDsp - firstOverlapDsp:F3}s of the " +
                $"{TransitionSeconds}s transition. A seamless loop holds both open for the whole transition " +
                "time; a plain loop overlaps only for ScheduledPlaybackWarmUpTime (~0.1s) before the seam.");
            Assert.Greater(incomingPlayheadLast, 0,
                "The incoming player must already be rendering samples while the outgoing one is still audible. " +
                "AudioSource.isPlaying reports true from the PlayScheduled call onwards, so only the playhead " +
                "tells a crossfade apart from a player that is merely warmed up and waiting for the seam.");
            Assert.Less(outgoingVolumeLast, outgoingVolumeFirst - MinVolumeTravel,
                "The outgoing player must be fading out across the transition window - PlaybackPreference." +
                "ApplySeamlessFade applies TransitionTime as its fade-out.");
            Assert.Greater(incomingVolumeLast, incomingVolumeFirst + MinVolumeTravel,
                "The incoming player must be fading in across the same window - the other half of the crossfade, " +
                "carried over on the handed-over pref.");

            yield return WaitUntilOrTimeout(() => GetActivePlayers(id).Count == 1,
                "the crossfade to finish, leaving only the handed-over player active", HandoverWaitSeconds);
        }

        // Unlike every other handover here, the outro handover has no DSP gate: it is in effect when Stop() returns.
        [UnityTest]
        public IEnumerator ChainedPlayMode_HandsOverIntroToLoopToOutro_OutroHandoverFiresSynchronouslyOnStop()
        {
            yield return RequireRealtimeAudioClock();

            // Long intro keeps the "only the intro player" check over a second clear of the loop player's pre-spawn.
            const float IntroSeconds = 2f;
            const float ClipSeconds = 0.3f;
            AudioClip introClip = NewClip(IntroSeconds, "Intro");
            AudioClip loopClip = NewClip(ClipSeconds, "Loop");
            AudioClip outroClip = NewClip(ClipSeconds, "Outro");
            AudioEntity entity = NewEntity("ChainedSfx", BroAudioType.SFX, introClip, loopClip, outroClip);
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Chained);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player, "the intro clip to start playing");
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the intro clip", DefaultPlaybackWaitSeconds);

            List<AudioPlayer> atStart = GetActivePlayers(id);
            Assert.AreEqual(1, atStart.Count, "Only the intro player should be active at the very start.");
            Assert.AreEqual(introClip, ClipOf(atStart[0]),
                "Chained playback must start on the intro (Start-stage) clip.");

            yield return WaitUntilOrTimeout(() => GetActivePlayers(id).Exists(p => ClipOf(p) == loopClip),
                "the intro clip to hand over to the loop clip", HandoverWaitSeconds);

            double keepLoopingUntilDsp = startDsp.Value + IntroSeconds + (ClipSeconds * 2);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= keepLoopingUntilDsp,
                "the dsp clock to pass a second loop-stage seam", HandoverWaitSeconds);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The loop stage must still be alive after re-chaining at least twice.");

            BroAudio.Stop(id);
            bool outroAlreadyActiveSameFrame = GetActivePlayers(id).Exists(p => ClipOf(p) == outroClip);
            Assert.IsTrue(outroAlreadyActiveSameFrame,
                "characterizes: the Chained outro handover fires synchronously from StopControl, not gated " +
                "behind a DSP wait like every other handover in this file.");

            yield return WaitUntilOrTimeout(() => !BroAudio.HasAnyPlayingInstances(id),
                "the outro clip to finish and playback to end for good (no further handover past the End stage)", HandoverWaitSeconds);
        }

        // Pausing inside a seamless-loop seam, after BeginHandover has run, is a known NRE source.
        // Pause(0f), not Pause(): the no-arg overload resolves to a TransitionTime-long fade, so the source
        // would not actually pause until well past the seam.
        [UnityTest]
        public IEnumerator Pause_DuringSeamlessLoopHandoverSeam_DoesNotThrowAndResumes()
        {
            yield return RequireRealtimeAudioClock();

            // 1s of slack either side of the crossfade midpoint, and the next crossfade a further second clear.
            const float ClipSeconds = 5f;
            const float TransitionSeconds = 2f;
            AudioEntity entity = NewEntity("PauseSeamSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", DefaultPlaybackWaitSeconds);

            double seamMidpointDsp = startDsp.Value + ClipSeconds - (TransitionSeconds / 2.0);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= seamMidpointDsp,
                "the dsp clock to reach the middle of the crossfade seam", HandoverWaitSeconds * 2);

            Assert.AreEqual(2, GetActivePlayers(id).Count,
                "Precondition: the pause has to land while the outgoing and incoming players are both audible. " +
                "A failure here means the frame overshot the crossfade window, not that pausing misbehaved.");

            Assert.DoesNotThrow(() => player.Pause(0f),
                "Pause landing inside a seamless-loop handover seam must never throw.");

            Assert.IsFalse(player.IsPlaying,
                "With the fade-out region skipped, StopControl reaches AudioSource.Pause() synchronously: the " +
                "handle - which BeginHandover has already re-pointed at the incoming player - must stop reading " +
                "as playing the instant Pause returns.");
            Assert.IsTrue(player.IsActive, "A paused player stays active; pause is not a teardown.");
            List<AudioPlayer> stillPlaying = GetActivePlayers(id);
            Assert.AreEqual(1, stillPlaying.Count,
                "characterizes: pausing the caller's handle inside the seam pauses the incoming player only - " +
                "the outgoing player is nobody's handle any more and plays its fade-out tail to its own end.");

            int pausedPlayhead = player.AudioSource.timeSamples;
            Assert.Greater(pausedPlayhead, 0,
                "Precondition: the paused player must already be rendering mid-crossfade rather than still " +
                "sitting at its start sample, warmed up and waiting for the seam.");
            Assert.Less(pausedPlayhead, PlayheadOf(stillPlaying[0]),
                "The pause must have landed on the *incoming* player: BeginHandover re-points the handle at it " +
                "at the top of the crossfade window, which leaves the one still playing - the outgoing player - " +
                "a whole TransitionTime further into its clip.");

            // The playhead advances on the dsp clock, so measure the freeze on it.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(pausedPlayhead, player.AudioSource.timeSamples,
                "A paused AudioSource must not advance its playhead, not even one paused mid-handover.");

            yield return WaitUntilOrTimeout(() => !BroAudio.HasAnyPlayingInstances(id),
                "the outgoing player's tail to reach its scheduled end, leaving the paused sound silent",
                TransitionSeconds + 2f);

            Assert.DoesNotThrow(() => player.UnPause(),
                "UnPause after a seam-straddling pause must never throw.");

            yield return WaitUntilOrTimeout(() => player.IsPlaying && player.AudioSource.timeSamples > pausedPlayhead,
                "the resumed player to carry its playhead on past where it froze", HandoverWaitSeconds);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The resumed sound must be audible again through the public surface, not merely un-paused internally.");
        }

        // Sequence mode makes each re-pick a distinct, predictable clip.
        [UnityTest]
        public IEnumerator Loop_WithChangeClipPerLoopAndSequence_AdvancesClipAtEachSeam()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.5f;
            AudioClip[] clips = { NewClip(ClipSeconds, "PerLoopClip0"), NewClip(ClipSeconds, "PerLoopClip1"), NewClip(ClipSeconds, "PerLoopClip2") };
            AudioEntity entity = NewEntity("ChangeClipPerLoopSfx", BroAudioType.SFX, clips);
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Sequence);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Flags), AudioEntityFlag.ChangeClipPerLoop);
            SoundID id = IdOf(entity);

            BroAudio.Play(id);

            // Keyed on clip change, so a pooled player reused for a later iteration still counts.
            var picked = new List<AudioClip>();
            var lastClipOf = new Dictionary<AudioPlayer, AudioClip>();
            float deadline = Time.realtimeSinceStartup + (ClipSeconds * 4f) + 3f;
            while (picked.Count < 4)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline,
                    $"Timed out after {picked.Count} iteration(s): [{string.Join(", ", picked.ConvertAll(c => c.name))}] - the loop stopped handing over.");
                foreach (AudioPlayer active in GetActivePlayers(id))
                {
                    AudioClip clip = ClipOf(active);
                    if (clip && (!lastClipOf.TryGetValue(active, out AudioClip last) || last != clip))
                    {
                        lastClipOf[active] = clip;
                        picked.Add(clip);
                    }
                }
                yield return null;
            }

            CollectionAssert.AreEqual(new[] { clips[0], clips[1], clips[2], clips[0] }, picked.GetRange(0, 4),
                "Each loop iteration should re-pick through the Sequence strategy - a reused clip means ChangeClipPerLoop was ignored.");
        }

        // Pins TEST_FINDINGS #58.
        [UnityTest]
        [Category("Finding_58")]
        public IEnumerator Stop_ByTypeWithFade_FadesOneShotsButALoopFallsSilentAtItsCurrentIterationEnd()
        {
            yield return RequireRealtimeAudioClock();

            const float FadeSeconds = 3f;
            const float LoopClipSeconds = 0.5f;
            SoundID oneShotA = NewSound("TypeStopOneShotA", BroAudioType.SFX, NewClip(10f));
            SoundID oneShotB = NewSound("TypeStopOneShotB", BroAudioType.SFX, NewClip(10f));
            AudioEntity loopEntity = NewEntity("TypeStopLoop", BroAudioType.SFX, NewClip(LoopClipSeconds));
            TestAudioLibrary.SetPrivateField(loopEntity, nameof(AudioEntity.Loop), true);
            SoundID loopId = IdOf(loopEntity);
            SoundID uiId = NewSound("TypeStopUi", BroAudioType.UI, NewClip(10f));

            IAudioPlayer a = BroAudio.Play(oneShotA);
            IAudioPlayer b = BroAudio.Play(oneShotB);
            IAudioPlayer loop = BroAudio.Play(loopId);
            IAudioPlayer ui = BroAudio.Play(uiId);
            yield return WaitForPlaybackStart(a);
            yield return WaitForPlaybackStart(b);
            yield return WaitForPlaybackStart(loop);
            yield return WaitForPlaybackStart(ui);
            // Past at least one seam, so the loop's handle has been handed over before the stop.
            yield return WaitDspSeconds(LoopClipSeconds * 1.5);
            Assert.IsTrue(loop.IsActive, "Precondition: the loop must survive its first seam.");

            BroAudio.Stop(BroAudioType.SFX, FadeSeconds);

            yield return new WaitForSeconds(1f);
            Assert.IsTrue(a.IsActive && a.IsPlaying && a.GetVolume() < 0.95f, "One-shot A should be audibly fading, not cut, 1s into a 3s stop fade.");
            Assert.IsTrue(b.IsActive && b.IsPlaying && b.GetVolume() < 0.95f, "One-shot B should be audibly fading, not cut, 1s into a 3s stop fade.");
            Assert.IsTrue(loop.IsActive, "The loop's handle should still be live 1s into a 3s stop fade.");
            Assert.IsFalse(BroAudio.HasAnyPlayingInstances(loopId),
                "characterizes: 1s into the fade the loop is already silent - its voice ended with the 0.5s iteration that was playing when Stop cancelled the handover.");

            yield return WaitForRecycle(a, "one-shot A to end once the fade completes", FadeSeconds + 1f);
            yield return WaitForRecycle(b, "one-shot B to end once the fade completes", DefaultPlaybackWaitSeconds);
            yield return WaitForRecycle(loop, "the loop to end once the fade completes", DefaultPlaybackWaitSeconds);
            Assert.IsTrue(ui.IsPlaying, "Stop(SFX) must leave the UI player alone.");

            // A cancelled handover that still fired would start a new iteration here.
            float quietUntil = Time.realtimeSinceStartup + LoopClipSeconds * 3f;
            while (Time.realtimeSinceStartup < quietUntil)
            {
                Assert.IsFalse(BroAudio.HasAnyPlayingInstances(loopId), "The stopped loop must not restart at a later seam.");
                yield return null;
            }

        }

        // Unlike GetActivePlayers, includes a player fading out past its clip's end, which no longer reports IsPlaying.
        private static List<AudioPlayer> GetCheckedOutPlayers(SoundID id)
        {
            var all = CurrentAudioPlayers();
            var matches = new List<AudioPlayer>();
            foreach (AudioPlayer candidate in all)
            {
                if (candidate.IsActive && candidate.ID.Equals(id))
                {
                    matches.Add(candidate);
                }
            }
            return matches;
        }

        // Pins TEST_FINDINGS #59; fixing it turns the MaxStarts assertion red, the intended update point.
        // Over three transitions, once-per-TransitionTime shows 4-5 starts, once-per-clip ~9, and unbounded
        // recursion never returns from Play; the bounds sit a full start clear of each.
        [UnityTest]
        [Category("Finding_59")]
        public IEnumerator SeamlessLoop_WithTransitionLongerThanTheClip_LoopsOncePerTransitionWithABoundedPlayerCount()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.5f;
            const float TransitionSeconds = 1.5f;
            const float WindowSeconds = TransitionSeconds * 3f;
            const int MinStarts = 3;
            const int MaxStarts = 6;
            const int MaxConcurrentPlayers = 4;
            AudioEntity entity = NewEntity("LongTransitionSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), TransitionSeconds);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // A start is an inactive-to-active edge, not a new instance: pooled players come back.
            int starts = 0;
            int maxConcurrent = 0;
            var activeLastFrame = new HashSet<AudioPlayer>();
            var startTimes = new List<string>();
            float windowStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - windowStart < WindowSeconds)
            {
                List<AudioPlayer> active = GetCheckedOutPlayers(id);
                maxConcurrent = Mathf.Max(maxConcurrent, active.Count);
                foreach (AudioPlayer candidate in active)
                {
                    if (!activeLastFrame.Contains(candidate))
                    {
                        starts++;
                        startTimes.Add((Time.realtimeSinceStartup - windowStart).ToString("F2") + "s");
                    }
                }
                activeLastFrame = new HashSet<AudioPlayer>(active);
                yield return null;
            }

            string observed = $"{starts} start(s) at [{string.Join(", ", startTimes)}], at most {maxConcurrent} player(s) live at once";
            Assert.LessOrEqual(maxConcurrent, MaxConcurrentPlayers,
                $"The live player count must stay bounded ({observed}) - each player spawns one successor, then fades out and recycles.");
            Assert.GreaterOrEqual(starts, MinStarts,
                $"characterizes: the loop keeps handing over, once per TransitionTime ({observed}).");
            Assert.LessOrEqual(starts, MaxStarts,
                $"characterizes: the loop's period is the {TransitionSeconds}s transition, not the {ClipSeconds}s clip ({observed}).");
            Assert.IsTrue(player.IsActive, $"The caller's handle must still drive the loop after {WindowSeconds}s of handovers.");

            // An outgoing player nobody holds finishes its own fade-out, so this wait spans up to one transition.
            player.Stop(0f);
            yield return WaitUntilOrTimeout(() => GetCheckedOutPlayers(id).Count == 0,
                "every player of the loop to recycle after Stop()", TransitionSeconds + DefaultPlaybackWaitSeconds);
        }
    }
}