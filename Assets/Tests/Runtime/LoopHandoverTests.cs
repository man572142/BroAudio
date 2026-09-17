using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory slice 2.1, 2.2, 2.3, 2.11: looping and chained playback, all implemented via player
    /// handover rather than AudioSource.loop. See Docs/inventory/time-dependent.md, sections
    /// "Plain looping", "Seamless looping", "Chained playback", "Pause across a handover seam".
    /// <para>
    /// This file takes it as contract that the IAudioPlayer handle a caller kept keeps driving the sound
    /// across a handover seam. AudioPlayerInstanceWrapper.UpdateInstance
    /// exists for no other reason - it re-points the wrapper at the incoming player and carries the
    /// registered callbacks, decorators and added effect components over - and looping BGM, the default use
    /// of this library, leaves its owner with no handle other than the one Play returned. So the survival of
    /// that handle is public API, not an internal detail:
    /// Play_WithPlainLoop_HandleKeepsDrivingTheSoundAcrossTwoSeams pins it directly, driving SetVolume,
    /// OnEnd and Stop on the original handle after two handovers. The other tests here still track a
    /// handed-over sound through BroAudio.HasAnyPlayingInstances and through the GetActivePlayers reflection
    /// helper below (a thin window onto SoundManager's private player pool), because what they are about is
    /// which players exist and what each one is playing, not what the caller's handle points at.
    /// </para>
    /// <para>
    /// Not included: a SeamlessLoop whose TransitionTime exceeds the clip. It may recurse into an uncatchable
    /// StackOverflowException and kill the Editor - see Docs/TEST_INVENTORY.md before writing it.
    /// </para>
    /// </summary>
    public class LoopHandoverTests : BroAudioTestFixture
    {
        private static readonly MethodInfo GetCurrentAudioPlayersMethod = GetCurrentAudioPlayersMethodOrThrow();

        // Guards the reflection lookup above: an un-checked null here would NRE at Invoke with no useful
        // message if SoundManager.GetCurrentAudioPlayers is ever renamed.
        private static MethodInfo GetCurrentAudioPlayersMethodOrThrow()
        {
            MethodInfo method = typeof(SoundManager).GetMethod("GetCurrentAudioPlayers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "Reflection: SoundManager.GetCurrentAudioPlayers not found - renamed? Update LoopHandoverTests.");
            return method;
        }

        /// <summary>
        /// All active, audibly-playing AudioPlayer instances for a SoundID - mirrors the filter behind the
        /// public BroAudio.HasAnyPlayingInstances, but returns the players themselves so a test can inspect
        /// clip identity or count distinct instances (e.g. two players crossfading at once).
        /// </summary>
        private static List<AudioPlayer> GetActivePlayers(SoundID id)
        {
            var all = (IReadOnlyList<AudioPlayer>)GetCurrentAudioPlayersMethod.Invoke(SoundManager.Instance, null);
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
        /// The player's live playhead, in samples. A player that has only been PlayScheduled-armed reports
        /// AudioSource.isPlaying true while this still sits at the clip's start sample, so this is what
        /// separates a player that is genuinely being heard from one that is merely queued.
        /// </summary>
        private static int PlayheadOf(AudioPlayer player) => ((IAudioPlayer)player).AudioSource.timeSamples;

        /// <summary>
        /// The player's current level - AudioPlayer.Volume.cs's _clipVolume.Current * _trackVolume.Current *
        /// _audioTypeVolume.Current. Nothing here touches the latter two, so it reads back _clipVolume alone,
        /// which is the fader a seamless loop's crossfade drives.
        /// </summary>
        private static float VolumeOf(AudioPlayer player) => ((IAudioPlayer)player).GetVolume();

        // 2.2 - a looping entity never sets AudioSource.loop; instead a fresh player is handed over at (or
        // near) the natural end of each iteration. This test confirms survival through
        // BroAudio.HasAnyPlayingInstances; the handle's own continuity is the next test's subject.
        [UnityTest]
        public IEnumerator Play_WithPlainLoop_NeverSetsAudioSourceLoopAndSurvivesMultipleSeams()
        {
            const float ClipSeconds = 0.4f;
            AudioEntity entity = NewEntity("PlainLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            Assert.IsFalse(player.AudioSource.loop,
                "characterizes: BroAudio implements looping via player handover, never via AudioSource.loop.");

            double firstSeamDsp = startDsp.Value + ClipSeconds;
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= firstSeamDsp + 0.2,
                "the dsp clock to pass the first loop seam", 5f);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The looping sound must still be audible via a handed-over player after the first seam.");

            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= firstSeamDsp + ClipSeconds + 0.2,
                "the dsp clock to pass a second loop seam", 5f);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The loop must survive a second seam too, not just the first.");
        }

        // 2.2 (handle continuity) - the other half of the same handover: what the caller is left holding.
        // ScheduleNextPlayback bakes the outgoing player's _trackVolume.Target into
        // PlaybackHandoverData.TrackVolume and ReceiveHandover completes the
        // incoming player's fader on it, while UpdateInstance moves the registered onEnd
        // delegates to the incoming player and leaves the outgoing player's _onEnd null
        // (via AudioPlayer.TransferOnEnds) -
        // so EndPlaying's _onEnd?.Invoke is a no-op at a seam and fires once,
        // at the real end. None of that is observable except through the handle the caller kept.
        // A plain loop rather than a seamless one on purpose: with no crossfade, _clipVolume sits completed
        // at its target the whole time, so GetVolume() reads back the track volume alone.
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
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            // Both are registered on the first player, well before the first seam. GetVolume() is
            // _clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current;
            // the latter two are 1 here, so it reads back exactly what SetVolume put on the track fader.
            player.OnEnd(_ => onEndCount++);
            player.SetVolume(TargetVolume);
            Assert.AreEqual(TargetVolume, player.GetVolume(), LinearTolerance,
                "Precondition: SetVolume must land on the first player before any handover.");

            double secondSeamDsp = startDsp.Value + (ClipSeconds * 2);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= secondSeamDsp + 0.2,
                "the dsp clock to pass two loop seams", 5f);

            // If the handle had been left behind on the first player, the wrapper would have been recycled
            // with it (via AudioPlayer.Recycle()) and IsActive would read false.
            Assert.IsTrue(player.IsActive,
                "The caller's IAudioPlayer must still be live after two handovers - UpdateInstance re-points " +
                "it at the incoming player, and the owner of a looping sound has no other handle to hold.");
            Assert.AreEqual(TargetVolume, player.GetVolume(), LinearTolerance,
                "The volume set before the first seam must ride across both handovers, via " +
                "PlaybackHandoverData.TrackVolume and ReceiveHandover's _trackVolume.Complete.");
            Assert.AreEqual(0, onEndCount,
                "characterizes: OnEnd is an end-of-sound callback, not a per-iteration one - BeginHandover " +
                "transfers the delegate away before the outgoing player's EndPlaying could invoke it.");

            // The real proof that the handle still commands the sound: a handle stranded on the recycled
            // first player would make this a no-op and the loop would keep running.
            player.Stop(0f);
            yield return WaitFrames(3);

            Assert.AreEqual(0, GetActivePlayers(id).Count,
                "Stop() on the handle must stop the handed-over player and the one already scheduled behind it.");
            Assert.AreEqual(1, onEndCount,
                "OnEnd must fire exactly once, at the real end of the sound, no matter how many seams it crossed.");
        }

        // 2.3 - a seamless loop's transition time is applied as both the outgoing player's fade-out and the
        // incoming player's fade-in, and BeginHandover runs before the fade-out starts - so for the whole
        // transition window, two distinct players are simultaneously active and audible (a real crossfade).
        // <para>
        // Two live players is not by itself evidence of that. A *plain* loop has two as well, for
        // ScheduledPlaybackWarmUpTime (at least AudioConstant.MixerWarmUpTime, 0.1s) before every seam:
        // ScheduleNextPlayback spawns the next player that far ahead of the seam, and AudioSource.PlayScheduled
        // makes isPlaying report true from the call onwards even though that player's playhead has not moved
        // yet. So waiting for a count of 2 passes on a plain loop too - and neither player's clip volume moves
        // at all there, because with no fade SetupClipVolume completes both straight onto their target. What
        // only a crossfade produces is an overlap that stays open for a large fraction of the transition time,
        // with the incoming playhead already advancing and the two clip volumes travelling in opposite
        // directions - which is what this measures.
        // </para>
        [UnityTest]
        public IEnumerator Play_WithSeamlessLoop_CrossfadesTwoPlayersAcrossTheSeam()
        {
            yield return RequireRealtimeAudioClock();

            // ClipSeconds has to hold the whole transition window AND still leave a stretch after the seam
            // where the handed-over player is alone, for the tail assertion. Each player's own crossfade
            // opens TransitionSeconds before its own end, so at ClipSeconds == TransitionSeconds * 2 the
            // crossfades abut and two players are live forever - the tail would then never come true.
            const float ClipSeconds = 5f;
            const float TransitionSeconds = 2f;
            // Half the transition. The scan below accumulates the overlap instead of sampling chosen instants,
            // so a slow frame at either edge only trims the measurement - and it would have to swallow a full
            // second before this became unreachable, while a plain loop's ~0.1s warm-up overlap can never reach
            // it at all.
            const double MinOverlapSeconds = TransitionSeconds / 2d;
            // Both clip volumes traverse the full 0..1 range across the window, so requiring a quarter of it
            // is unmistakable under any fade Ease while staying far from the float tolerances.
            const float MinVolumeTravel = 0.25f;
            AudioEntity entity = NewEntity("SeamlessLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            double crossfadeStartDsp = startDsp.Value + ClipSeconds - TransitionSeconds;
            double seamDsp = startDsp.Value + ClipSeconds;
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= crossfadeStartDsp,
                "the dsp clock to reach the start of the crossfade window", 10f);

            // Scan every frame of the window rather than sampling two chosen dsp instants: each frame that
            // sees both players extends the measured overlap, so an overshoot at either edge shortens the
            // measurement slightly instead of missing a sample point outright.
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
                    // The outgoing player is the one further into its clip - the incoming one only started
                    // at the top of this window.
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

            // Past the seam the outgoing player's scheduled end has fired and its fade-out has run out, so the
            // handed-over player is alone until its own crossfade opens ClipSeconds - TransitionSeconds later.
            yield return WaitUntilOrTimeout(() => GetActivePlayers(id).Count == 1,
                "the crossfade to finish, leaving only the handed-over player active", 5f);
        }

        // 2.11 - Chained mode plays clip[Start] once, hands over to clip[Loop] repeatedly (seamless, per
        // RuntimeSetting's chained-mode default), and on Stop() hands over one more time to clip[End].
        // Unlike every other handover in this file, the outro handover fires synchronously inside
        // StopControl - there is no DSP wait gate before it, so it is already in effect the instant the
        // Stop() call returns, with no frame yielded in between.
        [UnityTest]
        public IEnumerator ChainedPlayMode_HandsOverIntroToLoopToOutro_OutroHandoverFiresSynchronouslyOnStop()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.3f;
            AudioClip introClip = NewClip(ClipSeconds, "Intro");
            AudioClip loopClip = NewClip(ClipSeconds, "Loop");
            AudioClip outroClip = NewClip(ClipSeconds, "Outro");
            AudioEntity entity = NewEntity("ChainedSfx", BroAudioType.SFX, introClip, loopClip, outroClip);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Chained);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player, "the intro clip to start playing");
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the intro clip", 2f);

            List<AudioPlayer> atStart = GetActivePlayers(id);
            Assert.AreEqual(1, atStart.Count, "Only the intro player should be active at the very start.");
            Assert.AreEqual(introClip, ClipOf(atStart[0]),
                "Chained playback must start on the intro (Start-stage) clip.");

            yield return WaitUntilOrTimeout(() => GetActivePlayers(id).Exists(p => ClipOf(p) == loopClip),
                "the intro clip to hand over to the loop clip", 3f);

            // Wait past a second loop-stage seam to confirm the loop stage keeps re-chaining to itself,
            // rather than the earlier handover having been a one-off.
            double keepLoopingUntilDsp = startDsp.Value + (ClipSeconds * 3);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= keepLoopingUntilDsp,
                "the dsp clock to pass a second loop-stage seam", 5f);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The loop stage must still be alive after re-chaining at least twice.");

            BroAudio.Stop(id);
            bool outroAlreadyActiveSameFrame = GetActivePlayers(id).Exists(p => ClipOf(p) == outroClip);
            Assert.IsTrue(outroAlreadyActiveSameFrame,
                "characterizes: the Chained outro handover fires synchronously from StopControl, not gated " +
                "behind a DSP wait like every other handover in this file.");

            yield return WaitUntilOrTimeout(() => !BroAudio.HasAnyPlayingInstances(id),
                "the outro clip to finish and playback to end for good (no further handover past the End stage)", 3f);
        }

        // 2.1 - THE highest-risk behavior in this file: pausing right inside a seamless-loop handover seam
        // (project memory: looping-via-handover.md names this a live NRE source). Landed deterministically
        // via the dsp clock rather than a fixed real-time delay, using a wide crossfade window so the pause
        // reliably lands after BeginHandover has already run (it fires at the start of the crossfade
        // window, before the fade-out itself begins).
        // <para>
        // Pause(0f), not Pause(): ApplySeamlessFade leaves TransitionTime standing as the player's fade-out
        // base, so the no-argument overload resolves to a transition-long fade and StopControl would not reach
        // AudioSource.Pause() until well past the seam - the opposite of pausing *inside* it. The 0f override
        // is consumed by TryGetFadeOut, which then reports no fade, and the source is paused synchronously
        // within the call, inside the window.
        // </para>
        [UnityTest]
        public IEnumerator Pause_DuringSeamlessLoopHandoverSeam_DoesNotThrowAndResumes()
        {
            // The seam is a dsp-clock position; without a realtime clock the wait below can cross the whole
            // clip in one frame and the pause lands anywhere but the seam.
            yield return RequireRealtimeAudioClock();

            // A 2s crossfade window leaves 1s of slack either side of its midpoint, and ClipSeconds keeps the
            // *next* crossfade (and the warm-up player it spawns) another second clear of it, so the
            // two-players-exactly precondition below is not sitting on a boundary in either direction.
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
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            double seamMidpointDsp = startDsp.Value + ClipSeconds - (TransitionSeconds / 2.0);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= seamMidpointDsp,
                "the dsp clock to reach the middle of the crossfade seam", 10f);

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

            // The playhead advances on the dsp clock, so measure the freeze against that clock.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(pausedPlayhead, player.AudioSource.timeSamples,
                "A paused AudioSource must not advance its playhead, not even one paused mid-handover.");

            yield return WaitUntilOrTimeout(() => !BroAudio.HasAnyPlayingInstances(id),
                "the outgoing player's tail to reach its scheduled end, leaving the paused sound silent",
                TransitionSeconds + 2f);

            Assert.DoesNotThrow(() => player.UnPause(),
                "UnPause after a seam-straddling pause must never throw.");

            yield return WaitUntilOrTimeout(() => player.IsPlaying && player.AudioSource.timeSamples > pausedPlayhead,
                "the resumed player to carry its playhead on past where it froze", 3f);
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "The resumed sound must be audible again through the public surface, not merely un-paused internally.");
        }

        // ChangeClipPerLoop makes ScheduleNextPlayback hand over no clip, so every iteration re-picks through
        // the entity's strategy. Sequence mode makes each pick a distinct, predictable clip.
        [UnityTest]
        public IEnumerator Loop_WithChangeClipPerLoopAndSequence_AdvancesClipAtEachSeam()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 0.5f;
            AudioClip[] clips = { NewClip(ClipSeconds, "PerLoopClip0"), NewClip(ClipSeconds, "PerLoopClip1"), NewClip(ClipSeconds, "PerLoopClip2") };
            AudioEntity entity = NewEntity("ChangeClipPerLoopSfx", BroAudioType.SFX, clips);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Flags), AudioEntityFlag.ChangeClipPerLoop);
            SoundID id = IdOf(entity);

            BroAudio.Play(id);

            // Record each clip in the order a player first takes it on. A pooled player re-used for a later
            // iteration counts again because its clip changes.
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

        // SoundManager.Stop(type, fade) calls Stop(fade) on every live player of that type. For a loop,
        // StopControl cancels the pending handover and discards any pre-spawned next player, so the sound
        // never comes back - but it also goes silent at the current iteration's end, while its handle stays
        // active for the rest of the fade (TEST_FINDINGS #58).
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
            yield return WaitForRecycle(b, "one-shot B to end once the fade completes", 1f);
            yield return WaitForRecycle(loop, "the loop to end once the fade completes", 1f);
            Assert.IsTrue(ui.IsPlaying, "Stop(SFX) must leave the UI player alone.");

            // A cancelled handover that still fired would start a new iteration here.
            float quietUntil = Time.realtimeSinceStartup + LoopClipSeconds * 3f;
            while (Time.realtimeSinceStartup < quietUntil)
            {
                Assert.IsFalse(BroAudio.HasAnyPlayingInstances(loopId), "The stopped loop must not restart at a later seam.");
                yield return null;
            }

        }
    }
}