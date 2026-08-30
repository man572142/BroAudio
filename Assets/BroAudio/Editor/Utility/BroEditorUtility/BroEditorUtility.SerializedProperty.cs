using UnityEditor;
using Ami.BroAudio.Data;
using static Ami.Extension.EditorScriptingExtension;
using Ami.Extension;
using UnityEngine;

namespace Ami.BroAudio.Editor
{
	public static partial class BroEditorUtility
    {
        public static void ResetBroAudioClipSerializedProperties(SerializedProperty property)
		{
#if PACKAGE_ADDRESSABLES
            var assetRefGuidProp = property
                .FindPropertyRelative(BroAudioClip.NameOf.AudioClipAssetReference)
                .FindPropertyRelative(AssetReferenceGUIDFieldName);
            assetRefGuidProp.stringValue = string.Empty; 
#endif
            property.FindPropertyRelative(BroAudioClip.NameOf.AudioClip).objectReferenceValue = null;
			property.FindPropertyRelative(nameof(BroAudioClip.Weight)).intValue = 0;
			ResetBroClipPlaybackSetting(property);
		}

		public static void ResetBroClipPlaybackSetting(SerializedProperty property)
		{
			property.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue = AudioConstant.FullVolume;
			property.FindPropertyRelative(nameof(BroAudioClip.StartPosition)).floatValue = 0f;
			property.FindPropertyRelative(nameof(BroAudioClip.EndPosition)).floatValue = 0f;
			property.FindPropertyRelative(nameof(BroAudioClip.FadeIn)).floatValue = 0f;
			property.FindPropertyRelative(nameof(BroAudioClip.FadeOut)).floatValue = 0f;
            property.FindPropertyRelative(nameof(BroAudioClip.Delay)).floatValue = 0f;
		}

        public static void SafeSetCurve(this SerializedProperty property, AnimationCurve curve)
        {
            if (curve != null && curve.keys.Length > 0)
            {
                property.animationCurveValue = curve;
            }
        }
	}
}