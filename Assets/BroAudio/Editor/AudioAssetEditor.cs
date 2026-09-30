using System;
using Ami.BroAudio.Data;
using Ami.BroAudio.Tools;
using Ami.Extension;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Ami.Extension.Reflection;
using System.Reflection;
using static Ami.BroAudio.Editor.BroEditorUtility;
using static Ami.Extension.EditorScriptingExtension;
using System.Collections.Generic;
using System.IO;

namespace Ami.BroAudio.Editor
{
    [CustomEditor(typeof(AudioAsset), true)]
    public class AudioAssetEditor : UnityEditor.Editor
    {
        private ReorderableList _entityList;
        // Search results: shares entityList's AudioEntityEditor instances so expand/edit state carries over.
        private ReorderableList _filteredList;
        private ReorderableList entityList
        {
            get
            {
                if (_entityList != null)
                {
                    return _entityList;
                }

                RebuildList();

                return _entityList;
            }
        }

        public Instruction CurrInstruction { get; private set; }
        public IAudioAsset Asset => target as IAudioAsset;

        public void AddEntitiesListener()
        {
            AudioEntityEditor.OnDuplicateEntity += OnDuplicateSelectedEntity;
            AudioEntityEditor.OnRemoveEntity += OnRemoveSelectedEntity;
            AudioEntityEditor.OnExpandAll += SetAllElementsExpanded;
        }

        public void RemoveEntitiesListener()
        {
            AudioEntityEditor.OnDuplicateEntity -= OnDuplicateSelectedEntity;
            AudioEntityEditor.OnRemoveEntity -= OnRemoveSelectedEntity;
            AudioEntityEditor.OnExpandAll -= SetAllElementsExpanded;
        }

        private void OnDestroy()
        {
            RemoveEntitiesListener();
        }

        // AudioEntityEditor's events are static, so every subscribed asset editor hears them; only the entity's owner may act.
        private bool IsOwnEntity(AudioEntityEditor editor)
        {
            return editor && editor.target is AudioEntity entity && entity.AudioAsset == (AudioAsset)Asset;
        }

        private void OnRemoveSelectedEntity(AudioEntityEditor editor)
        {
            if (IsOwnEntity(editor))
            {
                OnRemoveSelectedEntity(false, editor);
            }
        }

        private void OnRemoveSelectedEntity(bool showDialog, AudioEntityEditor editor)
        {
            if(showDialog)
            {
                if(!EditorUtility.DisplayDialog("Remove Entity", $"Do you want to remove [{editor.target.name}]?", "Yes", "No"))
                {
                    return;
                }
            }

            if (_entityList != null)
            {
                _entityList.list.Remove(editor);
            }
            _filteredList?.list.Remove(editor);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(editor.target));
            DestroyImmediate(editor.target, true);
        }

        private void OnDuplicateSelectedEntity(AudioEntityEditor editor)
        {
            if (IsOwnEntity(editor) && editor.target is AudioEntity entity)
            {
                var newEntity = Instantiate(entity);
                var path = AssetDatabase.GenerateUniqueAssetPath(AssetDatabase.GetAssetPath(entity));
                newEntity.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(newEntity, path);

                // Duplicates land next to their source, which may be outside AssetOutputPath (e.g. the Demo library).
                // The cache postprocessor's path heuristic then skips it, so refresh explicitly to keep the list/dropdown in sync.
                AudioAssetCache.RefreshCache();

                var newEditor = AddOrMoveToEnd(newEntity);
                if (_entityList != null)
                {
                    _entityList.index = _entityList.count - 1;
                }
                newEditor.IsExpanded = true;
            }
        }

        public void SetData(string guid, string assetName)
        {
            string assetGUIDPropertyPath = GetFieldName(nameof(IAudioAsset.AssetGUID));
            serializedObject.FindProperty(assetGUIDPropertyPath).stringValue = guid;

            string assetNamePropertyPath = GetBackingFieldName(nameof(IAudioAsset.AssetName));
            serializedObject.FindProperty(assetNamePropertyPath).stringValue = assetName;

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private string GetAudioAssetDirectoryPath()
        {
            if (Asset != null)
            {
                var path = Path.Combine(AssetOutputPath, Asset.AssetName).Replace('\\', '/');

                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                }

                return path;
            }

            return AssetOutputPath;
        }

        private static AudioEntityEditor GetAudioEntityEditor(ReorderableList list, int index)
        {
            if (list != null && index >= 0 && index < list.count)
            {
                return list.list[index] as AudioEntityEditor;
            }

            return null;
        }

        public void RebuildList()
        {
            var audioAsset = Asset as AudioAsset;

            if (audioAsset == null)
            {
                _entityList = new ReorderableList(new List<AudioEntityEditor>(), typeof(AudioEntityEditor));
                return;
            }

            List<AudioEntity> entities = new List<AudioEntity>();
            GetAudioEntities(entities, Asset as AudioAsset);

            List<AudioEntityEditor> editors = new List<AudioEntityEditor>(entities.Count);

            if (_entityList != null) // Some logic to retain the current sorting until we've fully closed
            {
                foreach (var rawEditor in _entityList.list)
                {
                    if (rawEditor is AudioEntityEditor editor)
                    {
                        var entity = editor.target as AudioEntity;
                        var audioEntityListIndex = entities.IndexOf(entity);

                        if (audioEntityListIndex == -1)
                        {
                            continue; // not included in the new list
                        }

                        // remove it from our entities list
                        entities.RemoveAt(audioEntityListIndex);

                        // add it to the new list
                        editors.Add(editor);
                    }
                }
            }

            foreach (var entity in entities)
            {
                AudioEntityEditor entityEditor = (AudioEntityEditor)CreateEditor(entity, typeof(AudioEntityEditor));
                editors.Add(entityEditor);
            }

            _entityList = CreateEntityList(editors, displayAddButton: true);
            _entityList.onAddCallback = OnAdd;

            void OnAdd(ReorderableList list)
            {
                BroAudioType audioType = BroAudioType.None;

                if (list.count > 0)
                {
                    var lastEditor = GetAudioEntityEditor(list, list.count - 1);
                    if (lastEditor.target is AudioEntity entity)
                    {
                        audioType = entity.AudioType;
                    }
                }

                CreateNewEntity("New Sound", audioType);
            }
        }

        private ReorderableList CreateEntityList(List<AudioEntityEditor> editors, bool displayAddButton)
        {
            ReorderableList list = null;
            list = new ReorderableList(editors, typeof(AudioEntityEditor),
                draggable: false, displayHeader: false, displayAddButton: displayAddButton, displayRemoveButton: true)
            {
                onRemoveCallback = OnRemove,
                drawElementCallback = (rect, index, isActive, isFocused) => OnDrawElement(list, rect, index),
                elementHeightCallback = index => OnGetPropertyHeight(list, index)
            };
            return list;
        }

        private void OnRemove(ReorderableList list)
        {
            var editor = GetAudioEntityEditor(list, list.index);
            if (editor != null)
            {
                OnRemoveSelectedEntity(true, editor);
            }
        }

        private void OnDrawElement(ReorderableList list, Rect rect, int index)
        {
            var editor = GetAudioEntityEditor(list, index);
            if (editor != null)
            {
                if (!editor.IsExpanded && EventExtension.IsRightClick(rect))
                {
                    list.index = index;
                    list.GrabKeyboardFocus();
                    // the element background doesn't repaint right away, so we delay the dropdown to the next drawing process
                    EditorApplication.delayCall += () => editor.OnOpenOptionMenu();
                    EditorWindow.focusedWindow.Repaint();
                }

                HandleKeyboardShortcuts(list);

                editor.DrawGUI(rect);
            }
        }

        private static float OnGetPropertyHeight(ReorderableList list, int index)
        {
            var editor = GetAudioEntityEditor(list, index);

            if (editor != null)
            {
                return editor.GetHeight();
            }

            return EditorGUIUtility.singleLineHeight;
        }

        public void ClearList()
        {
            _entityList = null;
            _filteredList = null;
        }

        public void SetFilter(IReadOnlyList<AudioEntity> entities)
        {
            var editors = new List<AudioEntityEditor>(entities.Count);
            foreach (var entity in entities)
            {
                var editor = FindEntityEditor(entity);
                if (editor == null)
                {
                    RebuildList(); // entity created since the list was built
                    editor = FindEntityEditor(entity);
                }

                if (editor != null)
                {
                    editors.Add(editor);
                }
            }
            _filteredList = CreateEntityList(editors, displayAddButton: false);

            AudioEntityEditor FindEntityEditor(AudioEntity entity)
            {
                foreach (var rawEditor in entityList.list)
                {
                    if (rawEditor is AudioEntityEditor editor && editor.target == entity)
                    {
                        return editor;
                    }
                }
                return null;
            }
        }

        public void ClearFilter()
        {
            _filteredList = null;
        }

        public void DrawFilteredEntitiesList(out float height)
        {
            height = 0f;
            if (_filteredList != null)
            {
                _filteredList.DoLayoutList();
                height = _filteredList.GetHeight();
            }
        }

        private void HandleKeyboardShortcuts(ReorderableList list)
        {
            var current = Event.current;
            if(current.type != EventType.KeyDown)
            {
                return;
            }

            // Search results draw one filtered list per asset; only the focused one may act.
            if (list == _filteredList && !list.HasKeyboardControl())
            {
                return;
            }

            bool isCtrl = current.control || current.modifiers == EventModifiers.Control;
            if (GetAudioEntityEditor(list, list.index) is AudioEntityEditor selected)
            {
                if (isCtrl && current.keyCode == KeyCode.D)
                {
                    OnDuplicateSelectedEntity(selected);
                    current.Use();
                }
                else if (current.keyCode == KeyCode.Delete)
                {
                    if (!selected.IsExpanded)
                    {
                        OnRemoveSelectedEntity(true, selected);
                        current.Use();
                    }
                }
            }
        }

        public override void OnInspectorGUI()
        {
            if(GUILayout.Button("Open " + BroName.MenuItem_LibraryManager))
            {
                LibraryManagerWindow window = LibraryManagerWindow.ShowWindow();
                window.SelectAsset(Asset as AudioAsset);
            }
        }

        public void DrawEntitiesList(out float height)
        {
            entityList.DoLayoutList();
            height = entityList.GetHeight();
        }

        private void SetAllElementsExpanded(bool isExpanded)
        {
            foreach (var editor in entityList.list)
            {
                if (editor is AudioEntityEditor entityEditor)
                {
                    entityEditor.IsExpanded = isExpanded;
                }
            }
        }

        public void SetAssetName(string newName)
        {
            var asset = Asset as AudioAsset;
            string path = AssetDatabase.GetAssetPath(asset);
            AssetDatabase.RenameAsset(path, newName);

            serializedObject.Update();
            serializedObject.FindProperty(GetBackingFieldName(nameof(AudioAsset.AssetName))).stringValue = newName;
            serializedObject.ApplyModifiedProperties();
        }

        public void AssignExistingEntity(AudioEntity entity)
        {
            entity.SetAudioAsset(Asset as AudioAsset);
            EditorUtility.SetDirty(entity);
            AddOrMoveToEnd(entity);
            if (_entityList != null)
            {
                _entityList.index = _entityList.count - 1;
            }
        }

        public (AudioEntity entity, AudioEntityEditor editor) CreateNewEntity(string name, BroAudioType audioType = BroAudioType.None)
        {
            var path = Path.Combine(GetAudioAssetDirectoryPath(), name + ".asset").Replace('\\', '/');
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            name = Path.GetFileNameWithoutExtension(path);

            var newEntity = AudioEntity.CreateNewInstance(Asset as AudioAsset, name, audioType);

            AssetDatabase.CreateAsset(newEntity, path);
            var editor = AddOrMoveToEnd(newEntity);
            return (newEntity, editor);
        }

        private AudioEntityEditor AddOrMoveToEnd(AudioEntity entity)
        {
            if (_entityList == null)
            {
                return CreateEditor(entity, typeof(AudioEntityEditor)) as AudioEntityEditor; // just so we don't error
            }

            AudioEntityEditor editor = null;

            for (int i = _entityList.count - 1; i >= 0; i--)
            {
                if (_entityList.list[i] is AudioEntityEditor listEditorItem && listEditorItem.target == entity)
                {
                    editor = listEditorItem;

                    if (i != _entityList.count - 1)
                    {
                        // swap the item with the last item
                        (_entityList.list[i], _entityList.list[_entityList.count - 1]) = (_entityList.list[_entityList.count - 1], _entityList.list[i]);
                    }

                    break;
                }
            }

            if (editor == null)
            {
                editor = CreateEditor(entity, typeof(AudioEntityEditor)) as AudioEntityEditor;
                _entityList.list.Add(editor);
            }

            return editor;
        }

        public void SetClipList(SerializedProperty clipListProp, int index, AudioClip clip)
        {
            clipListProp.InsertArrayElementAtIndex(index);
            SerializedProperty elementProp = clipListProp.GetArrayElementAtIndex(index);
            elementProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClip).objectReferenceValue = clip;
            elementProp.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue = AudioConstant.FullVolume;
        }

        public void SelectEntity(AudioEntity entity, out float entityVerticalPos)
        {
            entityVerticalPos = 0f;
            foreach (var editor in entityList.list)
            {
                if (editor is AudioEntityEditor entityEditor)
                {
                    var isTarget = entityEditor.target == entity;
                    entityEditor.IsExpanded = isTarget;
                    if (isTarget)
                    {
                        entityList.index = entityList.list.IndexOf(editor);
                        entityVerticalPos = entityList.index * entityList.elementHeight;
                    }
                }
            }
        }

        public void Verify()
        {
            if(VerifyAsset())
            {
                CurrInstruction = default;
            }
        }

        private bool VerifyAsset()
        {
            if (IsInvalidName(Asset.AssetName, out ValidationErrorCode code))
            {
                CurrInstruction = code switch 
                {
                    ValidationErrorCode.IsNullOrEmpty => Instruction.AssetNaming_IsNullOrEmpty,
                    ValidationErrorCode.StartWithNumber => Instruction.AssetNaming_StartWithNumber,
                    ValidationErrorCode.ContainsInvalidWord => Instruction.AssetNaming_ContainsInvalidWords,
                    ValidationErrorCode.ContainsWhiteSpace => Instruction.AssetNaming_ContainsWhiteSpace,
                    _ => default,
                };
                return false;
            }
            return true;
        }
    }
}