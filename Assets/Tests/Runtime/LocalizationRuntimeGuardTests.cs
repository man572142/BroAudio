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
    /// Localization-mode guards reachable without an AssetTable: load, release and play of an entity whose
    /// LocalizedAudio names no table. Anything past the guards needs an AssetTable fixture.
    /// </summary>
    public class LocalizationRuntimeGuardTests : BroAudioTestFixture
    {
        /// <summary>Still carries an ordinary clip, so only the missing table can matter.</summary>
        private SoundID NewLocalizationSoundWithoutTable(string name)
        {
            AudioEntity entity = NewEntity(name, BroAudioType.SFX, NewClip(1f));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Localization);
            return IdOf(entity);
        }

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

        // "Silent" is enforced by the runner: an unexpected error log fails the test.
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