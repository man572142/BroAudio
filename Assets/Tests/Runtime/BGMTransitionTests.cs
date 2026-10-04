using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins <see cref="IMusicPlayer.SetTransition(Transition, float)"/> and its <see cref="StopMode"/>
    /// overload: sequencing vs overlap per <see cref="Transition"/> mode, and what happens to the outgoing BGM.
    /// <para>
    /// Tests that set DefaultBGMTransition to Immediate do so because the implicit auto-BGM transition is a
    /// CrossFade by factory default and could produce an overlap or fade on its own. It is applied
    /// synchronously inside Play(), so the explicit SetTransition overwrites it either way.
    /// </para>
    /// </summary>
    public class BGMTransitionTests : BroAudioTestFixture
    {
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

        [UnityTest]
        public IEnumerator SetTransition_CrossFade_OutgoingAndIncomingBGMOverlap()
        {
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            SoundID firstId = NewSound("CrossfadeBgmA", BroAudioType.Music, NewClip(2f));
            SoundID secondId = NewSound("CrossfadeBgmB", BroAudioType.Music, NewClip(2f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.CrossFade, 0.5f);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.CrossFade, 0.5f);

            yield return WaitUntilOrTimeout(() => first.IsPlaying && second.IsPlaying,
                "both the outgoing and incoming BGM to be audible at once during a CrossFade transition", DefaultPlaybackWaitSeconds);
        }

        [UnityTest]
        public IEnumerator SetTransition_WithStopModePause_PausesOutgoingBGMInPlaceAndItResumesOnUnPause()
        {
            // The playhead comparisons below run on the DSP clock; one frame of a decoupled clock could carry the
            // outgoing clip to its end before the transition pauses it.
            yield return RequireRealtimeAudioClock();

            SoundID firstId = NewSound("StopModePauseBgmA", BroAudioType.Music, NewClip(4f));
            SoundID secondId = NewSound("StopModePauseBgmB", BroAudioType.Music, NewClip(4f));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate); // first BGM - no prior player to transition off
            yield return WaitForPlaybackStart(first, "first BGM to start");
            // Moves the playhead clearly off its start, so a resume cannot be mistaken for a restart from 0.
            yield return WaitDspSeconds(0.5);

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.Immediate, StopMode.Pause);

            yield return WaitUntilOrTimeout(() => !first.IsPlaying, "the outgoing BGM to pause rather than stop", DefaultPlaybackWaitSeconds);
            Assert.IsTrue(first.IsActive, "StopMode.Pause must leave the outgoing BGM active, not ended.");
            yield return WaitForPlaybackStart(second, "the incoming BGM to be playing");

            int pausedSamples = first.AudioSource.timeSamples;
            Assert.Greater(pausedSamples, 0,
                "Precondition: the outgoing playhead must have moved before the pause, or the resume check below cannot tell a resume from a restart.");
            // Measured on the DSP clock, which is what moves a playhead: a few frames can fit inside one buffer.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(pausedSamples, first.AudioSource.timeSamples, "The paused outgoing BGM's playhead must not advance.");

            first.UnPause();
            yield return WaitForPlaybackStart(first, "the paused-off BGM to resume via a plain UnPause()");
            Assert.GreaterOrEqual(first.AudioSource.timeSamples, pausedSamples,
                "Resuming the StopMode.Pause'd BGM must continue from where it was paused, not restart from 0.");
        }

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
            yield return WaitUntilOrTimeout(() => first.GetVolume() < 0.05f, "the outgoing BGM's linear volume to drop to (near) zero", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(first.IsPlaying,
                "characterizes: StopMode.Mute never calls AudioSource.Pause/Stop - the muted BGM keeps AudioSource.isPlaying true, running silently in the background.");
            Assert.IsTrue(first.IsActive, "A muted BGM must stay active, not ended.");
        }

        /// <summary>
        /// Long enough that no BGM here reaches its natural end inside a transition window, so only the
        /// transition can explain a player ending.
        /// </summary>
        private const float TransitionBgmClipLength = 9f;

        /// <summary>
        /// A 1s-in sample sits ≥1s clear of both fade ends. Bands there derive from the factory eases (fade-out
        /// OutSine: 0.5 a third of the way in; fade-in InCubic: under 0.04).
        /// </summary>
        private const float TransitionFadeSeconds = 3f;

        [UnityTest]
        public IEnumerator SetTransition_OnlyFadeOut_OutgoingFadesWhileIncomingStartsAtFullVolume()
        {
            yield return RequireRealtimeAudioClock();
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            SoundID firstId = NewSound("OnlyFadeOutBgmA", BroAudioType.Music, NewClip(TransitionBgmClipLength));
            SoundID secondId = NewSound("OnlyFadeOutBgmB", BroAudioType.Music, NewClip(TransitionBgmClipLength));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.OnlyFadeOut, TransitionFadeSeconds);

            bool overlapped = false;
            float sampleAt = Time.realtimeSinceStartup + 1f;
            float midFadeVolume = -1f;
            float deadline = Time.realtimeSinceStartup + TransitionFadeSeconds + 2f;
            while (!second.IsPlaying)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "Timed out waiting for the incoming BGM to start after the outgoing fade-out.");
                overlapped |= first.IsPlaying && second.IsPlaying;
                if (midFadeVolume < 0f && Time.realtimeSinceStartup >= sampleAt)
                {
                    midFadeVolume = first.GetVolume();
                }
                yield return null;
            }

            Assert.IsFalse(overlapped, "OnlyFadeOut must be sequential - the incoming BGM waits for the outgoing fade-out to finish.");
            Assert.Greater(midFadeVolume, 0.05f, "1s into a 3s fade-out the outgoing BGM should still be audible - a read near 0 means it was cut, not faded.");
            Assert.Less(midFadeVolume, 0.95f, "1s into a 3s fade-out the outgoing BGM should already be well below full volume.");
            Assert.IsFalse(first.IsActive, "The outgoing BGM should have ended by the time the incoming one starts.");
            Assert.AreEqual(AudioConstant.FullVolume, second.GetVolume(), LinearTolerance,
                "OnlyFadeOut gives the incoming BGM no fade-in: it must be at full volume on its first playing frame.");
        }

        [UnityTest]
        public IEnumerator SetTransition_OnlyFadeIn_OutgoingCutsWhileIncomingFadesInFromSilence()
        {
            yield return RequireRealtimeAudioClock();
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            SoundID firstId = NewSound("OnlyFadeInBgmA", BroAudioType.Music, NewClip(TransitionBgmClipLength));
            SoundID secondId = NewSound("OnlyFadeInBgmB", BroAudioType.Music, NewClip(TransitionBgmClipLength));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.OnlyFadeIn, TransitionFadeSeconds);
            yield return WaitForPlaybackStart(second, "the incoming BGM to start without waiting on the outgoing one");
            float startedAt = Time.realtimeSinceStartup;

            Assert.IsFalse(first.IsActive, "OnlyFadeIn stops the outgoing BGM with no fade - it must already be gone when the incoming one starts.");

            yield return new WaitForSeconds(1f);
            float midFadeVolume = second.GetVolume();
            Assert.Less(midFadeVolume, 0.5f, "1s into a 3s fade-in the incoming BGM should still be well short of full volume - a full read means the fade-in was skipped.");

            yield return WaitUntilOrTimeout(() => second.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the incoming BGM's fade-in to reach full volume", TransitionFadeSeconds + 1.5f);
            Assert.Greater(Time.realtimeSinceStartup - startedAt, TransitionFadeSeconds - 1f,
                "The fade-in should take roughly its stated time, not complete almost at once.");
        }

        // The other half of the CrossFade overlap test: the overlap has to end.
        [UnityTest]
        public IEnumerator SetTransition_CrossFade_EndsOutgoingBGMOnceItsFadeOutCompletes()
        {
            yield return RequireRealtimeAudioClock();
            SoundManager.Instance.Setting.DefaultBGMTransition = Transition.Immediate;

            SoundID firstId = NewSound("CrossfadeEndBgmA", BroAudioType.Music, NewClip(TransitionBgmClipLength));
            SoundID secondId = NewSound("CrossfadeEndBgmB", BroAudioType.Music, NewClip(TransitionBgmClipLength));

            IAudioPlayer first = BroAudio.Play(firstId);
            first.AsBGM().SetTransition(Transition.Immediate);
            yield return WaitForPlaybackStart(first, "first BGM to start");

            IAudioPlayer second = BroAudio.Play(secondId);
            second.AsBGM().SetTransition(Transition.CrossFade, TransitionFadeSeconds);
            yield return WaitUntilOrTimeout(() => first.IsPlaying && second.IsPlaying,
                "both BGMs to be audible at once as the CrossFade opens", DefaultPlaybackWaitSeconds);

            yield return new WaitForSeconds(1f);
            Assert.IsTrue(first.IsActive && first.IsPlaying,
                "1s into a 3s CrossFade the outgoing BGM should still be playing - a CrossFade fades it, it does not cut it.");
            float outgoingMidFade = first.GetVolume();
            Assert.Greater(outgoingMidFade, 0.05f, "1s into a 3s CrossFade the outgoing BGM should still be audible.");
            Assert.Less(outgoingMidFade, 0.95f, "1s into a 3s CrossFade the outgoing BGM should already be well below full volume.");
            Assert.Less(second.GetVolume(), 0.5f, "1s into a 3s CrossFade the incoming BGM should still be well short of full volume.");

            yield return WaitForRecycle(first,
                "the outgoing BGM to end once its CrossFade fade-out completes (a timeout means the crossfade left it playing)",
                TransitionFadeSeconds + 1f);
            Assert.IsTrue(second.IsActive && second.IsPlaying, "Ending the outgoing BGM must leave the incoming one playing.");
            yield return WaitUntilOrTimeout(() => second.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the incoming BGM's fade-in to reach full volume alongside the outgoing fade-out", DefaultPlaybackWaitSeconds);
        }
    }
}
