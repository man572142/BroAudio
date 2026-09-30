using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ami.BroAudio.Data;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using static Ami.BroAudio.Editor.BroEditorUtility;

namespace Ami.BroAudio.Editor
{
    public partial class LibraryManagerWindow : EditorWindow
    {
        public const string ClipSearchToken = "t:AudioClip";
        private const float SearchBarWidth = 200f;
        private const float SearchByTypeButtonWidth = 26f;
        private const string SearchByTypeIcon = "FilterByType"; // built-in icon of the Project window's search-by-type button

        private SearchField _searchField;
        private string _searchQuery = string.Empty;
        private Vector2 _searchScrollPos;
        private Rect _assetListSearchRect;
        private readonly List<AudioAssetEditor> _searchResults = new List<AudioAssetEditor>();

        private bool IsSearching => !string.IsNullOrEmpty(_searchQuery);
        private float SearchBarRowHeight => EditorGUIUtility.singleLineHeight + _verticalGapDrawer.SingleLineSpace;

        // Pulls the clip token out of any position; the remaining words are joined without spaces so "foo bar" still matches "Foo_Bar".
        public static void ParseQuery(string query, out bool byClip, out string text)
        {
            byClip = false;
            var words = new List<string>();
            foreach (string word in (query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(word, ClipSearchToken, StringComparison.OrdinalIgnoreCase))
                {
                    byClip = true;
                }
                else
                {
                    words.Add(word);
                }
            }
            text = string.Concat(words);
        }

        private void DrawSearchBar(Rect rect)
        {
            if (_searchField == null)
            {
                _searchField = new SearchField();
            }

            var evt = Event.current;
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape && IsSearching && _searchField.HasFocus())
            {
                SetSearchQuery(string.Empty);
                evt.Use();
            }

            Rect typeButtonRect = new Rect(rect.xMax - SearchByTypeButtonWidth, rect.y, SearchByTypeButtonWidth, rect.height);
            rect.width -= SearchByTypeButtonWidth + 2f;

            // Field before button, so the field's control ID doesn't depend on the button.
            string query = _searchField.OnGUI(rect, _searchQuery);
            GUI.Label(rect, new GUIContent(string.Empty, _instruction.GetText(Instruction.LibraryManager_SearchTooltip)));

            ParseQuery(query, out bool byClip, out _);
            var typeButtonContent = new GUIContent(EditorGUIUtility.IconContent(SearchByTypeIcon).image, _instruction.GetText(Instruction.LibraryManager_SearchByTypeTooltip));
            if (GUI.Toggle(typeButtonRect, byClip, typeButtonContent, EditorStyles.miniButton) != byClip)
            {
                query = ToggleClipSearchToken(query);
                GUIUtility.keyboardControl = 0; // a focused text field ignores external changes to its value
            }

            if (query != _searchQuery)
            {
                SetSearchQuery(query);
            }
        }

        public static string ToggleClipSearchToken(string query)
        {
            string[] words = (query ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            string[] rest = words.Where(w => !string.Equals(w, ClipSearchToken, StringComparison.OrdinalIgnoreCase)).ToArray();
            return rest.Length != words.Length
                ? string.Join(" ", rest)
                : ClipSearchToken + " " + string.Join(" ", rest);
        }

        private void SetSearchQuery(string query)
        {
            bool wasSearching = IsSearching;
            _searchQuery = query ?? string.Empty;

            if (IsSearching)
            {
                RunSearch();
            }
            else if (wasSearching)
            {
                ClearSearchResults();
                RefreshAssetEditors(assetList); // listeners back to the selected asset only
            }
        }

        private void RerunSearchIfActive()
        {
            if (IsSearching)
            {
                RunSearch();
                Repaint();
            }
        }

        private void ClearSearchResults()
        {
            foreach (var editor in _searchResults)
            {
                if (editor)
                {
                    editor.ClearFilter();
                }
            }
            _searchResults.Clear();
        }

        private void RunSearch()
        {
            ClearSearchResults();
            ParseQuery(_searchQuery, out bool byClip, out string text);

            var entities = new List<AudioEntity>();
            GetAudioEntities(entities);

            var scores = new Dictionary<AudioEntity, int>();
            var groups = new Dictionary<AudioAsset, List<AudioEntity>>();
            foreach (var entity in entities)
            {
                if (!entity.AudioAsset || !TryScore(entity, byClip, text, out int score))
                {
                    continue;
                }

                scores[entity] = score;
                if (!groups.TryGetValue(entity.AudioAsset, out var group))
                {
                    group = new List<AudioEntity>();
                    groups.Add(entity.AudioAsset, group);
                }
                group.Add(entity);
            }

            var bestScores = new Dictionary<AudioAssetEditor, int>();
            foreach (AudioAssetEditor editor in assetList.list)
            {
                editor.RemoveEntitiesListener();
                if (editor.target is AudioAsset asset && groups.TryGetValue(asset, out var group))
                {
                    // OrderBy is stable, so ties keep GetAudioEntities' natural name order.
                    var ordered = group.OrderByDescending(e => scores[e]).ToList();
                    editor.SetFilter(ordered);
                    // Owner routing is guarded inside AudioAssetEditor, so every result asset can listen at once.
                    editor.AddEntitiesListener();
                    bestScores[editor] = scores[ordered[0]];
                    _searchResults.Add(editor);
                }
            }

            var sorted = _searchResults.OrderByDescending(e => bestScores[e]).ToList();
            _searchResults.Clear();
            _searchResults.AddRange(sorted);
        }

        private static bool TryScore(AudioEntity entity, bool byClip, string text, out int score)
        {
            if (!byClip)
            {
                return FuzzyMatcher.TryMatch(text, entity.Name, out score);
            }

            score = 0;
            bool matched = false;
            using (var serializedEntity = new SerializedObject(entity))
            {
                var clipsProp = serializedEntity.FindProperty(nameof(AudioEntity.Clips));
                for (int i = 0; clipsProp != null && i < clipsProp.arraySize; i++)
                {
                    string clipName = GetClipName(clipsProp.GetArrayElementAtIndex(i));
                    if (!string.IsNullOrEmpty(clipName) && FuzzyMatcher.TryMatch(text, clipName, out int clipScore) && (!matched || clipScore > score))
                    {
                        score = clipScore;
                        matched = true;
                    }
                }
            }
            return matched;
        }

        // Reads the serialized data instead of BroAudioClip.GetAudioClip(), which can load Addressables synchronously and throw.
        private static string GetClipName(SerializedProperty clipProp)
        {
            var clip = clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClip)?.objectReferenceValue;
            if (clip)
            {
                return clip.name;
            }

            var guidProp = clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClipAssetReference)?.FindPropertyRelative(AssetReferenceGUIDFieldName);
            if (guidProp == null || string.IsNullOrEmpty(guidProp.stringValue))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guidProp.stringValue);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }

        private void DrawSearchResultsPanel()
        {
            Rect rect = new Rect(position);
            rect.width -= _verticalGapDrawer.GetTotalSpace();
            rect.height -= DefaultLayoutPadding * 2 + SearchBarRowHeight;

            EditorGUILayout.BeginVertical();
            {
                GUILayout.Space(SearchBarRowHeight);
                EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(rect.width), GUILayout.Height(rect.height));
                {
                    _searchScrollPos = EditorGUILayout.BeginScrollView(_searchScrollPos);
                    DrawSearchResults();
                    EditorGUILayout.EndScrollView();
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndVertical();
        }

        private Rect GetFullWidthSearchBarRect()
        {
            float gap = _verticalGapDrawer.SingleLineSpace;
            return new Rect(gap, gap * 0.5f, position.width - gap * 2, EditorGUIUtility.singleLineHeight);
        }

        private void DrawSearchResults()
        {
            if (_searchResults.Count == 0)
            {
                EditorGUILayout.LabelField(_instruction.GetText(Instruction.LibraryManager_SearchNoResult));
                return;
            }

            // Indexed: removing an entity can re-run the search mid-draw.
            for (int i = 0; i < _searchResults.Count; i++)
            {
                var editor = _searchResults[i];
                if (!editor || editor.Asset == null)
                {
                    continue;
                }

                EditorGUILayout.LabelField(editor.Asset.AssetName, EditorStyles.boldLabel);
                editor.DrawFilteredEntitiesList(out _);
            }
        }
    }
}