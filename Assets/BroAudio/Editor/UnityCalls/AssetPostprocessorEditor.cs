using Ami.BroAudio.Editor.Setting;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    public class AssetPostprocessorEditor : AssetPostprocessor
    {
        private static bool _userDataChecked = false;

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            OnReimportAsset(importedAssets);

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