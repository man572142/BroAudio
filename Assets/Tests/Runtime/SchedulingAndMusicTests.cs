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
    /// Inventory slice 2.5, 2.7, 2.8, 2.9: clip.Delay vs. explicit scheduling priority, the
    /// ISchedulable start/end-time contract (including the documented "pause on reschedule"
    /// quirk), mid-play pitch rescaling the derived end time, and BGM transitions via
    /// IMusicPlayer.SetTransition / AlwaysPlayMusicAsBGM / OnBGMChanged. See Docs/inventory/time-dependent.md.
    /// </summary>
    public class SchedulingAndMusicTests : BroAudioTestFixture
    {
        // 2.5 - clip.Delay alone postpones the audible start; IsPlaying still flips true immediately
        // (matches PlayScheduled/PlayDelayed semantics per the engine-facts doc).
        [UnityTest]
        public IEnumerator Play_WithClipDelayOnly_PostponesAudibleStartButNotIsPlaying()
        {
            // 1.5s delay (was 0.6s) with a full 1s cushion before the "still silent" check below - the old
            // fixed 0.25s offset was thinner than a single capped hitch frame (Time.maximumDeltaTime caps
            // Utility.GetDeltaTime at ~0.333s), which could push the check past the delay boundary.
            const float delay = 1.5f;
            AudioEntity entity = NewEntity("ClipDelaySfx", BroAudioType.SFX, NewClip(3f));
            entity.Clips[0].Delay = delay;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);

            yield return WaitForPlaybackStart(player, "IsPlaying to flip true immediately despite the pending delay");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Playhead must not have moved yet - still inside clip.Delay.");

            // Land a full second before the delay elapses: still silent.
            yield return WaitDspSeconds(delay - 1.0);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still inside clip.Delay - playback must not have started audibly.");

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "audible playback to start once clip.Delay elapses", 2f);
        }

        // 2.5 - the priority rule that has already regressed once (project memory, commit 7bdc9431):
        // an explicit schedule set before the queued Play() drains must win outright over clip.Delay,
        // not add to it. A 5s clip.Delay left un-overridden would blow well past this test's timeout.
        [UnityTest]
        public IEnumerator SetScheduledStartTime_CalledBeforeQueueDrains_OverridesClipDelay()
        {
            // The held-voice checks below sample the DSP clock at a point inside the schedule. A machine with no
            // audio output device runs that clock decoupled from wall time - hundreds of times faster - so a single
            // frame can carry WaitDspSeconds past the whole schedule and the voice would read as started. The
            // schedule and the observation share the clock, so a uniformly fast clock is harmless; it is the
            // per-frame jump that breaks it. AudioClockProbeTests is what stops this ignore from hiding a broken
            // CI image.
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayVsScheduleSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            // 1.5s (was 0.3s): long enough to observe the voice being *held*, not merely to observe it starting.
            double target = AudioSettings.dspTime + 1.5;
            player.SetScheduledStartTime(target); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            // SetScheduledStartTime -> PlayInternal -> SchedulePlayback runs AudioSource.PlayScheduled synchronously,
            // and PlayScheduled reports isPlaying true from the call, so IsPlaying cannot tell "held" from "started".
            // Only the playhead can: PlayControl seeds it with
            // AudioSource.timeSamples = GetSample(audioClip.frequency, _clip.StartPosition) (AudioPlayer.Playback.cs),
            // and the fixture's clips leave StartPosition at 0, so a held voice reads exactly 0.
            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit schedule, not started at once.");

            // Sample 0.5s into the 1.5s schedule: 1s of decisive window remains - three capped hitch frames
            // (Time.maximumDeltaTime ~0.333s) wide. Without these two checks the test would still pass if the
            // schedule were dropped entirely and the voice started immediately.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit schedule must not have been ignored.");

            // 3s covers the ~1s of schedule that is left with 2s to spare, yet stays 1.5s short of an
            // un-overridden 5s clip.Delay (3.5s short of an additive 6.5s), so either regression fails loudly.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit schedule to win over the 5s clip.Delay", 3f);
        }

        // 2.5 - SetDelay (ISchedulable.SetDelay) is sugar for SetScheduledStartTime(dspTime + delay), so
        // it must inherit the same override-not-additive relationship with clip.Delay: a much shorter
        // explicit SetDelay has to win outright rather than stacking on top of the clip's own delay.
        [UnityTest]
        public IEnumerator SetDelay_CalledBeforeQueueDrains_OverridesClipDelayRatherThanAddingToIt()
        {
            // The held-voice checks below sample the DSP clock at a point inside the schedule. A machine with no
            // audio output device runs that clock decoupled from wall time - hundreds of times faster - so a single
            // frame can carry WaitDspSeconds past the whole schedule and the voice would read as started. The
            // schedule and the observation share the clock, so a uniformly fast clock is harmless; it is the
            // per-frame jump that breaks it. AudioClockProbeTests is what stops this ignore from hiding a broken
            // CI image.
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayOverrideSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            player.SetDelay(1.5f); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            // 1.5s (was 0.3s) so the held voice can be observed, not just its eventual start. SetDelay resolves to
            // SetScheduledStartTime(dspTime + delay), which arms AudioSource.PlayScheduled synchronously and makes
            // isPlaying true right away - so the playhead, not IsPlaying, is the only signal that says "held".
            // PlayControl seeds it with GetSample(audioClip.frequency, _clip.StartPosition) (AudioPlayer.Playback.cs)
            // and the fixture's clips leave StartPosition at 0, so a held voice reads exactly 0.
            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit SetDelay, not started at once.");

            // Sample 0.5s into the 1.5s delay: 1s of decisive window remains - three capped hitch frames
            // (Time.maximumDeltaTime ~0.333s) wide. Without these two checks the test would still pass if the
            // delay were dropped entirely and the voice started immediately.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit SetDelay must not have been ignored.");

            // 3s covers the ~1s of delay that is left with 2s to spare, yet stays 1.5s short of an un-overridden
            // 5s clip.Delay (3.5s short of an additive 6.5s), so either regression fails loudly.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit SetDelay to win over the 5s clip.Delay", 3f);
        }

        // 2.7 - the documented quirk: SetScheduledStartTime on an already-playing source pauses it
        // until the new dspTime, and this is invisible in IAudioPlayer state (Scheduling.cs:172-173).
        [UnityTest]
        public IEnumerator SetScheduledStartTime_OnAlreadyPlayingSource_StallsPlayheadWithoutChangingIsPlaying()
        {
            yield return RequireRealtimeAudioClock();

            int onPauseCount = 0;
            // 6s clip (was 3s): the stall does not push the end out - _playbackEndDspTime is fixed when PlayControl
            // resolves the timing and SetScheduledStartTime never recalculates it - so the clip has to outlast the
            // 2s stall plus the resume observation, or the player would end playback while still stalled.
            SoundID id = NewSound("RescheduleWhilePlayingSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnPause(_ => onPauseCount++);

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "playback to audibly start", 2f);
            yield return WaitFrames(3);

            double now = AudioSettings.dspTime;
            player.SetScheduledStartTime(now + 2.0); // reschedule while already playing - stalls the source for 2s

            // characterizes: IAudioPlayer.IsPlaying/IsActive stay true and OnPause never fires - the
            // pause is only visible on the raw AudioSource, per the "left as is" comment in Scheduling.cs.
            Assert.IsTrue(player.IsPlaying, "IAudioPlayer.IsPlaying must not flip false from a mid-play reschedule.");
            Assert.IsTrue(player.IsActive, "IAudioPlayer.IsActive must not flip false from a mid-play reschedule.");
            Assert.AreEqual(0, onPauseCount, "OnPause must not fire for a reschedule-induced stall - only Pause()/StopMode.Pause does that.");

            int stalledSamples = player.AudioSource.timeSamples;
            // Sample a full second into the 2s stall (was 0.2s into a 0.4s stall - a 0.2s margin, thinner than one
            // capped hitch frame at Time.maximumDeltaTime ~0.333s): the playhead is now proven frozen across a whole
            // second, and a whole second of stall still remains, so no hitch can carry this check past the resume.
            yield return WaitDspSeconds(1.0);
            Assert.AreEqual(stalledSamples, player.AudioSource.timeSamples,
                "characterizes: the AudioSource playhead stalls while paused-for-reschedule, though nothing in IAudioPlayer reflects it.");
            Assert.IsTrue(player.IsPlaying, "IsPlaying must still read true while the source sits stalled.");
            Assert.AreEqual(0, onPauseCount, "OnPause still must not have fired while the stall is in progress.");

            // ~1s of stall is left; 2s keeps a full second of slack for the resume to be observed.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > stalledSamples,
                "the playhead to resume advancing once the rescheduled dspTime arrives", 2f);
        }

        // 2.7 - SetScheduledEndTime is an absolute-time contract: it stops playback at the given
        // dspTime regardless of the clip's own (much longer) natural length.
        [UnityTest]
        public IEnumerator SetScheduledEndTime_StopsPlaybackAtExplicitDspTimeRegardlessOfClipLength()
        {
            SoundID id = NewSound("ExplicitEndSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // 2s (was 1s) so the "still active" sample below lands a full second short of the end rather than 0.3s,
            // which was thinner than one capped hitch frame (Time.maximumDeltaTime ~0.333s).
            double target = AudioSettings.dspTime + 2.0;
            player.SetScheduledEndTime(target);

            yield return WaitDspSeconds(1.0);
            Assert.IsTrue(player.IsActive, "Player must still be active a full second before the explicit end time (clip is 4s long).");

            // 2s past the sample point is 1s past the 2s target - three capped hitch frames of slack - and still
            // ~1s short of the clip's natural 4s end, so an ignored explicit end still times out here.
            yield return WaitUntilOrTimeout(() => !player.IsActive, "playback to end at the explicit scheduled end time, not the clip's natural 4s length", 2f);
        }

        // 2.9 - SetPitch mid-play recomputes the *derived* end time from the actual playhead
        // (RecalculateScheduledEndTime), so raising pitch visibly shortens remaining playback.
        [UnityTest]
        public IEnumerator SetPitch_AboveOneMidPlay_ShortensDerivedRemainingDuration()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("PitchShortenSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(2);

            player.SetPitch(2f);

            // Doubling pitch near the start of a 3s clip should finish it in well under 3s (~1.5s of
            // remaining audio at 2x speed). 2.3s is a generous upper bound - well short of the
            // un-accelerated 3s, so this fails loudly if the rescale regresses to a no-op.
            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "pitch-doubled playback to end well before the clip's natural 3s length", 2.3f);
        }

        // 2.9 - contrast case: once SetScheduledEndTime has been called explicitly,
        // _isEndTimeDerivedFromClip is false and RecalculateScheduledEndTime declines to touch it -
        // a later pitch change must NOT move the explicit end time.
        [UnityTest]
        public IEnumerator SetPitch_AfterExplicitScheduledEndTime_DoesNotRescaleEndTime()
        {
            // 6s clip (was 4s): what this test has to rule out is not the clip's natural end but the *recalculated*
            // one - if RecalculateScheduledEndTime stopped honouring _isEndTimeDerivedFromClip it would re-derive
            // the end from the playhead as clipLength / pitch, i.e. ~4s here (later than the explicit end, not
            // sooner). At 4s the clip put that regression at ~2.7s, too close to a 2s explicit end to separate the
            // two by a whole second.
            SoundID id = NewSound("PitchVsExplicitEndSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // 2s (was 1s) so the "still active" sample below lands a full second short of the end rather than 0.3s,
            // which was thinner than one capped hitch frame (Time.maximumDeltaTime ~0.333s).
            double target = AudioSettings.dspTime + 2.0;
            player.SetScheduledEndTime(target);
            player.SetPitch(1.5f); // must not move the explicit 2s end time (a recalculation would push it out to ~4s)

            yield return WaitDspSeconds(1.0);
            Assert.IsTrue(player.IsActive, "Player must still be active a full second before the explicit end time even after a pitch change.");

            // 2s past the sample point is 1s past the 2s target, and still ~1s short of the ~4s end a pitch-driven
            // recalculation would have produced - decisive by a full second in both directions.
            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end at the still-unmoved explicit end time (~2s), not at the pitch-rescaled ~4s", 2f);
        }

        // 2.8 - Default/OnlyFadeOut transitions are sequential: PlayControl explicitly waits on
        // musicPlayer.IsWaitingForTransition before starting the new BGM, so the two must never
        // both report IsPlaying at once.
        [UnityTest]
        public IEnumerator SetTransition_Default_OutgoingAndIncomingBGMNeverOverlap()
        {
            SoundID firstId = NewSound("SequentialBgmA", BroAudioType.Music, NewClip(2f));
            SoundID secondId = NewSound("SequentialBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Default, 0.3f);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Default, 0.3f);

            bool bothPlayingAtOnce = false;
            float deadline = Time.realtimeSinceStartup + 3f;
            while (first.IsActive && Time.realtimeSinceStartup < deadline)
            {
                if (first.IsPlaying && second.IsPlaying)
                {
                    bothPlayingAtOnce = true;
                }
                yield return null;
            }
            yield return WaitForPlaybackStart(second, "second BGM to eventually start once the first finishes fading out");

            Assert.IsFalse(bothPlayingAtOnce, "Transition.Default must be sequential - outgoing and incoming BGM must never both report IsPlaying.");
        }

        // 2.8 - CrossFade is the opposite: BeginHandover/DoTransition does not gate the new player on
        // IsWaitingForTransition, so both BGMs are deliberately audible together during the crossfade.
        [UnityTest]
        public IEnumerator SetTransition_CrossFade_OutgoingAndIncomingBGMOverlap()
        {
            SoundID firstId = NewSound("CrossfadeBgmA", BroAudioType.Music, NewClip(2f));
            SoundID secondId = NewSound("CrossfadeBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.CrossFade, 0.5f);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.CrossFade, 0.5f);

            yield return WaitUntilOrTimeout(() => first.IsPlaying && second.IsPlaying,
                "both the outgoing and incoming BGM to be audible at once during a CrossFade transition", 2f);
        }

        // 2.8 - RuntimeSetting.AlwaysPlayMusicAsBGM (default true): a Music-typed Play() is
        // auto-wrapped with AsBGM()+SetTransition even when the caller never calls AsBGM() themselves.
        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Enabled_AutoTransitionsUnrelatedMusicPlaysWithoutExplicitAsBGM()
        {
            // fixture restores RuntimeSetting in TearDown - Immediate keeps this deterministic and fast.
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = true;
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            // The first clip's length is load-bearing: at 2s it used to reach its own natural end within
            // the 2s deactivation wait below, so the assertion passed even with the auto-BGM feature
            // deleted. 9s puts the clip's natural end far outside the 3s wait, so only the auto-transition
            // can explain the first player deactivating.
            SoundID firstId = NewSound("AutoBgmA", BroAudioType.Music, NewClip(9f));
            SoundID secondId = NewSound("AutoBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId); // never calls AsBGM() explicitly
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId); // also never calls AsBGM() explicitly

            // 3s is well under the 9s clip length, so this stays discriminating; the transition itself is
            // Immediate, so 3s is a generous CI-safe margin rather than a tight bound on the transition.
            yield return WaitUntilOrTimeout(() => !first.IsActive,
                "the first Music player to be auto-transitioned off by SoundManager's implicit AsBGM()+SetTransition", 3f);
            yield return WaitForPlaybackStart(second, "the second Music player to take over as BGM");
        }

        // 2.8 - with the setting off, two Music plays are just two ordinary concurrent players -
        // no transition machinery engages, so both play at once for their full overlap.
        [UnityTest]
        public IEnumerator AlwaysPlayMusicAsBGM_Disabled_MusicPlaysOverlapFreelyWithoutTransition()
        {
            SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = false; // fixture restores RuntimeSetting in TearDown

            SoundID firstId = NewSound("NoBgmA", BroAudioType.Music, NewClip(2f));
            SoundID secondId = NewSound("NoBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId);
            yield return WaitForPlaybackStart(first, "first Music play to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            yield return WaitForPlaybackStart(second, "second Music play to start");

            yield return WaitFrames(3);
            Assert.IsTrue(first.IsPlaying, "With AlwaysPlayMusicAsBGM off, the first Music player must keep playing - no auto-transition should have stopped it.");
            Assert.IsTrue(second.IsPlaying, "The second Music player must be playing concurrently, not sequenced after the first.");
        }

        // 2.8 - BroAudio.OnBGMChanged fires exactly once per actual CurrentBGMPlayer change.
        [UnityTest]
        public IEnumerator OnBGMChanged_WhenANewBGMReplacesTheCurrentOne_ReportsTheNewPlayer()
        {
            // characterizes: replacing a BGM raises OnBGMChanged *twice*, and both in the same frame —
            // first with null as the outgoing player clears itself in Recycle(), then with the incoming
            // player. A subscriber that dereferences the argument without a null check will throw.
            // Poll for the meaningful arrival rather than an exact count: an == comparison on the count
            // is never satisfiable, because it skips straight past 2 within a single frame.
            List<IAudioPlayer> received = new List<IAudioPlayer>();
            SubscribeBgmChanged(p => received.Add(p));

            SoundID firstId = NewSound("EventBgmA", BroAudioType.Music, NewClip(4f));
            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);

            yield return WaitUntilOrTimeout(() => received.Count >= 1,
                "OnBGMChanged to fire when the first BGM becomes current", 2f);
            Assert.AreEqual(firstId, received[0].ID, "The first event carries the incoming BGM player.");

            SoundID secondId = NewSound("EventBgmB", BroAudioType.Music, NewClip(4f));
            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate);

            yield return WaitUntilOrTimeout(
                () => received.Exists(p => p != null && p.ID.Equals(secondId)),
                "OnBGMChanged to report the second BGM player", 4f);

            Assert.IsTrue(received.Exists(p => p == null),
                "A null argument is raised as the outgoing BGM clears.");
        }

        // 2.8 - SetTransition(Transition, StopMode) overload: MusicPlayer.DoTransition's StopCurrentPlayer
        // calls the outgoing BGM's Stop(fadeOut, stopMode, onFinished) with the caller's StopMode instead
        // of the default Stop. With StopMode.Pause the outgoing player is paused in place (AudioPlayer.
        // Playback.cs StopControl's switch case) rather than ended: it stays IsActive, its AudioSource
        // playhead freezes, and it resumes exactly like a manual Pause()/UnPause() would.
        [UnityTest]
        public IEnumerator SetTransition_WithStopModePause_PausesOutgoingBGMInPlaceAndItResumesOnUnPause()
        {
            SoundID firstId = NewSound("StopModePauseBgmA", BroAudioType.Music, NewClip(4f));
            SoundID secondId = NewSound("StopModePauseBgmB", BroAudioType.Music, NewClip(4f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate); // first BGM - no prior player to transition off
            yield return WaitForPlaybackStart(first, "first BGM to start");
            yield return WaitFrames(5); // let the playhead move so a frozen-vs-advancing check is meaningful

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate, StopMode.Pause);

            yield return WaitUntilOrTimeout(() => !first.IsPlaying, "the outgoing BGM to pause rather than stop", 2f);
            Assert.IsTrue(first.IsActive, "StopMode.Pause must leave the outgoing BGM active, not ended.");
            yield return WaitForPlaybackStart(second, "the incoming BGM to be playing");

            int pausedSamples = first.AudioSource.timeSamples;
            yield return WaitFrames(5);
            Assert.AreEqual(pausedSamples, first.AudioSource.timeSamples, "The paused outgoing BGM's playhead must not advance.");

            first.UnPause();
            yield return WaitForPlaybackStart(first, "the paused-off BGM to resume via a plain UnPause()");
            Assert.GreaterOrEqual(first.AudioSource.timeSamples, pausedSamples,
                "Resuming the StopMode.Pause'd BGM must continue from where it was paused, not restart from 0.");
        }

        // 2.8 - StopMode.Mute is the "keep playing silently" mode: StopControl's switch case for Mute only
        // calls SetVolume(0f) - it never calls AudioSource.Pause()/Stop() - so the outgoing BGM keeps
        // AudioSource.isPlaying true and its playhead keeps advancing in the background; only its linear
        // volume drops to (near) zero.
        [UnityTest]
        public IEnumerator SetTransition_WithStopModeMute_MutesOutgoingBGMButLeavesItAudiblyPlaying()
        {
            SoundID firstId = NewSound("StopModeMuteBgmA", BroAudioType.Music, NewClip(4f));
            SoundID secondId = NewSound("StopModeMuteBgmB", BroAudioType.Music, NewClip(4f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate); // first BGM - no prior player to transition off
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate, StopMode.Mute);

            yield return WaitForPlaybackStart(second, "the incoming BGM to be playing");
            yield return WaitUntilOrTimeout(() => first.GetVolume() < 0.05f, "the outgoing BGM's linear volume to drop to (near) zero", 2f);

            Assert.IsTrue(first.IsPlaying,
                "characterizes: StopMode.Mute never calls AudioSource.Pause/Stop - the muted BGM keeps AudioSource.isPlaying true, running silently in the background.");
            Assert.IsTrue(first.IsActive, "A muted BGM must stay active, not ended.");
        }
    }
}