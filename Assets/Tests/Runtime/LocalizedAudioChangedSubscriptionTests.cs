#if PACKAGE_LOCALIZATION
using System;
using System.Collections;
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
    /// AssetTable behind <c>LocalizedAsset.AssetChanged</c>, which this project does not have. Also covers
    /// <see cref="BroAudio.PlayOnLocalizedAudioChanged"/>, the cached <c>Action&lt;SoundID&gt;</c> adapter meant
    /// to be hooked onto <see cref="SoundID.LocalizedAudioChanged"/> directly - it needs no AssetTable at all,
    /// since it is just <c>id => Play(id)</c>.
    /// </summary>
    public class LocalizedAudioChangedSubscriptionTests : BroAudioTestFixture
    {
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

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
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

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            id.LocalizedAudioChanged += OnChanged;

            Assert.IsFalse(HasSubscriptionEntry(id), "An entity with no table or entry set must not get a subscription entry.");
            Assert.DoesNotThrow(() => id.LocalizedAudioChanged -= OnChanged,
                "Unsubscribing a handler that was never registered must be a silent no-op.");
            yield break;
        }

        [UnityTest]
        public IEnumerator PlayOnLocalizedAudioChanged_IsCachedAndPlaysTheGivenSoundIdWhenInvoked()
        {
            // The property is `_playOnLocalizedAudioChanged ??= (id => Play(id))` - a lazily-built adapter
            // meant for `id.LocalizedAudioChanged += BroAudio.PlayOnLocalizedAudioChanged;`, whose caller
            // later has to unsubscribe with `-=` on that exact same delegate instance. Two reads must
            // therefore be reference-equal, not merely equivalent delegates.
            Action<SoundID> first = BroAudio.PlayOnLocalizedAudioChanged;
            Action<SoundID> second = BroAudio.PlayOnLocalizedAudioChanged;
            Assert.IsNotNull(first, "PlayOnLocalizedAudioChanged should never read as null.");
            Assert.AreSame(first, second, "Repeated reads must return the exact same cached delegate instance, so a caller can unsubscribe with it later.");

            SoundID id = NewSound("PlayOnLocalizedAudioChangedSfx", BroAudioType.SFX, NewClip(2f));
            Assert.IsFalse(BroAudio.HasAnyPlayingInstances(id), "Precondition: nothing of this freshly-built, never-played entity should already be playing.");

            first.Invoke(id);

            // BroAudio.Play only enqueues - SoundManager.LateUpdate starts the voice - so this has to poll
            // rather than assert in the same frame.
            yield return WaitUntilOrTimeout(() => BroAudio.HasAnyPlayingInstances(id),
                "PlayOnLocalizedAudioChanged to start playback of the SoundID it was invoked with");
            Assert.IsTrue(BroAudio.HasAnyPlayingInstances(id),
                "Invoking the cached delegate with a SoundID should actually play that sound - it is a plain forward to Play(id), not a no-op stub.");
        }
    }
}
#endif