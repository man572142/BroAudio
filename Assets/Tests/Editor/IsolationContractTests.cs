using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Guards the fixture itself. Every other file in this suite trusts BroEditorTestFixture to hand back
    /// an unmodified project; if these go red, every later green is meaningless.
    /// <para>
    /// NUnit runs these in alphabetical order within the fixture, which is the whole point of the A_-E_
    /// prefixes: A_ dirties and B_ observes the restore, C_ creates the temp folder and D_ observes its
    /// removal, E_ checks nothing was left dirty. Renaming one, or reordering them, breaks the contract.
    /// </para>
    /// </summary>
    public class IsolationContractTests : BroEditorTestFixture
    {
        private const string ProbeValue = "BroEditorTestFixture-probe";

        // Stashed by A_ before it dirties anything, so B_ can assert the values came BACK rather than
        // merely that they are no longer the probe - a restore to the wrong value would pass that.
        private static int _trackCountBefore;
        private static string _lastEditAssetBefore;
        private static string _clipboardBefore;

        [Test]
        public void A_MutatedEditorSetting_IsDirtiedByTheTest()
        {
            EditorSetting setting = BroEditorUtility.EditorSetting;
            Assert.IsTrue(setting, "EditorSetting asset is missing from Editor/Resources.");

            _trackCountBefore = setting.VirtualTrackCount;
            _lastEditAssetBefore = setting.LastEditAudioAsset;
            _clipboardBefore = EditorGUIUtility.systemCopyBuffer;

            setting.ShowVUColorOnVolumeSlider = !EditorSetting.FactorySettings.ShowVUColorOnVolumeSlider;
            setting.VirtualTrackCount = 99;
            setting.LastEditAudioAsset = ProbeValue;
            EditorGUIUtility.systemCopyBuffer = ProbeValue;

            Assert.AreEqual(99, setting.VirtualTrackCount);
        }

        [Test]
        public void B_AfterAMutatingTest_SettingAndClipboardAreRestored()
        {
            EditorSetting setting = BroEditorUtility.EditorSetting;
            Assert.AreEqual(_trackCountBefore, setting.VirtualTrackCount, "EditorSetting was not restored to its pre-test value.");
            Assert.AreEqual(_lastEditAssetBefore, setting.LastEditAudioAsset, "LastEditAudioAsset (EditorPrefs) was not restored to its pre-test value.");
            Assert.AreEqual(_clipboardBefore, EditorGUIUtility.systemCopyBuffer, "The system clipboard was not restored to its pre-test contents.");
        }

        [Test]
        public void C_TempFolder_IsCreatedOnDemand()
        {
            string path = EnsureTempFolder();
            Assert.IsTrue(AssetDatabase.IsValidFolder(path), "The temp folder was not created.");
        }

        [Test]
        public void D_TempFolder_DoesNotSurviveAPreviousTest()
        {
            Assert.IsFalse(AssetDatabase.IsValidFolder(TempFolder), "A temp asset folder survived TearDown.");
        }

        [Test]
        public void E_SettingAssets_AreNotDirtyAfterAMutatingTest()
        {
            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.EditorSetting), "EditorSetting was left dirty by an earlier test.");
            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.RuntimeSetting), "RuntimeSetting was left dirty by an earlier test.");
        }
    }
}