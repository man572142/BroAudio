using UnityEngine;
using UnityEditor;
using System.Reflection;
using System;
using Ami.Extension;
using Ami.BroAudio.Data;
using static Ami.Extension.EditorScriptingExtension;

namespace Ami.BroAudio.Editor
{
	public class SpatialSettingsEditorWindow : EditorWindow
	{
		public const string ReverbZoneMixLabel = "Reverb Zone Mix";
		public Vector2 WindowSize => new Vector2(400f, 570f);

		private MethodInfo _draw3DGUIMethod = null;
        private MethodInfo _handleLowPassFilterMethod = null;
		private FieldInfo _lowpassObjectField = null;
		private UnityEditor.Editor _audioSourceEditor = null;
		private readonly Keyframe[] _dummyFrameArray = new Keyframe[] { new Keyframe(0f, 0f) };
		private GameObject _tempObj = null;
        private SerializedObject _spatialSO = null;

		private MultiLabel _stereoPanLabels => new MultiLabel() { Main = "Stereo Pan", Left = "Left", Right = "Right" };
		private MultiLabel _spatialBlendLabels => new MultiLabel() { Main = "Spatial Blend", Left = "2D", Right = "3D" };

		private SerializedObject LowpassObject => _lowpassObjectField?.GetValue(_audioSourceEditor) as SerializedObject;

		public static void ShowWindow(SerializedProperty spatialProp)
		{
			var window = GetWindow<SpatialSettingsEditorWindow>();
			window.minSize = window.WindowSize;
			window.maxSize = window.WindowSize;
            window.titleContent = new GUIContent("Spatial Settings");
            window.Init(spatialProp);
            window.Show();
        }

        private void Init(SerializedProperty spatialProp)
        {
            if (!(spatialProp.objectReferenceValue is SpatialSetting spatialSetting))
            {
                Debug.LogError("Invalid spatial setting");
                return;
            }
            
            ExtractAudioSourceInspectorMembers();

            _tempObj = new GameObject("AudioSourceInspector");
			_tempObj.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector | HideFlags.DontSave;
			AudioSource audioSource = _tempObj.AddComponent<AudioSource>();
            _audioSourceEditor = UnityEditor.Editor.CreateEditor(audioSource);
            
            _spatialSO = new SerializedObject(spatialProp.objectReferenceValue);
            if (_spatialSO.FindProperty(nameof(SpatialSetting.HasLowPassFilter)).boolValue)
            {
                AddAndInitializeLowPassFilter(audioSource, spatialSetting);
            }
            foreach (SpatialPropertyType propType in Enum.GetValues(typeof(SpatialPropertyType)))
            {
                SetAudioSourceSpatialProperty(propType, spatialSetting, _audioSourceEditor.serializedObject);
            }
            _audioSourceEditor.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private void ExtractAudioSourceInspectorMembers()
        {
            Assembly unityEditorAssembly = typeof(AudioImporter).Assembly;
            Type audioSourceInspector = unityEditorAssembly?.GetType($"UnityEditor.AudioSourceInspector");
            _draw3DGUIMethod = audioSourceInspector?.GetMethod("Audio3DGUI", BindingFlags.NonPublic | BindingFlags.Instance);
            _handleLowPassFilterMethod = audioSourceInspector?.GetMethod("HandleLowPassFilter", BindingFlags.NonPublic | BindingFlags.Instance);
            _lowpassObjectField = audioSourceInspector?.GetField("m_LowpassObject", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private void SetAudioSourceSpatialProperty(SpatialPropertyType propType, SpatialSetting spatialSetting, SerializedObject serializedObject)
        {
			switch (propType)
			{
				case SpatialPropertyType.StereoPan:
                    serializedObject.FindProperty(AudioSourcePropertyPath.StereoPan).floatValue = spatialSetting.StereoPan;
					break;
				case SpatialPropertyType.DopplerLevel:
                    serializedObject.FindProperty(AudioSourcePropertyPath.DopplerLevel).floatValue = spatialSetting.DopplerLevel;
					break;
				case SpatialPropertyType.MinDistance:
                    serializedObject.FindProperty(AudioSourcePropertyPath.MinDistance).floatValue = spatialSetting.MinDistance;
					break;
				case SpatialPropertyType.MaxDistance:
                    serializedObject.FindProperty(AudioSourcePropertyPath.MaxDistance).floatValue = spatialSetting.MaxDistance;
					break;
				case SpatialPropertyType.SpatialBlend:
					if(!spatialSetting.SpatialBlend.IsDefaultCurve(AudioConstant.SpatialBlend_2D))
					{
                        serializedObject.FindProperty(AudioSourcePropertyPath.SpatialBlend).animationCurveValue = spatialSetting.SpatialBlend;
					}
					break;
				case SpatialPropertyType.ReverbZoneMix:
					if (!spatialSetting.ReverbZoneMix.IsDefaultCurve(AudioConstant.DefaultReverZoneMix))
					{
                        serializedObject.FindProperty(AudioSourcePropertyPath.ReverbZoneMix).animationCurveValue = spatialSetting.ReverbZoneMix;
					}
					break;
				case SpatialPropertyType.Spread:
					if (!spatialSetting.Spread.IsDefaultCurve(AudioConstant.DefaultSpread))
					{
                        serializedObject.FindProperty(AudioSourcePropertyPath.Spread).animationCurveValue = spatialSetting.Spread;
					}
					break;
				case SpatialPropertyType.CustomRolloff:
					if(spatialSetting.RolloffMode != AudioConstant.DefaultRolloffMode)
					{
                        serializedObject.FindProperty(AudioSourcePropertyPath.CustomRolloff).animationCurveValue = spatialSetting.CustomRolloff;
					}
					break;
				case SpatialPropertyType.RolloffMode:
                    serializedObject.FindProperty(AudioSourcePropertyPath.RolloffMode).enumValueIndex = (int)spatialSetting.RolloffMode;
					break;
			}
        }

        private void AddAndInitializeLowPassFilter(AudioSource audioSource, SpatialSetting spatialSetting)
        {
            audioSource.gameObject.AddComponent<AudioLowPassFilter>();
            _handleLowPassFilterMethod?.Invoke(_audioSourceEditor, null);
            var so = LowpassObject;
            if (so != null)
            {
                var property = so.FindProperty(AudioSourcePropertyPath.LowpassCustomCurve);
                property.animationCurveValue = spatialSetting.LowpassLevelCustomCurve;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
            
		private void OnDisable()
        {
            if (_audioSourceEditor == null)
            {
                return;
            }
				
            DestroyImmediate(_tempObj, true);
            DestroyImmediate(_audioSourceEditor);
            _audioSourceEditor = null;
        }

        private void OnGUI()
		{
			if (_audioSourceEditor == null || _spatialSO == null)
			{
				return;
			}
            
            // HandleLowPassFilter must run before serializedObject.Update(), as in Unity's own AudioSourceInspector.
            var hasLowPassFilterProp = _spatialSO.FindProperty(nameof(SpatialSetting.HasLowPassFilter));
            if (hasLowPassFilterProp.boolValue)
            {
                _handleLowPassFilterMethod?.Invoke(_audioSourceEditor, null);
            }
            var serializedObject = _audioSourceEditor.serializedObject;
            serializedObject.Update();
            var lowpassObject = LowpassObject;
            lowpassObject?.Update();

			DrawStereoPan();
			EditorGUILayout.Space();

			var sourceSpatialBlendProp = serializedObject.FindProperty(AudioSourcePropertyPath.SpatialBlend);
            var spatialBlendProp = _spatialSO.FindProperty(nameof(SpatialSetting.SpatialBlend));
			SerializeAnimateCurveValue(sourceSpatialBlendProp, spatialBlendProp,_spatialBlendLabels.Main, 
                currValue => Draw2SidesLabelSliderLayout(_spatialBlendLabels, currValue, 0f, 1f));
			EditorGUILayout.Space();

			var sourceReverbZoneProp = serializedObject.FindProperty(AudioSourcePropertyPath.ReverbZoneMix);
            var reverbZoneProp = _spatialSO.FindProperty(nameof(SpatialSetting.ReverbZoneMix));
			SerializeAnimateCurveValue(sourceReverbZoneProp, reverbZoneProp, ReverbZoneMixLabel, 
                currValue => EditorGUILayout.Slider(ReverbZoneMixLabel, currValue, 0f, 1.1f)); // reverb zone can accept value up to 1.1
			EditorGUILayout.Space();

			EditorGUILayout.LabelField("3D Sound Settings".ToBold(), GUIStyleHelper.RichText);
            
            EditorGUI.BeginChangeCheck();
            bool newValue = EditorGUILayout.Toggle("Enable Low Pass Filter", hasLowPassFilterProp.boolValue);
            if (EditorGUI.EndChangeCheck())
            {
                hasLowPassFilterProp.boolValue = newValue;
                AddOrRemoveLowPassFilter(newValue);
                _spatialSO.ApplyModifiedProperties();
            }
            
            _draw3DGUIMethod?.Invoke(_audioSourceEditor, null);
            Sync3DSpatialSettings();
            SyncLowPassFilterSetting(lowpassObject);
            serializedObject.ApplyModifiedProperties();
		}
        
        private void Sync3DSpatialSettings()
        {
            var audioSourceSO = _audioSourceEditor.serializedObject;
            if (_spatialSO == null || !audioSourceSO.hasModifiedProperties)
            {
                return;
            }

            _spatialSO.FindProperty(nameof(SpatialSetting.DopplerLevel)).floatValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.DopplerLevel).floatValue;
            _spatialSO.FindProperty(nameof(SpatialSetting.MinDistance)).floatValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.MinDistance).floatValue;
            _spatialSO.FindProperty(nameof(SpatialSetting.MaxDistance)).floatValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.MaxDistance).floatValue;
            _spatialSO.FindProperty(nameof(SpatialSetting.SpatialBlend)).animationCurveValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.SpatialBlend).animationCurveValue;
            _spatialSO.FindProperty(nameof(SpatialSetting.ReverbZoneMix)).animationCurveValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.ReverbZoneMix).animationCurveValue;
            _spatialSO.FindProperty(nameof(SpatialSetting.Spread)).animationCurveValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.Spread).animationCurveValue;
            var rolloffMode = (AudioRolloffMode)audioSourceSO.FindProperty(AudioSourcePropertyPath.RolloffMode).enumValueIndex;
            if (rolloffMode != AudioConstant.DefaultRolloffMode)
            {
                _spatialSO.FindProperty(nameof(SpatialSetting.CustomRolloff)).animationCurveValue = audioSourceSO.FindProperty(AudioSourcePropertyPath.CustomRolloff).animationCurveValue;
            }
            _spatialSO.FindProperty(nameof(SpatialSetting.RolloffMode)).enumValueIndex = (int)rolloffMode;
            _spatialSO.ApplyModifiedProperties();
        }

        private void SyncLowPassFilterSetting(SerializedObject lowpassObject)
        {
            if (lowpassObject != null && lowpassObject.hasModifiedProperties)
            {
                _spatialSO.FindProperty(nameof(SpatialSetting.LowpassLevelCustomCurve)).animationCurveValue = 
                    lowpassObject.FindProperty(AudioSourcePropertyPath.LowpassCustomCurve).animationCurveValue;
                _spatialSO.ApplyModifiedProperties();
                lowpassObject.ApplyModifiedProperties();
            }
        }

        private void AddOrRemoveLowPassFilter(bool enableLowPassFilter)
        {
            if (_spatialSO.targetObject is SpatialSetting spatialSetting &&
                _audioSourceEditor.serializedObject.targetObject is AudioSource audioSource && audioSource)
            {
                if (enableLowPassFilter)
                {
                    AddAndInitializeLowPassFilter(audioSource ,spatialSetting);
                }
                else
                {
                    DestroyImmediate(audioSource.GetComponent<AudioLowPassFilter>());
                    _handleLowPassFilterMethod?.Invoke(_audioSourceEditor, null);
                }
            }
        }

        private void DrawStereoPan()
		{
			SerializedProperty stereoPanProp = _audioSourceEditor.serializedObject.FindProperty(AudioSourcePropertyPath.StereoPan);
            EditorGUI.BeginChangeCheck();
			stereoPanProp.floatValue = Draw2SidesLabelSliderLayout(_stereoPanLabels, stereoPanProp.floatValue, -1f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                _spatialSO.FindProperty(nameof(SpatialSetting.StereoPan)).floatValue = stereoPanProp.floatValue;
                _spatialSO.ApplyModifiedProperties();
            }
		}

		private void SerializeAnimateCurveValue(SerializedProperty audioSourceProp, SerializedProperty spatialProp, string label, Func<float, float> onDrawSlider)
		{
			if (audioSourceProp.animationCurveValue.length > 1)
			{
				GUIStyle leftMiniGrey = new GUIStyle(EditorStyles.centeredGreyMiniLabel);
				leftMiniGrey.alignment = TextAnchor.MiddleLeft;
				EditorGUILayout.LabelField(label, "Controlled by curve", leftMiniGrey);
			}
			else if (audioSourceProp.animationCurveValue.length == 1)
			{
				EditorGUI.BeginChangeCheck();
				float newValue = onDrawSlider.Invoke(audioSourceProp.animationCurveValue[0].value);
				if (EditorGUI.EndChangeCheck())
				{
					AnimationCurve curve = audioSourceProp.animationCurveValue;
					_dummyFrameArray[0] = new Keyframe(0f, newValue);
					curve.keys = _dummyFrameArray;
					audioSourceProp.animationCurveValue = curve;
					audioSourceProp.serializedObject.ApplyModifiedProperties();
                    
                    spatialProp.animationCurveValue = curve;
                    spatialProp.serializedObject.ApplyModifiedProperties();
				}
			}
		}
	}
}