using System;
using Ami.BroAudio.Data;
using Ami.BroAudio.Editor;
using UnityEngine.Audio;
using Ami.BroAudio;
using System.IO;
using UnityEditor;
using static Ami.BroAudio.Editor.BroEditorUtility;
using static Ami.BroAudio.Tools.BroName;
using System.Collections.Generic;
using Ami.BroAudio.Tools;
using Ami.BroAudio.Editor.Setting;
using UnityEngine;

public static class BroUpdater
{
    private const string DemoAudioAssetGUID = "6ae8d9cb032bc1c40a8f7d4bd78c9337";
    private static Version PlaybackGroupFirstReleasedVersion => new Version(2,0,0);
    private static Version AssetBasedSoundIDFirstReleasedVersion => new Version(3,1,0);

#pragma warning disable CS0618 // Type or member is obsolete
    public static void Process(AudioMixer mixer, BroAudioData coreData)
#pragma warning restore CS0618 // Type or member is obsolete
    {
        Version targetVersion = Version.Parse(BroVersion.CodeBaseVersion);
        Version currentVersion = BroVersion.Version;

        if(currentVersion >= targetVersion)
        {
            return;
        }

        if (TryLoadResources<RuntimeSetting>(RuntimeSettingPath, out var runtimeSetting))
        {
            bool isDirty = false;
            CreateGlobalPlaybackGroup(ref isDirty, currentVersion, runtimeSetting);

            if (isDirty)
            {
                EditorUtility.SetDirty(runtimeSetting);
            }
        }

        if (TryLoadResources<EditorSetting>(EditorSettingPath, out var editorSetting))
        {
            bool isDirty = false;
            CreateDefaultSpectrumColors(ref isDirty, editorSetting);
            AddPlaybackGroupDrawedProperty(ref isDirty, currentVersion, editorSetting);

            if (isDirty)
            {
                EditorUtility.SetDirty(editorSetting);
            }
        }

        string mixerPath = AssetDatabase.GetAssetPath(mixer);
        string corePath = mixerPath.Remove(mixerPath.LastIndexOf('/'));

        MoveEditorAssets(corePath);
        RemoveDemoAssetsIfNotExist(corePath, coreData);

        if (BroVersion.Version < AssetBasedSoundIDFirstReleasedVersion)
        {
            EnsureLegacyEntitiesConverted();
            UpgradeSoundIDs();
        }

        BroUserDataGenerator.RefreshInstructionAsset();
        BroVersion.UpdateVersion();

        if (coreData != null)
        {
            EditorUtility.SetDirty(coreData);
            AssetDatabase.SaveAssetIfDirty(coreData);
        }

        UnityEngine.Debug.Log(Utility.LogTitle + $"BroAudio has been successfully upgraded from {currentVersion} to {targetVersion}!");
    }

    private static void EnsureLegacyEntitiesConverted()
    {
        var audioAssets = new List<AudioAsset>();
        GetAudioAssets(audioAssets);

        bool anyConverted = false;
#pragma warning disable CS0618
        foreach (var asset in audioAssets)
        {
            anyConverted |= asset.ForceConvertLegacyEntities();
        }
#pragma warning restore CS0618

        if (anyConverted)
        {
            AudioAssetCache.RefreshCache();
        }
    }

    private static void UpgradeSoundIDs()
    {
        var choice = EditorUtility.DisplayDialog("Existing BroAudio Sound IDs must be upgraded",
            "All existing Sound IDs must be upgraded to the new version. This process may take a long time and it will not be undoable without version control or backups.\n" +
            "If you skip this process, existing Sound IDs will not be upgraded and may be broken.\n" +
            "The process can be done at any time by using Tools > BroAudio > Other > Upgrade Sound IDs.",
            "OK", "Skip");

        if (choice)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            SoundIDUpgrader.StartUpgrade();
#pragma warning restore CS0618 // Type or member is obsolete
        }
    }

    private static void AddPlaybackGroupDrawedProperty(ref bool isDirty, Version oldVersion, EditorSetting editorSetting)
    {
        if (oldVersion < PlaybackGroupFirstReleasedVersion)
        {
            for (int i = 0; i < editorSetting.AudioTypeSettings.Count; i++)
            {
                var typeSetting = editorSetting.AudioTypeSettings[i];
                typeSetting.DrawedProperty |= DrawedProperty.PlaybackGroup;
                editorSetting.AudioTypeSettings[i] = typeSetting;
            }
            isDirty = true;
        }
    }

    private static void CreateDefaultSpectrumColors(ref bool isDirty, EditorSetting editorSetting)
    {
        if (editorSetting.SpectrumBandColors == null || editorSetting.SpectrumBandColors.Count == 0)
        {
            editorSetting.CreateDefaultSpectrumColors();
            isDirty = true;
        }
    }

    private static void CreateGlobalPlaybackGroup(ref bool isDirty, Version oldVersion, RuntimeSetting runtimeSetting)
    {
        if(oldVersion < PlaybackGroupFirstReleasedVersion && runtimeSetting.GlobalPlaybackGroup == null)
        {
            string resourcePath = Path.GetDirectoryName(AssetDatabase.GetAssetPath(runtimeSetting));
            var globalPlaybackGroup = CreateScriptableObjectIfNotExist<DefaultPlaybackGroup>(Combine(resourcePath, GlobalPlaybackGroupPath) + ".asset");
            var serializeObj = new SerializedObject(globalPlaybackGroup);
            var combProp = serializeObj.FindProperty(DefaultPlaybackGroup.NameOf.CombFilteringTime)?.FindPropertyRelative(nameof(Rule<int>.Value));
            if (combProp != null)
            {
#pragma warning disable CS0618
                combProp.floatValue = runtimeSetting.CombFilteringPreventionInSeconds;
#pragma warning restore CS0618
                serializeObj.ApplyModifiedPropertiesWithoutUndo();
            }
            runtimeSetting.GlobalPlaybackGroup = globalPlaybackGroup;
            isDirty = true;
        }
    }

    private static void MoveEditorAssets(string corePath)
    {
        string oldPath = corePath + $"/{ResourcesFolder}/{EditorFolder}";
        string newPath = corePath + $"/{EditorFolder}/{ResourcesFolder}";

        if (Directory.Exists(newPath) || !Directory.Exists(oldPath))
        {
            return;
        }
        AssetDatabase.RenameAsset(oldPath, ResourcesFolder);
        string newPathRoot = newPath.Remove(newPath.LastIndexOf('/'));
        Directory.CreateDirectory(newPathRoot);
        AssetDatabase.Refresh();
        AssetDatabase.MoveAsset(oldPath.Replace(EditorFolder, ResourcesFolder), newPathRoot + $"/{ResourcesFolder}");
        AssetDatabase.Refresh();
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private static void RemoveDemoAssetsIfNotExist(string corePath, BroAudioData coreData)
#pragma warning restore CS0618 // Type or member is obsolete
    {
        if (coreData == null)
        {
            return;
        }

        string broPath = corePath.Remove(corePath.LastIndexOf('/'));
        string demoPath = Combine(broPath, Demo);

        if (Directory.Exists(demoPath))
        {
            return;
        }

#pragma warning disable CS0618 // Type or member is obsolete
        for (int i = 0; i < coreData.Assets.Count; i++)
        {
            if (coreData.Assets[i].AssetGUID == DemoAudioAssetGUID && coreData.Assets[i].AssetName == Demo && 
                coreData.Assets is List<AudioAsset> assetList)
            {
                // Removing the reference instead of deleting the asset in case the user actually needs it
                assetList.RemoveAt(i);
            }
        }
#pragma warning restore CS0618 // Type or member is obsolete
    }
}
