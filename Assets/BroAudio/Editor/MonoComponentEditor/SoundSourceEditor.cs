using UnityEngine;
using UnityEditor;
using Ami.BroAudio.Runtime;
using System.Collections.Generic;
using static Ami.BroAudio.SoundSource.NameOf;
using static Ami.Extension.EditorScriptingExtension;

namespace Ami.BroAudio.Editor
{
    [CustomEditor(typeof(SoundSource))]
    public class SoundSourceEditor : UnityEditor.Editor
    {
        private const float FadeOutFieldWidth = 70f;
        private const string TimeUnit = "sec";

        private RectOffset _inspectorPadding;
        private bool _isInit;
        private float _currentDrawnPosY;
        private BroInstructionHelper _instruction = new BroInstructionHelper();

        private GUIContent _playOnEnableContent;
        private GUIContent _onlyOnceContent;
        private GUIContent _stopOnDisableContent;
        private GUIContent _fadeOutContent;
        private GUIContent _positionModeContent;
        private GUIContent _overrideGroupContent;
        private GUIContent _delayContent;
        private Dictionary<string, SerializedProperty> _mainPropertyDict = new Dictionary<string, SerializedProperty>();

        private void OnEnable()
        {
            _inspectorPadding = InspectorPadding;
            _playOnEnableContent = new GUIContent("Play On Enable", _instruction.GetText(Instruction.SoundSource_PlayOnEnable));
            _onlyOnceContent = new GUIContent("Only Play Once", _instruction.GetText(Instruction.SoundSource_OnlyPlayOnce));
            _stopOnDisableContent = new GUIContent("Stop On Disable", _instruction.GetText(Instruction.SoundSource_StopOnDisable));
            _fadeOutContent = new GUIContent("Override Fade Out", _instruction.GetText(Instruction.SoundSource_OverrideFadeOut));
            _positionModeContent = new GUIContent("Position Mode", _instruction.GetText(Instruction.SoundSource_PositionMode));
            _overrideGroupContent = new GUIContent("Override Playback Group", _instruction.GetText(Instruction.SoundSource_OverridePlaybackGroup));
            _delayContent = new GUIContent("Delay", _instruction.GetText(Instruction.SoundSource_Delay));
            _isInit = true;
        }

        private SerializedProperty FindProperty(string path)
        {
            if (!_mainPropertyDict.TryGetValue(path, out var property))
            {
                property = serializedObject.FindProperty(path);
                _mainPropertyDict[path] = property;
            }
            return property;
        }

        public override void OnInspectorGUI()
        {
            if (!_isInit)
            {
                return;
            }
            var playOnEnableProp = FindProperty(PlayOnEnable);
            var stopOnDisableProp = FindProperty(StopOnDisable);
            var onlyOnceProp = FindProperty(OnlyPlayOnce);
            var fadeOutProp = FindProperty(OverrideFadeOut);
            var soundIDProp = FindProperty(SoundSource.NameOf.SoundID);
            var positionModeProp = FindProperty(PositionModeProperty);
            var overrideGroupProp = FindProperty(OverrideGroup);
            var delayProp = FindProperty(Delay);

            _currentDrawnPosY = 1f;

            DrawBackgroudWindow(3, _inspectorPadding, ref _currentDrawnPosY);
            DrawBoldToggle(playOnEnableProp, _inspectorPadding, _playOnEnableContent);
            using (new EditorGUI.IndentLevelScope(1))
            using (new EditorGUI.DisabledGroupScope(!playOnEnableProp.boolValue))
            {
                onlyOnceProp.boolValue = EditorGUILayout.Toggle(_onlyOnceContent, onlyOnceProp.boolValue);
                EditorGUILayout.BeginHorizontal();
                delayProp.floatValue = EditorGUILayout.FloatField(_delayContent, delayProp.floatValue);
                EditorGUILayout.LabelField(TimeUnit);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();
            _currentDrawnPosY += GUILayoutUtility.GetLastRect().height + EditorGUIUtility.standardVerticalSpacing;

            DrawBackgroudWindow(2, _inspectorPadding, ref _currentDrawnPosY);
            DrawBoldToggle(stopOnDisableProp, _inspectorPadding, _stopOnDisableContent);
            using (new EditorGUI.IndentLevelScope(1))
            using (new EditorGUI.DisabledGroupScope(!stopOnDisableProp.boolValue))
            using (new EditorGUILayout.HorizontalScope())
            {
                bool isOverrode = EditorGUILayout.Toggle(_fadeOutContent, fadeOutProp.floatValue >= 0f);
                
                EditorGUI.BeginDisabledGroup(!isOverrode);
                {
                    float tempFadeOut = Mathf.Max(fadeOutProp.floatValue, 0f);
                    tempFadeOut = EditorGUILayout.FloatField(GUIContent.none, tempFadeOut, GUILayout.Width(FadeOutFieldWidth));
                    fadeOutProp.floatValue = isOverrode && tempFadeOut >= 0f ? tempFadeOut : -1f;
                    EditorGUILayout.LabelField(TimeUnit);
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(soundIDProp);
            EditorGUILayout.PropertyField(positionModeProp, _positionModeContent);
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(overrideGroupProp, _overrideGroupContent);

            DrawOtherProperties();

            serializedObject.ApplyModifiedProperties();

            if(Application.isPlaying && target is SoundSource source)
            {
                EditorGUILayout.Space();
                AudioPlayer player = source.CurrentPlayer is AudioPlayerInstanceWrapper wrapper && wrapper.IsPlaying ? (AudioPlayer)wrapper : null;
                EditorGUI.BeginDisabledGroup(true);
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.ObjectField("Current Player", player, typeof(AudioPlayer), false);
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawOtherProperties()
        {
            var property = serializedObject.GetIterator();
            bool hasEntered = false;
            bool hasScriptChecked = false;

            while(property.NextVisible(!hasEntered))
            {
                hasEntered = true;

                if(!hasScriptChecked && property.name == "m_Script")
                {
                    hasScriptChecked = true;
                }
                else if(!_mainPropertyDict.ContainsKey(property.name))
                {
                    EditorGUILayout.PropertyField(property);
                }
            }
        }
    } 
}
