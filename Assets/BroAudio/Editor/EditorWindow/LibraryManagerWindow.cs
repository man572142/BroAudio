using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System;
using System.Linq;
using Ami.Extension;
using Ami.BroAudio.Data;
using Ami.BroAudio.Tools;
using System.IO;
using static Ami.BroAudio.Editor.BroEditorUtility;
using static Ami.BroAudio.Editor.Setting.BroAudioGUISetting;
using static Ami.Extension.EditorScriptingExtension;
using static Ami.BroAudio.Editor.Setting.PreferencesEditorWindow;

namespace Ami.BroAudio.Editor
{
    public partial class LibraryManagerWindow : EditorWindow, IHasCustomMenu
    {
        private const int AssetNameFontSize = 16;
        private const float BackButtonSize = 28f;
        private const float EntitiesFactoryRatio = 0.65f;
        private const string ModifyAssetKey = "ModifyAsset";
        private const string DragAndDropKey = "DragAndDrop";

        public static event Action OnCloseLibraryManagerWindow;
        public static event Action OnLostFocusEvent;
        public static event Action OnSelectAsset;

        private ReorderableList _assetList;
        private ReorderableList assetList
        {
            get
            {
                if (_assetList != null)
                {
                    return _assetList;
                }

                RebuildList();

                return _assetList;
            }
        }

        private readonly GapDrawingHelper _verticalGapDrawer = new GapDrawingHelper();
        private readonly BroInstructionHelper _instruction = new BroInstructionHelper();
        //private readonly EditorFlashingHelper _flasingHelper = new EditorFlashingHelper(Color.white, 1f, Ease.InCubic);
        private readonly Dictionary<string, string> _instructionHelpBoxKeys = new Dictionary<string, string>();

        private int _currSelectedAssetIndex = -1;
        private bool _isInEntitiesEditMode;
        private bool _hasOutputAssetPath;
        private bool _showSettings;

        private Vector2 _assetListScrollPos = Vector2.zero;
        private Vector2 _entitiesScrollPos = Vector2.zero;

        private static Vector2 EntitiesHeaderSize => new Vector2(200f, EditorGUIUtility.singleLineHeight * 2);
        private static float DefaultLayoutPadding => GUI.skin.box.padding.top;

        private EditorSetting EditorSetting => BroEditorUtility.EditorSetting;

        [MenuItem(LibraryManagerMenuPath, false, LibraryManagerMenuIndex)]
        [MenuItem(LibraryManagerMenuPath_Window, false, LibraryManagerMenuIndex)]
        public static LibraryManagerWindow ShowWindow()
        {
            EditorWindow window = GetWindow(typeof(LibraryManagerWindow));
            Texture titleIcon = Resources.Load<Texture>(MainLogoPath);
            window.titleContent = new GUIContent(BroName.MenuItem_LibraryManager, titleIcon);
            window.Show();
            return window as LibraryManagerWindow;
        }

        public static void ShowWindowAndLocateToEntity(AudioAsset asset, AudioEntity entity)
        {
            var window = ShowWindow();
            window.SelectAsset(asset);
            window.SelectEntity(asset, entity);
        }

        public void RebuildList()
        {
            List<AudioAsset> assets = new List<AudioAsset>();
            GetAudioAssets(assets);

            List<AudioAssetEditor> editors = new List<AudioAssetEditor>(assets.Count);

            if (_assetList != null) // Some logic to retain the current sorting
            {
                foreach (AudioAssetEditor editor in _assetList.list)
                {
                    var asset = editor.target as AudioAsset;
                    var assetListIndex = assets.IndexOf(asset);

                    if (assetListIndex == -1)
                    {
                        continue; // Not included in the list
                    }

                    assets.RemoveAt(assetListIndex);
                    editors.Add(editor);
                }
            }

            foreach (var asset in assets)
            {
                AudioAssetEditor editor = (AudioAssetEditor)UnityEditor.Editor.CreateEditor(asset, typeof(AudioAssetEditor));
                editors.Add(editor);
            }

            _assetList = new ReorderableList(editors, typeof(AudioAssetEditor))
            {
                drawHeaderCallback = OnDrawHeader,
                onAddCallback = OnAdd,
                onRemoveCallback = OnRemove,
                drawElementCallback = OnDrawElement,
                onSelectCallback = OnSelect,
                draggable = false
            };

            if (_currSelectedAssetIndex >= 0 && _currSelectedAssetIndex < _assetList.count)
            {
                _assetList.index = _currSelectedAssetIndex;
            }
            else
            {
                _currSelectedAssetIndex = -1;
            }

            void OnDrawHeader(Rect rect)
            {
                EditorGUI.LabelField(rect, "Asset List");
                HandleDragAndDropToAssetListHeader(rect);
            }

            void OnAdd(ReorderableList list)
            {
                ShowCreateAssetAskName();
                GUIUtility.ExitGUI();
            }

            void OnRemove(ReorderableList list)
            {
                var assetEditor = list.list[list.index] as AudioAssetEditor;
                var asset = assetEditor.target as AudioAsset;
                var choice = EditorUtility.DisplayDialogComplex(
                    "Delete Audio Asset?",
                    "Are you sure you want to delete the audio asset [" + asset.AssetName + "]?",
                    "Yes, and all sounds inside", "No", "Yes, but leave the sounds alone");

                if (choice == 1)
                {
                    return;
                }

                try
                {
                    AssetDatabase.StartAssetEditing();

                    if (choice == 0)
                    {
                        // delete the entities
                        List<AudioEntity> entities = new List<AudioEntity>();
                        GetAudioEntities(entities, asset);

                        for (int i = entities.Count - 1; i >= 0; i--)
                        {
                            AudioEntity entity = entities[i];

                            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(entity));
                        }
                    }

                    string path = AssetDatabase.GetAssetPath(asset);
                    AssetDatabase.DeleteAsset(path);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                AssetDatabase.Refresh();
                RebuildList();
            }

            void OnDrawElement(Rect rect, int index, bool isActive, bool isFocused)
            {
                var assetEditor = _assetList.list[index] as AudioAssetEditor;
                if (assetEditor == null || assetEditor.Asset == null)
                {
                    return;
                }
                var asset = assetEditor.Asset;

                EditorGUI.LabelField(rect, asset.AssetName, GUIStyleHelper.RichText);
                HandleDragAndDropToAsset(rect, assetEditor);

                if (index == _currSelectedAssetIndex && Event.current.isMouse && Event.current.clickCount >= 2)
                {
                    _isInEntitiesEditMode = true;
                }

                HandleContextMenu(rect, asset as AudioAsset);
            }
        }

        public void SelectAsset(AudioAsset asset)
        {
            for (int i = 0, count = assetList.list.Count; i < count; i++)
            {
                if (assetList.list[i] is AudioAssetEditor editor && editor.target == asset)
                {
                    assetList.index = i;
                    OnSelect(assetList);
                    return;
                }
            }
        }

        private void SelectEntity(AudioAsset asset, AudioEntity entity)
        {
            _isInEntitiesEditMode = true;

            for (int i = 0, count = assetList.list.Count; i < count; i++)
            {
                if (assetList.list[i] is AudioAssetEditor editor && editor.target == asset)
                {
                    editor.SelectEntity(entity, out float entityPos);
                    entityPos += EntitiesHeaderSize.y + ReorderableList.Defaults.padding + DefaultLayoutPadding;
                    _entitiesScrollPos = new Vector2(_entitiesScrollPos.x, entityPos);
                    return;
                }
            }
        }

        private void OnFocus()
        {
            EditorAudioPreviewer.Instance.OnPlaybackIndicatorUpdate += Repaint;
        }

        private void OnLostFocus()
        {
            EditorAudioPreviewer.Instance.StopAllClips();
            EditorAudioPreviewer.Instance.OnPlaybackIndicatorUpdate -= Repaint;
            OnLostFocusEvent?.Invoke();
        }

        private void OnEnable()
        {
            _hasOutputAssetPath = Directory.Exists(EditorSetting.AssetOutputPath);

            RefreshAssetEditors(assetList);

            Undo.undoRedoPerformed += Repaint;
            AudioEntityEditor.OnExpandAll += ResetEntitiesScrollPos;

            if (EditorSetting.OpenLastEditAudioAsset && !string.IsNullOrEmpty(EditorSetting.LastEditAudioAsset))
            {
                foreach (AudioAssetEditor editor in assetList.list)
                {
                    if (AssetDatabase.GetAssetPath(editor.target) == EditorSetting.LastEditAudioAsset)
                    {
                        SelectAsset(editor.target as AudioAsset);
                        _isInEntitiesEditMode = true;
                        break;
                    }
                }
            }

            InitBackgroundLogo();
        }

        private void OnDisable()
        {
            OnCloseLibraryManagerWindow?.Invoke();

            if (_assetList != null)
            {
                foreach (AudioAssetEditor editor in _assetList.list)
                {
                    DestroyImmediate(editor);
                }
            }
            Undo.undoRedoPerformed -= Repaint;
            AudioEntityEditor.OnExpandAll -= ResetEntitiesScrollPos;

            OnCloseLibraryManagerWindow = null;
            OnSelectAsset = null;
            OnLostFocusEvent = null;
        }

        #region Initialization
        private void HandleDragAndDropToAssetListHeader(Rect rect)
        {
            if (!rect.Contains(Event.current.mousePosition))
            {
                return;
            }

            var assets = DragAndDrop.objectReferences.OfType<AudioAsset>().ToList();
            if (!assets.Any())
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
            if (Event.current.type == EventType.DragPerform)
            {
                foreach (var asset in assets)
                {
                    bool alreadyExists = false;
                    foreach (AudioAssetEditor existing in _assetList.list)
                    {
                        if (existing.target == asset)
                        {
                            alreadyExists = true;
                            break;
                        }
                    }

                    if (!alreadyExists)
                    {
                        AddOrMoveToEnd(asset);
                    }
                }
                DragAndDrop.AcceptDrag();
                Repaint();
            }
        }

        private void HandleDragAndDropToAsset(Rect rect, AudioAssetEditor editor)
        {
            if (!rect.Contains(Event.current.mousePosition))
            {
                return;
            }

            if (Event.current.type == EventType.DragPerform)
            {
                var entities = DragAndDrop.objectReferences.OfType<AudioEntity>()
                    .Where(e => e.AudioAsset != (AudioAsset)editor.Asset)
                    .ToList();
                if (entities.Any())
                {
                    foreach (var entity in entities)
                    {
                        editor.AssignExistingEntity(entity);
                    }
                    DragAndDrop.AcceptDrag();
                    AssetDatabase.SaveAssets();
                    SelectAsset(editor.Asset as AudioAsset);
                    _isInEntitiesEditMode = true;
                    return;
                }

                var clips = GetAudioClipsFromDragAndDrop();
                if (clips.Any())
                {
                    ProcessDraggingClips(editor, clips);
                }
            }
        }

        private void HandleDragAndDropEntitiesToEntityList(AudioAssetEditor editor)
        {
            var entities = DragAndDrop.objectReferences.OfType<AudioEntity>()
                .Where(e => e.AudioAsset != (AudioAsset)editor.Asset)
                .ToList();

            if (!entities.Any())
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;

            if (Event.current.type == EventType.DragPerform)
            {
                foreach (var entity in entities)
                {
                    editor.AssignExistingEntity(entity);
                }
                DragAndDrop.AcceptDrag();
                AssetDatabase.SaveAssets();
            }
        }

        private void HandleContextMenu(Rect rect, AudioAsset editorAsset)
        {
            var evt = Event.current;
            if (evt.type == EventType.ContextClick && editorAsset && rect.Contains(evt.mousePosition))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Ping"), false,() => EditorGUIUtility.PingObject(editorAsset));
                menu.ShowAsContext();
            }
        }

        private void OnSelect(ReorderableList list)
        {
            if (list.index != _currSelectedAssetIndex)
            {
                OnSelectAsset?.Invoke();
                _currSelectedAssetIndex = list.index;
                EditorAudioPreviewer.Instance.StopAllClips();
                RefreshAssetEditors(list);
                if(EditorSetting.OpenLastEditAudioAsset && list.list[list.index] is AudioAssetEditor editor)
                {
                    EditorSetting.LastEditAudioAsset = AssetDatabase.GetAssetPath(editor.target);
                }
            }
        }

        private void RefreshAssetEditors(ReorderableList list)
        {
            foreach (AudioAssetEditor editor in list.list)
            {
                editor.RemoveEntitiesListener();
            }

            if (list.index >= 0 && list.index < list.list.Count && list.list[list.index] is AudioAssetEditor selectedEditor)
            {
                selectedEditor.AddEntitiesListener();
                selectedEditor.Verify();
                selectedEditor.RebuildList();
            }
        }
        #endregion

        private bool TryGetCurrentAssetEditor(out AudioAssetEditor editor)
        {
            editor = null;

            if (_assetList == null)
            {
                return false;
            }

            if (_assetList.index >= 0 && _assetList.index < _assetList.list.Count && _assetList.list[_assetList.index] is AudioAssetEditor selectedEditor)
            {
                editor = selectedEditor;
                return true;
            }

            return false;
        }

        #region Asset Creation
        private void ShowCreateAssetAskName(Action<AudioAssetEditor> onAssetCreated = null)
        {
            // In the following case. List has better performance than IEnumerable, even with a ToList() method.
            List<AudioAsset> assets = new List<AudioAsset>();
            GetAudioAssets(assets);

            List<string> assetNames = assets.Select(x => x.AssetName).ToList();

            AssetNameEditorWindow.ShowWindow(assetNames, assetName =>
            {
                var editor = CreateAsset(assetName);
                onAssetCreated?.Invoke(editor);
            });
        }

        private AudioAssetEditor CreateAsset(string entityName)
        {
            if (!_hasOutputAssetPath && !ValidateAndSetAssetOutputPath())
            {
                return null;
            }

            string path;
            string fileName;

            if (EditorSetting.PromptForPathOnAssetCreation)
            {
                if (!PromptForAssetPath(entityName, out path, out fileName))
                {
                    return null;
                }
            }
            else
            {
                if (!TryGetNewPath(entityName, out path, out fileName))
                {
                    return null;
                }
            }

            var asset = (AudioAsset)ScriptableObject.CreateInstance(typeof(AudioAsset));
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            var editor = AddOrMoveToEnd(asset);

            string guid = AssetDatabase.AssetPathToGUID(path);
            editor.SetData(guid, fileName);

            assetList.index = assetList.count - 1;
            return editor;
        }

        private bool ValidateAndSetAssetOutputPath()
        {
            if (!EditorUtility.DisplayDialog(
                    "Asset Output Folder Not Found",
                    $"The configured asset output folder doesn't exist:\n\n{AssetOutputPath}\n\nPlease select a new folder to save audio assets. It will be used for all future audio asset creations.",
                    "OK",
                    "Cancel"))
            {
                return false;
            }

            string selectedPath = EditorUtility.OpenFolderPanel("Select Asset Output Folder", "Assets", "");
            if (string.IsNullOrEmpty(selectedPath))
            {
                return false;
            }

            string relativePath = FileUtil.GetProjectRelativePath(selectedPath);
            _hasOutputAssetPath = Directory.Exists(relativePath);
            if (!_hasOutputAssetPath)
            {
                Debug.LogError(Utility.LogTitle +
                    $"Failed to set asset output path. Please ensure the selected folder is within the project.");
                return false;
            }

            EditorSetting.AssetOutputPath = relativePath;
            EditorUtility.SetDirty(EditorSetting);
            return true;
        }

        private bool TryGetNewPath(string entityName, out string path, out string result)
        {
            path = string.Empty;
            result = entityName;
            if (!string.IsNullOrEmpty(AssetOutputPath))
            {
                int index = 0;
                path = GetNewAssetPath(entityName);
                while (File.Exists(path))
                {
                    index++;
                    result = entityName + index.ToString();
                    path = GetNewAssetPath(result);
                }
                return true;
            }
            return false;

            string GetNewAssetPath(string fileName)
            {
                return GetFilePath(AssetOutputPath, fileName + ".asset");
            }
        }

        private bool PromptForAssetPath(string entityName, out string path, out string fileName)
        {
            path = string.Empty;
            fileName = entityName;

            string directory = string.IsNullOrEmpty(AssetOutputPath) ? "Assets" : AssetOutputPath;
            string absolutePath = EditorUtility.SaveFilePanel(
                "Save Audio Asset",
                directory,
                entityName,
                "asset");

            if (string.IsNullOrEmpty(absolutePath))
            {
                return false;
            }

            path = FileUtil.GetProjectRelativePath(absolutePath);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError(Utility.LogTitle + "Selected path must be within the project folder.");
                return false;
            }

            fileName = Path.GetFileNameWithoutExtension(path);
            return true;
        }
        #endregion

        #region GUI Drawing
        private void OnGUI()
        {
            _verticalGapDrawer.DrawLineCount = 0;

            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.Space(_verticalGapDrawer.GetSpace());

                if (_isInEntitiesEditMode && TryGetCurrentAssetEditor(out var editor))
                {
                    DrawEntitiesList(editor);
                }
                else
                {
                    SplitRectHorizontal(position, EntitiesFactoryRatio, _verticalGapDrawer.SingleLineSpace, out Rect entitiesFactoryRect, out Rect assetListRect);

                    DrawEntityFactory(entitiesFactoryRect);
                    GUILayout.Space(_verticalGapDrawer.GetSpace());

                    DrawAssetList(assetListRect);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAssetList(Rect assetListRect)
        {
            assetListRect.width -= _verticalGapDrawer.GetTotalSpace() - _verticalGapDrawer.SingleLineSpace;
            assetListRect.height -= DefaultLayoutPadding * 2;
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(assetListRect.width), GUILayout.Height(assetListRect.height));
            {
                _assetListScrollPos = EditorGUILayout.BeginScrollView(_assetListScrollPos);
                {
                    assetList.DoLayoutList();
                }
                EditorGUILayout.EndScrollView();

                if (TryGetCurrentAssetEditor(out var editor))
                {
                    DrawIssueMessage(editor);
                }
                else if (assetList.count > 0)
                {
                    DrawLibraryManagerInstructions();
                }
            }
            EditorGUILayout.EndVertical();

            void DrawIssueMessage(AudioAssetEditor assetEditor)
            {
                string assetName = assetEditor.Asset.AssetName.ToBold().SetColor(Color.white);
                string text = _instruction.GetText(assetEditor.CurrInstruction);

                switch (assetEditor.CurrInstruction)
                {
                    case Instruction.AssetNaming_StartWithTemp:
                        RichTextHelpBox(String.Format(text, assetName), MessageType.Error);
                        break;
                    case Instruction.None:
                        DrawLibraryManagerInstructions();
                        break;
                    default:
                        EditorGUILayout.HelpBox(text, MessageType.Error);
                        break;
                }
            }
        }

        private void DrawLibraryManagerInstructions()
        {
            DrawInstructionHelpBox(ModifyAssetKey, _instruction.GetText(Instruction.LibraryManager_ModifyAsset));
            DrawInstructionHelpBox(DragAndDropKey, "You can also drag and drop audio clips onto an asset to add them.");
        }

        private void DrawInstructionHelpBox(string messageName, string message)
        {
            if(!_instructionHelpBoxKeys.TryGetValue(messageName, out string key))
            {
                key = $"BroHelpBox_{Application.dataPath.GetHashCode()}_{messageName}";
                _instructionHelpBoxKeys.Add(messageName, key);
            }

            if (!EditorPrefs.GetBool(key))
            {
                HelpBoxClosable(message, MessageType.Info, () => EditorPrefs.SetBool(key, true));
            }
        }

        private void DrawEntitiesList(AudioAssetEditor editor)
        {
            Rect rect = new Rect(position);
            rect.width -= _verticalGapDrawer.GetTotalSpace();
            rect.height -= DefaultLayoutPadding * 2;

            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(rect.width), GUILayout.Height(rect.height));
            {
                _entitiesScrollPos = EditorGUILayout.BeginScrollView(_entitiesScrollPos);
                {
                    DrawEntitiesHeader(editor, editor.serializedObject, editor.SetAssetName);

                    if (_isInEntitiesEditMode) // don't draw if we backed out
                    {
                        editor.DrawEntitiesList(out float listHeight);
                        float compensateHeight = GetScrollPosCompensateHeight(listHeight);
                        GUILayout.Space(Mathf.Max(0f, compensateHeight));
                    }

                    HandleDragAndDropEntitiesToEntityList(editor);
                }
                EditorGUILayout.EndScrollView();
                //EditorAudioPreviewer.Instance.PlaybackIndicator?.Draw(rect.Scoping(position, new Vector2(offsetX, offsetY)), -_entitiesScrollPos);
            }
            EditorGUILayout.EndVertical();
        }

        private float GetScrollPosCompensateHeight(float listHeight)
        {
            float headerHeight = EntitiesHeaderSize.y + (DefaultLayoutPadding * 2) + ReorderableList.Defaults.padding;
            float scrollViewHeight = listHeight - (position.height - headerHeight);
            return _entitiesScrollPos.y - scrollViewHeight;
        }

        private void ResetEntitiesScrollPos(bool isExpanded)
        {
            if (!isExpanded)
            {
                _entitiesScrollPos = new Vector2(_entitiesScrollPos.x, 0f);
            }
        }

        // The header background is drawn manually: ReorderableList's default header style has a non-zero
        // fixedHeight and stretchHeight off, so it can't size to this header.
        private void DrawEntitiesHeader(AudioAssetEditor editor, SerializedObject serializedAsset, Action<string> onAssetNameChanged)
        {
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent(IconConstant.BackButton), GUILayout.Width(BackButtonSize), GUILayout.Height(BackButtonSize)))
                {
                    editor.ClearList();
                    _isInEntitiesEditMode = false;
                    assetList.index = -1;
                }
                GUILayout.Space(10f);

                Rect headerRect = GUILayoutUtility.GetRect(EntitiesHeaderSize.x, EntitiesHeaderSize.y);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.skin.window.Draw(headerRect, false, false, false, false);
                    EditorStyles.textField.Draw(headerRect.PolarCoordinates(-1f).AdjustWidth(1f), headerRect.Contains(Event.current.mousePosition), false, false, false);
                    EditorGUI.DrawRect(headerRect.PolarCoordinates(-2f).AdjustWidth(1f), new Color(1f, 1f, 1f, 0.1f));
                }

                var nameProp = serializedAsset.FindBackingFieldProperty(nameof(AudioAsset.AssetName));
                DrawAssetNameField(headerRect, nameProp, onAssetNameChanged);

                GUILayout.FlexibleSpace();

                _showSettings = EditorGUILayout.BeginFoldoutHeaderGroup(_showSettings, "Settings");
                if (_showSettings)
                {
                    var groupProp = serializedAsset.FindProperty(AudioAsset.NameOf.Group);
                    EditorGUI.BeginChangeCheck();
                    groupProp.objectReferenceValue = (PlaybackGroup)EditorGUILayout.ObjectField(groupProp.objectReferenceValue, typeof(PlaybackGroup), false);
                    if(EditorGUI.EndChangeCheck())
                    {
                        serializedAsset.ApplyModifiedProperties();
                    }
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAssetNameField(Rect headerRect, SerializedProperty nameProp, Action<string> onAssetNameChanged)
        {
            GUIStyle wordWrapStyle = new GUIStyle(GUIStyleHelper.MiddleCenterRichText);
            wordWrapStyle.wordWrap = true;
            wordWrapStyle.fontSize = AssetNameFontSize;

            string displayName = GetDisplayName();
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUI.DelayedTextField(headerRect, displayName, wordWrapStyle);

            if (EditorGUI.EndChangeCheck()
                && !newName.Equals(nameProp.stringValue) && !newName.Equals(displayName) && IsValidAssetName(newName))
            {
                nameProp.stringValue = newName;
                onAssetNameChanged?.Invoke(newName);
            }

            string GetDisplayName()
            {
                if (string.IsNullOrEmpty(nameProp.stringValue))
                {
                    return _instruction.GetText(Instruction.LibraryManager_NameTempAssetHint);
                }
                return nameProp.stringValue;
            }
        }

        private bool IsValidAssetName(string newName)
        {
            if (IsInvalidName(newName, out ValidationErrorCode code))
            {
                switch (code)
                {
                    case ValidationErrorCode.StartWithNumber:
                        ShowNotification(new GUIContent(_instruction.GetText(Instruction.AssetNaming_StartWithNumber)), 2f);
                        break;
                    case ValidationErrorCode.ContainsInvalidWord:
                        ShowNotification(new GUIContent(_instruction.GetText(Instruction.AssetNaming_ContainsInvalidWords)), 2f);
                        break;
                    case ValidationErrorCode.ContainsWhiteSpace:
                        ShowNotification(new GUIContent(_instruction.GetText(Instruction.AssetNaming_ContainsWhiteSpace)), 2f);
                        break;
                    case ValidationErrorCode.IsDuplicate:
                        ShowNotification(new GUIContent(_instruction.GetText(Instruction.AssetNaming_IsDuplicated)), 2f);
                        break;
                }
                return false;
            }
            return true;
        }
        #endregion

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddSeparator(string.Empty);
            menu.AddDisabledItem(new GUIContent("Bro Audio"));
            menu.AddItem(new GUIContent("Default Window Size"), false, () => position = new Rect(position.position, DefaultWindowSize));
            menu.AddItem(new GUIContent(ShowPlayButtonWhenCollapsed), EditorSetting.ShowPlayButtonWhenEntityCollapsed, () => 
            {
                EditorSetting.ShowPlayButtonWhenEntityCollapsed = !EditorSetting.ShowPlayButtonWhenEntityCollapsed;
                EditorUtility.SetDirty(EditorSetting);
            });
            menu.AddItem(new GUIContent(OpenLastEditedAssetLabel), EditorSetting.OpenLastEditAudioAsset, () => 
            {
                EditorSetting.OpenLastEditAudioAsset = !EditorSetting.OpenLastEditAudioAsset;
                EditorSetting.LastEditAudioAsset = string.Empty;
                if(EditorSetting.OpenLastEditAudioAsset && TryGetCurrentAssetEditor(out var editor))
                {
                    EditorSetting.LastEditAudioAsset = AssetDatabase.GetAssetPath(editor.target);
                    _isInEntitiesEditMode = true;
                }
                EditorUtility.SetDirty(EditorSetting);
            });
            menu.AddItem(new GUIContent(PreferencesDrawer.PromptForPathOnAssetCreationLabel), EditorSetting.PromptForPathOnAssetCreation,
                () =>
                {
                    EditorSetting.PromptForPathOnAssetCreation = !EditorSetting.PromptForPathOnAssetCreation;
                    EditorUtility.SetDirty(EditorSetting);
                });
        }

        private AudioAssetEditor AddOrMoveToEnd(AudioAsset asset)
        {
            if (_assetList == null)
            {
                return UnityEditor.Editor.CreateEditor(asset, typeof(AudioAssetEditor)) as AudioAssetEditor;
            }

            AudioAssetEditor editor = null;

            for (int i = _assetList.count - 1; i >= 0; i--)
            {
                if (_assetList.list[i] is AudioAssetEditor existingEditor && existingEditor.target == asset)
                {
                    editor = existingEditor;

                    if (i < _assetList.count - 1)
                    {
                        // swap to end
                        (_assetList.list[i], _assetList.list[_assetList.count - 1]) = (_assetList.list[_assetList.count - 1], _assetList.list[i]);
                    }

                    break;
                }
            }

            if (editor == null)
            {
                editor = UnityEditor.Editor.CreateEditor(asset, typeof(AudioAssetEditor)) as AudioAssetEditor;
                _assetList.list.Add(editor);
            }

            return editor;
        }

        public void Refresh()
        {
            if (assetList != null)
            {
                RefreshAssetEditors(assetList);
            }
        }
    }
}