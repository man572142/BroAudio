#if PACKAGE_LOCALIZATION
using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The Localization-mode runtime paths that can be reached without a real AssetTable: a Localization entity
    /// whose LocalizedAudio names no table or entry. Loading, releasing and playing such an entity are all
    /// guarded before anything asks LocalizationSettings for a table, so none of them needs one.
    /// <para>
    /// Everything past those guards - a resolved locale clip, the preload cache, a locale switch - needs an
    /// AssetTable fixture this project does not have, and stays deferred until one exists. The subscription
    /// guards are in <see cref="LocalizedAudioChangedSubscriptionTests"/>.
    /// </para>
    /// </summary>
    public class LocalizationRuntimeGuardTests : BroAudioTestFixture
    {
        /// <summary>A Localization-mode entity that still carries an ordinary clip, so only the missing table can matter.</summary>
        private SoundID NewLocalizationSoundWithoutTable(string name)
        {
            AudioEntity entity = NewEntity(name, BroAudioType.SFX, NewClip(1f));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Localization);
            return IdOf(entity);
        }

        // SoundManager.LoadLocalizedAssetAsync checks HasValidLocalizationReferences first: it warns and returns
        // `default`, an invalid handle, and never creates a cache entry - so the entity reads as not loaded.
        // LoadAllAssetsAsync wraps the same call and passes the invalid handle through.
        [UnityTest]
        public IEnumerator LoadAssetAsync_ForALocalizationEntityWithoutATable_WarnsAndReturnsAnInvalidHandle()
        {
            SoundID id = NewLocalizationSoundWithoutTable("LocalizedLoadNoTableSfx");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            AsyncOperationHandle<AudioClip> handle = BroAudio.LoadAssetAsync(id);
            Assert.IsFalse(handle.IsValid(), "A Localization entity with no table yields an invalid handle, not a failed one.");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            AsyncOperationHandle<IList<AudioClip>> allHandle = BroAudio.LoadAllAssetsAsync(id);
            Assert.IsFalse(allHandle.IsValid(), "LoadAllAssetsAsync passes the same invalid handle through.");

            Assert.IsFalse(BroAudio.IsLoaded(id), "Nothing was cached, so the entity is not loaded.");
            Assert.IsFalse(BroAudio.IsLoaded(id, 0), "In Localization mode the clip index is ignored - still not loaded.");
            yield break;
        }

        // ReleaseLocalizationClipInternal returns early when there is no cache entry with a valid preload handle,
        // before it reaches LocalizationSettings - so releasing something never loaded is a silent no-op
        // (no log: an unexpected error would fail this test).
        [UnityTest]
        public IEnumerator ReleaseVerbs_ForALocalizationEntityThatWasNeverLoaded_AreSilentNoOps()
        {
            SoundID id = NewLocalizationSoundWithoutTable("LocalizedReleaseNeverLoadedSfx");

            Assert.DoesNotThrow(() => BroAudio.ReleaseAsset(id));
            Assert.DoesNotThrow(() => BroAudio.ReleaseAsset(id, 0));
            Assert.DoesNotThrow(() => BroAudio.ReleaseAllAssets(id));
            Assert.IsFalse(BroAudio.IsLoaded(id));
            yield break;
        }

        // Play is accepted (nothing inspects the clip until the queue drains), then LocalizationClipStrategy
        // rejects the missing table with an error and returns no clip, and PlayControl ends the player. The
        // entity's ordinary clip is never used as a fallback.
        [UnityTest]
        public IEnumerator Play_ForALocalizationEntityWithoutATable_LogsOneErrorAndRecyclesWithoutSounding()
        {
            SoundID id = NewLocalizationSoundWithoutTable("LocalizedPlayNoTableSfx");

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer player = BroAudio.Play(id);
            Assert.IsTrue(player.IsActive, "The play is accepted before the queue drains.");

            bool everPlayed = false;
            yield return WaitUntilOrTimeout(() =>
            {
                everPlayed |= player.IsPlaying;
                return !player.IsActive;
            }, "the Localization player with no table to be ended and recycled");
            Assert.IsFalse(everPlayed, "characterizes: the entity's ordinary clip is not used as a fallback.");
        }
    }
}
#endif