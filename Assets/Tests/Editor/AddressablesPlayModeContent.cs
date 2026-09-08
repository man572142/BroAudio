using System.IO;
using UnityEngine;
using UnityEngine.TestTools;
#if PACKAGE_ADDRESSABLES
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine.AddressableAssets;
#endif

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Builds the Addressables play mode catalog before <see cref="AddressablesTests"/> enters play mode.
    /// <para>
    /// The catalog — <c>Library/com.unity.addressables/aa/&lt;platform&gt;/settings.json</c> and the content
    /// beside it — is what <c>Addressables.InitializeAsync</c> reads to turn a fixture GUID into a location.
    /// Nothing in the project builds it: the Editor does that as a side effect of entering play mode, and on
    /// CI that side effect only landed when <c>Library</c> was cold. Every run that restored a cached
    /// <c>Library</c> (workflow runs 7, 8 and 12) came up without a catalog and lost the whole suite to
    /// <c>InvalidKeyException: No Location found for Key=&lt;fixture guid&gt;</c>, while the cold runs passed.
    /// Deleting the subtree after the cache restore did not help, so whatever the restored Library suppresses
    /// is not the file itself.
    /// </para>
    /// <para>
    /// Rather than keep guessing at the Editor's trigger conditions, this builds the catalog outright.
    /// <see cref="IPrebuildSetup.Setup"/> runs in the Editor before the PlayMode run enters play mode, which
    /// is exactly where the active play mode data builder has to run, and it runs the same way on a cold
    /// Library, a restored one, and a developer's machine.
    /// </para>
    /// </summary>
    public class AddressablesPlayModeContent : IPrebuildSetup
    {
        public void Setup()
        {
#if PACKAGE_ADDRESSABLES
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (!settings)
            {
                Debug.LogError(Utility.LogTitle + "AddressableAssetSettings was not found, so the play mode " +
                               "catalog cannot be built. The addressable suite will fail.");
                return;
            }

            IDataBuilder builder = settings.ActivePlayModeDataBuilder;
            if (builder == null)
            {
                Debug.LogError(Utility.LogTitle + "Addressables has no active play mode data builder, so the " +
                               "play mode catalog cannot be built. The addressable suite will fail.");
                return;
            }

            // The base result type rather than the play mode one: BuildData only has to hand back something
            // assignable to it, so this works whichever builder the play mode slot happens to hold.
            AddressableAssetBuildResult result =
                builder.BuildData<AddressableAssetBuildResult>(new AddressablesDataBuilderInput(settings));
            if (result == null)
            {
                Debug.LogError(Utility.LogTitle + $"{builder.Name} refused to build the Addressables play mode " +
                               "catalog. The addressable suite will fail.");
                return;
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.LogError(Utility.LogTitle + $"{builder.Name} failed to build the Addressables play mode " +
                               $"catalog: {result.Error}");
                return;
            }

            // Check the file the suite actually reads. A builder that reports success but writes nothing is the
            // case that cost three CI runs, and it should say so here rather than eight tests later.
            string catalogSettings = Path.Combine(Addressables.BuildPath, "settings.json");
            if (!File.Exists(catalogSettings))
            {
                Debug.LogError(Utility.LogTitle + $"{builder.Name} reported success but wrote no catalog at " +
                               $"<b>{catalogSettings}</b>. The addressable suite will fail.");
                return;
            }

            Debug.Log(Utility.LogTitle + $"{builder.Name} built the Addressables play mode catalog at " +
                      $"<b>{catalogSettings}</b>.");
#endif
        }
    }
}