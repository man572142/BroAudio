#if PACKAGE_ADDRESSABLES
using System;
using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Addressables load-on-play, preloading, release, and the unused-entity cleanup routine. Entities are
    /// built in code against <see cref="TestAudioLibrary.AddressableClipGuids"/>.
    /// </summary>
    [Category("Slow")]
    public class AddressablesTests : BroAudioTestFixture
    {
        private readonly List<AudioEntity> _addressableEntities = new List<AudioEntity>();
        private readonly List<SoundID> _backDatedIds = new List<SoundID>();

        /// <summary>Creates a tracked addressable entity whose handles are released after the test.</summary>
        private AudioEntity NewAddressableEntity(string name, params string[] guids)
        {
            AudioEntity entity = TestAudioLibrary.CreateAddressableEntity(name, BroAudioType.SFX, guids);
            Track(entity);
            _addressableEntities.Add(entity);
            return entity;
        }

        [UnityTearDown]
        public IEnumerator ReleaseAddressableHandles()
        {
            // Addressables handles outlive the ScriptableObject; the fixture's Destroy pass doesn't release them.
            foreach (AudioEntity entity in _addressableEntities)
            {
                if (entity)
                {
                    entity.ReleaseAllAssets();
                }
            }
            _addressableEntities.Clear();

            // A back-dated key left behind keeps the routine ticking on a destroyed entity, whose error log fails
            // an unrelated later test. Must run before the base teardown destroys the entities (NUnit runs the
            // derived one first): a destroyed entity hashes to 0 and the key can no longer be found.
            if (SoundManager.HasInstance)
            {
                Dictionary<SoundID, double> tracked = LastPlayedTimes();
                foreach (SoundID id in _backDatedIds)
                {
                    tracked.Remove(id);
                }
            }
            _backDatedIds.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator Play_WithAutomaticLoadingEnabled_LoadsTheAddressableClipAndPlaysIt()
        {
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            AudioEntity entity = NewAddressableEntity("AddrPlay", TestAudioLibrary.AddressableClipGuids[0]);
            Assert.IsTrue(entity.Clips[0].IsAddressablesAvailable(), "The clip should resolve through Addressables.");
            Assert.IsFalse(entity.Clips[0].IsLoaded, "Nothing should be loaded before the first play.");

            SoundID id = IdOf(entity);
            IAudioPlayer player = BroAudio.Play(id);

            yield return WaitUntilOrTimeout(() => player.IsPlaying,
                "the addressable clip to load and playback to start", SlowAddressableWaitSeconds);
            Assert.IsTrue(entity.Clips[0].IsLoaded, "Playing loads the asset.");
            Assert.IsNotNull(player.AudioSource.clip);
        }

        [UnityTest]
        public IEnumerator LoadAssetAsync_Preloaded_ReportsLoadedBeforePlaybackStarts()
        {
            yield return RequireRealtimeAudioClock();

            AudioEntity entity = NewAddressableEntity("AddrPreload", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", SlowAddressableWaitSeconds);

            Assert.AreEqual(AsyncOperationStatus.Succeeded, handle.Status);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id), "The entity reports loaded after preloading.");

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start from the preloaded clip", SlowAddressableWaitSeconds);
            Assert.AreSame(handle.Result, player.AudioSource.clip, "Playback uses the preloaded asset.");
        }

        [UnityTest]
        public IEnumerator LoadAllAssetsAsync_OnMultiClipEntity_LoadsEveryClip()
        {
            AudioEntity entity = NewAddressableEntity("AddrPreloadAll",
                TestAudioLibrary.AddressableClipGuids[0], TestAudioLibrary.AddressableClipGuids[1]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<IList<AudioClip>> handle = BroAudio.LoadAllAssetsAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the group preload handle to complete", SlowAddressableWaitSeconds);

            Assert.AreEqual(AsyncOperationStatus.Succeeded, handle.Status);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id, 0));
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id, 1));
        }

        [UnityTest]
        public IEnumerator Play_WhileTheClipIsStillLoading_WaitsForTheLoadRatherThanFailing()
        {
            // characterizes: a play issued mid-load is deferred, not dropped or loaded synchronously.
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            AudioEntity entity = NewAddressableEntity("AddrMidLoad", TestAudioLibrary.AddressableClipGuids[1]);
            SoundID id = IdOf(entity);

            entity.Clips[0].LoadAssetAsync();

            // "Use Asset Database" mode can load synchronously, leaving no mid-load window. Assume, not Assert:
            // inconclusive says the run couldn't stage the scenario; green would be a lie.
            Assume.That(entity.Clips[0].IsLoaded, Is.False,
                "The addressable load completed synchronously, so there is no mid-load window to characterize.");

            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive, "The play is accepted even though the clip is not loaded yet.");

            // Don't assert !IsPlaying this frame: that proves the play queue, not the load, and any later
            // "still loading" check races the load. The Assume above is the proof of the deferring branch.
            yield return WaitUntilOrTimeout(() => player.IsPlaying,
                "the deferred play to start once loading finishes", SlowAddressableWaitSeconds);
            Assert.IsNotNull(player.AudioSource.clip);
        }

        [UnityTest]
        public IEnumerator ReleaseAllAssets_AfterPreloading_MarksTheEntityUnloaded()
        {
            AudioEntity entity = NewAddressableEntity("AddrRelease", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", SlowAddressableWaitSeconds);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id));

            BroAudio.ReleaseAllAssets(id);
            yield return null;

            Assert.IsFalse(SoundManager.Instance.IsLoaded(id), "Releasing clears the loaded state.");
        }

        [UnityTest]
        [Category("Finding_14")]
        public IEnumerator CleanupRoutine_WithTheUnloadDelaySetToFiveSeconds_StillMeasuresStalenessAgainstSixtySeconds()
        {
            // Pins TEST_FINDINGS #14, both halves in one pass of the routine. Both entities are back-dated in
            // the same frame and a tick walks every key without yielding, so the tick that released the stale
            // one also kept the fresh one - a positive result, and the stale wait times out if the routine never
            // runs. Wiring the setting to the threshold releases the fresh one too (30s > 5s) and fails this.
            // The tick interval is clamped to at most 5s, which bounds the stale wait.
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            SoundManager.Instance.Setting.AutomaticallyUnloadUnusedAddressableAudioClipsAfter = 5f;

            // Two different addressable assets, so releasing one cannot be confused with a shared refcount.
            AudioEntity staleEntity = NewAddressableEntity("AddrCleanupStale", TestAudioLibrary.AddressableClipGuids[0]);
            AudioEntity freshEntity = NewAddressableEntity("AddrCleanupFresh", TestAudioLibrary.AddressableClipGuids[1]);
            SoundID stale = IdOf(staleEntity);
            SoundID fresh = IdOf(freshEntity);

            AsyncOperationHandle<AudioClip> staleHandle = BroAudio.LoadAssetAsync(stale);
            AsyncOperationHandle<AudioClip> freshHandle = BroAudio.LoadAssetAsync(fresh);
            yield return WaitUntilOrTimeout(() => staleHandle.IsDone && freshHandle.IsDone,
                "both preload handles to complete", SlowAddressableWaitSeconds);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(stale));
            Assert.IsTrue(SoundManager.Instance.IsLoaded(fresh));

            // Automatic loading on is every precondition registration checks, yet neither entity is tracked.
            Dictionary<SoundID, double> tracked = LastPlayedTimes();
            Assert.IsFalse(tracked.ContainsKey(stale),
                "Characterizes TEST_FINDINGS #14: a public preload never registers the entity with the cleanup " +
                "routine, so in a real player the routine has nothing to iterate and never unloads anything.");
            Assert.IsFalse(tracked.ContainsKey(fresh));

            BackDateLastPlayedTime(stale, 61d);
            BackDateLastPlayedTime(fresh, 30d);

            yield return WaitUntilOrTimeout(() => !SoundManager.Instance.IsLoaded(stale),
                "the cleanup routine to release the entity idled past its hardcoded 60s threshold", 15f);

            Assert.IsTrue(SoundManager.Instance.IsLoaded(fresh),
                "Characterizes TEST_FINDINGS #14: 30s of idling is far past the 5s unload delay this test asked " +
                "for, but the routine compares against its own hardcoded 60s. The very tick that released the " +
                "61s entity walked this one in the same loop and kept its assets.");
        }

        [UnityTest]
        public IEnumerator ReleaseAsset_AfterLoading_MarksTheEntityUnloaded()
        {
            AudioEntity entity = NewAddressableEntity("AddrReleaseAsset", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", SlowAddressableWaitSeconds);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id));

            BroAudio.ReleaseAsset(id);
            yield return null;

            Assert.IsFalse(SoundManager.Instance.IsLoaded(id), "ReleaseAsset clears the loaded state.");
        }

        [UnityTest]
        public IEnumerator ReleaseAsset_OnAnEntityThatWasNeverLoaded_IsASilentNoOp()
        {
            AudioEntity entity = NewAddressableEntity("AddrReleaseUnloaded", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);
            Assert.IsFalse(SoundManager.Instance.IsLoaded(id), "Nothing should be loaded before ReleaseAsset is called.");

            Assert.DoesNotThrow(() => BroAudio.ReleaseAsset(id), "Releasing an unloaded asset must be a no-op, not throw.");
            yield return null;

            Assert.IsFalse(SoundManager.Instance.IsLoaded(id));
        }

        #region Factory default and a failing key
        /// <summary>
        /// A well-formed GUID no catalog has. Pin only BroAudio's reaction, never how Addressables reports it.
        /// </summary>
        private const string UnresolvableClipGuid = "0000000000000000000000000000dead";

        /// <summary>Frames kept inside the log-collection window after its condition holds, for logs raised on completion.</summary>
        private const int LogSettleFrames = 3;

        // The factory-default error is a warning to the developer, not a refusal.
        [UnityTest]
        public IEnumerator Play_WithTheFactoryDefaultAutomaticLoadingOff_LogsAnErrorThenLoadsAndPlaysAnyway()
        {
            Assert.IsFalse(RuntimeSetting.FactorySettings.AutomaticallyLoadAddressableAudioClips,
                "Precondition: automatic loading ships turned off.");
            Assert.AreEqual(LogType.Error, RuntimeSetting.FactorySettings.AddressablesNonPreloadedLogLevel,
                "Precondition: the non-preloaded message ships at Error level.");
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = RuntimeSetting.FactorySettings.AutomaticallyLoadAddressableAudioClips;

            AudioEntity entity = NewAddressableEntity("AddrFactoryDefault", TestAudioLibrary.AddressableClipGuids[0]);
            Assert.IsFalse(entity.Clips[0].IsLoaded, "Precondition: nothing is preloaded.");

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer player = BroAudio.Play(IdOf(entity));

            yield return WaitUntilOrTimeout(() => player.IsPlaying,
                "the non-preloaded clip to be loaded on demand and played", SlowAddressableWaitSeconds);
            Assert.IsTrue(entity.Clips[0].IsLoaded, "characterizes: the play loads the clip itself despite automatic loading being off.");
            Assert.IsNotNull(player.AudioSource.clip);
        }

        [UnityTest]
        public IEnumerator Play_WithTheNonPreloadedLogLevelAtWarning_WarnsInsteadAndStillLoadsAndPlays()
        {
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = false;
            SoundManager.Instance.Setting.AddressablesNonPreloadedLogLevel = LogType.Warning;

            AudioEntity entity = NewAddressableEntity("AddrWarnLevel", TestAudioLibrary.AddressableClipGuids[1]);

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer player = BroAudio.Play(IdOf(entity));

            yield return WaitUntilOrTimeout(() => player.IsPlaying,
                "the non-preloaded clip to be loaded on demand and played", SlowAddressableWaitSeconds);
            Assert.IsTrue(entity.Clips[0].IsLoaded);
        }

        [UnityTest]
        public IEnumerator LoadAssetAsync_WithAKeyNoCatalogResolves_CompletesFailedAndBroAudioLogsNothingOfItsOwn()
        {
            AudioEntity entity = NewAddressableEntity("AddrBadKeyPreload", UnresolvableClipGuid);
            Assert.IsTrue(entity.Clips[0].IsAddressablesAvailable(), "Precondition: a non-empty GUID makes the clip resolve through Addressables.");
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = default;
            var logs = new List<(LogType Type, string Message)>();
            yield return CollectLogsUntil(() => handle = BroAudio.LoadAssetAsync(id),
                () => handle.IsValid() && handle.IsDone, "the preload of the unresolvable key to complete", logs);

            Assert.AreEqual(AsyncOperationStatus.Failed, handle.Status, "A key no catalog resolves fails the preload handle.");
            Assert.IsFalse(SoundManager.Instance.IsLoaded(id), "The entity keeps reading as not loaded.");
            Assert.IsFalse(logs.Exists(log => log.Message.Contains(Utility.LogTitle)),
                "characterizes: BroAudio logs nothing of its own for a failed preload.");
        }

        // Pins TEST_FINDINGS #66; a fix that ends the player on a failed load turns the IsActive and
        // Exception asserts red. The stranded player is reclaimed by the fixture's teardown Stop.
        [UnityTest]
        [Category("Finding_66")]
        public IEnumerator Play_WithAKeyThatCannotLoad_ThrowsOutOfPlayControlAndStrandsThePlayerActiveAndSilent()
        {
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = false;
            AudioEntity entity = NewAddressableEntity("AddrBadKeyPlay", UnresolvableClipGuid);
            BroAudioClip clip = entity.Clips[0];
            SoundID id = IdOf(entity);

            IAudioPlayer player = null;
            var logs = new List<(LogType Type, string Message)>();
            yield return CollectLogsUntil(() => player = BroAudio.Play(id),
                () => clip.GetCurrentOperationHandle().IsValid() && clip.GetCurrentOperationHandle().IsDone,
                "PlayControl's on-demand load of the unresolvable key to complete", logs);

            Assert.IsTrue(logs.Exists(log => log.Type == LogType.Error && log.Message.Contains(Utility.LogTitle)),
                "The non-preloaded error is logged first, as for any addressable clip nobody preloaded.");
            Assert.IsTrue(logs.Exists(log => log.Type == LogType.Exception),
                "characterizes: the failed load surfaces as an exception thrown out of the playback coroutine.");
            Assert.IsTrue(player.IsActive, "characterizes: the player is never ended - it stays checked out of the pool.");
            Assert.IsFalse(player.IsPlaying, "It never sounds.");
            Assert.IsFalse(clip.IsLoaded);
        }

        /// <summary>
        /// Runs <paramref name="action"/>, then waits for <paramref name="condition"/> plus
        /// <see cref="LogSettleFrames"/>, collecting every log instead of failing on it - Addressables' own logs
        /// are not pinned. LogAssert.ignoreFailingMessages is static, so it is restored in a finally before the
        /// timeout is asserted.
        /// </summary>
        private static IEnumerator CollectLogsUntil(Action action, Func<bool> condition, string what, List<(LogType Type, string Message)> logs)
        {
            void OnLog(string message, string stackTrace, LogType type)
            {
                logs.Add((type, message));
            }

            bool timedOut = false;
            Application.logMessageReceived += OnLog;
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                action();
                float deadline = Time.realtimeSinceStartup + SlowAddressableWaitSeconds;
                while (!condition())
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        timedOut = true;
                        break;
                    }
                    yield return null;
                }

                for (int i = 0; i < LogSettleFrames; i++)
                {
                    yield return null;
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
                Application.logMessageReceived -= OnLog;
            }

            Assert.IsFalse(timedOut, $"Timed out after {SlowAddressableWaitSeconds}s waiting for: {what}");
        }
        #endregion

        /// <summary>
        /// Rewinds the cleanup routine's last-played record so it reads as stale. The indexer write is also
        /// the only thing that creates the record (TEST_FINDINGS #14); <see cref="ReleaseAddressableHandles"/>
        /// drops it again.
        /// </summary>
        private void BackDateLastPlayedTime(SoundID id, double secondsAgo)
        {
            LastPlayedTimes()[id] = Time.unscaledTimeAsDouble - secondsAgo;
            _backDatedIds.Add(id);
        }

        /// <summary>The cleanup routine's private record of when each tracked entity last played.</summary>
        private static Dictionary<SoundID, double> LastPlayedTimes()
        {
            return TestAudioLibrary.GetPrivateField<Dictionary<SoundID, double>>(
                SoundManager.Instance, TestAudioLibrary.Reflected.SoundManager.LoadedEntityLastPlayedTime);
        }
    }
}
#endif