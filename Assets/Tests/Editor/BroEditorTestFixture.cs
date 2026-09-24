using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Ami.BroAudio.Data;
using Ami.BroAudio.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Base fixture for every EditMode test.
    /// <para>
    /// The runtime suite's isolation problem is a singleton; this suite's is <b>the project on disk</b>.
    /// EditorSetting and RuntimeSetting are real assets, EditorPrefs and the system clipboard are real
    /// user state, and a leaked temp asset dirties the repo. All of that is solved here, once.
    /// </para>
    /// <para>
    /// Every TearDown step runs even when an earlier one throws - <see cref="OnTearDown"/> included - and the
    /// failures are rethrown together at the end. A skipped restore would otherwise become every later
    /// test's baseline, since the snapshot is per test. <see cref="EditorRunIsolationGuard"/> checks the whole
    /// run from outside, at the level this per-test restore cannot see: the settings files' bytes on disk.
    /// </para>
    /// <para>
    /// Nothing in this fixture may trigger a domain reload — a reload mid-run kills the whole suite.
    /// </para>
    /// </summary>
    public abstract class BroEditorTestFixture
    {
        /// <summary>Concrete audio types, i.e. All without the composite flag.</summary>
        protected static readonly BroAudioType[] ConcreteAudioTypes = TestAudioLibrary.ConcreteAudioTypes;

        /// <summary>The only folder a test may write into. Never write into Assets/BroAudio/ — that subtree is the shipped package.</summary>
        // Must not contain "BroAudio", "Bro_Audio", or "com.ami.broaudio": AssetPostprocessorEditor.
        // OnPostprocessAllAssets (Assets/BroAudio/Editor/UnityCalls/AssetPostprocessorEditor.cs) matches
        // every imported asset path against those substrings to decide whether to run BroUserDataGenerator
        // against the shipped package's own Resources folders. A temp folder whose name matches would fire
        // that generator for every asset this fixture creates here, guarded only by a static bool latch -
        // do not rename this back to something containing the package name.
        protected internal const string TempFolder = "Assets/EditorTestsScratch_Temp";
        private const string TempFolderName = "EditorTestsScratch_Temp";

        private readonly List<Object> _createdObjects = new List<Object>();
        private string _editorSettingSnapshot;
        private string _runtimeSettingSnapshot;
        private bool _editorSettingWasDirty;
        private bool _runtimeSettingWasDirty;
        private EditorPrefsSnapshot _lastEditAudioAssetPref;
        private string _copyBuffer;
        private bool _tempFolderCreated;

        /// <summary>
        /// The EditorPrefs key behind <see cref="EditorSetting.LastEditAudioAsset"/>, built the way production builds
        /// it (a private prefix plus the project's GUID). Snapshotting the raw key rather than the property lets a
        /// key that did not exist be deleted again, instead of being left behind holding an empty string.
        /// </summary>
        internal static string LastEditAudioAssetPrefsKey =>
            EditorReflected.StringConstant(typeof(EditorSetting), EditorReflected.EditorSetting.LastEditAudioAssetPrefsKey)
            + PlayerSettings.productGUID;

        [SetUp]
        public void BroEditorSetUp()
        {
            // Mutate-and-restore, never delete-and-recreate: BroEditorUtility caches both assets statically,
            // so a re-created asset leaves every later test holding a stale reference.
            EditorSetting editorSetting = BroEditorUtility.EditorSetting;
            RuntimeSetting runtimeSetting = BroEditorUtility.RuntimeSetting;
            _editorSettingSnapshot = Snapshot(editorSetting);
            _runtimeSettingSnapshot = Snapshot(runtimeSetting);
            _editorSettingWasDirty = editorSetting && EditorUtility.IsDirty(editorSetting);
            _runtimeSettingWasDirty = runtimeSetting && EditorUtility.IsDirty(runtimeSetting);
            _lastEditAudioAssetPref = EditorPrefsSnapshot.OfString(LastEditAudioAssetPrefsKey);
            _copyBuffer = EditorGUIUtility.systemCopyBuffer;
            OnSetUp();
        }

        [TearDown]
        public void BroEditorTearDown()
        {
            RunEveryStep(
                OnTearDown,
                () => Restore(_editorSettingSnapshot, BroEditorUtility.EditorSetting, _editorSettingWasDirty),
                () => Restore(_runtimeSettingSnapshot, BroEditorUtility.RuntimeSetting, _runtimeSettingWasDirty),
                () => _lastEditAudioAssetPref.Restore(),
                // PropertyClipboard writes the developer's actual system clipboard.
                () => EditorGUIUtility.systemCopyBuffer = _copyBuffer,
                DestroyCreatedObjects,
                DeleteTempFolder);
        }

        /// <summary>Per-fixture setup. Runs after the isolation snapshot.</summary>
        protected virtual void OnSetUp() { }

        /// <summary>Per-fixture teardown. Runs before the isolation restore; a throw here no longer skips it.</summary>
        protected virtual void OnTearDown() { }

        /// <summary>
        /// Runs every step whether or not an earlier one threw, then rethrows: one failure as itself (stack
        /// trace kept), several as an <see cref="AggregateException"/> so none is hidden behind another.
        /// </summary>
        private static void RunEveryStep(params Action[] steps)
        {
            List<Exception> failures = null;
            foreach (Action step in steps)
            {
                try
                {
                    step();
                }
                catch (Exception e)
                {
                    (failures ??= new List<Exception>()).Add(e);
                }
            }

            if (failures == null)
            {
                return;
            }
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }
            throw new AggregateException("Several BroEditorTestFixture TearDown steps failed.", failures);
        }

        private static string Snapshot(Object asset) => asset ? JsonUtility.ToJson(asset) : null;

        private static void Restore(string json, Object asset, bool wasDirty)
        {
            if (json == null || !asset)
            {
                return;
            }
            JsonUtility.FromJsonOverwrite(json, asset);

            // Put the dirty bit back the way the test found it rather than clearing it: clearing would also
            // throw away an unsaved edit the developer made before the run.
            if (wasDirty)
            {
                EditorUtility.SetDirty(asset);
            }
            else
            {
                EditorUtility.ClearDirty(asset);
            }
        }

        private void DestroyCreatedObjects()
        {
            Object[] objects = _createdObjects.ToArray();
            _createdObjects.Clear();

            var steps = new List<Action>();
            foreach (Object obj in objects)
            {
                steps.Add(() =>
                {
                    // An object a test turned into an asset (AssetDatabase.CreateAsset) cannot be DestroyImmediate'd
                    // without allowDestroyingAssets - which would delete it from disk. It lives in the temp folder,
                    // whose deletion below removes it.
                    if (obj && !AssetDatabase.Contains(obj))
                    {
                        Object.DestroyImmediate(obj);
                    }
                });
            }
            RunEveryStep(steps.ToArray());
        }

        private void DeleteTempFolder()
        {
            if (!_tempFolderCreated)
            {
                return;
            }
            _tempFolderCreated = false;
            AssetDatabase.DeleteAsset(TempFolder);
        }

        #region Temp assets
        /// <summary>
        /// Creates <see cref="TempFolder"/> on first use and deletes it in TearDown.
        /// The only sanctioned place for a test to write to disk.
        /// </summary>
        protected string EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolderName);
            }
            _tempFolderCreated = true;
            return TempFolder;
        }

        /// <summary>
        /// An in-memory ScriptableObject wrapped in a SerializedObject — the standard way to exercise
        /// SerializedProperty code without touching disk. Destroyed in TearDown.
        /// </summary>
        protected SerializedObject NewSerializedObject<T>() where T : ScriptableObject
            => new SerializedObject(NewScriptableObject<T>());

        /// <summary>Creates a tracked in-memory ScriptableObject. No disk footprint.</summary>
        protected T NewScriptableObject<T>() where T : ScriptableObject => Track(ScriptableObject.CreateInstance<T>());

        /// <summary>Registers an object for destruction in TearDown.</summary>
        protected T Track<T>(T obj) where T : Object
        {
            _createdObjects.Add(obj);
            return obj;
        }
        #endregion
    }

    /// <summary>
    /// One EditorPrefs string key, captured with whether it existed, so a restore can delete a key a test
    /// created instead of leaving it behind with a value.
    /// </summary>
    internal readonly struct EditorPrefsSnapshot : IEquatable<EditorPrefsSnapshot>
    {
        public readonly string Key;
        public readonly bool Existed;
        public readonly string Value;

        private EditorPrefsSnapshot(string key, bool existed, string value)
        {
            Key = key;
            Existed = existed;
            Value = value;
        }

        public static EditorPrefsSnapshot OfString(string key)
        {
            bool existed = EditorPrefs.HasKey(key);
            return new EditorPrefsSnapshot(key, existed, existed ? EditorPrefs.GetString(key) : null);
        }

        public void Restore()
        {
            if (Key == null)
            {
                return;
            }
            if (Existed)
            {
                if (EditorPrefs.GetString(Key) != Value)
                {
                    EditorPrefs.SetString(Key, Value);
                }
            }
            else if (EditorPrefs.HasKey(Key))
            {
                EditorPrefs.DeleteKey(Key);
            }
        }

        public bool Equals(EditorPrefsSnapshot other) => Key == other.Key && Existed == other.Existed && Value == other.Value;
        public override bool Equals(object obj) => obj is EditorPrefsSnapshot other && Equals(other);
        public override int GetHashCode() => (Key ?? string.Empty).GetHashCode() ^ Existed.GetHashCode() ^ (Value ?? string.Empty).GetHashCode();
        public override string ToString() => Existed ? $"{Key} = \"{Value}\"" : $"{Key} (absent)";
    }
}