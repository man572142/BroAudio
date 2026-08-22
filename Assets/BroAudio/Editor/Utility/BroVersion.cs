using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
    public static class BroVersion
    {
        public const string CodeBaseVersion = "3.2.3";

        private const string VersionResourceName = "BroAudioVersion";
        public const string VersionFileName = VersionResourceName + ".txt";

        private static Version _version = null;
        public static Version Version
        {
            get
            {
                if (_version != null)
                {
                    return _version;
                }

                // first try to load from Editor/Resources
                var versionAsset = Resources.Load<TextAsset>(VersionResourceName);
                if (versionAsset != null)
                {
                    if (System.Version.TryParse(versionAsset.text, out _version))
                    {
                        Resources.UnloadAsset(versionAsset);
                        return _version;
                    }
                    Resources.UnloadAsset(versionAsset);
                }

                // then try from old legacy broaudio data

#pragma warning disable CS0618 // Type or member is obsolete
                var coreData = Resources.Load<Data.BroAudioData>(BroEditorUtility.CoreDataResourcesPath);

                if (coreData != null)
                {
                    if (!string.IsNullOrEmpty(coreData._version) && System.Version.TryParse(coreData._version, out _version))
                    {
                        coreData._version = null;
                        EditorUtility.SetDirty(coreData);
                        SetVersion(_version);
                        return _version;
                    }
                }
#pragma warning restore CS0618 // Type or member is obsolete

                _version = new Version(CodeBaseVersion);
                SetVersion(_version);

                return _version;
            }
        }

        // Returns the asset-relative path to the Editor/Resources directory
        // where the version file should be written (always under Assets/, never Packages/)
        private static string GetEditorResourcesPath()
        {
            // If EditorSetting exists, co-locate the version file in the same directory
            var editorSetting = Resources.Load<EditorSetting>(BroEditorUtility.EditorSettingPath);
            if (editorSetting != null)
            {
                string settingAssetPath = AssetDatabase.GetAssetPath(editorSetting);
                Resources.UnloadAsset(editorSetting);
                if (!string.IsNullOrEmpty(settingAssetPath))
                {
                    return Path.GetDirectoryName(settingAssetPath).Replace('\\', '/');
                }
            }

            // Fall back to default: Assets/BroAudio/Editor/Resources
            return $"{Tools.BroName.MainAssetPath}/{Tools.BroName.EditorFolder}/{Tools.BroName.ResourcesFolder}";
        }

        private static void SetVersion(System.Version version)
        {
            string resourcesAssetDir = GetEditorResourcesPath();

            // Convert Unity asset path to an absolute file system path
            string resourcesAbsDir = Path.Combine(Application.dataPath, resourcesAssetDir.Substring("Assets/".Length));

            if (!Directory.Exists(resourcesAbsDir))
            {
                Directory.CreateDirectory(resourcesAbsDir);
            }

            string absFilePath = Path.Combine(resourcesAbsDir, VersionFileName);
            File.WriteAllText(absFilePath, version.ToString());

            AssetDatabase.ImportAsset(resourcesAssetDir + "/" + VersionFileName);

            _version = version;
        }

        public static void UpdateVersion()
        {
            SetVersion(Version.Parse(CodeBaseVersion));
        }

    }
}
