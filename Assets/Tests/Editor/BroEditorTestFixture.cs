using System.Collections.Generic;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
    /// Nothing in this fixture may trigger a domain reload — a reload mid-run kills the whole suite.
    /// </para>
    /// </summary>
    public abstract class BroEditorTestFixture
    {
        /// <summary>Concrete audio types, i.e. All without the composite flag.</summary>
        protected static readonly BroAudioType[] ConcreteAudioTypes =
        {
            BroAudioType.Music, BroAudioType.UI, BroAudioType.Ambience, BroAudioType.SFX, BroAudioType.VoiceOver,
        };

        /// <summary>The only folder a test may write into. Never write into Assets/BroAudio/ — that subtree is the shipped package.</summary>
        protected const string TempFolder = "Assets/BroAudioEditorTests_Temp";
        private const string TempFolderName = "BroAudioEditorTests_Temp";

        private readonly List<Object> _createdObjects = new List<Object>();
        private string _editorSettingSnapshot;
        private string _runtimeSettingSnapshot;
        private string _lastEditAudioAsset;
        private string _copyBuffer;
        private bool _tempFolderCreated;

        [SetUp]
        public void BroEditorSetUp()
        {
            // Mutate-and-restore, never delete-and-recreate: BroEditorUtility caches both assets statically,
            // so a re-created asset leaves every later test holding a stale reference.
            _editorSettingSnapshot = Snapshot(BroEditorUtility.EditorSetting);
            _runtimeSettingSnapshot = Snapshot(BroEditorUtility.RuntimeSetting);
            _lastEditAudioAsset = BroEditorUtility.EditorSetting ? BroEditorUtility.EditorSetting.LastEditAudioAsset : null;
            _copyBuffer = EditorGUIUtility.systemCopyBuffer;
            OnSetUp();
        }

        [TearDown]
        public void BroEditorTearDown()
        {
            OnTearDown();

            Restore(_editorSettingSnapshot, BroEditorUtility.EditorSetting);
            Restore(_runtimeSettingSnapshot, BroEditorUtility.RuntimeSetting);
            if (_lastEditAudioAsset != null && BroEditorUtility.EditorSetting)
            {
                BroEditorUtility.EditorSetting.LastEditAudioAsset = _lastEditAudioAsset;
            }

            // PropertyClipboard writes the developer's actual system clipboard.
            EditorGUIUtility.systemCopyBuffer = _copyBuffer;

            foreach (Object obj in _createdObjects)
            {
                if (obj)
                {
                    Object.DestroyImmediate(obj);
                }
            }
            _createdObjects.Clear();

            if (_tempFolderCreated)
            {
                AssetDatabase.DeleteAsset(TempFolder);
                _tempFolderCreated = false;
            }
        }

        /// <summary>Per-fixture setup. Runs after the isolation snapshot.</summary>
        protected virtual void OnSetUp() { }

        /// <summary>Per-fixture teardown. Runs before the isolation restore.</summary>
        protected virtual void OnTearDown() { }

        private static string Snapshot(Object asset) => asset ? JsonUtility.ToJson(asset) : null;

        private static void Restore(string json, Object asset)
        {
            if (json == null || !asset)
            {
                return;
            }
            JsonUtility.FromJsonOverwrite(json, asset);
            // Clear the dirty flag so the run leaves nothing staged in the user's working tree.
            EditorUtility.ClearDirty(asset);
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

        /// <summary>Shared instance — BroInstructionHelper caches the loaded asset per instance.</summary>
        protected static readonly BroInstructionHelper Instructions = new BroInstructionHelper();
    }
}
