using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ami.BroAudio.Editor;
using Ami.BroAudio.Editor.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Whole-run isolation check for the EditMode assembly, from outside the per-test fixture.
/// <para>
/// <see cref="BroEditorTestFixture"/> restores the settings assets in MEMORY after each test and resets their
/// dirty bits, so a production path that SAVES one mid-test (the <c>AssetOutputPath</c> getter on a blank path,
/// <c>WriteAssetOutputPathToSetting</c>) would reach disk unseen: the in-memory values come back and the dirty
/// flag is put back, while the file has already changed. Both files are gitignored, so no <c>git status</c>
/// would show it either. This compares their bytes on disk - plus the EditorPrefs keys BroAudio writes, the
/// system clipboard and the temp folder - before the first test and after the last.
/// </para>
/// <para>
/// Declared outside any namespace on purpose: NUnit applies a namespace-less <see cref="SetUpFixtureAttribute"/>
/// to every fixture in the assembly, and this assembly's fixtures span <c>Ami.BroAudio.Tests</c> and
/// <c>Ami.BroAudio.Editor.Tests</c>. A failure here is reported against the run's teardown rather than a test.
/// </para>
/// <para>
/// Known false positive: a developer's UNSAVED edit to a settings asset, flushed to disk by any
/// <c>AssetDatabase.SaveAssets()</c> during the run, changes the bytes too. Save before running.
/// </para>
/// </summary>
[SetUpFixture]
public class EditorRunIsolationGuard
{
    private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

    private readonly List<FileSnapshot> _files = new List<FileSnapshot>();
    private readonly List<EditorPrefsSnapshot> _prefs = new List<EditorPrefsSnapshot>();
    private string _clipboard;
    private bool _tempFolderExisted;

    private static string TempFolderMetaPath => Path.Combine(ProjectRoot, BroEditorTestFixture.TempFolder + ".meta");
    private static string TempFolderFullPath => Path.Combine(ProjectRoot, BroEditorTestFixture.TempFolder);

    [OneTimeSetUp]
    public void SnapshotProjectState()
    {
        _files.Clear();
        _prefs.Clear();

        _files.Add(FileSnapshot.Of(BroEditorUtility.EditorSetting));
        _files.Add(FileSnapshot.Of(BroEditorUtility.RuntimeSetting));

        _prefs.Add(EditorPrefsSnapshot.OfString(BroEditorTestFixture.LastEditAudioAssetPrefsKey));
        _prefs.Add(EditorPrefsSnapshot.OfString(
            EditorReflected.StringConstant(typeof(IssueReportWindow), EditorReflected.IssueReportWindow.LastSaveDirectoryPrefKey)
            + PlayerSettings.productGUID));

        _clipboard = EditorGUIUtility.systemCopyBuffer;
        _tempFolderExisted = Directory.Exists(TempFolderFullPath) || File.Exists(TempFolderMetaPath);
    }

    [OneTimeTearDown]
    public void AssertProjectStateUnchanged()
    {
        var leaks = new List<string>();

        foreach (FileSnapshot file in _files)
        {
            if (file.HasChangedOnDisk(out string description))
            {
                leaks.Add(description);
            }
        }

        foreach (EditorPrefsSnapshot before in _prefs)
        {
            EditorPrefsSnapshot after = EditorPrefsSnapshot.OfString(before.Key);
            if (!after.Equals(before))
            {
                leaks.Add($"EditorPrefs changed: was {before}, now {after}.");
                before.Restore(); // user state - put it back even though the run is already red
            }
        }

        string clipboardNow = EditorGUIUtility.systemCopyBuffer;
        if (clipboardNow != _clipboard)
        {
            leaks.Add("The system clipboard changed during the run.");
            EditorGUIUtility.systemCopyBuffer = _clipboard;
        }

        // A folder left by an earlier, crashed run is not this run's leak; only a new one is.
        if (!_tempFolderExisted && (Directory.Exists(TempFolderFullPath) || File.Exists(TempFolderMetaPath)))
        {
            leaks.Add($"{BroEditorTestFixture.TempFolder} (or its .meta) survived the run.");
        }

        Assert.IsEmpty(leaks, "The EditMode run changed project or user state it must leave untouched:\n" + string.Join("\n", leaks));
    }

    /// <summary>The bytes of an asset's file on disk, or its absence.</summary>
    private readonly struct FileSnapshot
    {
        private readonly string _path;
        private readonly byte[] _bytes;

        private FileSnapshot(string path, byte[] bytes)
        {
            _path = path;
            _bytes = bytes;
        }

        public static FileSnapshot Of(Object asset)
        {
            string assetPath = asset ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(assetPath))
            {
                return default;
            }
            string fullPath = Path.Combine(ProjectRoot, assetPath);
            return new FileSnapshot(fullPath, File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null);
        }

        public bool HasChangedOnDisk(out string description)
        {
            description = null;
            if (_path == null)
            {
                return false;
            }

            byte[] now = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            if (_bytes == null && now == null)
            {
                return false;
            }
            if (_bytes == null || now == null || !_bytes.SequenceEqual(now))
            {
                description = _bytes == null ? $"{_path} was created during the run."
                    : now == null ? $"{_path} was deleted during the run."
                    : $"{_path} was rewritten during the run (a production save reached disk).";
                return true;
            }
            return false;
        }
    }
}