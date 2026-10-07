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
    /// Base fixture for every EditMode test. It isolates the project on disk: the settings assets,
    /// EditorPrefs, the system clipboard and temp assets.
    /// <para>
    /// Every TearDown step runs even when an earlier one throws, or a skipped restore becomes every later
    /// test's baseline. <see cref="EditorRunIsolationGuard"/> checks the settings files' bytes on disk across
    /// the whole run. Nothing here may trigger a domain reload: a reload mid-run kills the suite.
    /// </para>
    /// </summary>
    public abstract class BroEditorTestFixture
    {
        /// <summary>Concrete audio types, i.e. All without the composite flag.</summary>
        protected static readonly BroAudioType[] ConcreteAudioTypes = TestAudioLibrary.ConcreteAudioTypes;

        /// <summary>The only folder a test may write into. Never write into Assets/BroAudio/ — that subtree is the shipped package.</summary>
        // Must not contain "BroAudio", "Bro_Audio" or "com.ami.broaudio": AssetPostprocessorEditor matches
        // those substrings and would run BroUserDataGenerator for every asset created here.
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
        /// The EditorPrefs key behind <see cref="EditorSetting.LastEditAudioAsset"/>. Snapshot the raw key, not
        /// the property, so a key that did not exist is deleted again rather than left holding "".
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
                    // Don't DestroyImmediate an asset (needs allowDestroyingAssets, deletes from disk);
                    // DeleteTempFolder removes it.
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

        /// <summary>Exercises SerializedProperty code without touching disk. Destroyed in TearDown.</summary>
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

    /// <summary>Captures whether the key existed, so a restore deletes a key a test created.</summary>
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