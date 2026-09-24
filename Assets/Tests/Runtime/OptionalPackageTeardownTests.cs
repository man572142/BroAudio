#if PACKAGE_ADDRESSABLES || PACKAGE_LOCALIZATION
using System;
using System.Collections;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The optional-package half of <see cref="TeardownTests"/>' sweep: the Addressables/Localization verbs on the
    /// <see cref="BroAudio"/> facade once SoundManager is gone, which that sweep does not list because they only
    /// compile with the packages installed. Same split as the core verbs - the release verbs go through the
    /// null-safe <c>BroAudio.Manager</c> and no-op, while the load/query verbs read <c>SoundManager.Instance</c>
    /// like Play and throw <see cref="BroAudioException"/>.
    /// <para>
    /// Destroy/restore is the same as TeardownTests': the whole manager GameObject goes with DestroyImmediate,
    /// and a <c>[UnityTearDown]</c> here re-bootstraps it before the base fixture's teardown runs.
    /// </para>
    /// </summary>
    public class OptionalPackageTeardownTests : BroAudioTestFixture
    {
        private static void DestroyManagerImmediate()
        {
            Assert.IsTrue(SoundManager.HasInstance, "Precondition failed: SoundManager must be alive before a test can destroy it.");
            UnityEngine.Object.DestroyImmediate(SoundManager.Instance.gameObject);
            Assert.IsFalse(SoundManager.HasInstance, "SoundManager.HasInstance must go false immediately after DestroyImmediate.");
        }

        /// <summary>See TeardownTests.RestoreSoundManagerAfterTest - Init() has no "already have one" guard, hence the check.</summary>
        [UnityTearDown]
        public IEnumerator RestoreSoundManagerAfterTest()
        {
            if (!SoundManager.HasInstance)
            {
                SoundManager.Init();
                // Start() runs next frame; AudioMixer.SetFloat silently fails before it.
                yield return null;
            }

            Assert.IsTrue(SoundManager.HasInstance,
                "OptionalPackageTeardownTests destroyed SoundManager and failed to restore it - every later PlayMode test will now fail to bootstrap.");
        }

#if PACKAGE_LOCALIZATION
        private static void OnLocalizedAudioChanged(SoundID _) { }
#endif

        [UnityTest]
        public IEnumerator ReleaseVerbs_ForOptionalPackages_WithManagerDestroyed_AreSilentNoOps()
        {
            SoundID id = NewSound("OptionalTeardownReleaseSfx", BroAudioType.SFX, NewClip(1f));

            DestroyManagerImmediate();

            (Action Verb, string Label)[] releaseVerbs =
            {
                (() => BroAudio.ReleaseAllAssets(id), "ReleaseAllAssets(SoundID)"),
                (() => BroAudio.ReleaseAsset(id), "ReleaseAsset(SoundID)"),
                (() => BroAudio.ReleaseAsset(id, 0), "ReleaseAsset(SoundID, clipIndex)"),
#if PACKAGE_LOCALIZATION
                (() => BroAudio.SubscribeLocalizedAudioChanged(id, OnLocalizedAudioChanged), "SubscribeLocalizedAudioChanged(SoundID, handler)"),
                (() => BroAudio.UnsubscribeLocalizedAudioChanged(id, OnLocalizedAudioChanged), "UnsubscribeLocalizedAudioChanged(SoundID, handler)"),
                (() => id.LocalizedAudioChanged += OnLocalizedAudioChanged, "SoundID.LocalizedAudioChanged +="),
                (() => id.LocalizedAudioChanged -= OnLocalizedAudioChanged, "SoundID.LocalizedAudioChanged -="),
#endif
            };

            foreach ((Action verb, string label) in releaseVerbs)
            {
                Assert.DoesNotThrow(() => verb(), $"{label} must be a silent no-op once SoundManager is destroyed.");
            }

            yield break;
        }

        // The acquire side of the same asymmetry: loading is Play-like and goes through SoundManager.Instance, and
        // so does the IsLoaded query - it is not null-safe even though it changes nothing.
        [UnityTest]
        public IEnumerator LoadAndIsLoadedVerbs_ForOptionalPackages_WithManagerDestroyed_ThrowBroAudioException()
        {
            SoundID id = NewSound("OptionalTeardownLoadSfx", BroAudioType.SFX, NewClip(1f));

            DestroyManagerImmediate();

            (Action Verb, string Label)[] acquireVerbs =
            {
                (() => BroAudio.LoadAssetAsync(id), "LoadAssetAsync(SoundID)"),
                (() => BroAudio.LoadAssetAsync(id, 0), "LoadAssetAsync(SoundID, clipIndex)"),
                (() => BroAudio.LoadAllAssetsAsync(id), "LoadAllAssetsAsync(SoundID)"),
                (() => BroAudio.IsLoaded(id), "IsLoaded(SoundID)"),
                (() => BroAudio.IsLoaded(id, 0), "IsLoaded(SoundID, clipIndex)"),
            };

            foreach ((Action verb, string label) in acquireVerbs)
            {
                Assert.Throws<BroAudioException>(() => verb(),
                    $"characterizes: {label} reads SoundManager.Instance, so it throws once SoundManager is destroyed.");
            }

            yield break;
        }
    }
}
#endif