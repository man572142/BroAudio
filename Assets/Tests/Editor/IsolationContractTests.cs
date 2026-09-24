using Ami.BroAudio.Data;
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
    /// <see cref="OrderAttribute"/> pinning that order. B_ and E_ guard themselves with
    /// <c>Assume.That(_aRan, ...)</c> and D_ with <c>Assume.That(_cRan, ...)</c>: if the fixture is
    /// filtered down to run one of them alone (so its predecessor never runs and never sets the flag),
    /// they go Inconclusive instead of reading default field values and reporting a false green or a
    /// false red. Both flags are reset in <see cref="ResetRunFlags"/>, so a filtered RE-run in the same
    /// domain does not read the previous run's <c>true</c>. Run the whole fixture to actually exercise the contract.
    /// </para>
    /// <para>
    /// Every assertion here has to be able to fail. A_ therefore checks that its probe values actually differ
    /// from what is already in place - a probe already in place means a previous run leaked it, which is a
    /// failure, not an Inconclusive - and records each asset's dirty bit before establishing a clean one, so
    /// E_ can check the fixture put the bit back the way it found it.
    /// </para>
    /// </summary>
    public class IsolationContractTests : BroEditorTestFixture
    {
        private const string ProbeValue = "BroEditorTestFixture-probe";
        private const int ProbeVirtualTrackCount = 99;
        private const int ProbePlayerPoolSize = 97;

        // Stashed by A_ before it dirties anything, so B_ can assert the values came BACK rather than
        // merely that they are no longer the probe - a restore to the wrong value would pass that.
        private static int _trackCountBefore;
        private static bool _showVUColorBefore;
        private static int _playerPoolSizeBefore;
        private static string _lastEditAssetBefore;
        private static bool _lastEditPrefExistedBefore;
        private static string _clipboardBefore;
        private static bool _editorSettingDirtyBefore;
        private static bool _runtimeSettingDirtyBefore;

        // Set at the end of A_ and C_ respectively - see the class doc's Assume.That() guards.
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

            // The fixture snapshotted these bits before this test ran and puts them back in TearDown; E_
            // checks it did. The clean baseline after that is only so the dirty flags asserted at the end of
            // this test can only have been raised here.
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

            // A probe that happens to equal the value already in place would make B_ pass without the
            // fixture restoring anything. These are failures, not Inconclusives: CI passes an Inconclusive,
            // and the likeliest cause is an earlier run leaking the probe - exactly what this fixture guards.
            Assert.That(_trackCountBefore, Is.Not.EqualTo(ProbeVirtualTrackCount), "VirtualTrackCount already holds the probe value - a previous run leaked it, or pick another probe; B_ cannot observe a restore of it.");
            Assert.That(_playerPoolSizeBefore, Is.Not.EqualTo(ProbePlayerPoolSize), "DefaultAudioPlayerPoolSize already holds the probe value - a previous run leaked it, or pick another probe; B_ cannot observe a restore of it.");
            Assert.That(_lastEditAssetBefore, Is.Not.EqualTo(ProbeValue), "LastEditAudioAsset already holds the probe value - a previous run leaked it, B_ cannot observe a restore of it.");
            Assert.That(_clipboardBefore, Is.Not.EqualTo(ProbeValue), "The system clipboard already holds the probe value - a previous run leaked it, B_ cannot observe a restore of it.");

            editorSetting.ShowVUColorOnVolumeSlider = !_showVUColorBefore;
            editorSetting.VirtualTrackCount = ProbeVirtualTrackCount;
            editorSetting.LastEditAudioAsset = ProbeValue;
            runtimeSetting.DefaultAudioPlayerPoolSize = ProbePlayerPoolSize;
            EditorGUIUtility.systemCopyBuffer = ProbeValue;

            // Writing a public field does not raise the dirty flag on its own; every production path that
            // edits these assets pairs the write with SetDirty (LibraryManagerWindow, the Preferences
            // window, BroUpdater...), so a test standing in for one has to do the same.
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

            // The fixture puts the bit back rather than clearing it, so a developer's own unsaved edit survives the
            // run. When the bit was already set before A_, this can no longer tell a restore from a leak - it is
            // only a real check on a project with no unsaved settings, which is what CI runs.
            Assert.AreEqual(_editorSettingDirtyBefore, EditorUtility.IsDirty(BroEditorUtility.EditorSetting),
                "EditorSetting's dirty bit was not put back the way A_ found it.");
            Assert.AreEqual(_runtimeSettingDirtyBefore, EditorUtility.IsDirty(BroEditorUtility.RuntimeSetting),
                "RuntimeSetting's dirty bit was not put back the way A_ found it.");
        }
    }
}