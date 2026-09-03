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
            AudioEntity entity = NewEntity("DelayVsScheduleSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            double target = AudioSettings.dspTime + 0.3;
            player.SetScheduledStartTime(target); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 0.3s explicit schedule to win over the 5s clip.Delay", 1.5f);
        }

        // 2.5 - SetDelay (ISchedulable.SetDelay) is sugar for SetScheduledStartTime(dspTime + delay), so
        // it must inherit the same override-not-additive relationship with clip.Delay: a much shorter
        // explicit SetDelay has to win outright rather than stacking on top of the clip's own delay.
        [UnityTest]
        public IEnumerator SetDelay_CalledBeforeQueueDrains_OverridesClipDelayRatherThanAddingToIt()
        {
            AudioEntity entity = NewEntity("DelayOverrideSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            player.SetDelay(0.3f); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 0.3s explicit SetDelay to win over the 5s clip.Delay", 1.5f);
        }

        // 2.7 - the documented quirk: SetScheduledStartTime on an already-playing source pauses it
        // until the new dspTime, and this is invisible in IAudioPlayer state (Scheduling.cs:172-173).
        [UnityTest]
        public IEnumerator SetScheduledStartTime_OnAlreadyPlayingSource_StallsPlayheadWithoutChangingIsPlaying()
        {
            int onPauseCount = 0;
            SoundID id = NewSound("RescheduleWhilePlayingSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnPause(_ => onPauseCount++);

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "playback to audibly start", 2f);
            yield return WaitFrames(3);

            double now = AudioSettings.dspTime;
            player.SetScheduledStartTime(now + 0.4); // reschedule while already playing

            // characterizes: IAudioPlayer.IsPlaying/IsActive stay true and OnPause never fires - the
            // pause is only visible on the raw AudioSource, per the "left as is" comment in Scheduling.cs.
            Assert.IsTrue(player.IsPlaying, "IAudioPlayer.IsPlaying must not flip false from a mid-play reschedule.");
            Assert.IsTrue(player.IsActive, "IAudioPlayer.IsActive must not flip false from a mid-play reschedule.");
            Assert.AreEqual(0, onPauseCount, "OnPause must not fire for a reschedule-induced stall - only Pause()/StopMode.Pause does that.");

            int stalledSamples = player.AudioSource.timeSamples;
            yield return WaitDspSeconds(0.2);
            Assert.AreEqual(stalledSamples, player.AudioSource.timeSamples,
                "characterizes: the AudioSource playhead stalls while paused-for-reschedule, though nothing in IAudioPlayer reflects it.");
            Assert.IsTrue(player.IsPlaying, "IsPlaying must still read true while the source sits stalled.");
            Assert.AreEqual(0, onPauseCount, "OnPause still must not have fired while the stall is in progress.");

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > stalledSamples,
                "the playhead to resume advancing once the rescheduled dspTime arrives", 1f);
        }

        // 2.7 - SetScheduledEndTime is an absolute-time contract: it stops playback at the given
        // dspTime regardless of the clip's own (much longer) natural length.
        [UnityTest]
        public IEnumerator SetScheduledEndTime_StopsPlaybackAtExplicitDspTimeRegardlessOfClipLength()
        {
            SoundID id = NewSound("ExplicitEndSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            double target = AudioSettings.dspTime + 1.0;
            player.SetScheduledEndTime(target);

            yield return WaitDspSeconds(0.7);
            Assert.IsTrue(player.IsActive, "Player must still be active shortly before the explicit end time (clip is 4s long).");

            // Margin: one MixerWarmUpTime-ish window (0.3s) past the 1s target to absorb frame granularity.
            yield return WaitUntilOrTimeout(() => !player.IsActive, "playback to end at the explicit scheduled end time, not the clip's natural 4s length", 1.3f);
        }

        // 2.9 - SetPitch mid-play recomputes the *derived* end time from the actual playhead
        // (RecalculateScheduledEndTime), so raising pitch visibly shortens remaining playback.
        [UnityTest]
        public IEnumerator SetPitch_AboveOneMidPlay_ShortensDerivedRemainingDuration()
        {
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
            SoundID id = NewSound("PitchVsExplicitEndSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            double target = AudioSettings.dspTime + 1.0;
            player.SetScheduledEndTime(target);
            player.SetPitch(1.5f); // must not shorten the explicit 1s end time

            yield return WaitDspSeconds(0.7);
            Assert.IsTrue(player.IsActive, "Player must still be active shortly before the explicit end time even after a pitch change.");

            yield return WaitUntilOrTimeout(() => !player.IsActive,
                "playback to end at the still-unmoved explicit end time (~1s), not sooner from the pitch change", 1.3f);
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