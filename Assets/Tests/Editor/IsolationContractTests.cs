using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Guards the fixture itself. Every other file in this suite trusts BroEditorTestFixture to hand back
    /// an unmodified project; if these go red, every later green is meaningless.
    /// <para>
    /// The A_-E_ prefixes describe the intended story - A_ dirties and B_ observes the restore, C_ creates
    /// the temp folder and D_ observes its removal, E_ checks nothing was left dirty - but NUnit does not
    /// guarantee alphabetical execution order on its own, so each test also carries an explicit
    /// <see cref="OrderAttribute"/> pinning that order. B_, D_ and E_ additionally guard themselves with
    /// <c>Assume.That(_aRan, ...)</c>: if the fixture is filtered down to run one of them alone (so A_
    /// never runs and never sets <see cref="_aRan"/>), they go Inconclusive instead of reading default
    /// field values and reporting a false red. Run the whole fixture to actually exercise the contract.
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

        // Set at the end of A_ - see the class doc's Assume.That() guard on B_/D_/E_.
        private static bool _aRan;

        [Test, Order(1)]
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
            _aRan = true;
        }

        [Test, Order(2)]
        public void B_AfterAMutatingTest_SettingAndClipboardAreRestored()
        {
            Assume.That(_aRan, "B_ depends on A_ having dirtied state first - run the full IsolationContractTests fixture, not this test alone.");

            EditorSetting setting = BroEditorUtility.EditorSetting;
            Assert.AreEqual(_trackCountBefore, setting.VirtualTrackCount, "EditorSetting was not restored to its pre-test value.");
            Assert.AreEqual(_lastEditAssetBefore, setting.LastEditAudioAsset, "LastEditAudioAsset (EditorPrefs) was not restored to its pre-test value.");
            Assert.AreEqual(_clipboardBefore, EditorGUIUtility.systemCopyBuffer, "The system clipboard was not restored to its pre-test contents.");
        }

        [Test, Order(3)]
        public void C_TempFolder_IsCreatedOnDemand()
        {
            string path = EnsureTempFolder();
            Assert.IsTrue(AssetDatabase.IsValidFolder(path), "The temp folder was not created.");
        }

        [Test, Order(4)]
        public void D_TempFolder_DoesNotSurviveAPreviousTest()
        {
            Assume.That(_aRan, "D_ depends on the fixture's own explicit order (C_ before D_) having run - run the full IsolationContractTests fixture, not this test alone.");

            Assert.IsFalse(AssetDatabase.IsValidFolder(TempFolder), "A temp asset folder survived TearDown.");
        }

        [Test, Order(5)]
        public void E_SettingAssets_AreNotDirtyAfterAMutatingTest()
        {
            Assume.That(_aRan, "E_ depends on A_ having dirtied state first - run the full IsolationContractTests fixture, not this test alone.");

            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.EditorSetting), "EditorSetting was left dirty by an earlier test.");
            Assert.IsFalse(EditorUtility.IsDirty(BroEditorUtility.RuntimeSetting), "RuntimeSetting was left dirty by an earlier test.");
        }
    }
}