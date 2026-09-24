using System;
using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Misuse and error paths of the public API that nothing else in the suite drives: a clip slot with no
    /// AudioClip, a null follow target, UnPause on a player that is not paused (or is on its way out), and
    /// the per-type SetVolume/SetPitch overloads with the two flag values that are not a concrete type -
    /// <see cref="BroAudioType.None"/> and Unity's "Everything" (-1), which is what an inspector mask field
    /// hands over when every box is ticked.
    /// </summary>
    public class ErrorPathTests : BroAudioTestFixture
    {
        /// <summary>
        /// A clip array whose slots are all non-null <see cref="BroAudioClip"/>s with no AudioClip (and no
        /// addressable key) behind them - what a designer leaves when they add a row and never drag a clip in.
        /// TestAudioLibrary.CreateEntity only generates a clip for an empty *array*, so an array of nulls is
        /// passed straight through to CreateBroClip(null).
        /// </summary>
        private static AudioClip[] EmptySlots(int count) => new AudioClip[count];

        /// <summary>Polls until the player is recycled, recording whether it was ever audible on the way.</summary>
        private static IEnumerator WaitForRecycleRecordingPlayback(IAudioPlayer player, bool[] everPlayed, string what)
        {
            yield return WaitUntilOrTimeout(() =>
            {
                everPlayed[0] |= player.IsPlaying;
                return !player.IsActive;
            }, what, DefaultPlaybackWaitSeconds);
        }

        #region Clip slot with no AudioClip
        // Single mode: SingleClipStrategy rejects an unset slot itself (it checks IsSet), logs, and returns null;
        // PlayControl then sees `_clip == null` and ends the player. The Play call is still accepted - nothing
        // before the queue drains looks at the clips - so the caller gets a live handle that dies on the next
        // LateUpdate without ever reaching AudioSource.Play.
        [UnityTest]
        public IEnumerator Play_SingleModeEntityWhoseSlotHasNoAudioClip_IsAcceptedThenLogsOneErrorAndRecyclesSilently()
        {
            SoundID id = NewSound("EmptySlotSingleSfx", BroAudioType.SFX, EmptySlots(1));

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive,
                "characterizes: Play accepts an entity with no playable clip - the slot is only inspected when the queue drains.");

            bool[] everPlayed = { false };
            yield return WaitForRecycleRecordingPlayback(player, everPlayed, "the player with no clip to be ended and recycled");
            Assert.IsFalse(everPlayed[0], "A slot with no AudioClip must never reach AudioSource.Play.");
        }

        // Random mode takes the other branch to the same end: RandomClipStrategy does not check IsSet, so with
        // all weights zero it returns the empty slot as if it were playable, and it is PlayControl's own
        // `audioClip != null` validation that logs and ends the player. Every slot is empty so the outcome
        // does not depend on which one the draw lands on.
        [UnityTest]
        public IEnumerator Play_RandomModeEntityWhoseSlotsHaveNoAudioClip_SelectsAnEmptySlotThenLogsOneErrorAndRecycles()
        {
            AudioEntity entity = NewEntity("EmptySlotRandomSfx", BroAudioType.SFX, EmptySlots(2));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Random);
            SoundID id = IdOf(entity);

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive, "The play is accepted; the empty slots are only found when the queue drains.");

            bool[] everPlayed = { false };
            yield return WaitForRecycleRecordingPlayback(player, everPlayed, "the player with no clip to be ended and recycled");
            Assert.IsFalse(everPlayed[0], "An empty slot picked by Random must never reach AudioSource.Play.");
        }
        #endregion

        #region Null follow target
        // Characterizes TEST_FINDINGS #60: SoundManager.Play(SoundID, Transform, ...) passes
        // `followTarget.position` as an argument to IsPlayable, so a null Transform is dereferenced before the
        // SoundID, the entity or the playback group is looked at. The caller gets a raw NullReferenceException
        // out of the facade - not the logged error and inert Empty player that every other invalid-input path
        // produces (compare the SoundID.Invalid contrast at the end), and not a BroAudioException either.
        // Nothing is checked out of the pool first, so the throw leaks no player.
        // Distinct from TeardownTests.Play_OnBroAudioFacade_WithManagerDestroyed_ThrowsBroAudioException, which
        // passes the same null Transform with the manager gone: there SoundManager.Instance throws first.
        [UnityTest]
        [Category("Finding_60")]
        public IEnumerator Play_WithANullFollowTarget_ThrowsNullReferenceExceptionBeforeAnyValidation()
        {
            SoundID id = NewSound("NullFollowTargetSfx", BroAudioType.SFX, NewClip(1f));

            Assert.Throws<NullReferenceException>(() => BroAudio.Play(id, (Transform)null),
                "characterizes: Play(id, (Transform)null) dereferences the target before validating anything.");
            Assert.Throws<NullReferenceException>(() => BroAudio.Play(id, (Transform)null, 0.5f),
                "characterizes: the fade-in overload shares the same unguarded dereference.");

            // No LogAssert.Expect here: the unassigned SoundID would log an error if validation ran first, and
            // an unexpected error log fails the test - so this also proves the dereference comes before it.
            Assert.Throws<NullReferenceException>(() => BroAudio.Play(SoundID.Invalid, (Transform)null),
                "characterizes: even an unassigned SoundID reaches the null dereference before its own validation.");

            yield return WaitFrames(2);
            Assert.IsFalse(BroAudio.HasAnyPlayingInstances(id), "None of the throwing calls may have started a voice.");

            // The contrast: the same unassigned SoundID through an overload with no Transform is logged and rejected.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer rejected = BroAudio.Play(SoundID.Invalid);
            Assert.IsFalse(rejected.IsActive, "Every other overload logs and returns the inert Empty player instead of throwing.");
        }
        #endregion

        #region UnPause misuse
        // AudioPlayer's UnPause guard is `_stopMode != StopMode.Pause` -> warn and return, so on a player that is
        // simply playing it touches nothing: no restart (a re-Play would rewind the playhead to 0), no fade-in.
        // The facade reaches the same guard once per matching player.
        [UnityTest]
        public IEnumerator UnPause_OnAPlayerThatIsNotPaused_WarnsAndLeavesPlaybackUntouched()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("UnPauseNotPausedSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            yield return WaitDspSeconds(0.3);

            int playheadBefore = player.AudioSource.timeSamples;
            Assert.Greater(playheadBefore, 0, "Precondition: the playhead must be moving before UnPause is called.");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            player.UnPause(0f);
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            BroAudio.UnPause(id);

            Assert.IsTrue(player.IsPlaying, "UnPause on a playing player must not stop it.");
            Assert.GreaterOrEqual(player.AudioSource.timeSamples, playheadBefore, "UnPause on a playing player must not rewind it.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance, "UnPause on a playing player must not restart a fade-in.");

            yield return WaitDspSeconds(0.3);
            Assert.Greater(player.AudioSource.timeSamples, playheadBefore, "Playback carries on as if UnPause had never been called.");
        }

        // A faded Stop sets _stopMode to Stop for the whole fade, so UnPause hits the same not-paused guard: it
        // warns and cannot rescue the player, which still fades out and recycles on schedule. The clip is far
        // longer than the recycle budget, so a stop that UnPause had cancelled would time the wait out.
        [UnityTest]
        public IEnumerator UnPause_WhileAFadedStopIsInProgress_WarnsAndTheStopStillCompletes()
        {
            yield return RequireRealtimeAudioClock();

            const float StopFadeSeconds = 1f;
            SoundID id = NewSound("UnPauseMidStopSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.Stop(StopFadeSeconds);
            yield return null;
            Assert.IsTrue(InstanceOf(player).IsStopping, "Precondition: the faded Stop must still be in progress.");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            player.UnPause(0f);
            Assert.IsTrue(player.IsActive, "UnPause must not end the stopping player early either.");

            yield return WaitForRecycle(player, "the faded Stop to finish and recycle the player despite the UnPause",
                StopFadeSeconds + DefaultPlaybackWaitSeconds);
        }

        // Unlike a Stop, a Pause fade-out sets _stopMode to Pause the moment it starts, so UnPause passes its
        // guard and goes through PlayInternal - whose RestartCoroutine(PlayControl, ref _playbackControlCoroutine)
        // kills the StopControl coroutine running the pause. The resume itself works (SetupClipVolume snaps the
        // clip fader back to full), but StopControl's closing `IsStopping = false` never runs. With IsStopping
        // stuck true, Stop()'s own guard (`IsStopping && fade != Immediate` -> return) discards every later
        // Stop with a fade - including the clip-setting default of BroAudio.Stop(id) - so the sound plays on.
        // Only a zero-fade Stop still gets through, which is what the fixture's teardown uses.
        [UnityTest]
        public IEnumerator UnPause_DuringAPauseFadeOut_ResumesButLeavesIsStoppingSet_SoALaterFadedStopIsIgnored()
        {
            yield return RequireRealtimeAudioClock();

            const float PauseFadeSeconds = 2f;
            const float StopFadeSeconds = 0.5f;
            SoundID id = NewSound("UnPauseMidPauseFadeSfx", BroAudioType.SFX, NewClip(8f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.Pause(PauseFadeSeconds);
            yield return WaitFrames(2);
            AudioPlayer concrete = InstanceOf(player);
            Assert.IsTrue(concrete.IsStopping, "Precondition: the pause fade-out must still be in progress.");
            Assert.IsTrue(player.IsPlaying, "Precondition: a fading pause keeps the source playing until the fade ends.");

            player.UnPause(0f);
            yield return null;
            Assert.IsTrue(player.IsPlaying, "UnPause during the pause fade resumes playback.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance, "The resumed player is back at full volume.");
            Assert.IsTrue(concrete.IsStopping,
                "characterizes: the interrupted StopControl never cleared IsStopping, so the playing player still reports it.");

            player.Stop(StopFadeSeconds);
            // Frame clock, like the fade itself (Fader accumulates Utility.GetDeltaTime), with a second to spare.
            yield return new WaitForSeconds(StopFadeSeconds + 1f);

            Assert.IsTrue(player.IsActive && player.IsPlaying,
                "characterizes: Stop(fade) is discarded by the stuck IsStopping guard, so the sound keeps playing.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "characterizes: the discarded Stop never started its fade-out either.");
        }
        #endregion

        #region SetVolume / SetPitch with non-concrete flags
        // SoundManager.SetVolume/SetPitch both short-circuit on BroAudioType.None with a warning, before any
        // per-type pref or live player is touched - and before the master branch, so Master is untouched too.
        [UnityTest]
        public IEnumerator SetVolumeAndSetPitch_WithBroAudioTypeNone_WarnAndChangeNothing()
        {
            SoundID id = NewSound("NoneFlagSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            Assert.IsTrue(mixer.GetFloat(BroName.MasterTrackName, out float masterBefore));

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            BroAudio.SetVolume(BroAudioType.None, 0.3f, 0f);
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            BroAudio.SetPitch(BroAudioType.None, 1.7f, 0f);
            yield return WaitFrames(1);

            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance, "SetVolume(None) must not reach a live player.");
            Assert.AreEqual(AudioConstant.DefaultPitch, player.AudioSource.pitch, LinearTolerance, "SetPitch(None) must not reach a live player.");
            Assert.IsTrue(mixer.GetFloat(BroName.MasterTrackName, out float masterAfter));
            Assert.AreEqual(masterBefore, masterAfter, DecibelTolerance, "SetVolume(None) must not fall through to the master volume.");
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref));
                Assert.AreEqual(AudioConstant.FullVolume, pref.Volume, LinearTolerance, $"SetVolume(None) must not store a volume for {audioType}.");
                Assert.AreEqual(AudioConstant.DefaultPitch, pref.Pitch, LinearTolerance, $"SetPitch(None) must not store a pitch for {audioType}.");
            }
        }

        // ConvertEverythingFlag maps Unity's Everything (-1) onto BroAudioType.All before the All check, so
        // SetVolume with it is the master volume: it writes the Master parameter and nothing per type. Without
        // the conversion, -1 would miss the All branch and Contains() every type instead - a per-type write the
        // assertions below would catch.
        [UnityTest]
        public IEnumerator SetVolume_WithUnitysEverythingFlag_IsTreatedAsAllAndWritesOnlyTheMasterVolume()
        {
            const float MasterTarget = 0.5f;
            SoundID id = NewSound("EverythingVolumeSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            BroAudio.SetVolume((BroAudioType)Utility.UnityEverythingFlag, MasterTarget, 0f);
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float masterDb));
            Assert.AreEqual(MasterTarget.ToDecibel(), masterDb, DecibelTolerance, "Everything must reach the Master parameter, like All.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "Everything must not be applied as a per-type volume on live players.");
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref));
                Assert.AreEqual(AudioConstant.FullVolume, pref.Volume, LinearTolerance, $"Everything must not store a per-type volume for {audioType}.");
            }
        }

        // SetPitch has no master branch (TEST_FINDINGS #56), so Everything -> All writes every concrete type's
        // pref and every live player. Here the conversion is not observable on its own - (BroAudioType)(-1)
        // Contains() every type as well - so this pins the outcome a mask field's "Everything" produces.
        [UnityTest]
        public IEnumerator SetPitch_WithUnitysEverythingFlag_ReachesLivePlayersAndEveryConcreteTypePref()
        {
            const float TargetPitch = 1.5f;
            SoundID id = NewSound("EverythingPitchSfx", BroAudioType.SFX, NewClip(5f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            BroAudio.SetPitch((BroAudioType)Utility.UnityEverythingFlag, TargetPitch, 0f);
            yield return WaitFrames(1);

            Assert.AreEqual(TargetPitch, player.AudioSource.pitch, LinearTolerance, "Everything must reach a live player's pitch.");
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref));
                Assert.AreEqual(TargetPitch, pref.Pitch, LinearTolerance, $"Everything must store the pitch for {audioType}.");
            }
        }
        #endregion
    }
}