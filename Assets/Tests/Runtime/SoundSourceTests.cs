using System.Collections;
using System.Text.RegularExpressions;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SoundSource"/> no-code component: the inspector toggles (Play On Enable, Only Play
    /// Once, Stop On Disable, Override Fade Out, Delay, Override Playback Group), the three PositionModes,
    /// and the Play/Stop/Pause/SetVolume/SetPitch verbs a UnityEvent wires up.
    /// <para>
    /// SoundSource owns no audio state of its own - it is a thin, serialized front-end over
    /// <see cref="BroAudio"/> plus one cached <see cref="IAudioPlayer"/>. So every assertion here is about
    /// the *dispatch*: which BroAudio overload a mode picks, whether a lifecycle hook fires the call at
    /// all, and whether the guard clauses keep a missing player from throwing. The audio behavior each
    /// call ultimately produces is already pinned by the lifecycle / fade / group files.
    /// </para>
    /// <para>
    /// Two mechanics shape the whole file. First, AddComponent on an *active* GameObject runs OnEnable
    /// immediately, so <see cref="NewSource"/> builds the host deactivated, writes the serialized fields,
    /// and only then activates it - otherwise every playOnEnable test would play with default settings.
    /// Second, BroAudio.Play only enqueues (SoundManager.LateUpdate drains), so nothing asserts on
    /// AudioSource state without first yielding, and the one test that deliberately exploits that window
    /// (<see cref="OnDisable_InTheSameFrameAsOnEnable_LeavesTheQueuedVoicePlaying"/>) says so.
    /// </para>
    /// </summary>
    public class SoundSourceTests : BroAudioTestFixture
    {
        // Positions are copied verbatim from one transform to another - no interpolation - so this only
        // has to absorb float noise, not motion.
        private const float PositionTolerance = 0.0001f;

        // GetVolume() is a live fade read, so "still at full volume" / "already ramping" are thresholds,
        // not equalities. Matches FadeAndTrimTests' NearTargetThreshold.
        private const float NearTargetVolume = 0.95f;
        private const float LinearTolerance = 0.01f;

        // SoundSource.NameOf is compiled only under UNITY_EDITOR, but Tests.asmdef targets every platform,
        // so the field names are spelled out here the same way the other fixtures spell out AudioEntity's.
        private const string SoundField = "_sound";
        private const string PositionModeField = "_positionMode";
        private const string PlayOnEnableField = "_playOnEnable";
        private const string OnlyPlayOnceField = "_onlyPlayOnce";
        private const string StopOnDisableField = "_stopOnDisable";
        private const string OverrideFadeOutField = "_overrideFadeOut";
        private const string DelayField = "_delay";
        private const string OverrideGroupField = "_overrideGroup";

        /// <summary>
        /// The pooled AudioPlayer behind a handle, via the wrapper's own explicit conversion (the same one
        /// SoundSourceEditor uses to draw the Current Player field). Needed because position mode is only
        /// observable on the player's Transform - IAudioSourceProxy exposes no transform or gameObject.
        /// Returns null for Empty.AudioPlayer and for a recycled handle.
        /// </summary>
        private static AudioPlayer PlayerBehind(IAudioPlayer player)
            => player is AudioPlayerInstanceWrapper wrapper ? (AudioPlayer)wrapper : null;

        private static void AssertPosition(Vector3 expected, Vector3 actual, string message)
            => Assert.Less(Vector3.Distance(expected, actual), PositionTolerance, $"{message} (expected {expected}, was {actual})");

        /// <summary>
        /// Builds a tracked SoundSource with its serialized fields already written.
        /// <para>
        /// The host starts deactivated so the fields land before the first OnEnable; the returned source is
        /// active, which means a playOnEnable source has *already played* by the time this returns.
        /// </para>
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
            TestAudioLibrary.SetPrivateField(source, SoundField, id);
            TestAudioLibrary.SetPrivateField(source, PositionModeField, positionMode);
            TestAudioLibrary.SetPrivateField(source, PlayOnEnableField, playOnEnable);
            TestAudioLibrary.SetPrivateField(source, OnlyPlayOnceField, onlyPlayOnce);
            TestAudioLibrary.SetPrivateField(source, StopOnDisableField, stopOnDisable);
            TestAudioLibrary.SetPrivateField(source, OverrideFadeOutField, overrideFadeOut);
            TestAudioLibrary.SetPrivateField(source, DelayField, delay);
            TestAudioLibrary.SetPrivateField(source, OverrideGroupField, overrideGroup);

            host.SetActive(true);
            return source;
        }

        /// <summary>A group that rejects any play beyond the first, with every other rule disabled.</summary>
        private DefaultPlaybackGroup NewSingleVoiceGroup()
        {
            DefaultPlaybackGroup group = Track(ScriptableObject.CreateInstance<DefaultPlaybackGroup>());
            TestAudioLibrary.SetPrivateField(group, "_maxPlayableCount", (MaxPlayableCountRule)1);
            TestAudioLibrary.SetPrivateField(group, "_combFilteringTime", (CombFilteringRule)0f);
            TestAudioLibrary.SetPrivateField(group, "_ignoreCombFilteringIfSameFrame", false);
            TestAudioLibrary.SetPrivateField(group, "_ignoreIfDistanceIsGreaterThan", 0f);
            TestAudioLibrary.SetPrivateField(group, "_logCombFilteringWarning", false);
            return group;
        }

        #region Position modes
        // PositionMode.Global must reach BroAudio.Play(id) - the 2D overload - no matter where the host
        // sits. The position sentinel (negativeInfinity) is what makes SetSpatial skip its SetTo3D branch.
        [UnityTest]
        public IEnumerator Play_WithGlobalPositionMode_StaysTwoDimensionalWhereverTheHostSits()
        {
            SoundID id = NewSound("GlobalSourceSfx", BroAudioType.SFX, NewClip(2f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.Global, new Vector3(12f, 3f, -7f));

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", 2f);

            AudioPlayer player = PlayerBehind(source.CurrentPlayer);
            Assert.IsNotNull(player, "CurrentPlayer should wrap a real pooled AudioPlayer.");
            Assert.IsTrue(Utility.IsPlayedGlobally(player.PlayingPosition),
                "PositionMode.Global must route to the global Play overload, so the playback position stays the sentinel rather than the host's transform.");
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "A globally played sound must stay 2D even though its SoundSource sits away from the origin.");
        }

        // PositionMode.StayHere snapshots transform.position at the moment of the Play call: the voice is
        // 3D, placed where the host was, and stays there when the host moves on.
        [UnityTest]
        public IEnumerator Play_WithStayHerePositionMode_PlacesTheVoiceAndLeavesItBehindWhenTheHostMoves()
        {
            Vector3 origin = new Vector3(4f, 1f, -2f);
            SoundID id = NewSound("StayHereSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.StayHere, origin);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", 2f);

            AudioPlayer player = PlayerBehind(source.CurrentPlayer);
            AssertPosition(origin, player.PlayingPosition, "StayHere must play at the host's position");
            AssertPosition(origin, player.transform.position, "The pooled player should have been moved to the play position");
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "Playing with a specified position forces the voice to 3D.");

            source.transform.position = new Vector3(-20f, 8f, 15f);
            yield return WaitFrames(3);

            AssertPosition(origin, player.PlayingPosition, "StayHere is a snapshot: moving the host must not move the voice");
            AssertPosition(origin, player.transform.position, "StayHere is a snapshot: moving the host must not move the player");
        }

        // PositionMode.FollowGameObject passes the Transform itself, so the voice keeps tracking it. This is
        // also the suite's only coverage of BroAudio.Play(SoundID, Transform).
        [UnityTest]
        public IEnumerator Play_WithFollowGameObjectPositionMode_KeepsTheVoiceOnTheMovingHost()
        {
            Vector3 start = new Vector3(2f, 0f, 5f);
            Vector3 moved = new Vector3(-9f, 4f, 1f);
            SoundID id = NewSound("FollowSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, SoundSource.PositionMode.FollowGameObject, start);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", 2f);

            AudioPlayer player = PlayerBehind(source.CurrentPlayer);
            AssertPosition(start, player.transform.position, "A follow-target play should start on the target");
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.CurrentPlayer.AudioSource.spatialBlend, PositionTolerance,
                "Playing with a follow target forces the voice to 3D.");

            source.transform.position = moved;

            // AudioPlayer.Update writes transform.position from the target; script order between it and the
            // test coroutine is undefined, so poll rather than assuming the very next frame.
            yield return WaitUntilOrTimeout(() => Vector3.Distance(player.transform.position, moved) < PositionTolerance,
                "the player to catch up with its follow target", 1f);
            AssertPosition(moved, player.PlayingPosition,
                "PlaybackPreference resolves Position from the live follow target, so it moves with the host too");
        }
        #endregion

        #region Enable / disable hooks
        // Play On Enable without Only Play Once is a per-enable trigger: the sound restarts every time the
        // GameObject is switched back on.
        [UnityTest]
        public IEnumerator OnEnable_WithPlayOnEnable_PlaysAgainOnEveryReEnable()
        {
            SoundID id = NewSound("PlayOnEnableSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", 2f);

            source.gameObject.SetActive(false);
            yield return WaitFrames(2);
            // With no Override Fade Out and no clip FadeOut the stop resolves to an immediate one, so the
            // voice is gone within a frame rather than ramping - the contrast case for the fade test below.
            Assert.IsFalse(id.HasAnyPlayingInstances(), "Stop On Disable with the default fade must cut the voice immediately.");

            source.gameObject.SetActive(true);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "a second OnEnable to start playback again", 2f);
        }

        // Only Play Once clears _playOnEnable from inside the first OnEnable, so re-enabling is silent for
        // the rest of that component's life.
        [UnityTest]
        public IEnumerator OnEnable_WithOnlyPlayOnce_NeverPlaysASecondTime()
        {
            SoundID id = NewSound("OnlyOnceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, onlyPlayOnce: true, stopOnDisable: true);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the first OnEnable to start playback", 2f);

            source.gameObject.SetActive(false);
            yield return WaitFrames(2);

            source.gameObject.SetActive(true);
            yield return WaitFrames(3);

            Assert.IsFalse(source.IsPlaying, "Only Play Once must suppress the second OnEnable's play.");
            Assert.IsFalse(id.HasAnyPlayingInstances(), "No voice at all should exist for the sound after a suppressed re-enable.");
        }

        // Without Stop On Disable the voice outlives its component: playback belongs to SoundManager's pool,
        // not to the host GameObject.
        [UnityTest]
        public IEnumerator OnDisable_WithoutStopOnDisable_LeavesTheVoicePlaying()
        {
            SoundID id = NewSound("KeepPlayingSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: false);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", 2f);
            IAudioPlayer player = source.CurrentPlayer;

            source.gameObject.SetActive(false);
            yield return WaitFrames(3);

            Assert.IsTrue(player.IsPlaying,
                "Disabling the host must not stop the sound unless Stop On Disable is set - the player lives on SoundManager.");
        }

        // Override Fade Out feeds the disable-time Stop, so the voice ramps down over that duration instead
        // of being cut. (The default of -1 is FadeData.UseClipSetting, which is what the other tests get.)
        [UnityTest]
        public IEnumerator OnDisable_WithOverrideFadeOut_RampsTheVoiceDownInsteadOfCuttingIt()
        {
            const float fadeOut = 0.5f;
            SoundID id = NewSound("DisableFadeSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true, overrideFadeOut: fadeOut);

            yield return WaitUntilOrTimeout(() => source.IsPlaying, "OnEnable to start playback", 2f);
            yield return WaitFrames(2);

            IAudioPlayer player = source.CurrentPlayer;
            Assert.GreaterOrEqual(player.GetVolume(), NearTargetVolume, "The clip has no FadeIn, so it should be at full volume before the disable.");

            source.gameObject.SetActive(false);

            // The default-fade case above is already gone two frames after the disable; a 0.5s override has
            // to still be alive and audible here. That is the whole contrast.
            yield return WaitFrames(2);
            Assert.IsTrue(player.IsActive, "A 0.5s override fade-out must keep the player alive while it ramps, not cut it instantly.");
            Assert.IsTrue(player.IsPlaying, "The voice stays audible for the length of the fade.");

            // The ramp itself runs on the frame clock and starts immediately (a Stop fade has no DSP wait
            // gate), so poll for the drop rather than assuming an ease shape.
            yield return WaitUntilOrTimeout(() => player.GetVolume() < 0.5f, "the override fade-out to ramp the volume down", fadeOut + 0.5f);
            yield return WaitUntilOrTimeout(() => !player.IsActive, "the override fade-out to finish and recycle the player", fadeOut + 1f);
        }

        // characterizes: Stop On Disable is skipped entirely when the object is disabled in the same frame it
        // was enabled. OnDisable's guard is CurrentPlayer.IsPlaying, but Play has only *enqueued* by then -
        // SoundManager.LateUpdate has not run, so AudioSource.isPlaying is still false and the queued voice
        // is never stopped. It starts on the next LateUpdate and plays out in full, detached from any
        // SoundSource that could stop it. Reachable from object pooling (spawn then immediately despawn).
        // See Docs/TEST_FINDINGS.md #35.
        [UnityTest]
        public IEnumerator OnDisable_InTheSameFrameAsOnEnable_LeavesTheQueuedVoicePlaying()
        {
            SoundID id = NewSound("SameFrameDisableSfx", BroAudioType.SFX, NewClip(2f));

            // NewSource activates the host, which runs OnEnable -> Play(); this deactivation lands in the
            // same frame, before the queue is drained.
            SoundSource source = NewSource(id, playOnEnable: true, stopOnDisable: true);
            Assert.IsFalse(source.IsPlaying, "Precondition: the play is still queued, not yet audible, when OnDisable runs.");
            source.gameObject.SetActive(false);

            yield return WaitUntilOrTimeout(() => id.HasAnyPlayingInstances(),
                "the queued voice to start despite Stop On Disable having already run", 2f);
        }
        #endregion

        #region Play / Stop / Pause verbs
        // Every Play overload calls Stop() first, so a SoundSource is a single-voice front-end: re-triggering
        // it replaces the sound rather than layering a second copy.
        [UnityTest]
        public IEnumerator Play_WhileAlreadyPlaying_ReplacesThePreviousVoiceRatherThanLayeringIt()
        {
            SoundID id = NewSound("ReplaceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the first playback to start", 2f);

            bool firstEnded = false;
            IAudioPlayer firstHandle = source.CurrentPlayer;
            firstHandle.OnEnd(_ => firstEnded = true);

            source.Play();
            Assert.AreNotSame(firstHandle, source.CurrentPlayer, "Each Play must hand the component a fresh player handle.");

            yield return WaitUntilOrTimeout(() => firstEnded, "the replaced voice to be stopped by the new Play", 2f);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the replacement voice to start playing", 2f);
        }

        // The Stop/Pause/UnPause verbs are pure delegation behind an IsActive guard, and the component's own
        // IsPlaying/IsActive mirror the player through the pause window and past recycling.
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
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "playback to start", 2f);

            source.Pause(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !source.IsPlaying, "Pause to freeze the voice", 2f);
            Assert.IsTrue(source.IsActive, "A paused SoundSource reports not playing, but still active.");

            source.UnPause(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "UnPause to resume the voice", 2f);

            source.Stop(FadeData.Immediate);
            yield return WaitUntilOrTimeout(() => !source.IsActive, "Stop to end playback and recycle the player", 2f);
            Assert.IsFalse(source.IsPlaying, "A stopped SoundSource is neither active nor playing, and reading it after recycle must not throw.");
        }

        // SetVolume/SetPitch reach the live voice, and their IsPlaying guard silently drops writes aimed at a
        // voice that has already finished - the SoundSource keeps no pending value to apply on the next play.
        [UnityTest]
        public IEnumerator SetVolumeAndSetPitch_ApplyToTheLiveVoiceOnly()
        {
            SoundID id = NewSound("SourceModulationSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "playback to start", 2f);
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
            yield return WaitUntilOrTimeout(() => !source.IsActive, "the voice to stop and recycle", 2f);

            source.SetVolume(0.25f);
            source.SetPitch(2f);

            source.Play();
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the second playback to start", 2f);
            yield return WaitFrames(1);

            Assert.AreEqual(1f, source.CurrentPlayer.GetVolume(), LinearTolerance,
                "Writes made while nothing was playing are dropped by the IsPlaying guard - a new play starts from the entity's own settings.");
            Assert.AreEqual(1f, source.CurrentPlayer.AudioSource.pitch, LinearTolerance,
                "Same for pitch: the guard drops the write rather than queueing it for the next play.");
        }
        #endregion

        #region Delay, group override, unassigned ID
        // Delay postpones the on-enable play by scheduling its start on the DSP clock. The player is active
        // (and AudioSource.isPlaying already reads true, since PlayScheduled holds the voice from the call),
        // so the playhead is what proves the sound is not audible yet.
        [UnityTest]
        public IEnumerator OnEnable_WithDelay_HoldsThePlayheadUntilTheDelayElapses()
        {
            const float delay = 0.5f;
            SoundID id = NewSound("DelayedSourceSfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: true, delay: delay);

            yield return WaitFrames(2);
            Assert.IsTrue(source.IsActive, "A delayed play is active from the moment OnEnable schedules it.");
            Assert.AreEqual(0, source.CurrentPlayer.AudioSource.timeSamples, "The playhead must not have moved - still inside the Delay.");

            // Well short of the full delay even counting the two frames above, so this stays a real
            // assertion rather than a race with the scheduled start.
            yield return WaitDspSeconds(0.15);
            Assert.AreEqual(0, source.CurrentPlayer.AudioSource.timeSamples, "Partway through the Delay, playback must still not have started audibly.");

            yield return WaitUntilOrTimeout(() => source.CurrentPlayer.AudioSource.timeSamples > 0,
                "audible playback to start once the Delay elapses", delay + 1f);
        }

        // Delay is an on-enable-only feature: OnEnable applies it, Play() does not. That matches the shipped
        // instruction text ("Delays playback triggered on enable") and the inspector, which nests and greys
        // the Delay field under Play On Enable - so this pins documented behavior, not a defect.
        [UnityTest]
        public IEnumerator Play_CalledDirectly_IgnoresTheInspectorDelay()
        {
            const float delay = 1f;
            SoundID id = NewSound("DirectPlayDelaySfx", BroAudioType.SFX, NewClip(3f));
            SoundSource source = NewSource(id, playOnEnable: false, delay: delay);

            float startedAt = Time.realtimeSinceStartup;
            source.Play();

            // A generous timeout so a stalled frame fails loudly rather than flakily; the discriminating
            // assertion is the elapsed time below - had Play() applied the Delay, this would take ~1s.
            yield return WaitUntilOrTimeout(() => source.CurrentPlayer.AudioSource.timeSamples > 0,
                "a direct Play to start audibly", 3f);
            Assert.Less(Time.realtimeSinceStartup - startedAt, delay,
                "Play() must start on the next queue drain, not wait out the inspector's Delay.");
        }

        // Override Playback Group is handed to BroAudio.Play as the IPlayableValidator, which takes priority
        // over whatever group the entity itself belongs to - so the group gates the component's plays.
        [UnityTest]
        public IEnumerator Play_WithOverrideGroup_LetsTheGroupRejectTheSecondSource()
        {
            DefaultPlaybackGroup group = NewSingleVoiceGroup();
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

            yield return WaitUntilOrTimeout(() => first.IsPlaying, "the accepted voice to start", 2f);
            Assert.IsFalse(second.IsPlaying, "The rejected SoundSource must never become audible.");
        }

        // A SoundSource whose SoundID was never assigned is the most common authoring mistake. It must log
        // once and stay inert - notably OnEnable calls CurrentPlayer.SetDelay unguarded, so the empty player
        // returned by a failed Play is what keeps that line from throwing.
        [UnityTest]
        public IEnumerator OnEnable_WithUnassignedSoundID_LogsOnceAndStaysInert()
        {
            // Expect before constructing: NewSource activates the host, so the error is logged inside it.
            LogAssert.Expect(LogType.Error, new Regex("SoundID hasn't been assigned"));

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