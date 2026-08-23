#if PACKAGE_ADDRESSABLES
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory phase 5: Addressables load-on-play, preloading, release, and the unused-entity cleanup routine.
    /// <para>
    /// Two demo clips are marked addressable for this suite — <c>BroAudioTest/Footstep1</c> and
    /// <c>BroAudioTest/Footstep2</c> in the Default Local Group. Their GUIDs live in
    /// <see cref="TestAudioLibrary.AddressableClipGuids"/>. Entities are built in code against those GUIDs, so
    /// no authored <c>AudioEntity</c> asset is involved.
    /// </para>
    /// </summary>
    public class AddressablesTests : BroAudioTestFixture
    {
        private readonly List<AudioEntity> _addressableEntities = new List<AudioEntity>();

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
                "the addressable clip to load and playback to start", 10f);
            Assert.IsTrue(entity.Clips[0].IsLoaded, "Playing loads the asset.");
            Assert.IsNotNull(player.AudioSource.clip);
        }

        [UnityTest]
        public IEnumerator LoadAssetAsync_Preloaded_ReportsLoadedBeforePlaybackStarts()
        {
            AudioEntity entity = NewAddressableEntity("AddrPreload", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", 10f);

            Assert.AreEqual(AsyncOperationStatus.Succeeded, handle.Status);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id), "The entity reports loaded after preloading.");

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start from the preloaded clip", 5f);
            Assert.AreSame(handle.Result, player.AudioSource.clip, "Playback uses the preloaded asset.");
        }

        [UnityTest]
        public IEnumerator LoadAllAssetsAsync_OnMultiClipEntity_LoadsEveryClip()
        {
            AudioEntity entity = NewAddressableEntity("AddrPreloadAll",
                TestAudioLibrary.AddressableClipGuids[0], TestAudioLibrary.AddressableClipGuids[1]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<IList<AudioClip>> handle = BroAudio.LoadAllAssetsAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the group preload handle to complete", 10f);

            Assert.AreEqual(AsyncOperationStatus.Succeeded, handle.Status);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id, 0));
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id, 1));
        }

        [UnityTest]
        public IEnumerator Play_WhileTheClipIsStillLoading_WaitsForTheLoadRatherThanFailing()
        {
            // characterizes: PlayControl yields on WaitForAddressablesToLoad before touching AudioSource.clip,
            // so a play issued mid-load is deferred, not dropped. IsActive is true throughout that window
            // while IsPlaying stays false — the same pair of states as the queued-but-not-drained window.
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            AudioEntity entity = NewAddressableEntity("AddrMidLoad", TestAudioLibrary.AddressableClipGuids[1]);
            SoundID id = IdOf(entity);

            entity.Clips[0].LoadAssetAsync();
            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive, "The play is accepted even though the clip is not loaded yet.");

            yield return WaitUntilOrTimeout(() => player.IsPlaying,
                "the deferred play to start once loading finishes", 10f);
            Assert.IsNotNull(player.AudioSource.clip);
        }

        [UnityTest]
        public IEnumerator ReleaseAllAssets_AfterPreloading_MarksTheEntityUnloaded()
        {
            AudioEntity entity = NewAddressableEntity("AddrRelease", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", 10f);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id));

            BroAudio.ReleaseAllAssets(id);
            yield return null;

            Assert.IsFalse(SoundManager.Instance.IsLoaded(id), "Releasing clears the loaded state.");
        }

        [UnityTest]
        public IEnumerator CleanupRoutine_WhenAnEntityHasBeenIdleLongEnough_ReleasesItsAssets()
        {
            // The routine's staleness threshold is a hardcoded 60 seconds, so a test cannot wait it out.
            // Back-dating the tracked timestamp puts the entity past the threshold immediately; the routine's
            // own tick interval (clamped to at most 5s) is then the only thing left to wait for.
            SoundManager.Instance.Setting.AutomaticallyLoadAddressableAudioClips = true;
            AudioEntity entity = NewAddressableEntity("AddrCleanup", TestAudioLibrary.AddressableClipGuids[0]);
            SoundID id = IdOf(entity);

            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            yield return WaitUntilOrTimeout(() => handle.IsDone, "the preload handle to complete", 10f);
            Assert.IsTrue(SoundManager.Instance.IsLoaded(id));

            BackDateLastPlayedTime(id, 61d);

            yield return WaitUntilOrTimeout(() => !SoundManager.Instance.IsLoaded(id),
                "the cleanup routine to release the idle entity", 12f);
        }

        /// <summary>
        /// Rewinds the cleanup routine's record of when this entity last played, so it reads as stale now.
        /// </summary>
        private static void BackDateLastPlayedTime(SoundID id, double secondsAgo)
        {
            FieldInfo field = typeof(SoundManager).GetField("_loadedEntityLastPlayedTime",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tracked = (Dictionary<SoundID, double>)field.GetValue(SoundManager.Instance);
            tracked[id] = Time.unscaledTimeAsDouble - secondsAgo;
        }
    }
}
#endif
