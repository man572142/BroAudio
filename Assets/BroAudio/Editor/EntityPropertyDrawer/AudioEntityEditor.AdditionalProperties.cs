using UnityEngine;
using UnityEditor;
using Ami.BroAudio.Data;
using Ami.Extension;
using Ami.BroAudio.Runtime;
using System;
using Ami.BroAudio.Tools;
using static Ami.Extension.EditorScriptingExtension;
using static Ami.BroAudio.Editor.EditorSetting;
using static Ami.BroAudio.Data.AudioEntity;
using static Ami.BroAudio.Editor.BroEditorUtility;
using static Ami.BroAudio.Editor.PropertyClipboardDataFactory;
using static Ami.Extension.FlagsExtension;

namespace Ami.BroAudio.Editor
{
    public partial class AudioEntityEditor
    {
        private const float RandomToolBarWidth = 40f;
        
        private readonly GUIContent _masterVolLabel = new GUIContent("Master Volume","Represent the master volume of all clips");
        private readonly GUIContent _loopingLabel = new GUIContent("Looping");
        private readonly GUIContent _changeClipPerLoopLabel = new GUIContent("Change Clip Per Loop", "Pick a new clip on every loop iteration");
        private readonly GUIContent _seamlessLabel = new GUIContent("Seamless Setting");
        private readonly GUIContent _pitchLabel = new GUIContent(nameof(AudioEntity.Pitch));
        private readonly GUIContent _spatialLabel = new GUIContent("Spatial & Mix Settings", "Configures 3D sound settings, Stereo Pan, Reverb Zone Mix, and other audio properties");
        private readonly float[] _seamlessSettingRectRatio = new float[] { 0.25f, 0.25f, 0.2f, 0.15f, 0.15f };

        private Rect[] _loopingRects;
        private Rect[] _seamlessRects;
        private SerializedProperty[] _loopingToggles = new SerializedProperty[2];

        private static int OverallDrawedPropStartIndex => DrawedPropertyConstant.OverallPropertyStartIndex;

        private float GetAdditionalBasePropertiesHeight(AudioTypeSetting setting)
        {
            var drawFlags = setting.DrawedProperty;
            ConvertUnityEverythingFlagsToAll(ref drawFlags);
            int intBits = 32;
            int lineCount = GetDrawingLineCount(drawFlags, OverallDrawedPropStartIndex, intBits, IsDefaultValue);
            var loopProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Loop));
            var seamlessProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.SeamlessLoop));
            if (seamlessProp.boolValue)
            {
                lineCount++;
            }
            var clipsProp = serializedObject.FindProperty(nameof(AudioEntity.Clips));
            if ((loopProp.boolValue || seamlessProp.boolValue) && clipsProp.arraySize > 1)
            {
                lineCount++;
            }

#if PACKAGE_ADDRESSABLES
            if (BroEditorUtility.EditorSetting.ShowEntityAddressableToggle)
            {
                lineCount++;
            }
#endif

            float offset = 0f;
            offset += IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.Priority) ? 0f : TwoSidesLabelOffsetY;
            offset += IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.MasterVolume) ? 0f : SnapVolumePadding;

            return lineCount * SingleLineSpace + offset;
        }

        private float GetAdditionalClipPropertiesHeight(AudioTypeSetting setting)
        {
            var drawFlags = setting.DrawedProperty;
            ConvertUnityEverythingFlagsToAll(ref drawFlags);
            int lineCount = GetDrawingLineCount(drawFlags, 0, OverallDrawedPropStartIndex - 1, IsDefaultValue);
            return lineCount * SingleLineSpace;
        }

        private void DrawAdditionalBaseProperties(Rect position, AudioTypeSetting setting)
        {
#if PACKAGE_ADDRESSABLES
            if (BroEditorUtility.EditorSetting.ShowEntityAddressableToggle)
            {
                DrawEntityAddressableProperty(position);
            }
#endif

            var drawFlags = setting.DrawedProperty;
            ConvertUnityEverythingFlagsToAll(ref drawFlags);
            DrawPlaybackGroup(drawFlags, position);
            DrawMasterVolume(drawFlags, position);
            DrawPitchProperty(drawFlags, position);
            DrawPriorityProperty(drawFlags, position);
            DrawLoopProperty(drawFlags, position);
            DrawSpatialSetting(drawFlags, position);
        }

        private void DrawEntityAddressableProperty(Rect position)
        {
            var entity = serializedObject.targetObject as AudioEntity;
            if (entity == null)
                return;

#if PACKAGE_ADDRESSABLES
            Offset -= SingleLineSpace * 0.5f; // closer to the top 
            Rect rect = GetRectAndIterateLine(position);

            var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorGUI.LabelField(rect, "Addressables not configured");
                return;
            }

            var addressableGUIContent = new GUIContent("Addressable Entity Asset",
                "Registers this entity asset (ScriptableObject) itself as an Addressables entry so your code can load it by address, " +
                "e.g. Addressables.LoadAssetAsync<AudioEntity>(address).");
            var addressableToggleSize = EditorStyles.toggle.CalcSize(addressableGUIContent);

            // Checkbox at the start
            var checkboxRect = new Rect(rect.x, rect.y, addressableToggleSize.x, rect.height);
            rect.xMin = checkboxRect.xMax + 5f;

            // Get asset path and entry
            string assetPath = AssetDatabase.GetAssetPath(entity);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var entry = settings.FindAssetEntry(guid);

            bool isAddressable = entry != null;

            EditorGUI.BeginChangeCheck();
            isAddressable = EditorGUI.ToggleLeft(checkboxRect, addressableGUIContent, isAddressable);

            if (EditorGUI.EndChangeCheck())
            {
                if (isAddressable)
                {
                    // Make addressable
                    entry = settings.CreateOrMoveEntry(guid, settings.DefaultGroup);
                    //entry.address = entity.name;
                    EditorUtility.SetDirty(settings);
                }
                else
                {
                    // Unset addressable
                    settings.RemoveAssetEntry(guid);
                    EditorUtility.SetDirty(settings);
                }
            }

            if (isAddressable)
            {
                EditorGUI.BeginChangeCheck();

                var groupRect = new Rect(rect);
                groupRect.xMin = (groupRect.xMax + groupRect.xMin) * 0.5f; // half the size
                groupRect.xMin = Mathf.Max(groupRect.xMax - 200f, groupRect.xMin); // max 200

                // Address field
                var addressRect = new Rect(rect);
                addressRect.xMax = groupRect.xMin - 5f;

                string newAddress = EditorGUI.DelayedTextField(addressRect, entry.address);

                var newGroup = EditorGUI.ObjectField(groupRect, entry.parentGroup, typeof(UnityEditor.AddressableAssets.Settings.AddressableAssetGroup), false) as UnityEditor.AddressableAssets.Settings.AddressableAssetGroup;

                if (EditorGUI.EndChangeCheck())
                {
                    if (newAddress != entry.address)
                        entry.address = newAddress;

                    if (newGroup != entry.parentGroup)
                        settings.MoveEntry(entry, newGroup);

                    EditorUtility.SetDirty(settings);
                }
            }
#endif
        }

        private void DrawPlaybackGroup(DrawedProperty drawFlags, Rect position)
        {
            if(IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.PlaybackGroup, out var groupProp, out _))
            {
                return;
            }

            Rect groupRect = GetRectAndIterateLine(position);
            groupRect.width *= DefaultFieldRatio;
            EditorGUI.PropertyField(groupRect, groupProp);
        }
        
        private void DrawMasterVolume(DrawedProperty drawFlags, Rect position)
        {
            if (IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.MasterVolume, out var masterVolProp, out var volRandProp))
            {
                return;
            }

            Offset += SnapVolumePadding;
            Rect masterVolRect = GetRectAndIterateLine(position);
            masterVolRect.width *= DefaultFieldRatio;

            Rect randButtonRect = new Rect(masterVolRect.xMax + 5f, masterVolRect.y, RandomToolBarWidth, masterVolRect.height);
            SerializedProperty randFlagsProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.RandomFlags));
            if (DrawRandomButton(randButtonRect, RandomFlag.Volume, randFlagsProp))
            {
                    
                float vol = masterVolProp.floatValue;
                float volRange = volRandProp.floatValue;

                Action<Rect> onDrawVU = null;
#if !UNITY_WEBGL
                if(BroEditorUtility.EditorSetting.ShowVUColorOnVolumeSlider)
                {
                    onDrawVU = sliderRect => DrawVUMeter(sliderRect, Setting.BroAudioGUISetting.VUMaskColor);
                }
#endif
                GetMixerMinMaxVolume(out float minVol, out float maxVol);
                DrawRandomRangeSlider(masterVolRect,_masterVolLabel,ref vol,ref volRange, minVol, maxVol, SliderType.BroVolume, onDrawVU);
                masterVolProp.floatValue = vol;
                volRandProp.floatValue = volRange;
            }
            else
            {
                SerializedProperty snapVolProp = serializedObject.FindProperty(EditorPropertyName.SnapToFullVolume);
                masterVolProp.floatValue = DrawVolumeSlider(masterVolRect, _masterVolLabel, masterVolProp.floatValue, snapVolProp.boolValue, () =>
                {
                    snapVolProp.boolValue = !snapVolProp.boolValue;
                });
            }

            int flag = randFlagsProp.intValue & (int)RandomFlag.Volume;
            var rndData = GetMasterVolumeData(masterVolProp.floatValue, volRandProp.floatValue, flag);
            PropertyClipboard.HandleClipboardContextMenu(masterVolRect, serializedObject, rndData, OnPasteMasterVolumeValues);
                
            static void OnPasteMasterVolumeValues(SerializedObject serializedObject, RandomizableData data)
            {
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.MasterVolume)).floatValue = data.Main;
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.VolumeRandomRange)).floatValue = data.RandomRange;
                var flagsProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.RandomFlags));
                int flags = flagsProp.intValue;
                OverwriteFlag(ref flags, (int)RandomFlag.Volume, data.RandomFlag);
                flagsProp.intValue = flags;
                serializedObject.ApplyModifiedProperties();
            }
        }
        
        private void DrawPitchProperty(DrawedProperty drawFlags, Rect position)
        {
            if (IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.Pitch, out var pitchProp, out var pitchRandProp))
            {
                return;
            }
                
            Rect pitchRect = GetRectAndIterateLine(position);
            pitchRect.width *= DefaultFieldRatio;

            float minPitch = AudioConstant.MinPlayablePitch;
            float maxPitch = AudioConstant.MaxAudioSourcePitch;

            Rect randButtonRect = new Rect(pitchRect.xMax + 5f, pitchRect.y, RandomToolBarWidth, pitchRect.height);
            SerializedProperty randFlagsProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.RandomFlags));
            bool hasRandom = DrawRandomButton(randButtonRect, RandomFlag.Pitch, randFlagsProp);

            float pitch = Mathf.Clamp(pitchProp.floatValue, minPitch, maxPitch);
            float pitchRange = pitchRandProp.floatValue;

            if (hasRandom)
            {
                DrawRandomRangeSlider(pitchRect, _pitchLabel, ref pitch, ref pitchRange, minPitch, maxPitch, SliderType.Linear);
            }
            else
            {
                pitch = EditorGUI.Slider(pitchRect, _pitchLabel, pitch, minPitch, maxPitch);
            }

            pitchProp.floatValue = pitch;
            pitchRandProp.floatValue = pitchRange;
                
            int flag = randFlagsProp.intValue & (int)RandomFlag.Pitch;
            var rndData = GetPitchData(pitchProp.floatValue, pitchRandProp.floatValue, flag);
            PropertyClipboard.HandleClipboardContextMenu(pitchRect, serializedObject, rndData, OnPastePitchValues);
                
            static void OnPastePitchValues(SerializedObject serializedObject, RandomizableData data)
            {
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Pitch)).floatValue = data.Main;
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.PitchRandomRange)).floatValue = data.RandomRange;
                var flagsProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.RandomFlags));
                int flags = flagsProp.intValue;
                OverwriteFlag(ref flags, (int)RandomFlag.Pitch, data.RandomFlag);
                flagsProp.intValue = flags;
                serializedObject.ApplyModifiedProperties();
            }
        }
        
        private void DrawPriorityProperty(DrawedProperty drawFlags, Rect position)
        {
            if (IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.Priority, out var priorityProp, out _))
            {
                return;
            }

            Rect priorityRect = GetRectAndIterateLine(position);
            priorityRect.width *= DefaultFieldRatio;
                
            EditorGUI.BeginProperty(priorityRect, GUIContent.none, priorityProp);
            MultiLabel multiLabels = new MultiLabel() { Main = nameof(AudioEntity.Priority), Left = "High", Right = "Low" };
            priorityProp.intValue = (int)Draw2SidesLabelSlider(priorityRect, multiLabels, priorityProp.intValue, AudioConstant.HighestPriority, AudioConstant.LowestPriority);
            Offset += TwoSidesLabelOffsetY;
            EditorGUI.EndProperty();
        }

        private void DrawLoopProperty(DrawedProperty drawFlags, Rect position)
        {
            if (IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.Loop, out var loopProp, out var seamlessProp))
            {
                return;
            }

            _loopingToggles[0] = loopProp;
            _loopingToggles[1] = seamlessProp;

            Rect loopRect = GetRectAndIterateLine(position);
            _loopingRects ??= new Rect[_loopingToggles.Length];
            _loopingRects[0] = new Rect(loopRect) { width = 100f, x = loopRect.x + EditorGUIUtility.labelWidth };
            _loopingRects[1] = new Rect(loopRect) { width = 200f, x = _loopingRects[0].xMax };
            DrawToggleGroup(loopRect, _loopingLabel, _loopingToggles, _loopingRects);

            Rect totalRect = loopRect;
            var data = GetLoopData(loopProp.boolValue, seamlessProp.boolValue);
            if (seamlessProp.boolValue)
            {
                DrawSeamlessSetting(position, ref data);
                totalRect.height += EditorGUIUtility.singleLineHeight;
            }

            PropertyClipboard.HandleClipboardContextMenu(totalRect, serializedObject, data, OnPasteLoopSettingValues);

            var clipsProp = serializedObject.FindProperty(nameof(AudioEntity.Clips));
            if ((loopProp.boolValue || seamlessProp.boolValue) && clipsProp.arraySize > 1)
            {
                SerializedProperty entityFlagsProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Flags));
                int entityFlags = entityFlagsProp.intValue;
                bool changeClipPerLoop = ((AudioEntityFlag)entityFlags).Contains(AudioEntityFlag.ChangeClipPerLoop);
                Rect changeClipRect = GetRectAndIterateLine(position);
                changeClipRect.xMin += EditorGUIUtility.labelWidth;
                EditorGUI.BeginChangeCheck();
                changeClipPerLoop = EditorGUI.ToggleLeft(changeClipRect, _changeClipPerLoopLabel, changeClipPerLoop);
                if (EditorGUI.EndChangeCheck())
                {
                    if (changeClipPerLoop)
                        AddFlag(ref entityFlags, (int)AudioEntityFlag.ChangeClipPerLoop);
                    else
                        RemoveFlag(ref entityFlags, (int)AudioEntityFlag.ChangeClipPerLoop);
                    entityFlagsProp.intValue = entityFlags;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            
            static void OnPasteLoopSettingValues(SerializedObject serializedObject, LoopData value)
            {
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Loop)).boolValue = value.Loop;
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.SeamlessLoop)).boolValue = value.SeamlessLoop;
                serializedObject.FindBackingFieldProperty(nameof(AudioEntity.TransitionTime)).floatValue = value.TransitionTime;
                serializedObject.FindProperty(EditorPropertyName.SeamlessType).intValue = (int)value.SeamlessTransitionType;
                var tempoProp = serializedObject.FindProperty(EditorPropertyName.TransitionTempo);
                tempoProp.FindPropertyRelative(nameof(TempoTransition.BPM)).floatValue = value.TransitionTempo.BPM;
                tempoProp.FindPropertyRelative(nameof(TempoTransition.Beats)).intValue = value.TransitionTempo.Beats;
                serializedObject.ApplyModifiedProperties();
            }
        }
        
        private void DrawSeamlessSetting(Rect totalPosition, ref LoopData data)
        {
            Rect suffixRect = EditorGUI.PrefixLabel(GetRectAndIterateLine(totalPosition), _seamlessLabel);
            _seamlessRects ??= new Rect[_seamlessSettingRectRatio.Length];
            SplitRectHorizontal(suffixRect, 10f, _seamlessRects, _seamlessSettingRectRatio);

            int drawIndex = 0;
            EditorGUI.LabelField(_seamlessRects[drawIndex], "Transition By");
            drawIndex++;

            var seamlessTypeProp = serializedObject.FindProperty(EditorPropertyName.SeamlessType);
            SeamlessType currentType = (SeamlessType)seamlessTypeProp.enumValueIndex;
            currentType = (SeamlessType)EditorGUI.EnumPopup(_seamlessRects[drawIndex], currentType);
            seamlessTypeProp.enumValueIndex = (int)currentType;
            data.SeamlessTransitionType = currentType;
            drawIndex++;

            var transitionTimeProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.TransitionTime));
            switch (currentType)
            {
                case SeamlessType.Time:
                    transitionTimeProp.floatValue = Mathf.Abs(EditorGUI.FloatField(_seamlessRects[drawIndex], transitionTimeProp.floatValue));
                    break;
                case SeamlessType.Tempo:
                    var tempoProp = serializedObject.FindProperty(EditorPropertyName.TransitionTempo);
                    var bpmProp = tempoProp.FindPropertyRelative(nameof(TempoTransition.BPM));
                    var beatsProp = tempoProp.FindPropertyRelative(nameof(TempoTransition.Beats));

                    SplitRectHorizontal(_seamlessRects[drawIndex], 0.5f, 2f, out var tempoValue, out var tempoLabel);
                    bpmProp.floatValue = Mathf.Abs(EditorGUI.FloatField(tempoValue, bpmProp.floatValue));
                    EditorGUI.LabelField(tempoLabel, "BPM");
                    drawIndex++;

                    SplitRectHorizontal(_seamlessRects[drawIndex], 0.5f, 2f, out var beatsValue, out var beatsLabel);
                    beatsProp.intValue = EditorGUI.IntField(beatsValue, beatsProp.intValue);
                    EditorGUI.LabelField(beatsLabel, "Beats");

                    data.TransitionTempo = new TempoTransition() { BPM = bpmProp.floatValue, Beats = beatsProp.intValue };
                    transitionTimeProp.floatValue = Mathf.Abs(AudioExtension.TempoToTime(bpmProp.floatValue, beatsProp.intValue));
                    break;
                case SeamlessType.ClipSetting:
                    transitionTimeProp.floatValue = FadeData.UseClipSetting;
                    break;
            }
            data.TransitionTime = transitionTimeProp.floatValue;
        }
        
        private void DrawSpatialSetting(DrawedProperty drawFlags, Rect position)
        {
            if (IsDefaultValueAndCanNotDraw(drawFlags, DrawedProperty.SpatialSettings, out var spatialProp, out _))
            {
                return;
            }

            Rect spatialRect = GetRectAndIterateLine(position);
            Rect suffixRect = EditorGUI.PrefixLabel(spatialRect, _spatialLabel);
            SplitRectHorizontal(suffixRect, 0.5f, 5f, out Rect objFieldRect, out Rect buttonRect);
            EditorGUI.BeginProperty(spatialRect, _spatialLabel, spatialProp);
            EditorGUI.ObjectField(objFieldRect, spatialProp, GUIContent.none);
            EditorGUI.EndProperty();
            bool hasSetting = spatialProp.objectReferenceValue != null;
            string buttonLabel = hasSetting ? "Open Panel" : "Create And Open";
            if (GUI.Button(buttonRect, buttonLabel))
            {
                if (!hasSetting)
                {
                    string path = EditorUtility.SaveFilePanelInProject("Save Spatial Setting", target.name + "_Spatial", "asset", "Message");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var newSetting = ScriptableObject.CreateInstance<SpatialSetting>();
                        AssetDatabase.CreateAsset(newSetting, path);
                        spatialProp.objectReferenceValue = newSetting;
                        spatialProp.serializedObject.ApplyModifiedProperties();

                        SpatialSettingsEditorWindow.ShowWindow(spatialProp);
                        GUIUtility.ExitGUI();
                        AssetDatabase.SaveAssets();
                    }
                }
                else
                {
                    SpatialSettingsEditorWindow.ShowWindow(spatialProp);
                    GUIUtility.ExitGUI();
                }
            }
        }

        private bool IsDefaultValue(DrawedProperty drawedProperty)
        {
            return IsDefaultValue(drawedProperty, out _, out _);
        }

        private bool IsDefaultValue(DrawedProperty drawedProperty, out SerializedProperty mainProp, out SerializedProperty secondaryProp)
        {
            mainProp = null;
            secondaryProp = null;
            switch (drawedProperty)
            {
                case DrawedProperty.MasterVolume:
                    mainProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.MasterVolume));
                    secondaryProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.VolumeRandomRange));
                    return Mathf.Approximately(mainProp.floatValue, AudioConstant.FullVolume) && secondaryProp.floatValue == 0f;
                case DrawedProperty.Loop:
                    mainProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Loop));
                    secondaryProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.SeamlessLoop));
                    return !mainProp.boolValue && !secondaryProp.boolValue;
                case DrawedProperty.Priority:
                    mainProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Priority));
                    return mainProp.intValue == AudioConstant.DefaultPriority;
                case DrawedProperty.SpatialSettings:
                    mainProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.SpatialSetting));
                    return mainProp.objectReferenceValue == null;
                case DrawedProperty.Pitch:
                    mainProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.Pitch));
                    secondaryProp = serializedObject.FindBackingFieldProperty(nameof(AudioEntity.PitchRandomRange));
                    return Mathf.Approximately(mainProp.floatValue, AudioConstant.DefaultPitch) && secondaryProp.floatValue == 0f;
                case DrawedProperty.PlaybackGroup:
                    mainProp = serializedObject.FindProperty(EditorPropertyName.PlaybackGroup);
                    return mainProp.objectReferenceValue == null;
            }
            return true;
        }

        private bool IsDefaultValueAndCanNotDraw(DrawedProperty drawFlags, DrawedProperty drawTarget)
        {
            return IsDefaultValueAndCanNotDraw(drawFlags, drawTarget, out _, out _);
        }

        private bool IsDefaultValueAndCanNotDraw(DrawedProperty drawFlags, DrawedProperty drawTarget, out SerializedProperty mainProp, out SerializedProperty secondaryProp)
        {
            return IsDefaultValue(drawTarget, out mainProp, out secondaryProp) && !drawFlags.Contains(drawTarget);
        }

        private bool DrawRandomButton(Rect rect, RandomFlag targetFlag, SerializedProperty property)
        {
            RandomFlag randomFlags = (RandomFlag)property.intValue;
            bool hasRandom = randomFlags.Contains(targetFlag);
            hasRandom = GUI.Toggle(rect, hasRandom, "RND", EditorStyles.miniButton);
            randomFlags = hasRandom ? randomFlags | targetFlag : randomFlags & ~targetFlag;
            property.intValue = (int)randomFlags;
            return hasRandom;
        }
        
        private static int GetDrawingLineCount(DrawedProperty flags, int startIndex, int lastIndex, Func<DrawedProperty, bool> onGetIsDefaultValue)
        {
            int count = 0;
            for (int i = startIndex; i <= lastIndex; i++)
            {
                int drawFlag = (1 << i);
                if(drawFlag > (int)DrawedProperty.All)
                {
                    break;
                }
                if(!DrawedProperty.All.Contains((DrawedProperty)drawFlag))
                {
                    continue;
                }
                
                bool canDraw = ((int)flags & drawFlag) != 0;
                if (canDraw || !onGetIsDefaultValue.Invoke((DrawedProperty)drawFlag))
                {
                    count++;
                }
            }
            return count;
        }

        private static void ConvertUnityEverythingFlagsToAll(ref DrawedProperty drawedProperty)
        {
            if((int)drawedProperty == -1)
            {
                drawedProperty = DrawedProperty.All;
            }
        }
    }
}
