using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// An explicit schedule set before the queued Play() drains overrides clip.Delay rather than adding to it.
    /// Trap: a scheduled voice reports IsPlaying at once, so only the playhead says "held" - and with the
    /// fixture's StartPosition of 0, a held voice reads exactly 0.
    /// </summary>
    public class ClipDelayAndSchedulingTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator Play_WithClipDelayOnly_PostponesAudibleStartButNotIsPlaying()
        {
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

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0, "audible playback to start once clip.Delay elapses", DefaultPlaybackWaitSeconds);
        }

        [UnityTest]
        public IEnumerator SetScheduledStartTime_CalledBeforeQueueDrains_OverridesClipDelay()
        {
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayVsScheduleSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            // Long enough to observe the voice being held, not merely starting.
            const double ScheduleAheadSeconds = 1.5;
            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            double target = AudioSettings.dspTime + ScheduleAheadSeconds;
            player.SetScheduledStartTime(target); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit schedule, not started at once.");

            // 1s of decisive window left. Without the held checks, a dropped schedule would still pass.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit schedule must not have been ignored.");

            // 2s to spare over the remaining ~1s, yet 1.5s short of an un-overridden 5s clip.Delay (3.5s short of
            // an additive 6.5s).
            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit schedule to win over the 5s clip.Delay", (float)ScheduleAheadSeconds + 1.5f);
        }

        // Same windows as the SetScheduledStartTime test above.
        [UnityTest]
        public IEnumerator SetDelay_CalledBeforeQueueDrains_OverridesClipDelayRatherThanAddingToIt()
        {
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewEntity("DelayOverrideSfx", BroAudioType.SFX, NewClip(2f));
            entity.Clips[0].Delay = 5f;
            SoundID id = IdOf(entity);

            const float SetDelaySeconds = 1.5f;
            IAudioPlayer player = BroAudio.Play(id); // only enqueued - SoundManager.LateUpdate hasn't drained it yet
            player.SetDelay(SetDelaySeconds); // SetClipDelayIfNotScheduled will see ScheduledStartTime > 0 and skip the 5s clip.Delay

            yield return WaitForPlaybackStart(player, "PlayScheduled to arm the voice immediately");
            Assert.AreEqual(0, player.AudioSource.timeSamples, "The voice must be held by the explicit SetDelay, not started at once.");

            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, player.AudioSource.timeSamples, "Still held 0.5s in - the explicit SetDelay must not have been ignored.");

            yield return WaitUntilOrTimeout(() => player.AudioSource.timeSamples > 0,
                "the 1.5s explicit SetDelay to win over the 5s clip.Delay", SetDelaySeconds + 1.5f);
        }
    }
}
