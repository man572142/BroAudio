using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The ISchedulable start/end-time contract, including the documented "pause on reschedule" quirk,
    /// and mid-play pitch rescaling the derived end time. See Docs/inventory/time-dependent.md.
    /// </summary>
    public class ScheduledPlaybackContractTests : BroAudioTestFixture
    {
        // The documented quirk: SetScheduledStartTime on an already-playing source pauses it
        // until the new dspTime, and this is invisible in IAudioPlayer state.
        [UnityTest]
        public IEnumerator SetScheduledStartTime_OnAlreadyPlayingSource_StallsPlayheadWithoutChangingIsPlaying()
        {
            yield return RequireRealtimeAudioClock();

            int onPauseCount = 0;
            // 6s clip: the stall does not push the end out - _playbackEndDspTime is fixed when PlayControl
            // resolves the timing and SetScheduledStartTime never recalculates it - so the clip has to outlast the
            // 2s stall plus the resume observation, or the player would end playback while still stalled.
            SoundID id = NewSound("RescheduleWhilePlayingSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnPause(_ => onPauseCount++);

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "playback to audibly start", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(3);

            double now = AudioSettings.dspTime;
            player.SetScheduledStartTime(now + 2.0); // reschedule while already playing - stalls the source for 2s

            // characterizes: IAudioPlayer.IsPlaying/IsActive stay true and OnPause never fires - the
            // pause is only visible on the raw AudioSource, per ISchedulable.SetScheduledStartTime in AudioPlayer.Scheduling.cs.
            Assert.IsTrue(player.IsPlaying, "IAudioPlayer.IsPlaying must not flip false from a mid-play reschedule.");
            Assert.IsTrue(player.IsActive, "IAudioPlayer.IsActive must not flip false from a mid-play reschedule.");
            Assert.AreEqual(0, onPauseCount, "OnPause must not fire for a reschedule-induced stall - only Pause()/StopMode.Pause does that.");

            int stalledSamples = player.AudioSource.timeSamples;
            // Sample a full second into the 2s stall: the playhead is proven frozen across a whole second, and a
            // whole second of stall still remains, so no slow frame can carry this check past the resume.
            yield return WaitDspSeconds(1.0);
            Assert.AreEqual(stalledSamples, player.AudioSource.timeSamples,
                "characterizes: the AudioSource playhead stalls while paused-for-reschedule, though nothing in IAudioPlayer reflects it.");
            Assert.IsTrue(player.IsPlaying, "IsPlaying must still read true while the source sits stalled.");
            Assert.AreEqual(0, onPauseCount, "OnPause still must not have fired while the stall is in progress.");

            // ~1s of stall is left; 2s keeps a full second of slack for the resume to be observed.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > stalledSamples,
                "the playhead to resume advancing once the rescheduled dspTime arrives", DefaultPlaybackWaitSeconds);
        }

        // SetScheduledEndTime is an absolute-time contract: it stops playback at the given
        // dspTime regardless of the clip's own (much longer) natural length.
        [UnityTest]
        public IEnumerator SetScheduledEndTime_StopsPlaybackAtExplicitDspTimeRegardlessOfClipLength()
        {
            // Samples inside the 2s explicit end window, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("ExplicitEndSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // 2s so the "still active" sample below lands a full second short of the end.
            double target = AudioSettings.dspTime + 2.0;
            player.SetScheduledEndTime(target);

            yield return WaitDspSeconds(1.0);
            Assert.IsTrue(player.IsActive, "Player must still be active a full second before the explicit end time (clip is 4s long).");

            // Decisive in both directions, so it is derived here rather than taken from a shared budget:
            // 2s past the sample point is 1s past the explicit end and still ~1s short of the clip's natural
            // 4s end, so an ignored explicit end times out and a premature stop fails the check above.
            const float DecisiveRecycleWaitSeconds = 2f;
            yield return WaitForRecycle(player,
                "playback to end at the explicit scheduled end time, not the clip's natural 4s length",
                DecisiveRecycleWaitSeconds);
        }

        // SetPitch mid-play recomputes the *derived* end time from the actual playhead
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
            yield return WaitForRecycle(player,
                "pitch-doubled playback to end well before the clip's natural 3s length", 2.3f);
        }

        // Contrast case: once SetScheduledEndTime has been called explicitly,
        // _isEndTimeDerivedFromClip is false and RecalculateScheduledEndTime declines to touch it -
        // a later pitch change must NOT move the explicit end time.
        [UnityTest]
        public IEnumerator SetPitch_AfterExplicitScheduledEndTime_DoesNotRescaleEndTime()
        {
            // Samples inside the still-unmoved 2s explicit end window, which one frame of a decoupled DSP clock
            // would skip past.
            yield return RequireRealtimeAudioClock();

            // 6s clip: what this test has to rule out is not the clip's natural end but the *recalculated* one -
            // if RecalculateScheduledEndTime stopped honouring _isEndTimeDerivedFromClip it would re-derive the
            // end from the playhead as clipLength / pitch, i.e. ~4s here (later than the explicit end, not
            // sooner). The clip must be long enough to keep that regression a whole second past the 2s explicit end.
            SoundID id = NewSound("PitchVsExplicitEndSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // 2s so the "still active" sample below lands a full second short of the end.
            double target = AudioSettings.dspTime + 2.0;
            player.SetScheduledEndTime(target);
            player.SetPitch(1.5f); // must not move the explicit 2s end time (a recalculation would push it out to ~4s)

            yield return WaitDspSeconds(1.0);
            Assert.IsTrue(player.IsActive, "Player must still be active a full second before the explicit end time even after a pitch change.");

            // Decisive in both directions, so it is derived here rather than taken from a shared budget:
            // 2s past the sample point is 1s past the explicit end, and still ~1s short of the ~4s end a
            // pitch-driven recalculation would have produced.
            const float DecisiveRecycleWaitSeconds = 2f;
            yield return WaitForRecycle(player,
                "playback to end at the still-unmoved explicit end time (~2s), not at the pitch-rescaled ~4s",
                DecisiveRecycleWaitSeconds);
        }
    }
}
