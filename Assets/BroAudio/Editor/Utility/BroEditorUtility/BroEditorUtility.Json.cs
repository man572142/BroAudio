using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

namespace Ami.BroAudio.Editor
{
	public static partial class BroEditorUtility
	{
		[Serializable]
		public class SerializedCoreData
		{
			public string AssetOutputPath;
			public List<string> GUIDs;

			public SerializedCoreData(string assetOutputPath,List<string> guids)
			{
				AssetOutputPath = assetOutputPath;
				GUIDs = guids;
            }
		}

		public static bool TryGetOldCoreDataTextAsset(out TextAsset asset)
		{
            asset = Resources.Load<TextAsset>("Editor/" + CoreDataResourcesPath);
			return asset != null;
        }

		public static bool TryParseCoreData(TextAsset textAsset,out SerializedCoreData coreData)
		{
			coreData = default;
            if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
			{
                try
                {
                    coreData = JsonUtility.FromJson<SerializedCoreData>(textAsset.text);
                    return true;
                }
                catch (ArgumentException ex)
                {
                    Debug.LogWarning(Utility.LogTitle + $"Ignoring malformed core data '{textAsset.name}': {ex.Message}");
                }
            }
			return false;
		}
	}
}