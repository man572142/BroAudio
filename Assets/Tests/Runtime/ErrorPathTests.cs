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
    /// Public-API misuse: empty clip slots, a null follow target, UnPause on a non-paused player, and
    /// SetVolume/SetPitch with <see cref="BroAudioType.None"/> or Unity's "Everything" (-1) mask value.
    /// </summary>
    public class ErrorPathTests : BroAudioTestFixture
    {
        /// <summary>
        /// Non-null <see cref="BroAudioClip"/>s with no AudioClip behind them. Must be an array of nulls,
        /// not an empty array: CreateEntity fills an empty array with a generated clip.
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
        // Single mode: the strategy itself rejects the unset slot.
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

        // Random mode doesn't check IsSet, so PlayControl's own validation is what rejects the slot. Every
        // slot is empty so the outcome doesn't depend on the draw.
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
        [UnityTest]
        public IEnumerator Play_WithANullOrDestroyedFollowTarget_LogsAndReturnsAnInactivePlayer()
        {
            SoundID id = NewSound("NullFollowTargetSfx", BroAudioType.SFX, NewClip(1f));
            var destroyed = new GameObject("DestroyedFollowTarget").transform;
            UnityEngine.Object.DestroyImmediate(destroyed.gameObject);

            Func<IAudioPlayer>[] plays =
            {
                () => BroAudio.Play(id, (Transform)null),
                () => BroAudio.Play(id, (Transform)null, 0.5f),
                () => BroAudio.Play(SoundID.Invalid, (Transform)null),
                () => BroAudio.Play(id, destroyed),
            };

            foreach (Func<IAudioPlayer> play in plays)
            {
                LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
                IAudioPlayer rejected = play();
                Assert.IsFalse(rejected.IsActive, "A missing follow target must log and return the inert Empty player, not throw.");
            }

            yield return WaitFrames(2);
            Assert.IsFalse(BroAudio.HasAnyPlayingInstances(id), "None of the rejected calls may have started a voice.");
        }
        #endregion

        #region UnPause misuse
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

        // The clip outlasts the recycle budget, so a stop that UnPause cancelled would time the wait out.
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

        // Pins TEST_FINDINGS #65; a fix turns the "characterizes" asserts red.
        [UnityTest]
        [Category("Finding_65")]
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

        // Without ConvertEverythingFlag, -1 would miss the All branch and write every type instead.
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

        // SetPitch has no master branch, so the conversion isn't observable here; this
        // pins the outcome only.
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