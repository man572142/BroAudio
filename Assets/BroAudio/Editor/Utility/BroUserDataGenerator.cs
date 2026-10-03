using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Editor.Setting;
using static Ami.BroAudio.Editor.BroEditorUtility;
using static Ami.BroAudio.Tools.BroName;

namespace Ami.BroAudio.Editor
{
#if UNITY_EDITOR
    public static class BroUserDataGenerator
    {
        private static bool _isLoading = false;

        [MenuItem(MenuItem_BroAudio + "Others/Regenerate User Data")]
        private static void RegenerateUserData() => CheckAndGenerateUserData();

        public static void CheckAndGenerateUserData(Action onFinished = null)
        {
            if (_isLoading)
            {
                return;
            }
            _isLoading = true;

            ResourceRequest request;

            try
            {
                EnsureAllResources();

                request = Resources.LoadAsync<SoundManager>(nameof(SoundManager));
                request.completed += OnGetSoundManager;
            }
            catch
            {
                _isLoading = false;
                throw;
            }

            void OnGetSoundManager(AsyncOperation operation)
            {
                try
                {
                    request.completed -= OnGetSoundManager;
                    if (request.asset is SoundManager soundManager)
                    {
                        if (BroVersion.Version.Major >= 3 && 
                            (!TryLoadResources<RuntimeSetting>(RuntimeSettingPath, out _) || !TryLoadResources<EditorSetting>(EditorSettingPath, out _)))
                        {
                            StartGeneratingUserData(soundManager);
                            BroUpdater.Process(soundManager.AudioMixer, null);
                        }
                        else
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            if (TryGetCoreData(out var currentCoreData))
                            {
                                soundManager.AssignCoreData(currentCoreData);

                                BroUpdater.Process(soundManager.AudioMixer, currentCoreData);
                            }
                            else
                            {
                                StartGeneratingUserData(soundManager);
                                BroUpdater.Process(soundManager.AudioMixer, null);
                            }
#pragma warning restore CS0618 // Type or member is obsolete
                        }
                        onFinished?.Invoke();
                    }
                    else
                    {
                        Debug.LogError(Utility.LogTitle + $"Load {nameof(SoundManager)} fail, " +
                            $"please import it and place it in the Resources folder, and go to Tools/Preferences, switch to the last tab and hit [Regenerate User Data]");
                    }
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }

        private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

        private static IEnumerable<string> GetPackageResourcesDirectories()
        {
            // Loop upwards and find all Resources~ directories
            string currentPath = Path.GetDirectoryName(ThisFilePath());
            var cwd = Directory.GetCurrentDirectory();

            // Loop until we reach the project root
            while (!string.IsNullOrEmpty(currentPath) && (currentPath.Contains(cwd) || !Path.IsPathRooted(currentPath)))
            {
                var resourcesPath = Path.Combine(currentPath, "Resources~");

                if (Directory.Exists(resourcesPath))
                {
                    yield return resourcesPath;
                }

                currentPath = Path.GetDirectoryName(currentPath);
            }
        }

        // BroInstruction holds package-owned text only, so the user's copy is replaced on upgrade to pick up new keys.
        public static void RefreshInstructionAsset()
        {
            var instruction = Resources.Load<BroInstruction>(InstructionFileName);
            if (!instruction)
            {
                return;
            }

            string targetPath = AssetDatabase.GetAssetPath(instruction);
            foreach (string resourcesPath in GetPackageResourcesDirectories())
            {
                string sourcePath = Path.Combine(resourcesPath, EditorFolder, InstructionFileName + ".asset");
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, targetPath, true);
                    AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                    return;
                }
            }
        }

        private static void EnsureAllResources()
        {
            int changed = 0;
            foreach (string resourcesPath in GetPackageResourcesDirectories())
            {
                changed += CopyResourcesToLocalIfNotFound(resourcesPath);
            }

            if (changed > 0)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            static int CopyResourcesToLocalIfNotFound(string resourcesPath)
            {
                int changed = 0;
                string targetBasePath = Path.Combine(Application.dataPath, "BroAudio");

                // Copy files from Resources~/
                changed += CopyDirectoryIfNotExists(resourcesPath, Path.Combine(targetBasePath, "Resources"));

                // Copy files from Resources~/Editor/
                string editorResourcesPath = Path.Combine(resourcesPath, "Editor");
                if (Directory.Exists(editorResourcesPath))
                {
                    changed += CopyDirectoryIfNotExists(editorResourcesPath, Path.Combine(targetBasePath, "Editor", "Resources"));
                }

                return changed;
            }

            static int CopyDirectoryIfNotExists(string sourceDir, string targetDir)
            {
                int changed = 0;
                var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly);

                foreach (string sourceFile in files)
                {
                    string fileName = Path.GetFileName(sourceFile);
                    string targetFile = Path.Combine(targetDir, fileName);
                    string sourceMetaFile = sourceFile + ".meta";
                    string targetMetaFile = targetFile + ".meta";

                    // Only copy if no resource with the same name exists in Resources folders
                    if (!ResourceExistsInResourcesFolders(fileName))
                    {
                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        File.Copy(sourceFile, targetFile, true);
#if BroAudio_DevOnly
                        Debug.Log($"Copied {fileName} to {targetDir}");
#endif
                        changed++;

                        // Copy .meta file if it exists
                        if (File.Exists(sourceMetaFile))
                        {
                            File.Copy(sourceMetaFile, targetMetaFile, true);
#if BroAudio_DevOnly
                            Debug.Log($"Copied {fileName}.meta to {targetDir}");
#endif
                            changed++;
                        }
                    }
                }

                return changed;
            }

            static bool ResourceExistsInResourcesFolders(string fileName)
            {
                // Remove .asset extension if present for asset name matching
                string assetName = Path.GetFileNameWithoutExtension(fileName);

                // Search for existing assets with the same name in all Resources folders
                string[] guids = AssetDatabase.FindAssets(assetName, new[] { "Assets" });

                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    string assetFileName = Path.GetFileName(assetPath);

                    // Check if the asset is in a Resources folder and has the same name
                    if (assetPath.Contains("/Resources/") && assetFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Not found, so do a deeper dive to see if it exists in any subfolders
                foreach (string assetPath in Directory.EnumerateFiles("Assets", fileName, SearchOption.AllDirectories))
                {
                    if (assetPath.Contains("/Resources/"))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static void StartGeneratingUserData(SoundManager soundManager)
        {
            GetInitialData(out string audioAssetOutputPath);

            string resourcePath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(soundManager));

#pragma warning disable CS0618 // Type or member is obsolete
            if (TryGetCoreData(out var coreData))
            {
                soundManager.AssignCoreData(coreData);
            }
            else 
            {
                soundManager.AssignCoreData(null);
            }
#pragma warning restore CS0618 // Type or member is obsolete

            var runtimeSetting = CreateScriptableObjectIfNotExist<RuntimeSetting>(GetAssetSavePath(resourcePath, RuntimeSettingPath));
            if (runtimeSetting.GlobalPlaybackGroup == null)
            {
                runtimeSetting.GlobalPlaybackGroup = CreateScriptableObjectIfNotExist<DefaultPlaybackGroup>(GetAssetSavePath(resourcePath, GlobalPlaybackGroupPath));
                EditorUtility.SetDirty(runtimeSetting);
            }

            string editorResourcesPath = resourcePath.Replace(ResourcesFolder, $"{EditorFolder}/{ResourcesFolder}");
            var editorSetting = CreateScriptableObjectIfNotExist<EditorSetting>(GetAssetSavePath(editorResourcesPath, EditorSettingPath));
            if (string.IsNullOrEmpty(editorSetting.AssetOutputPath))
            {
                editorSetting.AssetOutputPath = audioAssetOutputPath;
                EditorUtility.SetDirty(editorSetting);
            }

            AssetDatabase.SaveAssetIfDirty(runtimeSetting);
            AssetDatabase.SaveAssetIfDirty(editorSetting);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static string GetAssetSavePath(string resourcesPath, string relativePath)
        {
            return Combine(resourcesPath, relativePath + ".asset");
        }

        private static void GetInitialData(out string audioAssetOutputPath)
        {
            if (TryGetOldCoreDataTextAsset(out var oldTextAsset) && TryParseCoreData(oldTextAsset, out var oldCoreData))
            {
                audioAssetOutputPath = oldCoreData.AssetOutputPath;

                string oldCoreDataPath = AssetDatabase.GetAssetPath(oldTextAsset);
                string fileName = Path.GetFileName(oldCoreDataPath);
                string deprecatedKeyword = "_deprecated.json";
                if (!fileName.EndsWith(deprecatedKeyword))
                {
                    fileName = fileName.Replace(".json", deprecatedKeyword);
                }
                AssetDatabase.RenameAsset(oldCoreDataPath, fileName);
                EditorUtility.SetDirty(oldTextAsset);
            }
            else
            {
                audioAssetOutputPath = DefaultAssetOutputPath;
                string demoPath = Combine(MainAssetPath, "Samples", Demo);
                string demoAssetPath = Combine(demoPath, "AudioAssets", Demo + ".asset");
                if (!Directory.Exists(demoPath))
                {
                    AssetDatabase.DeleteAsset(demoAssetPath);
                }
            }
        }
    }
#endif
    }