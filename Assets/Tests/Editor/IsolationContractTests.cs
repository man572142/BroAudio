using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Guards BroEditorTestFixture's isolation itself; if these go red, every other green is meaningless.
    /// <para>
    /// The tests form ordered pairs (A_ dirties / B_ and E_ observe; C_ creates / D_ observes), pinned with
    /// <see cref="OrderAttribute"/> since NUnit doesn't guarantee alphabetical order. Observers
    /// <c>Assume</c> their predecessor ran, so a filtered run goes Inconclusive instead of a false result;
    /// <see cref="ResetRunFlags"/> keeps a filtered re-run from reading the previous run's flags.
    /// </para>
    /// </summary>
    public class IsolationContractTests : BroEditorTestFixture
    {
        private const string ProbeValue = "BroEditorTestFixture-probe";
        private const int ProbeVirtualTrackCount = 99;
        private const int ProbePlayerPoolSize = 97;

        // B_ compares to these, not to "not the probe": a restore to the wrong value would pass that.
        private static int _trackCountBefore;
        private static bool _showVUColorBefore;
        private static int _playerPoolSizeBefore;
        private static string _lastEditAssetBefore;
        private static bool _lastEditPrefExistedBefore;
        private static string _clipboardBefore;
        private static bool _editorSettingDirtyBefore;
        private static bool _runtimeSettingDirtyBefore;

        private static bool _aRan;
        private static bool _cRan;

        [OneTimeSetUp]
        public void ResetRunFlags()
        {
            _aRan = false;
            _cRan = false;
        }

        [Test, Order(1)]
        public void A_MutatedSettingAssets_AreDirtiedByTheTest()
        {
            EditorSetting editorSetting = BroEditorUtility.EditorSetting;
            RuntimeSetting runtimeSetting = BroEditorUtility.RuntimeSetting;
            Assert.IsTrue(editorSetting, "EditorSetting asset is missing from Editor/Resources.");
            Assert.IsTrue(runtimeSetting, "RuntimeSetting asset is missing from Resources.");

            // Record for E_, then clear so the dirty flags asserted below can only have been raised here.
            _editorSettingDirtyBefore = EditorUtility.IsDirty(editorSetting);
            _runtimeSettingDirtyBefore = EditorUtility.IsDirty(runtimeSetting);
            EditorUtility.ClearDirty(editorSetting);
            EditorUtility.ClearDirty(runtimeSetting);

            _trackCountBefore = editorSetting.VirtualTrackCount;
            _showVUColorBefore = editorSetting.ShowVUColorOnVolumeSlider;
            _playerPoolSizeBefore = runtimeSetting.DefaultAudioPlayerPoolSize;
            _lastEditAssetBefore = editorSetting.LastEditAudioAsset;
            _lastEditPrefExistedBefore = EditorPrefs.HasKey(LastEditAudioAssetPrefsKey);
            _clipboardBefore = EditorGUIUtility.systemCopyBuffer;

            // Fail, not Assume: CI passes an Inconclusive, and a probe already in place most likely leaked.
            Assert.That(_trackCountBefore, Is.Not.EqualTo(ProbeVirtualTrackCount), "VirtualTrackCount already holds the probe value - a previous run leaked it, or pick another probe; B_ cannot observe a restore of it.");
            Assert.That(_playerPoolSizeBefore, Is.Not.EqualTo(ProbePlayerPoolSize), "DefaultAudioPlayerPoolSize already holds the probe value - a previous run leaked it, or pick another probe; B_ cannot observe a restore of it.");
            Assert.That(_lastEditAssetBefore, Is.Not.EqualTo(ProbeValue), "LastEditAudioAsset already holds the probe value - a previous run leaked it, B_ cannot observe a restore of it.");
            Assert.That(_clipboardBefore, Is.Not.EqualTo(ProbeValue), "The system clipboard already holds the probe value - a previous run leaked it, B_ cannot observe a restore of it.");

            editorSetting.ShowVUColorOnVolumeSlider = !_showVUColorBefore;
            editorSetting.VirtualTrackCount = ProbeVirtualTrackCount;
            editorSetting.LastEditAudioAsset = ProbeValue;
            runtimeSetting.DefaultAudioPlayerPoolSize = ProbePlayerPoolSize;
            EditorGUIUtility.systemCopyBuffer = ProbeValue;

            // A field write doesn't dirty the asset; production pairs it with SetDirty, so this must too.
            EditorUtility.SetDirty(editorSetting);
            EditorUtility.SetDirty(runtimeSetting);

            Assert.IsTrue(EditorUtility.IsDirty(editorSetting), "EditorSetting was not marked dirty by this test - E_ would then prove nothing.");
            Assert.IsTrue(EditorUtility.IsDirty(runtimeSetting), "RuntimeSetting was not marked dirty by this test - E_ would then prove nothing.");
            _aRan = true;
        }

        [Test, Order(2)]
        public void B_AfterAMutatingTest_SettingsAndClipboardAreRestored()
        {
            Assume.That(_aRan, "B_ depends on A_ having dirtied state first - run the full IsolationContractTests fixture, not this test alone.");

            EditorSetting editorSetting = BroEditorUtility.EditorSetting;
            RuntimeSetting runtimeSetting = BroEditorUtility.RuntimeSetting;
            Assert.AreEqual(_trackCountBefore, editorSetting.VirtualTrackCount, "EditorSetting.VirtualTrackCount was not restored to its pre-test value.");
            Assert.AreEqual(_showVUColorBefore, editorSetting.ShowVUColorOnVolumeSlider, "EditorSetting.ShowVUColorOnVolumeSlider was not restored to its pre-test value.");
            Assert.AreEqual(_playerPoolSizeBefore, runtimeSetting.DefaultAudioPlayerPoolSize, "RuntimeSetting.DefaultAudioPlayerPoolSize was not restored to its pre-test value.");
            Assert.AreEqual(_lastEditAssetBefore, editorSetting.LastEditAudioAsset, "LastEditAudioAsset (EditorPrefs) was not restored to its pre-test value.");
            Assert.AreEqual(_lastEditPrefExistedBefore, EditorPrefs.HasKey(LastEditAudioAssetPrefsKey),
                "LastEditAudioAsset's EditorPrefs key was left behind (or lost) - the restore must delete a key the test created.");
            Assert.AreEqual(_clipboardBefore, EditorGUIUtility.systemCopyBuffer, "The system clipboard was not restored to its pre-test contents.");
        }

        [Test, Order(3)]
        public void C_TempFolder_IsCreatedOnDemand()
        {
            string path = EnsureTempFolder();
            Assert.AreEqual(TempFolder, path, "EnsureTempFolder() returned a path other than the fixture's sanctioned temp folder.");
            Assert.IsTrue(AssetDatabase.IsValidFolder(path), "The temp folder was not created.");
            _cRan = true;
        }

        [Test, Order(4)]
        public void D_TempFolder_DoesNotSurviveAPreviousTest()
        {
            Assume.That(_cRan, "D_ depends on C_ having created the temp folder first - run the full IsolationContractTests fixture, not this test alone.");

            Assert.IsFalse(AssetDatabase.IsValidFolder(TempFolder), "A temp asset folder survived TearDown.");
        }

        [Test, Order(5)]
        public void E_SettingAssets_DirtyBitIsRestoredAfterAMutatingTest()
        {
            Assume.That(_aRan, "E_ depends on A_ having dirtied state first - run the full IsolationContractTests fixture, not this test alone.");

            // Restored, not cleared, so a developer's unsaved edit survives. Only a real check when the bit
            // started clean, as on CI.
            Assert.AreEqual(_editorSettingDirtyBefore, EditorUtility.IsDirty(BroEditorUtility.EditorSetting),
                "EditorSetting's dirty bit was not put back the way A_ found it.");
            Assert.AreEqual(_runtimeSettingDirtyBefore, EditorUtility.IsDirty(BroEditorUtility.RuntimeSetting),
                "RuntimeSetting's dirty bit was not put back the way A_ found it.");
        }
    }
}