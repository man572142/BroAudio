#if PACKAGE_LOCALIZATION
using System.Collections;
using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The guards in front of <see cref="SoundManager.SubscribeLocalizedAudioChanged"/>: an entity that cannot
    /// resolve a localized clip must warn and register nothing. The subscribed path itself needs a real
    /// AssetTable behind <c>LocalizedAsset.AssetChanged</c>, which this project does not have.
    /// </summary>
    public class LocalizedAudioChangedSubscriptionTests : BroAudioTestFixture
    {
        private static readonly Regex BroAudioLogPrefix = new Regex(Regex.Escape(Utility.LogTitle));

        private static void OnChanged(SoundID _) { }

        private static bool HasSubscriptionEntry(SoundID id)
        {
            IDictionary runtime = TestAudioLibrary.GetPrivateField<IDictionary>(SoundManager.Instance, TestAudioLibrary.Reflected.SoundManager.LocalizedRuntime);
            return runtime != null && runtime.Contains(id);
        }

        [UnityTest]
        public IEnumerator Subscribe_ForAnEntityNotInLocalizationMode_WarnsAndRegistersNothing()
        {
            // Valid-looking table references, so only the Localization-mode check can stop the subscription
            // before it hooks AssetChanged and starts loading a table that does not exist.
            AudioEntity entity = NewEntity("NotLocalizedSfx", BroAudioType.SFX, NewClip(1f));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.LocalizedAudio,
                new LocalizedAudioClip { TableReference = "TestTable", TableEntryReference = "TestEntry" });
            SoundID id = IdOf(entity);

            LogAssert.Expect(LogType.Warning, BroAudioLogPrefix);
            BroAudio.SubscribeLocalizedAudioChanged(id, OnChanged);

            Assert.IsFalse(HasSubscriptionEntry(id), "A non-Localization entity must not get a subscription entry.");
            Assert.DoesNotThrow(() => BroAudio.UnsubscribeLocalizedAudioChanged(id, OnChanged),
                "Unsubscribing a handler that was never registered must be a silent no-op.");
            yield break;
        }

        [UnityTest]
        public IEnumerator Subscribe_ThroughSoundIdEvent_ForALocalizationEntityWithoutTableReferences_WarnsAndRegistersNothing()
        {
            AudioEntity entity = NewEntity("UnsetLocalizedSfx", BroAudioType.SFX, NewClip(1f));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Localization);
            SoundID id = IdOf(entity);

            LogAssert.Expect(LogType.Warning, BroAudioLogPrefix);
            id.LocalizedAudioChanged += OnChanged;

            Assert.IsFalse(HasSubscriptionEntry(id), "An entity with no table or entry set must not get a subscription entry.");
            Assert.DoesNotThrow(() => id.LocalizedAudioChanged -= OnChanged,
                "Unsubscribing a handler that was never registered must be a silent no-op.");
            yield break;
        }
    }
}
#endif