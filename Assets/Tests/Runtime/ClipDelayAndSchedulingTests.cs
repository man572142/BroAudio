using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory slice 2.5: clip.Delay vs. explicit scheduling priority. See Docs/inventory/time-dependent.md.
    /// An explicit schedule set before the queued Play() drains must win outright over clip.Delay, not add to
    /// it — both ISchedulable.SetScheduledStartTime and its SetDelay sugar inherit that override-not-additive
    /// relationship.
    /// </summary>
    public class ClipDelayAndSchedulingTests : BroAudioTestFixture
    {
        // clip.Delay alone postpones the audible start; IsPlaying still flips true immediately (matches
        // PlayScheduled/PlayDelayed semantics per the engine-facts doc).
        [UnityTest]
        public IEnumerator Play_WithClipDelayOnly_PostponesAudibleStartButNotIsPlaying()
        {
            // Samples inside the 1.5s clip.Delay window, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

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

        // An explicit schedule set before the queued Play() drains must win outright over clip.Delay,
        // not add to it. A 5s clip.Delay left un-overridden would blow well past this test's timeout.
        [UnityTest]
        public IEnumerator SetScheduledStartTime_CalledBeforeQueueDrains_OverridesClipDelay()
        {
            // Samples inside the 1.5s schedule, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayVsScheduleSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            // 1.5s: long enough to observe the voice being *held*, not merely to observe it starting.
            double target = AudioSettings.dspTime + 1.5;
            player.SetScheduledStartTime(target); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            // SetScheduledStartTime -> PlayInternal -> SchedulePlayback runs AudioSource.PlayScheduled synchronously,
            // and PlayScheduled reports isPlaying true from the call, so IsPlaying cannot tell "held" from "started".
            // Only the playhead can: PlayControl seeds it with
            // AudioSource.timeSamples = GetSample(audioClip.frequency, _clip.StartPosition) (AudioPlayer.Playback.cs),
            // and the fixture's clips leave StartPosition at 0, so a held voice reads exactly 0.
            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit schedule, not started at once.");

            // Sample 0.5s into the 1.5s schedule, leaving 1s of decisive window. Without these two checks the
            // test would still pass if the schedule were dropped entirely and the voice started immediately.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit schedule must not have been ignored.");

            // 3s covers the ~1s of schedule that is left with 2s to spare, yet stays 1.5s short of an
            // un-overridden 5s clip.Delay (3.5s short of an additive 6.5s), so either regression fails loudly.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit schedule to win over the 5s clip.Delay", 3f);
        }

        // SetDelay (ISchedulable.SetDelay) is sugar for SetScheduledStartTime(dspTime + delay), so
        // it must inherit the same override-not-additive relationship with clip.Delay: a much shorter
        // explicit SetDelay has to win outright rather than stacking on top of the clip's own delay.
        [UnityTest]
        public IEnumerator SetDelay_CalledBeforeQueueDrains_OverridesClipDelayRatherThanAddingToIt()
        {
            // Samples inside the 1.5s delay, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayOverrideSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            player.SetDelay(1.5f); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            // 1.5s so the held voice can be observed, not just its eventual start. SetDelay resolves to
            // SetScheduledStartTime(dspTime + delay), which arms AudioSource.PlayScheduled synchronously and makes
            // isPlaying true right away - so the playhead, not IsPlaying, is the only signal that says "held".
            // PlayControl seeds it with GetSample(audioClip.frequency, _clip.StartPosition) (AudioPlayer.Playback.cs)
            // and the fixture's clips leave StartPosition at 0, so a held voice reads exactly 0.
            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit SetDelay, not started at once.");

            // Sample 0.5s into the 1.5s delay, leaving 1s of decisive window. Without these two checks the
            // test would still pass if the delay were dropped entirely and the voice started immediately.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit SetDelay must not have been ignored.");

            // 3s covers the ~1s of delay that is left with 2s to spare, yet stays 1.5s short of an un-overridden
            // 5s clip.Delay (3.5s short of an additive 6.5s), so either regression fails loudly.
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit SetDelay to win over the 5s clip.Delay", 3f);
        }
    }
}
