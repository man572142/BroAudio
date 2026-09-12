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
    /// across a handover seam. AudioPlayerInstanceWrapper.UpdateInstance (AudioPlayerInstanceWrapper.cs:112-156)
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

        // 2.2 - a looping entity never sets AudioSource.loop; instead a fresh player is handed over at (or
        // near) the natural end of each iteration. The original handle is not asserted to keep tracking
        // across the seam - only BroAudio.HasAnyPlayingInstances is used to confirm the sound survives.
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
        // PlaybackHandoverData.TrackVolume (AudioPlayer.Playback.cs:354) and ReceiveHandover completes the
        // incoming player's fader on it (line 399), while UpdateInstance moves the registered onEnd
        // delegates to the incoming player and leaves the outgoing player's _onEnd null
        // (AudioPlayerInstanceWrapper.cs:128-134 via AudioPlayer.TransferOnEnds, AudioPlayer.cs:341-350) -
        // so EndPlaying's _onEnd?.Invoke (AudioPlayer.Playback.cs:570) is a no-op at a seam and fires once,
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
            // _clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current (AudioPlayer.Volume.cs:113-116);
            // the latter two are 1 here, so it reads back exactly what SetVolume put on the track fader.
            player.OnEnd(_ => onEndCount++);
            player.SetVolume(TargetVolume);
            Assert.AreEqual(TargetVolume, player.GetVolume(), LinearTolerance,
                "Precondition: SetVolume must land on the first player before any handover.");

            double secondSeamDsp = startDsp.Value + (ClipSeconds * 2);
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= secondSeamDsp + 0.2,
                "the dsp clock to pass two loop seams", 5f);

            // If the handle had been left behind on the first player, the wrapper would have been recycled
            // with it (AudioPlayer.Recycling.cs:64) and IsActive would read false.
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
        [UnityTest]
        public IEnumerator Play_WithSeamlessLoop_CrossfadesTwoPlayersAcrossTheSeam()
        {
            yield return RequireRealtimeAudioClock();

            // TransitionSeconds widened to 1s (was 0.3s, ~0.15s slack either side of the sampled midpoint -
            // thinner than a single capped hitch frame at Time.maximumDeltaTime's ~0.333s). ClipSeconds
            // grows to match so the whole crossfade window still sits comfortably inside one clip iteration.
            const float ClipSeconds = 3f;
            const float TransitionSeconds = 1f;
            AudioEntity entity = NewEntity("SeamlessLoopSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), TransitionSeconds);
            SoundID id = IdOf(entity);

            double? startDsp = null;
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => startDsp ??= AudioSettings.dspTime);

            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => startDsp.HasValue, "OnStart to fire for the first iteration", 2f);

            // Wait to the start of the crossfade window, then poll for the 2-player overlap anywhere inside
            // it, rather than sampling a single dsp-clock instant - with a 1s-wide window any frame that
            // lands inside it will do, so this is no longer sensitive to one slow frame's overshoot.
            double crossfadeStartDsp = startDsp.Value + ClipSeconds - TransitionSeconds;
            yield return WaitUntilOrTimeout(() => AudioSettings.dspTime >= crossfadeStartDsp,
                "the dsp clock to reach the start of the crossfade window", 5f);

            yield return WaitUntilOrTimeout(() => GetActivePlayers(id).Count == 2,
                "both players to be simultaneously active at some point during the crossfade window", TransitionSeconds + 1f);

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
        [UnityTest]
        public IEnumerator Pause_DuringSeamlessLoopHandoverSeam_DoesNotThrowAndResumes()
        {
            const float ClipSeconds = 1f;
            const float TransitionSeconds = 0.5f; // wide crossfade window so the pause reliably lands inside it
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