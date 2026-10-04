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
        // The engine's pause-on-reschedule quirk (unity-audio-engine.md), invisible in IAudioPlayer state.
        [UnityTest]
        public IEnumerator SetScheduledStartTime_OnAlreadyPlayingSource_StallsPlayheadWithoutChangingIsPlaying()
        {
            yield return RequireRealtimeAudioClock();

            int onPauseCount = 0;
            // 6s clip: the stall doesn't push _playbackEndDspTime out, so the clip must outlast the 2s stall plus
            // the resume, or playback ends while still stalled.
            SoundID id = NewSound("RescheduleWhilePlayingSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnPause(_ => onPauseCount++);

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "playback to audibly start", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(3);

            double now = AudioSettings.dspTime;
            player.SetScheduledStartTime(now + 2.0); // reschedule while already playing - stalls the source for 2s

            Assert.IsTrue(player.IsPlaying, "IAudioPlayer.IsPlaying must not flip false from a mid-play reschedule.");
            Assert.IsTrue(player.IsActive, "IAudioPlayer.IsActive must not flip false from a mid-play reschedule.");
            Assert.AreEqual(0, onPauseCount, "OnPause must not fire for a reschedule-induced stall - only Pause()/StopMode.Pause does that.");

            int stalledSamples = player.AudioSource.timeSamples;
            // A second in: frozen across a second, with a second of stall left for a slow frame.
            yield return WaitDspSeconds(1.0);
            Assert.AreEqual(stalledSamples, player.AudioSource.timeSamples,
                "characterizes: the AudioSource playhead stalls while paused-for-reschedule, though nothing in IAudioPlayer reflects it.");
            Assert.IsTrue(player.IsPlaying, "IsPlaying must still read true while the source sits stalled.");
            Assert.AreEqual(0, onPauseCount, "OnPause still must not have fired while the stall is in progress.");

            // ~1s of stall is left; 2s keeps a full second of slack for the resume to be observed.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > stalledSamples,
                "the playhead to resume advancing once the rescheduled dspTime arrives", DefaultPlaybackWaitSeconds);
        }

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

            // 1s past the explicit end, ~1s short of the natural 4s end: an ignored end times out.
            const float DecisiveRecycleWaitSeconds = 2f;
            yield return WaitForRecycle(player,
                "playback to end at the explicit scheduled end time, not the clip's natural 4s length",
                DecisiveRecycleWaitSeconds);
        }

        [UnityTest]
        public IEnumerator SetPitch_AboveOneMidPlay_ShortensDerivedRemainingDuration()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("PitchShortenSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(2);

            player.SetPitch(2f);

            // ~1.5s of audio left at 2x; 2.3s is generous yet well short of the unpitched 3s.
            yield return WaitForRecycle(player,
                "pitch-doubled playback to end well before the clip's natural 3s length", 2.3f);
        }

        // The direction that can truncate: PlayControl ends at _playbackEndDspTime even mid-clip, so a no-op
        // rescale would cut the slowed clip at its unpitched length.
        [UnityTest]
        public IEnumerator SetPitch_BelowOneMidPlay_LengthensDerivedRemainingDuration()
        {
            yield return RequireRealtimeAudioClock();

            // Half pitch leaves ~6s. The sample sits 1.5s past the unpitched end and 1.5s short of the pitched
            // one; the budget ends 1.5s past it. Truncating fails the first check, over-stretching the second.
            const float ClipSeconds = 3f;
            const float Pitch = 0.5f;
            const double StillPlayingAtDspSeconds = 4.5;
            const float RecycleBudgetSeconds = 3f;
            SoundID id = NewSound("PitchLengthenSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "playback to audibly start", DefaultPlaybackWaitSeconds);

            player.SetPitch(Pitch);

            yield return WaitDspSeconds(StillPlayingAtDspSeconds);
            Assert.IsTrue(player.IsActive && player.IsPlaying,
                $"At half pitch a {ClipSeconds}s clip must still be playing {StillPlayingAtDspSeconds}s in - ending near {ClipSeconds}s means the derived end was not rescaled.");

            yield return WaitForRecycle(player,
                "half-pitch playback to end near twice the clip's length", RecycleBudgetSeconds);
        }

        // Contrast: an explicit end (_isEndTimeDerivedFromClip false) is not rescaled by pitch.
        [UnityTest]
        public IEnumerator SetPitch_AfterExplicitScheduledEndTime_DoesNotRescaleEndTime()
        {
            // Samples inside the 2s explicit end window, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

            // 6s clip: a regression re-derives the end as clipLength / pitch (~4s, later not sooner); the clip must
            // keep that a second past the 2s explicit end.
            SoundID id = NewSound("PitchVsExplicitEndSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // 2s so the "still active" sample below lands a full second short of the end.
            double target = AudioSettings.dspTime + 2.0;
            player.SetScheduledEndTime(target);
            player.SetPitch(1.5f); // must not move the explicit 2s end time (a recalculation would push it out to ~4s)

            yield return WaitDspSeconds(1.0);
            Assert.IsTrue(player.IsActive, "Player must still be active a full second before the explicit end time even after a pitch change.");

            // 1s past the explicit end, ~1s short of a recalculated ~4s end.
            const float DecisiveRecycleWaitSeconds = 2f;
            yield return WaitForRecycle(player,
                "playback to end at the still-unmoved explicit end time (~2s), not at the pitch-rescaled ~4s",
                DecisiveRecycleWaitSeconds);
        }
    }
}
