#if BroAudio_DevOnly
using System;
using UnityEditor;
using Ami.BroAudio.Tools;
using System.Linq;
using static Ami.BroAudio.Editor.Setting.BroAudioGUISetting;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ami.BroAudio.Editor.DevTool
{
    public static class PackageExporter
    {
		[MenuItem(BroName.MenuItem_BroAudio + "Export Package", priority = DevToolsMenuIndex + 13)]
        public static void Export()
        {
            Copy("Assets/BroAudio/Samples~", "Assets/BroAudio/Samples", recursive: true);
            Copy("Assets/BroAudio/Documentation~", "Assets/BroAudio/Documentation", recursive: true);
            Copy("Assets/BroAudio/Resources~", "Assets/BroAudio/Resources", recursive: false);
            Copy("Assets/BroAudio/Resources~/Editor", "Assets/BroAudio/Editor/Resources", recursive: false);

            string userFacingContextFilesDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..\\UserFacingContextFiles"));
            File.Copy(Path.Combine(userFacingContextFilesDir, "AGENTS.md"), "Assets/BroAudio/AGENTS.md", true);
            File.Copy(Path.Combine(userFacingContextFilesDir, "CLAUDE.md"), "Assets/BroAudio/CLAUDE.md", true);
            File.Copy(Path.Combine(userFacingContextFilesDir, "AGENTS.md.meta"), "Assets/BroAudio/AGENTS.md.meta", true);
            File.Copy(Path.Combine(userFacingContextFilesDir, "CLAUDE.md.meta"), "Assets/BroAudio/CLAUDE.md.meta", true);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            // Then wait for unity to update its db

            // THEN export

			List<string> allfilePaths = Directory.GetFiles("Assets/BroAudio", "*.*", SearchOption.AllDirectories)
                .Select(x => x.Replace("\\", "/"))
                .ToList();

            allfilePaths.RemoveAll(x => x.EndsWith($"{BroName.RuntimeSettingName}.asset", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith($"{BroName.EditorSettingName}.asset", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith($"{BroName.CoreDataName}.asset", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith($"{BroName.GlobalPlaybackGroupName}.asset", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith(BroVersion.VersionFileName, StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith("ImportDemoScene.unitypackage", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith("ImportDocumentation.unitypackage", StringComparison.OrdinalIgnoreCase) || 
                                        x.StartsWith("Assets/BroAudio/AudioAssets"));

            if(EditorUtility.DisplayDialog("Export BroAudio Package", $"Export Version:{BroVersion.CodeBaseVersion} ?", "Yes", "No"))
            {
#if UNITY_6000_6_OR_NEWER
                UnityEditor.AssetPackage.Package.Export(new UnityEditor.AssetPackage.ExportPackageParameters
                {
                    AssetPathNames = allfilePaths.ToArray(),
                    FileName = "BroAudio" + DateTime.Now.ToString("yy-MM-dd-HH-mm") + ".unitypackage",
                    Flags = ExportPackageOptions.Interactive
                });
#else
                AssetDatabase.ExportPackage(allfilePaths.ToArray(), "BroAudio" + DateTime.Now.ToString("yy-MM-dd-HH-mm") + ".unitypackage", ExportPackageOptions.Interactive);
#endif
            }
        }

        private static void Copy(string source, string target, bool recursive)
        {
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
            }

            foreach (string f in Directory.GetFiles(source, "*.*", SearchOption.TopDirectoryOnly))
            {
                string t = f.Replace(source, target);
                File.Copy(f, t, true);
            }

            if (recursive)
            {
                foreach (string d in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
                {
                    string t = d.Replace(source, target);
                    Copy(d, t, recursive);
                }
            }
        }
	}
}
#endif
