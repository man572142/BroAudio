using UnityEngine;
using UnityEditor;
using Ami.Extension;
using System;
using System.IO;
using static Ami.BroAudio.Editor.Setting.BroAudioGUISetting;
using static Ami.Extension.EditorScriptingExtension;
using Ami.BroAudio.Tools;
using Ami.BroAudio.Data;

namespace Ami.BroAudio.Editor
{
	public class ClipEditorWindow : EditorWindow, IHasCustomMenu
	{
        private const string SaveFilePanelTitle = "Save as a new file";
        private const string ConfirmOverwriteTitle = "Confirm overwrite";
        private const string PrefKey = "LastClipEditorSavePath";
        private const float Gap = 50f;
        private const string DefaultFileExt = "wav";
        private const string LossyFormatHintLabel = "Note on .ogg / .mp3 build size";
        private static Vector2 DefaultWindowSize => new Vector2(640f, 490f);

        // The serialization is used for Undo/Redo 
        [SerializeField] private BroAudioClip _clip;
        [SerializeField] private bool _isReverse;
        [SerializeField] private bool _isMono;
        [SerializeField] private MonoConversionMode _monoMode = MonoConversionMode.Downmixing;
        private DrawClipPropertiesHelper _clipPropHelper = new DrawClipPropertiesHelper();
		private SerializedTransport _transport;		
		private bool _isVolumeSnapped;
		private GenericMenu _monoModeMenu;
		private bool _isLoop;
		private bool _isPlaying;
        private bool _isShowPreferences;
        private Vector2 _scrollPos;
        private bool _isShowLossyFormatHint;
        private SerializedObject _serializedObject;
        private SerializedObject _editorSettingSO;
        private SerializedProperty _audioClipProp;
        private SerializedProperty _clipProp;

        private string _currSavingFilePath;
        private PreviewRequest _currentPreviewRequest;
		private readonly BroInstructionHelper _instruction = new BroInstructionHelper();
        
        private bool HasEdited
		{
			get
			{
				return !Mathf.Approximately(_clip.Volume, AudioConstant.FullVolume)
					|| _transport.HasDifferentPosition
					|| _transport.HasFading
					|| _isReverse
					|| _isMono;
			}
		}

        private AudioClip TargetClip
		{
			get => _audioClipProp.objectReferenceValue as AudioClip;
            set
			{
                if (value != _audioClipProp.objectReferenceValue)
                {
                    ResetSettings();
                    _audioClipProp.objectReferenceValue = value;
                    _audioClipProp.serializedObject.ApplyModifiedProperties();
                }
			}
		}

		[MenuItem(ClipEditorMenuPath,false,ClipEditorMenuIndex)]
		[MenuItem(ClipEditorMenuPath_Window,false,ClipEditorMenuIndex)]
		public static void ShowWindow()
		{
			EditorWindow window = GetWindow(typeof(ClipEditorWindow));
			window.minSize = DefaultWindowSize;
			window.titleContent = new GUIContent(BroName.MenuItem_ClipEditor, EditorGUIUtility.IconContent(IconConstant.AudioClip).image);
			window.Show();
		}

		private void OnEnable()
		{
            Undo.undoRedoPerformed += Repaint;

            _serializedObject = new SerializedObject(this);
            _clipProp = _serializedObject.FindProperty(nameof(_clip));
            _audioClipProp = _clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClip);

			ResetSettings();
		}

        private void OnDisable()
		{
            Undo.undoRedoPerformed -= Repaint;
        }

        private void OnFocus()
        {
            EditorAudioPreviewer.Instance.OnPlaybackIndicatorUpdate += Repaint;
        }

        private void OnLostFocus()
		{
            EditorAudioPreviewer.Instance.OnPlaybackIndicatorUpdate -= Repaint;
            EditorAudioPreviewer.Instance.StopAllClips();
			_isPlaying = false;

        }

		private void OnGUI()
		{
            _serializedObject.Update();
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.8f));
			DrawAudioClipObjectField();
            EditorGUILayout.Space();

			if (TargetClip == null || _transport == null)
			{
                TargetClip = null;
                EditorGUILayout.Space(position.height * 0.3f);
                EditorGUILayout.LabelField("No Audio Clip".SetSize(30).SetColor(Color.white), GUIStyleHelper.MiddleCenterRichText);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndScrollView();
                return;
			}

            EditorGUILayout.Space();
			DrawClipWaveform(position.height * 0.3f, out Rect previewRect);
            EditorGUILayout.Space();

            DrawPlaybackBar(previewRect);
            EditorGUILayout.Space();
            EditorGUILayout.Space();
            DrawVolumeSlider();
            DrawPlaybackPositionField();
            DrawFadingField();

            var isReverseProp = _serializedObject.FindProperty(nameof(_isReverse));
            var isMonoProp = _serializedObject.FindProperty(nameof(_isMono));
            isReverseProp.boolValue = EditorGUILayout.Toggle("Reverse", isReverseProp.boolValue);

            using (new EditorGUI.DisabledScope(TargetClip.channels != 2))
            {
                isMonoProp.boolValue = EditorGUILayout.Toggle("Convert To Mono", isMonoProp.boolValue);
                using (new EditorGUI.DisabledScope(!_isMono))
                {
                    Rect monoModeRect = GUILayoutUtility.GetLastRect().DissolveHorizontal(0.5f);
                    if (TargetClip.channels > 2)
                    {
                        EditorGUI.LabelField(monoModeRect, "Surround sound is not supported");
                    }
                    else if (EditorGUI.DropdownButton(monoModeRect, new GUIContent(_monoMode.ToString()), FocusType.Keyboard))
                    {
                        ShowMonoModeMenu(monoModeRect);
                    }
                }
            }

            EditorGUILayout.Space(EditorGUIUtility.singleLineHeight);
            _isShowPreferences = EditorGUILayout.BeginFoldoutHeaderGroup(_isShowPreferences, "Preferences");
            if (_isShowPreferences)
            {
                using (new EditorGUI.IndentLevelScope(1))
                {
                    GetPreferenceProperties(out var editNewClipOptionProp, out var pingNewClipOptionProp);
                    editNewClipOptionProp.boolValue = EditorGUILayout.ToggleLeft("Edit The New Clip After [Save As]", editNewClipOptionProp.boolValue);
                    pingNewClipOptionProp.boolValue = EditorGUILayout.ToggleLeft("Show The New Clip In Project Window After [Save As]", pingNewClipOptionProp.boolValue);
                    _editorSettingSO.ApplyModifiedProperties();
                }               
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            DrawLossySourceFormatHint();
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(!HasEdited);
            {
                DrawSavingButton();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            _serializedObject.ApplyModifiedProperties();

            if (_isPlaying)
            {
                EditorAudioPreviewer.Instance.UpdatePreview();
                EditorAudioPreviewer.Instance.PlaybackIndicator?.Draw();
            }
            EditorGUILayout.EndScrollView();
        }

        private void GetPreferenceProperties(out SerializedProperty editNewClipOptionProp, out SerializedProperty pingNewClipOptionProp)
        {
            _editorSettingSO ??= new SerializedObject(BroEditorUtility.EditorSetting);
            _editorSettingSO.Update();

            editNewClipOptionProp = _editorSettingSO.FindProperty(nameof(EditorSetting.EditTheNewClipAfterSaveAs));
            pingNewClipOptionProp = _editorSettingSO.FindProperty(nameof(EditorSetting.PingTheNewClipAfterSaveAs));
        }

		private void ShowMonoModeMenu(Rect rect)
        {
			if(_monoModeMenu == null)
			{
                _monoModeMenu = new GenericMenu();
                _monoModeMenu.AddDisabledItem(new GUIContent("Mix all channels into one"));
                AddModeItem(MonoConversionMode.Downmixing);
                _monoModeMenu.AddSeparator(string.Empty);

                _monoModeMenu.AddDisabledItem(new GUIContent("Select one channel"));
                AddModeItem(MonoConversionMode.Left);
                AddModeItem(MonoConversionMode.Right);
            }

			_monoModeMenu.DropDown(rect);

            void AddModeItem(MonoConversionMode mode)
			{
                _monoModeMenu.AddItem(new GUIContent(mode.ToString()), false, OnChangeMonoMode, mode);
            }
        }

        private void OnChangeMonoMode(object userData)
        {
            if(userData is MonoConversionMode mode)
			{
                var monoModeProp = _serializedObject.FindProperty(nameof(_monoMode));
                monoModeProp.enumValueIndex = (int)mode;
                _serializedObject.ApplyModifiedProperties();
			}
        }

        private void DrawVolumeSlider()
		{
            Rect rect = EditorGUILayout.GetControlRect();
            var volProp = _clipProp.FindPropertyRelative(nameof(BroAudioClip.Volume));
            volProp.floatValue = BroEditorUtility.DrawVolumeSlider(rect, new GUIContent("Volume"), volProp.floatValue, _isVolumeSnapped, () => _isVolumeSnapped = !_isVolumeSnapped);
		}

		private void DrawAudioClipObjectField()
		{
			EditorGUI.BeginChangeCheck();
			var newClip = EditorGUILayout.ObjectField("Audio Clip", TargetClip, typeof(AudioClip), false) as AudioClip;
			if(EditorGUI.EndChangeCheck() && newClip)
            {
                TargetClip = newClip;
                var clipProp = _serializedObject.FindProperty(nameof(_clip));
                _transport = new SerializedTransport(clipProp, TargetClip.length);
			}
		}

		private void DrawClipWaveform(float height, out Rect waveformRect)
		{
            waveformRect = EditorGUILayout.GetControlRect(GUILayout.Height(height));
            waveformRect.width = position.width * 0.95f;
            waveformRect.x = (position.width - waveformRect.width) * 0.5f;
            _clipPropHelper.DrawClipWaveformAndVisualEditing(waveformRect, _transport, TargetClip, TargetClip.name, PlayClipByWaveform);                                                                                                                               //DrawEmptyLine(GetLineCountByPixels(height));
		}

		private void DrawPlaybackPositionField()
		{
            Rect rect = EditorGUILayout.GetControlRect();
			_clipPropHelper.DrawPlaybackPositionField(rect, _transport);
		}

		private void DrawFadingField()
		{
            Rect rect = EditorGUILayout.GetControlRect();
            _clipPropHelper.DrawFadingField(rect, _transport);
		}

		private void DrawPlaybackBar(Rect previewRect)
		{
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
			var width = GUILayout.Width(EditorGUIUtility.singleLineHeight * 2);
            var height = GUILayout.Height(EditorGUIUtility.singleLineHeight * 2);
            string icon = _isPlaying ? IconConstant.StopButton : IconConstant.PlayButton;
			GUIContent playButtonContent = new GUIContent(EditorGUIUtility.IconContent(icon).image, EditorAudioPreviewer.IgnoreSettingTooltip);
			if (GUILayout.Button(playButtonContent, width, height))
			{
				if(_isPlaying)
				{
					EditorAudioPreviewer.Instance.StopAllClips();
				}
				else if(_audioClipProp.objectReferenceValue is AudioClip clip)
                {
                    var evt = Event.current;
                    float volume = evt.button == 0 ? _clipProp.FindPropertyRelative(nameof(_clip.Volume)).floatValue : AudioConstant.FullVolume;
                    _currentPreviewRequest = evt.CreatePreviewRequest(clip, volume, _transport);
                    PlayClip(_currentPreviewRequest, _isLoop ? new ReplayRequest(_clip) : null);
                    EditorAudioPreviewer.Instance.PlaybackIndicator.SetClipInfo(previewRect, _currentPreviewRequest);
				}
			}
			_isLoop = DrawButtonToggleLayout(_isLoop, EditorGUIUtility.IconContent(IconConstant.LoopIcon), width, height);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
		}

        private void PlayClip(PreviewRequest req, ReplayRequest replayRequest = null)
        {
            EditorAudioPreviewer.Instance.Play(req, replayRequest);
            EditorAudioPreviewer.Instance.OnFinished = () =>
            {
                _isPlaying = false;
                _currentPreviewRequest = null;
            };
            _isPlaying = true;
        }
        
        private void PlayClipByWaveform(string clipPath, PreviewRequest req)
        {
            req.ClipVolume = _clipProp.FindPropertyRelative(nameof(_clip.Volume)).floatValue;
            PlayClip(req);
        }

		// The result is always written as wav. Reassure the user that this is the better outcome: Unity
		// re-encodes every audio asset at build time anyway, so CopyImportSettings keeps the build size
		// unchanged, and not re-compressing here saves a generation of lossy encoding.
		private void DrawLossySourceFormatHint()
		{
			string extension = Path.GetExtension(AssetDatabase.GetAssetPath(TargetClip)).ToLowerInvariant();
			if (extension != ".ogg" && extension != ".mp3")
			{
				return;
			}

			_isShowLossyFormatHint = EditorGUILayout.BeginFoldoutHeaderGroup(_isShowLossyFormatHint, LossyFormatHintLabel);
			if (_isShowLossyFormatHint)
			{
				RichTextHelpBox(_instruction.GetText(Instruction.ClipEditorLossySourceFormat), MessageType.Info);
			}
			EditorGUILayout.EndFoldoutHeaderGroup();
		}

		private void DrawSavingButton()
		{
            Rect savingZoneRect = EditorGUILayout.GetControlRect(GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
            savingZoneRect.x += Gap * 2;
            savingZoneRect.width -= Gap * 4;

            SplitRectHorizontal(savingZoneRect, 0.5f, Gap, out Rect saveButton, out Rect saveAsButton);

			if (GUI.Button(saveButton, new GUIContent("Save")))
			{
				string path = AssetDatabase.GetAssetPath(TargetClip);
				string fullFilePath = BroEditorUtility.GetFullPath(path);

				if (File.Exists(fullFilePath) && EditorUtility.DisplayDialog(ConfirmOverwriteTitle, _instruction.GetText(Instruction.ClipEditorConfirmationDialog), "Yes","No"))
				{
					SaveClip(path);
				}
			}
			
			if(GUI.Button(saveAsButton,new GUIContent("Save As")))
			{
                string lastSavePath = SessionState.GetString(PrefKey, string.Empty);
                string assetPath = AssetDatabase.GetAssetPath(TargetClip);
                string assetDirectory = Path.GetDirectoryName(assetPath);
                lastSavePath = string.IsNullOrEmpty(lastSavePath) ? assetDirectory : lastSavePath;
				string newPath = EditorUtility.SaveFilePanelInProject(SaveFilePanelTitle, TargetClip.name, DefaultFileExt, SaveFilePanelTitle, lastSavePath);
				if(!string.IsNullOrEmpty(newPath))
				{
                    SessionState.SetString(PrefKey, newPath);
					SaveClip(newPath);
				}
			}
		}

		private void SaveClip(string savePath)
        {
            using var helper = new AudioClipEditingHelper(TargetClip);
            if (_transport.StartPosition != 0f || _transport.EndPosition != 0f)
            {
                helper.Trim(_transport.StartPosition, _transport.EndPosition);
            }

            if (_isMono)
            {
                helper.ConvertToMono(_monoMode);
            }

            if (_transport.Delay > 0f)
            {
                helper.AddSlient(_transport.Delay);
            }

            var volume = _clipProp.FindPropertyRelative(nameof(_clip.Volume)).floatValue;
			if(!Mathf.Approximately(volume, AudioConstant.FullVolume))
			{
				helper.AdjustVolume(volume);
			}

            if(_transport.FadeIn != 0f)
            {
                helper.FadeIn(_transport.FadeIn);
            }

            if(_transport.FadeOut != 0f)
            {
                helper.FadeOut(_transport.FadeOut);
            }

            if(_isReverse)
            {
                helper.Reverse();
            }

            if (HasEdited)
            {
                string sourcePath = AssetDatabase.GetAssetPath(TargetClip);
                SavWav.Save(savePath, helper.GetResultClip());
                _currSavingFilePath = savePath;
                AssetDatabase.Refresh();
                CopyImportSettings(sourcePath, savePath);
            }
        }

        // The saved file is always wav, but the build size is decided by the AudioImporter, not the file format.
        // Carrying the original settings over keeps the compressed size the same as the source clip.
        // ponytail: default settings only. Copy GetOverrideSampleSettings per BuildTarget if platform overrides matter.
        private static void CopyImportSettings(string sourcePath, string destPath)
        {
            if (sourcePath == destPath
                || !(AssetImporter.GetAtPath(sourcePath) is AudioImporter source)
                || !(AssetImporter.GetAtPath(destPath) is AudioImporter dest))
            {
                return;
            }

            dest.defaultSampleSettings = source.defaultSampleSettings;
            dest.forceToMono = source.forceToMono;
            dest.loadInBackground = source.loadInBackground;
            dest.ambisonic = source.ambisonic;
            dest.SaveAndReimport();
        }

		public void OnPostprocessAllAssets()
        {
            if (string.IsNullOrEmpty(_currSavingFilePath))
            {
                return;
            }
            
            AudioClip newClip = AssetDatabase.LoadAssetAtPath(_currSavingFilePath, typeof(AudioClip)) as AudioClip;
            bool isSaveAs = _currSavingFilePath != AssetDatabase.GetAssetPath(TargetClip);
            if (isSaveAs)
            {
                GetPreferenceProperties(out var editNewClipOptionProp, out var pingNewClipOptionProp);
                if (editNewClipOptionProp.boolValue)
                {
                    TargetClip = newClip;
                    ResetSettings();
                }

                if (pingNewClipOptionProp.boolValue)
                {
                    EditorGUIUtility.PingObject(newClip);
                }
            }
            else
            {
                TargetClip = newClip;
                ResetSettings();
            }         
        }

        private void ResetSettings()
		{
			_currSavingFilePath = null;
            _clip = null;
            _isReverse = false;
            _isMono = false;
            _monoMode = MonoConversionMode.Downmixing;
            
            _serializedObject = new SerializedObject(this);
            _clipProp = _serializedObject.FindProperty(nameof(_clip));
            _transport = TargetClip ? new SerializedTransport(_clipProp, TargetClip.length) : null;
        }
        
		public void AddItemsToMenu(GenericMenu menu)
		{
			menu.AddSeparator(string.Empty);
			menu.AddItem(new GUIContent("Default Window Size"), false, () => position = new Rect(position.position, DefaultWindowSize));
		}
	}
}