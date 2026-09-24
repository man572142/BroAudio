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
    /// Addressables load-on-play, preloading, release, and the unused-entity cleanup routine.
    /// <para>
    /// Two generated sine fixtures are marked addressable for this suite — <c>BroAudioTest/ToneA</c> and
    /// <c>BroAudioTest/ToneB</c> in the Default Local Group. Their GUIDs live in
    /// <see cref="TestAudioLibrary.AddressableClipGuids"/>. Entities are built in code against those GUIDs, so
    /// no authored <c>AudioEntity</c> asset is involved.
    /// </para>
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
            // Addressables handles outlive the ScriptableObject, so releasing has to be explicit —
            // the fixture's Destroy pass is not enough.
            foreach (AudioEntity entity in _addressableEntities)
            {
                if (entity)
                {
                    entity.ReleaseAllAssets();
                }
            }
            _addressableEntities.Clear();

            // The back-dated keys are the suite's own doing - production never adds one (see
            // CleanupRoutine_WithTheUnloadDelaySetToFiveSeconds_StillMeasuresStalenessAgainstSixtySeconds) - so
            // the suite has to take them back out. A key left behind outlives this fixture: the routine keeps
            // ticking for the rest of the run, and once the fixture has destroyed the entity the SoundID no longer
            // resolves, so TryGetEntity logs an error and fails whichever unrelated test is running at that moment.
            // This has to happen before BroAudioTestFixture's teardown destroys the entities - NUnit runs the
            // derived teardown first - because a destroyed entity hashes to 0 and the key can no longer be found.
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
            // characterizes: PlayControl yields on WaitForAddressablesToLoad before touching AudioSource.clip,
            // so a play issued mid-load is deferred, not dropped — it is accepted (IsActive) and starts by
            // itself once the load lands, rather than failing or falling back to a synchronous load.
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            AudioEntity entity = NewAddressableEntity("AddrMidLoad", TestAudioLibrary.AddressableClipGuids[1]);
            SoundID id = IdOf(entity);

            entity.Clips[0].LoadAssetAsync();

            // PlayControl only takes the deferring branch when `!broAudioClip.IsLoaded`, and Editor Addressables
            // in "Use Asset Database" mode can complete a load synchronously - in which case the mid-load window
            // this test is named for does not exist on this machine and the test would be re-proving
            // Play_WithAutomaticLoadingEnabled_LoadsTheAddressableClipAndPlaysIt instead. Assume, not Assert:
            // an inconclusive result says "this run could not stage the scenario", a green one would be a lie.
            Assume.That(entity.Clips[0].IsLoaded, Is.False,
                "The addressable load completed synchronously, so there is no mid-load window to characterize.");

            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive, "The play is accepted even though the clip is not loaded yet.");

            // Deliberately not asserted here: !player.IsPlaying in this frame. BroAudio.Play only enqueues and
            // SoundManager.LateUpdate drains the queue, so that holds for every play, addressable or not - it is
            // already pinned for the ordinary case by
            // PlaybackLifecycleTests.IsActiveAndIsPlaying_AroundQueueDrain_TrackDifferentWindows, and here it
            // would be evidence of the queue, not of the load. The load can also finish inside any single frame,
            // so there is no later frame in which "still loading" can be observed without a race. The Assume
            // above is the real proof that the deferring branch was the one taken.
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
            // Characterizes TEST_FINDINGS #14: both halves, pinned in one pass of the routine.
            //
            // (a) Nothing in production ever registers an entity with the cleanup routine.
            //     UpdateLoadedEntityLastPlayedTime is guarded by `if (_loadedEntityLastPlayedTime.ContainsKey(id))`,
            //     yet every call site - SoundManager.LoadAssetAsync, SoundManager.LoadAllAssetsAsync,
            //     StartLoadingAddressableClips and AudioPlayer.WaitForAddressablesToLoad - calls it to *register*
            //     an entity that is not in the dictionary yet. The only other writes are the refresh inside the
            //     routine, which walks keys that already exist, and the Remove after unloading. So in a real
            //     player the dictionary stays empty for the lifetime of the process and the auto-unload feature
            //     never fires at all. Asserted below right after a public preload with automatic loading on -
            //     the most favourable conditions the registration will ever get.
            //
            // (b) AutomaticallyUnloadUnusedAddressableAudioClipsAfter is not the unload delay its name promises:
            //     the routine feeds it to Mathf.Clamp(setting, 1f, 5f) as its *tick interval* only, and measures
            //     staleness against the hardcoded literal 60.0. So with the setting asking for a 5-second unload
            //     delay, the entity idle for 61s is released and the one idle for 30s is not.
            //
            // Both entities are back-dated in the same frame, and each tick snapshots every key into one list and
            // walks it without yielding. So the tick that released the stale one also visited the fresh one and
            // chose to keep it - the "still loaded" half is a positive result, not the absence of one. It is also
            // what makes this test fail if the routine never runs: the wait for the stale release times out.
            //
            // The setting written here is ignored twice over: _addressableCleanupInterval is built once, guarded
            // by `if (_addressableCleanupInterval == null)`, on the coroutine's first iteration back at
            // SoundManager start-up, so a mid-run write cannot move the tick rate either. The interval is
            // therefore still the clamped start-up value, and the clamp caps any value at 5s, which is what keeps
            // the wait below bounded.
            //
            // The day the setting is wired to the threshold, 30s > 5s, the fresh entity is released too and this
            // test fails. BroAudioTestFixture restores the whole RuntimeSetting from a JSON snapshot in teardown,
            // so the writes neither leak into the next test nor dirty the asset on disk.
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

            // (a). Both preloads ran UpdateLoadedEntityLastPlayedTime with automatic loading enabled, which is
            // every precondition that method checks - and neither entity is tracked afterwards.
            Dictionary<SoundID, double> tracked = LastPlayedTimes();
            Assert.IsFalse(tracked.ContainsKey(stale),
                "Characterizes TEST_FINDINGS #14: a public preload never registers the entity with the cleanup " +
                "routine, so in a real player the routine has nothing to iterate and never unloads anything.");
            Assert.IsFalse(tracked.ContainsKey(fresh));

            // Which is also why the back-dating below is what puts them in the dictionary in the first place:
            // the routine's staleness threshold is a hardcoded 60 seconds that no test can wait out, and the
            // indexer write is the only registration either entity will ever get.
            BackDateLastPlayedTime(stale, 61d);
            BackDateLastPlayedTime(fresh, 30d);

            // The tick interval is clamped to at most 5s, so three ticks is a generous bound.
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

            // ReleaseAsset is a teardown-safe release verb (routed through the null-safe Manager) - it must
            // not throw, and Unity Test Framework auto-fails on any unexpected error/exception log, so a
            // clean pass here also proves no error was logged.
            Assert.DoesNotThrow(() => BroAudio.ReleaseAsset(id), "Releasing an unloaded asset must be a no-op, not throw.");
            yield return null;

            Assert.IsFalse(SoundManager.Instance.IsLoaded(id));
        }

        #region Factory default and a failing key
        /// <summary>
        /// A well-formed AssetGUID that no asset in this project has, so the key is in no Addressables catalog
        /// and every load of it fails. Only what BroAudio does around that failure is pinned, never how
        /// Addressables itself reports it.
        /// </summary>
        private const string UnresolvableClipGuid = "0000000000000000000000000000dead";

        /// <summary>Frames kept inside the log-collection window after its condition holds, for logs raised on completion.</summary>
        private const int LogSettleFrames = 3;

        // The factory default is AutomaticallyLoadAddressableAudioClips = false with the non-preloaded log at
        // Error - so out of the box, playing an addressable entity nobody preloaded reports an error, and then
        // (AudioPlayer.WaitForAddressablesToLoad: "load it no matter what the user has set") loads it and plays
        // anyway. The error is a warning to the developer, not a refusal.
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

        // AddressablesNonPreloadedLogLevel is only the severity of that one message: at Warning the same play
        // warns instead of erroring, and loads and plays exactly the same way.
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

        // A preload of a key no catalog knows returns the AssetReference's own handle, and it completes Failed.
        // BroAudio adds no log of its own for the failure - the handle's status is the whole signal - and the
        // entity keeps reading as not loaded.
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

        // With automatic loading off (the factory default), PlayControl logs the non-preloaded error and loads the
        // key itself inside WaitForAddressablesToLoad. When that load fails, the follow-up _clip.GetAudioClip()
        // throws out of the coroutine rather than reporting and ending: in the Editor it reads
        // AudioClipAssetReference.editorAsset.name, and editorAsset is null for a GUID with no asset (in a player
        // build the same method's synchronous retry throws BroAudioException instead). The throw kills PlayControl
        // before EndPlaying, so the player is stranded: accepted, silent, and checked out of the pool until
        // something stops it - which the fixture's Stop(All, 0f) teardown does. WaitForAddressablesToLoad's own
        // "Failed to load" error never gets its turn: GetAudioClip throws rather than returning null.
        // Characterizes TEST_FINDINGS #66; a fix that ends the player on a failed load turns the IsActive and
        // Exception asserts red.
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
        /// <see cref="LogSettleFrames"/>, collecting every log in between instead of failing on it. For the
        /// failing-key tests, whose logs come partly from Addressables itself, in a count and form this suite does
        /// not pin - only BroAudio's side is asserted on afterwards. Scoped like AudioEffectTests'
        /// RunAndCollectBroAudioErrorLogs: LogAssert.ignoreFailingMessages is static, so it is restored in a
        /// finally, and the timeout is asserted only after that.
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
        /// Rewinds the cleanup routine's record of when this entity last played, so it reads as stale now.
        /// <para>
        /// The indexer write is also what *creates* that record: <c>UpdateLoadedEntityLastPlayedTime</c> is
        /// guarded by <c>ContainsKey</c> and nothing else ever adds to the dictionary, so in a real player
        /// it stays empty and the routine has nothing to iterate. The record is dropped again in teardown -
        /// see <see cref="ReleaseAddressableHandles"/>.
        /// </para>
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