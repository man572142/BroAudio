using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
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
    /// A handed-over sound is tracked through BroAudio.HasAnyPlayingInstances and through the
    /// GetActivePlayers reflection helper below (a thin window onto SoundManager's private player pool),
    /// never by asserting that the original IAudioPlayer handle's IsPlaying stays true across a seam -
    /// whether a caller's handle keeps tracking the sound after a handover is exactly the kind of internal
    /// detail these tests deliberately do not depend on either way.
    /// </para>
    /// <para>
    /// Not included: the inventory's "transition time longer than the clip" edge case for SeamlessLoop.
    /// Static reading of ScheduleNextPlayback (AudioPlayer.Playback.cs) shows that when TransitionTime
    /// exceeds the clip's playable duration, every handover's DSP wait gate is already satisfied at the
    /// moment it's evaluated (AudioSettings.dspTime does not advance across a purely synchronous call
    /// chain), so RestartCoroutine's synchronous-until-first-yield behavior causes each handover to spawn
    /// the next one immediately, recursively, with no natural terminator - a real risk of unbounded
    /// recursion / StackOverflowException rather than a one-time "collapses to immediate" quirk. That is
    /// a genuine finding worth fixing or guarding in production code, but not something to exercise from
    /// an automated test that would crash the whole Editor process if triggered. See the final report for
    /// this file.
    /// </para>
    /// </summary>
    public class LoopHandoverTests : BroAudioTestFixture
    {
        private static readonly MethodInfo GetCurrentAudioPlayersMethod =
            typeof(SoundManager).GetMethod("GetCurrentAudioPlayers", BindingFlags.Instance | BindingFlags.NonPublic);

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

        // 2.2 - a looping entity never sets AudioSource.loop; instead a fresh player is handed over at (or
        // near) the natural end of each iteration. The original handle is not asserted to keep tracking
        // across the seam - only BroAudio.HasAnyPlayingInstances is used to confirm the sound survives.
        [UnityTest]
        public IEnumerator Play_WithPlainLoop_NeverSetsAudioSourceLoopAndSurvivesMultipleSeams()
        {
            const float ClipSeconds = 0.4f;
            AudioEntity entity = NewEntity("PlainLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, "Loop", true);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
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

        // 2.3 - a seamless loop's transition time is applied as both the outgoing player's fade-out and the
        // incoming player's fade-in, and BeginHandover runs before the fade-out starts - so for the whole
        // transition window, two distinct players are simultaneously active and audible (a real crossfade).
        [UnityTest]
        public IEnumerator Play_WithSeamlessLoop_CrossfadesTwoPlayersAcrossTheSeam()
        {
            const float ClipSeconds = 1f;
            const float TransitionSeconds = 0.3f;
            AudioEntity entity = NewEntity("SeamlessLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, "SeamlessLoop", true);
            TestAudioLibrary.SetPrivateField(entity, "TransitionTime", TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            double crossfadeMidpointDsp = startDsp.Value + ClipSeconds - (TransitionSeconds / 2.0);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= crossfadeMidpointDsp,
                "the dsp clock to reach the middle of the crossfade window", 5f);

            List<AudioPlayer> duringCrossfade = GetActivePlayers(id);
            Assert.AreEqual(2, duringCrossfade.Count,
                "A seamless-loop crossfade must have exactly two active players overlapping mid-transition.");

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

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "the intro clip to start playing", 2f);
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
        [UnityTest]
        public IEnumerator Pause_DuringSeamlessLoopHandoverSeam_DoesNotThrowAndResumes()
        {
            const float ClipSeconds = 1f;
            const float TransitionSeconds = 0.5f; // wide crossfade window so the pause reliably lands inside it
            AudioEntity entity = NewEntity("PauseSeamSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, "SeamlessLoop", true);
            TestAudioLibrary.SetPrivateField(entity, "TransitionTime", TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            double seamMidpointDsp = startDsp.Value + ClipSeconds - (TransitionSeconds / 2.0);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= seamMidpointDsp,
                "the dsp clock to reach the middle of the crossfade seam", 5f);

            Assert.DoesNotThrow(() => player.Pause(),
                "Pause landing inside a seamless-loop handover seam must never throw.");

            yield return WaitFrames(3);

            Assert.DoesNotThrow(() => player.UnPause(),
                "UnPause after a seam-straddling pause must never throw.");

            yield return WaitUntilOrTimeout(() => BroAudio.HasAnyPlayingInstances(id),
                "the sound to be audibly playing again after UnPause", 3f);
        }
    }
}
