using Ami.BroAudio.Data;
using Ami.BroAudio.Editor.Setting;
using Ami.BroAudio.Runtime;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    public class AssetPostprocessorEditor : AssetPostprocessor
    {
        private static bool _userDataChecked = false;

#if UNITY_2021_2_OR_NEWER
        // A warm Library imports nothing of BroAudio's, so the import check alone never fires when the
        // generated user data is missing from disk (e.g. a gitignored Resources folder on a fresh checkout).
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload)
        {
            OnReimportAsset(importedAssets);
            if (!_userDataChecked && didDomainReload && IsUserDataMissing())
            {
                _userDataChecked = true;
                BroUserDataGenerator.CheckAndGenerateUserData(OnUserDataChecked);
                return;
            }
            CheckUserDataOnBroAudioImport(importedAssets);
        }

        private static bool IsUserDataMissing()
        {
            return !Resources.Load<SoundManager>(nameof(SoundManager))
                || !BroEditorUtility.TryLoadResources<RuntimeSetting>(BroEditorUtility.RuntimeSettingPath, out _)
                || !BroEditorUtility.TryLoadResources<EditorSetting>(BroEditorUtility.EditorSettingPath, out _);
        }
#else
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            OnReimportAsset(importedAssets);
            CheckUserDataOnBroAudioImport(importedAssets);
        }
#endif

        private static void CheckUserDataOnBroAudioImport(string[] importedAssets)
        {
            if (_userDataChecked)
            {
                return;
            }

            foreach (string assetPath in importedAssets)
            {
                if (assetPath.Contains("BroAudio") ||
                    assetPath.Contains("Bro_Audio") ||
                    assetPath.Contains("com.ami.broaudio"))
                {
                    _userDataChecked = true;
                    BroUserDataGenerator.CheckAndGenerateUserData(OnUserDataChecked);
                    break;
                }
            }
        }

        private static void OnUserDataChecked()
        {
            // Migrate legacy Core/Scripts layout before any data generation.
            FileStructureUpgrader.TryUpgradeFileStructure();
            
            var editorSetting = Resources.Load<EditorSetting>(BroEditorUtility.EditorSettingPath);
            if (!editorSetting || editorSetting.HasSetupWizardAutoLaunched || Application.isBatchMode)
            {
                return;
            }
            
            SetupWizardWindow.ShowWindow();
            editorSetting.HasSetupWizardAutoLaunched = true;
            EditorUtility.SetDirty(editorSetting);
        }

        private static void OnReimportAsset(string[] importedAssets)
        {
            if (importedAssets.Length > 0 && EditorWindow.HasOpenInstances<ClipEditorWindow>())
            {
                ClipEditorWindow window = EditorWindow.GetWindow<ClipEditorWindow>(null, false);
                window.OnPostprocessAllAssets();
            }
        }
    }
}