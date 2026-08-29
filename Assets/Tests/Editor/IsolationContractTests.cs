using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Guards the fixture itself. Every other file in this suite trusts BroEditorTestFixture to hand back
    /// an unmodified project; if these go red, every later green is meaningless.
    /// <para>
    /// The pairs run in NUnit's alphabetical order within the fixture — the A_ test dirties, the B_ test
    /// observes the restore.
    /// </para>
    /// </summary>
    public class IsolationContractTests : BroEditorTestFixture
    {
        private const string ProbeValue = "BroEditorTestFixture-probe";

        [Test]
        public void A_MutatedEditorSetting_IsDirtiedByTheTest()
        {
            EditorSetting setting = BroEditorUtility.EditorSetting;
            Assert.IsTrue(setting, "EditorSetting asset is missing from Editor/Resources.");

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
            Assert.AreNotEqual(99, setting.VirtualTrackCount, "EditorSetting leaked from the previous test.");
            Assert.AreNotEqual(ProbeValue, setting.LastEditAudioAsset, "LastEditAudioAsset (EditorPrefs) leaked.");
            Assert.AreNotEqual(ProbeValue, EditorGUIUtility.systemCopyBuffer, "The system clipboard leaked.");
        }

        [Test]
        public void SettingAssets_AreNotDirtyAtTestStart()
        {
            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.EditorSetting), "EditorSetting was left dirty by an earlier test.");
            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.RuntimeSetting), "RuntimeSetting was left dirty by an earlier test.");
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
    }
}
