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
    /// Inventory slice 1.1-1.5, 1.11: the Play/Stop/Pause lifecycle, the queued-vs-playing window,
    /// the stale-handle contract after recycle, the rejected-Play null-object path, and the
    /// OnStart/OnUpdate/OnPause/OnEnd callback contract. See Docs/inventory/lifecycle.md.
    /// </summary>
    public class PlaybackLifecycleTests : BroAudioTestFixture
    {
        /// <summary>Forces SoundManager.IsPlayable to reject every Play call, without needing a real PlaybackGroup asset.</summary>
        private class RejectingValidator : IPlayableValidator
        {
            public bool IsPlayable(SoundID id, Vector3 position) => false;
            public void OnGetPlayer(IAudioPlayer player) { }
        }

        // 1.1 - the single most important test in this file: a caller that keeps a completed
        // IAudioPlayer reference around (a very common real-world pattern) must never crash.
        [UnityTest]
        public IEnumerator StaleHandle_AfterRecycle_IsInertNotFatal()
        {
            // Silence AudioPlayerInstanceWrapper's stale-access warning; this is log suppression, not
            // the behavior under test. The fixture restores RuntimeSetting in TearDown.
            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = false;

            SoundID id = NewSound("StaleHandleSfx", BroAudioType.SFX, NewClip(0.2f));
            IAudioPlayer player = BroAudio.Play(id);

            yield return WaitForPlaybackStart(player);
            yield return WaitForRecycle(player, "the short clip to finish and the player to recycle");

            IAudioPlayer afterSetVolume = null;
            IMusicPlayer afterBGM = null;
            SoundID staleID = default;

            Assert.DoesNotThrow(() =>
            {
                player.Stop();
                afterSetVolume = player.SetVolume(0.5f);
                afterBGM = player.AsBGM();
                _ = player.AudioSource;
                staleID = player.ID;
            }, "Touching a stale, recycled handle must never throw.");

            Assert.IsFalse(player.IsActive, "A recycled handle must report inactive.");
            Assert.AreEqual(SoundID.Invalid, staleID, "A recycled handle's ID must read back as Invalid.");
            Assert.IsNotNull(afterSetVolume, "SetVolume on a stale handle must still return a usable object, not null.");
            Assert.IsNotNull(afterBGM, "AsBGM on a stale handle must still return a usable object, not null.");
        }

        // 1.2 - the suite's own teardown isolation depends on Stop(All, 0f) reliably clearing every type.
        [UnityTest]
        public IEnumerator Stop_WithAllFlag_DeactivatesEveryConcreteType()
        {
            List<IAudioPlayer> players = new List<IAudioPlayer>();
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                SoundID id = NewSound("AllFlag_" + audioType, audioType, NewClip(2f));
                players.Add(BroAudio.Play(id));
            }

            foreach (IAudioPlayer player in players)
            {
                yield return WaitForPlaybackStart(player, "each player to start playing");
            }

            BroAudio.Stop(BroAudioType.All, 0f);

            foreach (IAudioPlayer player in players)
            {
                yield return WaitForRecycle(player, "every player to stop after Stop(All, 0f)");
            }
        }

        [UnityTest]
        public IEnumerator Stop_WithSingleFlag_LeavesOtherTypesPlaying()
        {
            SoundID sfxId = NewSound("SingleFlagSfx", BroAudioType.SFX, NewClip(2f));
            SoundID musicId = NewSound("SingleFlagMusic", BroAudioType.Music, NewClip(2f));

            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);

            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", 2f);

            BroAudio.Stop(BroAudioType.SFX, 0f);

            yield return WaitForRecycle(sfxPlayer, "the SFX player to stop");
            yield return WaitFrames(2);

            Assert.IsTrue(musicPlayer.IsActive, "Stopping SFX must not deactivate a Music player.");
            Assert.IsTrue(musicPlayer.IsPlaying, "Stopping SFX must not stop a Music player.");
        }

        // 1.3 - "freeze in place": no playhead loss, no re-fade-in, no double OnStart.
        [UnityTest]
        public IEnumerator Pause_ThenUnPause_FreezesAndResumesFromSamePosition()
        {
            int onStartCount = 0;
            SoundID id = NewSound("PauseSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            player.OnStart(_ => onStartCount++);

            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(3);
            Assert.AreEqual(1, onStartCount, "OnStart should have fired once by the time playback is underway.");

            player.Pause();
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause", 2f);
            Assert.IsTrue(player.IsActive, "A paused player must remain active - pause does not deactivate.");

            int capturedTimeSamples = player.AudioSource.timeSamples;
            yield return WaitFrames(5);
            Assert.AreEqual(capturedTimeSamples, player.AudioSource.timeSamples, "A paused AudioSource must not advance its playhead.");

            player.UnPause();
            yield return WaitForPlaybackStart(player, "the player to resume after UnPause");

            Assert.GreaterOrEqual(player.AudioSource.timeSamples, capturedTimeSamples,
                "Resuming must continue from the paused position, not restart from 0.");
            Assert.AreEqual(1, onStartCount, "OnStart must not re-fire when resuming from pause.");
        }

        // 1.4 - the queued-but-not-yet-drained window: Play only enqueues, LateUpdate starts the voice.
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

        // 1.5 - a rejected Play must never hand back something that can crash calling code.
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
            IPlayerEffect dominator = null;
            IPlayerEffect quieted = null;

            Assert.DoesNotThrow(() =>
            {
                musicPlayer = player.SetVolume(0.5f).SetPitch(1.2f).AsBGM();
                transitioned = musicPlayer.SetTransition(Transition.Immediate);
                dominator = transitioned.AsDominator();
                quieted = dominator.QuietOthers(0.5f);
            }, "Chaining fluent calls off an inert, rejected Play handle must never throw.");

            Assert.IsNotNull(musicPlayer, "AsBGM at the end of an inert chain must never yield null.");
            Assert.IsNotNull(transitioned, "SetTransition on an inert chain must never yield null.");
            Assert.IsNotNull(dominator, "AsDominator on an inert chain must never yield null.");
            Assert.IsNotNull(quieted, "QuietOthers on an inert chain must never yield null.");

            yield break;
        }

        // 1.11 - OnStart fires once (not on resume), OnUpdate fires every frame while active, OnPause fires per transition.
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
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause", 2f);
            Assert.AreEqual(1, onPauseCount, "OnPause should fire on the pause transition.");

            player.UnPause();
            yield return WaitForPlaybackStart(player, "the player to resume");
            Assert.AreEqual(1, onStartCount, "OnStart must not re-fire when resuming from pause.");

            player.Pause();
            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the player to pause a second time", 2f);
            Assert.AreEqual(2, onPauseCount, "OnPause should fire again on a second, independent pause transition.");
        }

        // 1.11 - OnEnd fires once, and its SoundID argument still equals the original ID even though
        // Recycle() (which clears ID to Invalid) runs immediately after, from the same call site.
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

        // TryGetEntityInfo: a valid id resolves to the entity's real read-only data; an unassigned
        // SoundID (default) fails and leaves the out-param null.
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

        // Facade broadcast: BroAudio.Pause(BroAudioType)/UnPause(BroAudioType) forward to
        // SoundManager.Pause(BroAudioType, ...), which matches every live player by type flags
        // (SoundManager.Playback.cs ~229-241). Only the matching type must freeze/resume.
        [UnityTest]
        public IEnumerator Pause_ByBroAudioType_AffectsOnlyThatTypeAndUnPauseResumesFromSamePosition()
        {
            SoundID sfxId = NewSound("TypePauseSfx", BroAudioType.SFX, NewClip(3f));
            SoundID musicId = NewSound("TypePauseMusic", BroAudioType.Music, NewClip(3f));
            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);

            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", 2f);
            yield return WaitFrames(3);

            BroAudio.Pause(BroAudioType.SFX);
            yield return WaitUntilOrTimeout(() => !sfxPlayer.IsPlaying, "the SFX player to pause", 2f);

            Assert.IsTrue(sfxPlayer.IsActive, "A paused player must remain active - pause does not deactivate.");
            Assert.IsTrue(musicPlayer.IsPlaying, "Pausing by BroAudioType.SFX must not touch a Music player.");

            int pausedSamples = sfxPlayer.AudioSource.timeSamples;
            yield return WaitFrames(5);
            Assert.AreEqual(pausedSamples, sfxPlayer.AudioSource.timeSamples, "The paused SFX playhead must not advance.");

            BroAudio.UnPause(BroAudioType.SFX);
            yield return WaitForPlaybackStart(sfxPlayer, "the SFX player to resume after UnPause(type)");

            Assert.GreaterOrEqual(sfxPlayer.AudioSource.timeSamples, pausedSamples,
                "UnPause(type) must resume from the paused position, not restart from 0.");
        }

        // Facade broadcast: BroAudio.Pause(SoundID)/UnPause(SoundID) forward to
        // SoundManager.Pause(SoundID, ...), matching live players by exact id (SoundManager.Playback.cs
        // ~206-222) rather than by type - a second player of the same type but a different SoundID
        // must be left untouched.
        [UnityTest]
        public IEnumerator Pause_BySoundID_AffectsOnlyThatIdNotOtherPlayersOfTheSameType()
        {
            SoundID targetId = NewSound("IdPauseTargetSfx", BroAudioType.SFX, NewClip(3f));
            SoundID otherId = NewSound("IdPauseOtherSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer targetPlayer = BroAudio.Play(targetId);
            IAudioPlayer otherPlayer = BroAudio.Play(otherId);

            yield return WaitUntilOrTimeout(() => targetPlayer.IsPlaying && otherPlayer.IsPlaying, "both players to start playing", 2f);

            BroAudio.Pause(targetId);
            yield return WaitUntilOrTimeout(() => !targetPlayer.IsPlaying, "the targeted id's player to pause", 2f);
            yield return WaitFrames(2);

            Assert.IsTrue(otherPlayer.IsPlaying, "Pausing by SoundID must not affect a different SoundID of the same BroAudioType.");

            BroAudio.UnPause(targetId);
            yield return WaitForPlaybackStart(targetPlayer, "the targeted id's player to resume after UnPause(id)");
        }

        // Facade broadcast, fade overloads: BroAudio.Pause(type, fadeTime)/UnPause(type, fadeTime) feed
        // fadeTime through to AudioPlayer.Pause/UnPause's own fade-out/fade-in. Fade progress uses
        // capped Time.deltaTime, so the fade window is kept wide (>=1s) to stay CI-safe.
        [UnityTest]
        public IEnumerator Pause_ByTypeWithFadeTime_CompletesOnlyAfterTheFadeElapses()
        {
            const float fadeTime = 1f;
            SoundID id = NewSound("FadedPauseSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitFrames(3);

            BroAudio.Pause(BroAudioType.SFX, fadeTime);

            // Shortly after issuing the fade-out pause, the source must still be audibly playing -
            // AudioSource.Pause() is only called once the fade-out completes (AudioPlayer.Playback.cs
            // StopControl's fade region runs before the StopMode.Pause switch case).
            yield return WaitDspSeconds(0.35);
            Assert.IsTrue(player.IsPlaying, "A 1s fade-out pause must not have paused the AudioSource yet at 0.35s in.");

            yield return WaitUntilOrTimeout(() => !player.IsPlaying, "the fade-out to finish and the pause to actually take effect", fadeTime + 1f);
            Assert.IsTrue(player.IsActive, "A faded-out pause must still leave the player active, not recycled.");

            BroAudio.UnPause(BroAudioType.SFX, fadeTime);
            yield return WaitForPlaybackStart(player, "UnPause(type, fadeTime) to resume the AudioSource");
        }
    }
}