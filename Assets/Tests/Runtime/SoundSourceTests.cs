using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SoundSource"/> component: inspector toggles, the three PositionModes, and the UnityEvent verbs.
    /// It is a thin front-end over <see cref="BroAudio"/>, so assertions pin dispatch (which overload, whether a
    /// hook fires, guard clauses), not the resulting audio.
    /// <para>
    /// BroAudio.Play only enqueues (SoundManager.LateUpdate drains), so yield before asserting AudioSource state.
    /// </para>
    /// </summary>
    public class SoundSourceTests : BroAudioTestFixture
    {
        // Positions are copied, not interpolated: absorbs float noise only.
        private const float PositionTolerance = 0.0001f;

        // GetVolume() is a live fade read: a threshold, not an equality.
        private const float NearTargetVolume = 0.95f;

        private static void AssertPosition(Vector3 expected, Vector3 actual, string message)
            => Assert.Less(Vector3.Distance(expected, actual), PositionTolerance, $"{message} (expected {expected}, was {actual})");

        /// <summary>
        /// Builds a tracked SoundSource. The host starts deactivated because AddComponent on an active object runs
        /// OnEnable before the fields are written; the returned source is active, so a playOnEnable one has already played.
        /// </summary>
        private SoundSource NewSource(SoundID id,
            SoundSource.PositionMode positionMode = SoundSource.PositionMode.Global,
            Vector3 position = default,
            bool playOnEnable = false,
            bool onlyPlayOnce = false,
            bool stopOnDisable = false,
            float overrideFadeOut = FadeData.UseClipSetting,
            float delay = 0f,
            PlaybackGroup overrideGroup = null)
        {
            GameObject host = Track(new GameObject("SoundSourceHost"));
            host.SetActive(false);
            host.transform.position = position;

            SoundSource source = host.AddComponent<SoundSource>();
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.Sound, id);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.PositionMode, positionMode);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.PlayOnEnable, playOnEnable);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.OnlyPlayOnce, onlyPlayOnce);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.StopOnDisable, stopOnDisable);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.OverrideFadeOut, overrideFadeOut);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.Delay, delay);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.OverrideGroup, overrideGroup);

            host.SetActive(true);
            return source;
        }

        #region Position modes
        [UnityTest]
        public IEnumerator Play_WithGlobalPositionMode_StaysTwoDimensionalWhereverTheHostSits()
        {
            SoundID id = NewSound("GlobalSourceSfx", BroAudioType.SFX, NewClip(2f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.Global, new Vector3(12f, 3f, -7f));

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", DefaultPlaybackWaitSeconds);

            AudioPlayer player = InstanceOf(source.CurrentPlayer);
            Assert.IsNotNull(player, "CurrentPlayer should wrap a real pooled AudioPlayer.");
            Assert.IsTrue(Utility.IsPlayedGlobally(player.PlayingPosition),
                "PositionMode.Global must route to the global Play overload, so the playback position stays the sentinel rather than the host's transform.");
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "A globally played sound must stay 2D even though its SoundSource sits away from the origin.");
        }

        [UnityTest]
        public IEnumerator Play_WithStayHerePositionMode_PlacesTheVoiceAndLeavesItBehindWhenTheHostMoves()
        {
            Vector3 origin = new Vector3(4f, 1f, -2f);
            SoundID id = NewSound("StayHereSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.StayHere, origin);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", DefaultPlaybackWaitSeconds);

            AudioPlayer player = InstanceOf(source.CurrentPlayer);
            AssertPosition(origin, player.PlayingPosition, "StayHere must play at the host's position");
            AssertPosition(origin, player.transform.position, "The pooled player should have been moved to the play position");
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "Playing with a specified position forces the voice to 3D.");

            source.transform.position = new Vector3(-20f, 8f, 15f);
            yield return WaitFrames(3);

            AssertPosition(origin, player.PlayingPosition, "StayHere is a snapshot: moving the host must not move the voice");
            AssertPosition(origin, player.transform.position, "StayHere is a snapshot: moving the host must not move the player");
        }

        [UnityTest]
        public IEnumerator Play_WithFollowGameObjectPositionMode_KeepsTheVoiceOnTheMovingHost()
        {
            Vector3 start = new Vector3(2f, 0f, 5f);
            Vector3 moved = new Vector3(-9f, 4f, 1f);
            SoundID id = NewSound("FollowSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.FollowGameObject, start);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", DefaultPlaybackWaitSeconds);

            AudioPlayer player = InstanceOf(source.CurrentPlayer);
            AssertPosition(start, player.transform.position, "A follow-target play should start on the target");
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "Playing with a follow target forces the voice to 3D.");

            source.transform.position = moved;

            // AudioPlayer.Update vs. this coroutine has no defined order: poll, don't assume the next frame.
            yield return WaitUntilOrTimeout(() => Vector3.Distance(player.transform.position, moved) < PositionTolerance,
                "the player to catch up with its follow target", 1f);
            AssertPosition(moved, player.PlayingPosition,
                "PlaybackPreference resolves Position from the live follow target, so it moves with the host too");
        }
        #endregion

        #region Enable / disable hooks
        [UnityTest]
        public IEnumerator OnEnable_WithPlayOnEnable_PlaysAgainOnEveryReEnable()
        {
            SoundID id = NewSound("PlayOnEnableSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", DefaultPlaybackWaitSeconds);

            source.gameObject.SetActive(false);
            yield return WaitFrames(2);
            // No Override Fade Out and no clip FadeOut resolve to an immediate stop.
            Assert.IsFalse(id.HasAnyPlayingInstances(), "Stop On Disable with the default fade must cut the voice immediately.");

            source.gameObject.SetActive(true);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "a second OnEnable to start playback again", DefaultPlaybackWaitSeconds);
        }

        [UnityTest]
        public IEnumerator OnEnable_WithOnlyPlayOnce_NeverPlaysASecondTime()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("OnlyOnceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, onlyPlayOnce: true, stopOnDisable: true);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the first OnEnable to start playback", DefaultPlaybackWaitSeconds);

            source.gameObject.SetActive(false);
            yield return WaitFrames(2);

            source.gameObject.SetActive(true);
            yield return WaitFrames(3);

            Assert.IsFalse(source.IsPlaying, "Only Play Once must suppress the second OnEnable's play.");
            Assert.IsFalse(id.HasAnyPlayingInstances(), "No voice at all should exist for the sound after a suppressed re-enable.");
        }

        [UnityTest]
        public IEnumerator OnDisable_WithoutStopOnDisable_LeavesTheVoicePlaying()
        {
            SoundID id = NewSound("KeepPlayingSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: false);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", DefaultPlaybackWaitSeconds);
            IAudioPlayer player = source.CurrentPlayer;

            source.gameObject.SetActive(false);
            yield return WaitFrames(3);

            Assert.IsTrue(player.IsPlaying,
                "Disabling the host must not stop the sound unless Stop On Disable is set - the player lives on SoundManager.");
        }

        [UnityTest]
        public IEnumerator OnDisable_WithOverrideFadeOut_RampsTheVoiceDownInsteadOfCuttingIt()
        {
            const float fadeOut = 0.5f;
            SoundID id = NewSound("DisableFadeSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true, overrideFadeOut: fadeOut);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(2);

            IAudioPlayer player = source.CurrentPlayer;
            Assert.GreaterOrEqual(player.GetVolume(), NearTargetVolume, "The clip has no FadeIn, so it should be at full volume before the disable.");

            source.gameObject.SetActive(false);

            // Poll, not a fixed 2-frame wait: two slow frames can outlast this 0.5s fade and false-fail IsActive.
            // The default fade cuts instantly, so any decline at all discriminates.
            yield return WaitUntilOrTimeout(() => player.GetVolume() < NearTargetVolume,
                "the override fade-out to begin ramping the volume down", fadeOut);
            Assert.IsTrue(player.IsActive, "A 0.5s override fade-out must keep the player alive while it ramps, not cut it instantly.");
            Assert.IsTrue(player.IsPlaying, "The voice stays audible for the length of the fade.");

            // Poll for the drop rather than assume an ease shape.
            yield return WaitUntilOrTimeout(() => player.GetVolume() < 0.5f, "the override fade-out to ramp the volume down", fadeOut + 0.5f);
            yield return WaitForRecycle(player, "the override fade-out to finish and recycle the player", fadeOut + 1f);
        }

        // Pins TEST_FINDINGS #35.
        [UnityTest]
        [Category("Finding_35")]
        public IEnumerator OnDisable_InTheSameFrameAsOnEnable_LeavesTheQueuedVoicePlaying()
        {
            SoundID id = NewSound("SameFrameDisableSfx", BroAudioType.SFX, NewClip(2f));

            // NewSource already ran OnEnable -> Play(); this deactivation lands before the queue drains.
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true);
            Assert.IsFalse(source.IsPlaying, "Precondition: the play is still queued, not yet audible, when OnDisable runs.");
            source.gameObject.SetActive(false);

            yield return WaitUntilOrTimeout(() => id.HasAnyPlayingInstances(),
                "the queued voice to start despite Stop On Disable having already run", DefaultPlaybackWaitSeconds);
        }
        #endregion

        #region Play / Stop / Pause verbs
        [UnityTest]
        public IEnumerator Play_WhileAlreadyPlaying_ReplacesThePreviousVoiceRatherThanLayeringIt()
        {
            SoundID id = NewSound("ReplaceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the first playback to start", DefaultPlaybackWaitSeconds);

            bool firstEnded = false;
            IAudioPlayer firstHandle = source.CurrentPlayer;
            firstHandle.OnEnd(_ => firstEnded = true);

            source.Play();
            Assert.AreNotSame(firstHandle, source.CurrentPlayer, "Each Play must hand the component a fresh player handle.");

            yield return WaitUntilOrTimeout(() => firstEnded, "the replaced voice to be stopped by the new Play", DefaultPlaybackWaitSeconds);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the replacement voice to start playing", DefaultPlaybackWaitSeconds);
        }

        [UnityTest]
        public IEnumerator StopPauseUnPause_DelegateToTheCurrentPlayerAndAreInertWithoutOne()
        {
            SoundID id = NewSound("SourceControlSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id);

            Assert.IsFalse(source.IsPlaying, "Nothing has been played yet.");
            Assert.IsFalse(source.IsActive);
            Assert.DoesNotThrow(() =>
            {
                source.Stop();
                source.Pause();
                source.UnPause();
                source.SetVolume(0.5f);
                source.SetPitch(0.5f);
            }, "Every verb must be a silent no-op before anything has been played - CurrentPlayer is still null.");

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "playback to start", DefaultPlaybackWaitSeconds);

            source.Pause(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !source.IsPlaying, "Pause to freeze the voice", DefaultPlaybackWaitSeconds);
            Assert.IsTrue(source.IsActive, "A paused SoundSource reports not playing, but still active.");

            source.UnPause(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "UnPause to resume the voice", DefaultPlaybackWaitSeconds);

            source.Stop(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !source.IsActive, "Stop to end playback and recycle the player", DefaultPlaybackWaitSeconds);
            Assert.IsFalse(source.IsPlaying, "A stopped SoundSource is neither active nor playing, and reading it after recycle must not throw.");
        }

        [UnityTest]
        public IEnumerator SetVolumeAndSetPitch_ApplyToTheLiveVoiceOnly()
        {
            SoundID id = NewSound("SourceModulationSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "playback to start", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(1);

            Assert.AreEqual(1f, source.CurrentPlayer.GetVolume(), LinearTolerance, "A freshly played default entity starts at full linear volume.");

            source.SetVolume(0.5f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.5f, source.CurrentPlayer.GetVolume(), LinearTolerance,
                "SetVolume must reach the live player's linear volume product (fadeTime defaults to immediate).");

            source.SetPitch(0.5f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.5f, source.CurrentPlayer.AudioSource.pitch, LinearTolerance, "SetPitch must reach the live AudioSource.");

            source.Stop(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !source.IsActive, "the voice to stop and recycle", DefaultPlaybackWaitSeconds);

            source.SetVolume(0.25f);
            source.SetPitch(2f);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the second playback to start", DefaultPlaybackWaitSeconds);
            yield return WaitFrames(1);

            Assert.AreEqual(1f, source.CurrentPlayer.GetVolume(), LinearTolerance,
                "Writes made while nothing was playing are dropped by the IsPlaying guard - a new play starts from the entity's own settings.");
            Assert.AreEqual(1f, source.CurrentPlayer.AudioSource.pitch, LinearTolerance,
                "Same for pitch: the guard drops the write rather than queueing it for the next play.");
        }
        #endregion

        #region Delay, group override, unassigned ID
        // PlayScheduled reports isPlaying from the call, so the playhead, not IsPlaying, proves the delay holds.
        [UnityTest]
        public IEnumerator OnEnable_WithDelay_HoldsThePlayheadUntilTheDelayElapses()
        {
            // Samples inside the 1.5s Delay window, which one frame of a decoupled DSP clock would skip past.
            yield return RequireRealtimeAudioClock();

            const float delay = 1.5f;
            SoundID id = NewSound("DelayedSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, delay: delay);

            yield return WaitFrames(2);
            Assert.IsTrue(source.IsActive, "A delayed play is active from the moment OnEnable schedules it.");
            Assert.AreEqual(0, source.CurrentPlayer.AudioSource.timeSamples, "The playhead must not have moved - still inside the Delay.");

            // Still ~1s short of the delay: not a race with the scheduled start.
            yield return WaitDspSeconds(0.5);
            Assert.AreEqual(0, source.CurrentPlayer.AudioSource.timeSamples, "Partway through the Delay, playback must still not have started audibly.");

            yield return WaitUntilOrTimeout(() => source.CurrentPlayer.AudioSource.timeSamples > 0,
                "audible playback to start once the Delay elapses", delay + 1f);
        }

        // Delay is on-enable only by design (the instruction text and inspector nest it under Play On Enable):
        // documented behavior, not a defect.
        [UnityTest]
        public IEnumerator Play_CalledDirectly_IgnoresTheInspectorDelay()
        {
            // Wall-clock vs. a DSP-scheduled playhead: on a decoupled DSP clock a wrongly-applied Delay elapses
            // in a sliver of real time and false-passes.
            yield return RequireRealtimeAudioClock();

            const float delay = 1f;
            SoundID id = NewSound("DirectPlayDelaySfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: false, delay: delay);

            float startedAt = Time.realtimeSinceStartup;
            source.Play();

            // Generous timeout; the elapsed-time assert below is what discriminates.
            yield return WaitUntilOrTimeout(() => source.CurrentPlayer.AudioSource.timeSamples > 0,
                "a direct Play to start audibly", RampConvergenceWaitSeconds);
            Assert.Less(Time.realtimeSinceStartup - startedAt, delay,
                "Play() must start on the next queue drain, not wait out the inspector's Delay.");
        }

        [UnityTest]
        public IEnumerator Play_WithOverrideGroup_LetsTheGroupRejectTheSecondSource()
        {
            DefaultPlaybackGroup group = NewGroup(maxPlayableCount: 1);
            SoundID firstId = NewSound("GroupedSourceA", BroAudioType.SFX, NewClip(3f));
            SoundID secondId = NewSound("GroupedSourceB", BroAudioType.SFX, NewClip(3f));
            SoundSource first = NewSource(firstId, overrideGroup: group);
            SoundSource second = NewSource(secondId, overrideGroup: group);

            first.Play();
            second.Play();

            Assert.IsTrue(first.IsActive, "The first play is within the shared group's limit of one.");
            Assert.IsFalse(second.IsActive, "The Override Playback Group must gate the second SoundSource's play.");
            Assert.AreEqual(SoundID.Invalid, second.CurrentPlayer.ID,
                "A rejected play leaves the component holding the inert empty player, not null.");

            yield return WaitUntilOrTimeout(() => first.IsPlaying, "the accepted voice to start", DefaultPlaybackWaitSeconds);
            Assert.IsFalse(second.IsPlaying, "The rejected SoundSource must never become audible.");
        }

        [UnityTest]
        public IEnumerator OnEnable_WithUnassignedSoundID_LogsOnceAndStaysInert()
        {
            // Expect before constructing: NewSource activates the host, so the error is logged inside it.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

            SoundSource source = NewSource(SoundID.Invalid, playOnEnable: true, delay: 0.3f, stopOnDisable: true);

            yield return WaitFrames(2);

            Assert.IsNotNull(source.CurrentPlayer, "A failed play must still leave CurrentPlayer non-null - OnEnable calls SetDelay on it unguarded.");
            Assert.AreEqual(SoundID.Invalid, source.CurrentPlayer.ID);
            Assert.IsFalse(source.IsPlaying);
            Assert.IsFalse(source.IsActive);

            Assert.DoesNotThrow(() =>
            {
                source.Stop();
                source.Pause();
                source.UnPause();
                source.SetVolume(0.5f);
                source.SetPitch(1.5f);
                source.gameObject.SetActive(false);
            }, "Every verb, and the Stop On Disable path, must stay inert on an unassigned SoundSource.");
        }
        #endregion
    }
}