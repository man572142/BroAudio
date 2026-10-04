using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Play/Stop/Pause lifecycle, the queued-vs-playing window, stale handles after recycle, the
    /// rejected-Play null object, and the callback contract.
    /// </summary>
    public class PlaybackLifecycleTests : BroAudioTestFixture
    {
        /// <summary>Rejects every Play without needing a real PlaybackGroup asset.</summary>
        private class RejectingValidator : IPlayableValidator
        {
            public bool IsPlayable(SoundID id, Vector3 position) => false;
            public void OnGetPlayer(IAudioPlayer player) { }
        }

        // Keeping a finished handle around is common; it must never crash.
        [UnityTest]
        public IEnumerator StaleHandle_AfterRecycle_IsInertNotFatal()
        {
            // Checked with the warning on and off; the warning's presence is pinned, its wording is not.
            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = true;

            SoundID id = NewSound("StaleHandleSfx", BroAudioType.SFX, NewClip(0.2f));
            IAudioPlayer player = BroAudio.Play(id);

            yield return WaitForPlaybackStart(player);
            yield return WaitForRecycle(player, "the short clip to finish and the player to recycle");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            Assert.IsNull(player.AudioSource, "A recycled wrapper resolves AudioSource to null with the warning enabled.");

            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = false;
            Assert.IsNull(player.AudioSource, "Still null with the warning turned off.");

            IAudioPlayer afterSetVolume = null;
            IMusicPlayer afterBGM = null;
            SoundID staleID = default;
            IBroAudioClip staleClip = null;
            float[] staleOutput = { 1f };

            Assert.DoesNotThrow(() =>
            {
                player.Stop();
                afterSetVolume = player.SetVolume(0.5f);
                afterBGM = player.AsBGM();
                _ = player.AudioSource;
                staleID = player.ID;
                staleClip = player.CurrentPlayingClip;
                player.GetOutputData(staleOutput, 0);
            }, "Touching a stale, recycled handle must never throw.");

            Assert.IsFalse(player.IsActive, "A recycled handle must report inactive.");
            Assert.AreEqual(SoundID.Invalid, staleID, "A recycled handle's ID must read back as Invalid.");
            Assert.IsNull(staleClip, "A recycled handle's CurrentPlayingClip must read back as null.");
            Assert.AreEqual(1f, staleOutput[0], "GetOutputData on a recycled handle must leave the caller's buffer untouched.");
            Assert.IsNotNull(afterSetVolume, "SetVolume on a stale handle must still return a usable object, not null.");
            Assert.IsNotNull(afterBGM, "AsBGM on a stale handle must still return a usable object, not null.");
        }

        /// <summary>So only the Stop call can explain a player going inactive.</summary>
        private const float LongerThanAnyWaitClipSeconds = 10f;

        /// <summary>
        /// Gets the playhead clearly off 0, where "resumed at or after the paused position" would also be
        /// true of a restart.
        /// </summary>
        private const double PrePauseDspSeconds = 0.5;

        /// <summary>
        /// On the DSP clock: a handful of frames can fit inside one audio buffer and prove nothing.
        /// </summary>
        private const double FrozenPlayheadDspSeconds = 0.5;

        // The fixture's teardown isolation depends on this. A zero-fade Stop recycles inside the call, so no wait.
        [UnityTest]
        public IEnumerator Stop_WithAllFlag_DeactivatesEveryConcreteType()
        {
            List<IAudioPlayer> players = new List<IAudioPlayer>();
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                SoundID id = NewSound("AllFlag_" + audioType, audioType, NewClip(LongerThanAnyWaitClipSeconds));
                players.Add(BroAudio.Play(id));
            }

            foreach (IAudioPlayer player in players)
            {
                yield return WaitForPlaybackStart(player, "each player to start playing");
            }

            BroAudio.Stop(BroAudioType.All, 0f);

            for (int i = 0; i < players.Count; i++)
            {
                Assert.IsFalse(players[i].IsActive,
                    $"Stop(All, 0f) must have recycled the {ConcreteAudioTypes[i]} player by the time the call returns.");
            }

            yield return WaitFrames(2);
            for (int i = 0; i < players.Count; i++)
            {
                Assert.IsFalse(players[i].IsActive, $"The stopped {ConcreteAudioTypes[i]} player must stay stopped.");
            }
        }

        [UnityTest]
        public IEnumerator Stop_WithSingleFlag_LeavesOtherTypesPlaying()
        {
            SoundID sfxId = NewSound("SingleFlagSfx", BroAudioType.SFX, NewClip(LongerThanAnyWaitClipSeconds));
            SoundID musicId = NewSound("SingleFlagMusic", BroAudioType.Music, NewClip(LongerThanAnyWaitClipSeconds));

            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);

            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", DefaultPlaybackWaitSeconds);

            BroAudio.Stop(BroAudioType.SFX, 0f);
            Assert.IsFalse(sfxPlayer.IsActive, "Stop(SFX, 0f) must have recycled the SFX player by the time the call returns.");

            yield return WaitFrames(2);

            Assert.IsTrue(musicPlayer.IsActive, "Stopping SFX must not deactivate a Music player.");
            Assert.IsTrue(musicPlayer.IsPlaying, "Stopping SFX must not stop a Music player.");
        }

        // The other id shares the BroAudioType, so a match by type would wrongly catch it. The clips have no
        // FadeOut, so each matching player recycles inside the call.
        [UnityTest]
        public IEnumerator Stop_BySoundID_StopsEveryInstanceOfThatIdAndLeavesOtherIdsPlaying()
        {
            SoundID targetId = NewSound("IdStopTargetSfx", BroAudioType.SFX, NewClip(LongerThanAnyWaitClipSeconds));
            SoundID otherId = NewSound("IdStopOtherSfx", BroAudioType.SFX, NewClip(LongerThanAnyWaitClipSeconds));

            // One global, one positioned: comb-filtering in a playback group would reject a same-frame replay
            // of one id, but not this pair.
            IAudioPlayer targetGlobal = BroAudio.Play(targetId);
            IAudioPlayer targetPositioned = BroAudio.Play(targetId, Vector3.zero);
            IAudioPlayer other = BroAudio.Play(otherId);

            yield return WaitUntilOrTimeout(() => targetGlobal.IsPlaying && targetPositioned.IsPlaying && other.IsPlaying,
                "both instances of the target id and the other id to start playing", DefaultPlaybackWaitSeconds);

            BroAudio.Stop(targetId);

            Assert.IsFalse(targetGlobal.IsActive, "Stop(id) must stop the globally played instance of that id.");
            Assert.IsFalse(targetPositioned.IsActive, "Stop(id) must stop every instance of that id, not only the first one it finds.");
            Assert.IsTrue(other.IsActive && other.IsPlaying, "Stop(id) must leave a different id of the same BroAudioType playing.");

            yield return WaitFrames(2);
            Assert.IsFalse(BroAudio.HasAnyPlayingInstances(targetId), "No instance of the stopped id may be playing a frame later.");
            Assert.IsTrue(other.IsPlaying, "The other id must still be playing a frame later.");
        }

        // "Freeze in place": no playhead loss, no re-fade-in, no double OnStart.
        [UnityTest]
        public IEnumerator Pause_ThenUnPause_FreezesAndResumesFromSamePosition()
        {
            yield return RequireRealtimeAudioClock();

            int onStartCount = 0;
            SoundID id = NewSound("PauseSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => onStartCount++);

            yield return WaitForPlaybackStart(player);
            yield return WaitDspSeconds(PrePauseDspSeconds);
            Assert.AreEqual(1, onStartCount, "OnStart should have fired once by the time playback is underway.");

            player.Pause();
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause", DefaultPlaybackWaitSeconds);
            Assert.IsTrue(player.IsActive, "A paused player must remain active - pause does not deactivate.");

            int capturedTimeSamples = player.AudioSource.timeSamples;
            Assert.Greater(capturedTimeSamples, 0,
                "Precondition: the playhead must have moved before the pause, or the resume check below cannot tell a resume from a restart.");
            yield return WaitDspSeconds(FrozenPlayheadDspSeconds);
            Assert.AreEqual(capturedTimeSamples, player.AudioSource.timeSamples, "A paused AudioSource must not advance its playhead.");

            player.UnPause();
            yield return WaitForPlaybackStart(player, "the player to resume after UnPause");

            Assert.GreaterOrEqual(player.AudioSource.timeSamples, capturedTimeSamples,
                "Resuming must continue from the paused position, not restart from 0.");
            Assert.AreEqual(1, onStartCount, "OnStart must not re-fire when resuming from pause.");
        }

        // The queued-but-not-yet-drained window: Play only enqueues, LateUpdate starts the voice.
        [UnityTest]
        public IEnumerator IsActiveAndIsPlaying_AroundQueueDrain_TrackDifferentWindows()
        {
            SoundID id = NewSound("QueueWindowSfx", BroAudioType.SFX, NewClip(2f));

            IAudioPlayer player = BroAudio.Play(id);

            Assert.IsTrue(player.IsActive, "IsActive must be true the instant Play enqueues.");
            Assert.IsFalse(player.IsPlaying, "IsPlaying must still be false before SoundManager.LateUpdate drains the queue.");

            yield return WaitForPlaybackStart(player, "the queued player to start playing after a frame");

            Assert.IsTrue(player.IsActive);
            Assert.IsTrue(player.IsPlaying);
        }

        // A rejected Play must never hand back something that can crash calling code.
        [UnityTest]
        public IEnumerator Play_RejectedByValidator_ReturnsInertEmptyPlayer()
        {
            SoundID id = NewSound("RejectedSfx", BroAudioType.SFX, NewClip(1f));

            IAudioPlayer player = BroAudio.Play(id, new RejectingValidator());

            Assert.IsFalse(player.IsActive, "A rejected Play must return an inert handle.");
            Assert.IsFalse(player.IsPlaying);
            Assert.AreEqual(SoundID.Invalid, player.ID);

            IMusicPlayer musicPlayer = null;
            IAudioPlayer transitioned = null;
#if !UNITY_WEBGL
            IPlayerEffect dominator = null;
            IPlayerEffect quieted = null;
#endif

            Assert.DoesNotThrow(() =>
            {
                musicPlayer = player.SetVolume(0.5f).SetPitch(1.2f).AsBGM();
                transitioned = musicPlayer.SetTransition(Transition.Immediate);
#if !UNITY_WEBGL
                dominator = transitioned.AsDominator();
                quieted = dominator.QuietOthers(0.5f);
#endif
            }, "Chaining fluent calls off an inert, rejected Play handle must never throw.");

            Assert.IsNotNull(musicPlayer, "AsBGM at the end of an inert chain must never yield null.");
            Assert.IsNotNull(transitioned, "SetTransition on an inert chain must never yield null.");
#if !UNITY_WEBGL
            Assert.IsNotNull(dominator, "AsDominator on an inert chain must never yield null.");
            Assert.IsNotNull(quieted, "QuietOthers on an inert chain must never yield null.");
#endif

            yield break;
        }

        // OnStart fires once (not on resume), OnUpdate fires every frame while active, OnPause fires per transition.
        [UnityTest]
        public IEnumerator Callbacks_OnStartOnUpdateOnPause_FireWithExpectedCounts()
        {
            int onStartCount = 0;
            int onUpdateCount = 0;
            int onPauseCount = 0;

            SoundID id = NewSound("CallbackSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => onStartCount++);
            player.OnUpdate(_ => onUpdateCount++);
            player.OnPause(_ => onPauseCount++);

            yield return WaitForPlaybackStart(player);
            Assert.AreEqual(1, onStartCount, "OnStart should fire exactly once when playback starts.");

            yield return WaitFrames(3);
            Assert.Greater(onUpdateCount, 1, "OnUpdate should fire repeatedly (once per frame) while playing.");

            player.Pause();
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(1, onPauseCount, "OnPause should fire on the pause transition.");

            player.UnPause();
            yield return WaitForPlaybackStart(player, "the player to resume");
            Assert.AreEqual(1, onStartCount, "OnStart must not re-fire when resuming from pause.");

            player.Pause();
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause a second time", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(2, onPauseCount, "OnPause should fire again on a second, independent pause transition.");
        }

        // Trap: Recycle() clears ID to Invalid right after OnEnd, from the same call site.
        [UnityTest]
        public IEnumerator OnEnd_WhenPlaybackFinishes_FiresOnceWithOriginalID()
        {
            int onEndCount = 0;
            SoundID receivedID = default;

            SoundID id = NewSound("OnEndSfx", BroAudioType.SFX, NewClip(0.2f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnEnd(endedID =>
            {
                onEndCount++;
                receivedID = endedID;
            });

            yield return WaitForPlaybackStart(player);
            yield return WaitForRecycle(player, "the short clip to finish and recycle");

            Assert.AreEqual(1, onEndCount, "OnEnd should fire exactly once.");
            Assert.AreEqual(id, receivedID, "OnEnd's SoundID argument should equal the original ID despite the immediate recycle.");
        }

        [UnityTest]
        public IEnumerator TryGetEntityInfo_ForValidAndInvalidIds_ReturnsMatchingResult()
        {
            SoundID id = NewSound("EntityInfoSfx", BroAudioType.SFX, NewClip(1f));

            bool found = BroAudio.TryGetEntityInfo(id, out IReadOnlyAudioEntity entityInfo);

            Assert.IsTrue(found, "TryGetEntityInfo should succeed for a valid entity.");
            Assert.IsNotNull(entityInfo, "A successful lookup must hand back a non-null entity info.");
            Assert.AreEqual(1, entityInfo.Clips.Count, "The returned info should expose the entity's real clip data.");
            Assert.AreEqual(AudioConstant.FullVolume, entityInfo.MasterVolume, "The returned info should expose the entity's real MasterVolume.");

            bool foundInvalid = BroAudio.TryGetEntityInfo(default(SoundID), out IReadOnlyAudioEntity invalidInfo);

            Assert.IsFalse(foundInvalid, "TryGetEntityInfo should fail for an unassigned SoundID.");
            Assert.IsNull(invalidInfo, "The out-param must be null when the id doesn't resolve to an entity.");

            yield break;
        }

        [UnityTest]
        public IEnumerator Pause_ByBroAudioType_AffectsOnlyThatTypeAndUnPauseResumesFromSamePosition()
        {
            yield return RequireRealtimeAudioClock();

            SoundID sfxId = NewSound("TypePauseSfx", BroAudioType.SFX, NewClip(3f));
            SoundID musicId = NewSound("TypePauseMusic", BroAudioType.Music, NewClip(3f));
            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);

            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", DefaultPlaybackWaitSeconds);
            yield return WaitDspSeconds(PrePauseDspSeconds);

            BroAudio.Pause(BroAudioType.SFX);
            yield return WaitUntilOrTimeout(() => !sfxPlayer.IsPlaying, "the SFX player to pause", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(sfxPlayer.IsActive, "A paused player must remain active - pause does not deactivate.");
            Assert.IsTrue(musicPlayer.IsPlaying, "Pausing by BroAudioType.SFX must not touch a Music player.");

            int pausedSamples = sfxPlayer.AudioSource.timeSamples;
            Assert.Greater(pausedSamples, 0,
                "Precondition: the SFX playhead must have moved before the pause, or the resume check below cannot tell a resume from a restart.");
            yield return WaitDspSeconds(FrozenPlayheadDspSeconds);
            Assert.AreEqual(pausedSamples, sfxPlayer.AudioSource.timeSamples, "The paused SFX playhead must not advance.");

            BroAudio.UnPause(BroAudioType.SFX);
            yield return WaitForPlaybackStart(sfxPlayer, "the SFX player to resume after UnPause(type)");

            Assert.GreaterOrEqual(sfxPlayer.AudioSource.timeSamples, pausedSamples,
                "UnPause(type) must resume from the paused position, not restart from 0.");
        }

        // Same type, different id, so a match by type would wrongly catch the other player.
        [UnityTest]
        public IEnumerator Pause_BySoundID_AffectsOnlyThatIdNotOtherPlayersOfTheSameType()
        {
            SoundID targetId = NewSound("IdPauseTargetSfx", BroAudioType.SFX, NewClip(3f));
            SoundID otherId = NewSound("IdPauseOtherSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer targetPlayer = BroAudio.Play(targetId);
            IAudioPlayer otherPlayer = BroAudio.Play(otherId);

            yield return WaitUntilOrTimeout(() => targetPlayer.IsPlaying && otherPlayer.IsPlaying, "both players to start playing", DefaultPlaybackWaitSeconds);

            BroAudio.Pause(targetId);
            yield return WaitUntilOrTimeout(() => !targetPlayer.IsPlaying, "the targeted id's player to pause", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(2);

            Assert.IsTrue(otherPlayer.IsPlaying, "Pausing by SoundID must not affect a different SoundID of the same BroAudioType.");

            BroAudio.UnPause(targetId);
            yield return WaitForPlaybackStart(targetPlayer, "the targeted id's player to resume after UnPause(id)");
        }

        [UnityTest]
        public IEnumerator Pause_ByTypeWithFadeTime_CompletesOnlyAfterTheFadeElapses()
        {
            yield return RequireRealtimeAudioClock();

            const float fadeTime = 1f;
            SoundID id = NewSound("FadedPauseSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(3);

            BroAudio.Pause(BroAudioType.SFX, fadeTime);

            // AudioSource.Pause() only runs once the fade-out completes.
            yield return new WaitForSeconds(0.35f);
            Assert.IsTrue(player.IsPlaying, "A 1s fade-out pause must not have paused the AudioSource yet at 0.35s in.");

            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the fade-out to finish and the pause to actually take effect", fadeTime + 1f);
            Assert.IsTrue(player.IsActive, "A faded-out pause must still leave the player active, not recycled.");

            BroAudio.UnPause(BroAudioType.SFX, fadeTime);
            yield return WaitForPlaybackStart(player, "UnPause(type, fadeTime) to resume the AudioSource");
        }

        // Pins TEST_FINDINGS #41. The "not yet" half polls across a whole second rather than sampling one instant.
        [UnityTest]
        [Category("Finding_41")]
        public IEnumerator Stop_WithOnFinishedCallback_FiresAfterTheFadeButIsDroppedByARecycledHandle()
        {
            yield return RequireRealtimeAudioClock();

            const float fadeOut = 2f;
            bool fired = false;

            SoundID id = NewSound("StopCallbackSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(3);

            player.Stop(fadeOut, () => fired = true);

            float stillFadingUntil = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < stillFadingUntil)
            {
                Assert.IsFalse(fired, "onFinished must not fire while the 2s fade-out is still in flight.");
                yield return null;
            }

            Assert.IsTrue(player.IsPlaying,
                "The voice stays audible for the whole fade - StopControl's fade region runs before the StopMode.Stop switch case.");

            yield return WaitUntilOrTimeout(() => fired, "the fade-out to finish and Stop's onFinished callback to fire", fadeOut + 2f);
            Assert.IsFalse(player.IsActive,
                "EndPlaying() runs before onFinished, so the player is already recycled by the time the callback fires.");

            // The #41 defect half. Silences the recycled-handle warning, which is not under test here.
            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = false;

            bool firedOnRecycled = false;
            SoundID recycledId = NewSound("StopCallbackRecycledSfx", BroAudioType.SFX, NewClip(0.2f));
            IAudioPlayer recycledPlayer = BroAudio.Play(recycledId);

            yield return WaitForPlaybackStart(recycledPlayer);
            yield return WaitForRecycle(recycledPlayer, "the short clip to finish and the player to recycle");

            Assert.DoesNotThrow(() => recycledPlayer.Stop(() => firedOnRecycled = true),
                "Stop(onFinished) on a stale handle must never throw.");
            yield return WaitFrames(3);

            Assert.IsFalse(firedOnRecycled,
                "characterizes: Stop(onFinished) on a recycled handle is a silent no-op - the callback is dropped, never invoked.");
        }

        // A resume re-enters PlayControl, so the clip's fade-in setting applies to it too.
        [UnityTest]
        public IEnumerator UnPause_OnClipWithFadeIn_RestartsTheFadeInFromSilenceUnlessOverridden()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipFadeIn = 2f;
            AudioEntity entity = NewEntity("ResumeFadeInSfx", BroAudioType.SFX, NewClip(20f));
            entity.Clips[0].FadeIn = ClipFadeIn;
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            yield return WaitForPlaybackStart(player);
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the initial clip fade-in to complete", ClipFadeIn + 1.5f);

            player.Pause(0f);
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause");
            int pausedAt = player.AudioSource.timeSamples;

            player.UnPause();
            Assert.AreEqual(0f, player.GetVolume(), LinearTolerance,
                "characterizes: UnPause() with the clip's fade-in setting restarts that fade-in from silence, in the frame of the call.");
            yield return WaitForPlaybackStart(player, "the player to resume");
            Assert.GreaterOrEqual(player.AudioSource.timeSamples, pausedAt, "The resume must continue from the paused playhead, fade-in or not.");

            yield return new WaitForSeconds(0.5f);
            Assert.Less(player.GetVolume(), 0.9f, "0.5s into a 2s resume fade-in the volume should still be ramping.");
            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - 0.001f,
                "the resume fade-in to complete", ClipFadeIn + 1.5f);

            player.Pause(0f);
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause a second time");
            player.UnPause(0f);
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "UnPause(0f) overrides the clip fade-in: the resumed player is at full volume in the frame of the call.");
        }
    }
}