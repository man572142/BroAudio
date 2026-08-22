using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ami.BroAudio.Data;
using Ami.Extension;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using static Ami.BroAudio.Editor.Setting.BroAudioGUISetting;
using static Ami.BroAudio.Tools.BroName;
using static Ami.Extension.EditorScriptingExtension;

namespace Ami.BroAudio.Editor
{
    public class IssueReportWindow : MiEditorWindow
    {
        private const string LastSaveDirectoryPrefKey = "BroAudio.IssueReport.LastSaveDirectory.";

        [SerializeField] private IssueReportDraft _draft = new IssueReportDraft();

        private SerializedObject _serializedObject;
        private SerializedProperty _draftProp;
        private SerializedProperty _typeProp;
        private SerializedProperty _titleProp;
        private SerializedProperty _descriptionProp;
        private SerializedProperty _expectationProp;
        private SerializedProperty _problemSoundsProp;
        private SerializedProperty _targetObjectProp;
        private SerializedProperty _integrationProp;
        private SerializedProperty _scriptCollectionProp;
        private SerializedProperty _consoleOutputProp;

        private ReorderableList _problemSoundsList;
        private readonly BroInstructionHelper _instruction = new BroInstructionHelper();
        private RequiredField[] _requiredFields;
        private Vector2 _scrollPos;
        private Vector2 _scriptCollectionScrollPos;
        private bool _showPreview;

        private readonly struct RequiredField
        {
            public readonly string Label;
            public readonly Func<bool> IsMissing;

            public RequiredField(string label, Func<bool> isMissing)
            {
                Label = label;
                IsMissing = isMissing;
            }
        }

        public override float SingleLineSpace => EditorGUIUtility.singleLineHeight + 5f;

        [MenuItem(IssueReportMenuPath, false, IssueReportMenuIndex)]
        [MenuItem(IssueReportMenuPath_Window, false, IssueReportMenuIndex)]
        public static void ShowWindow()
        {
            EditorWindow window = GetWindow(typeof(IssueReportWindow));
            window.minSize = new Vector2(640f, 480f);
            window.titleContent = new GUIContent(MenuItem_IssueReport);
            window.Show();
        }

        private void OnEnable()
        {
            _serializedObject = new SerializedObject(this);
            _draftProp = _serializedObject.FindProperty(nameof(_draft));
            _typeProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.Type);
            _titleProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.Title);
            _descriptionProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.Description);
            _expectationProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.Expectation);
            _problemSoundsProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.ProblemSounds);
            _targetObjectProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.TargetObject);
            _integrationProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.Integration);
            _scriptCollectionProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.ScriptCollection);
            _consoleOutputProp = _draftProp.FindPropertyRelative(IssueReportDraft.NameOf.ConsoleOutput);

            _requiredFields = new RequiredField[]
            {
                new RequiredField("Issue Type", () => _typeProp.enumValueIndex == (int)IssueType.None),
                new RequiredField("Title", () => string.IsNullOrWhiteSpace(_titleProp.stringValue)),
                new RequiredField("Description", () => string.IsNullOrWhiteSpace(_descriptionProp.stringValue)),
                new RequiredField("Expectation", () => string.IsNullOrWhiteSpace(_expectationProp.stringValue)),
                new RequiredField("Problem Sounds", () => _problemSoundsProp.arraySize == 0),
            };

            _problemSoundsList = CreateProblemSoundsList();
        }

        private bool IsRequiredLabel(string label)
        {
            foreach (RequiredField field in _requiredFields)
            {
                if (field.Label == label)
                {
                    return true;
                }
            }
            return false;
        }

        private GUIContent FieldLabel(string label, Instruction tooltipKey)
        {
            string text = IsRequiredLabel(label) ? label + " *" : label;
            return new GUIContent(text, _instruction.GetText(tooltipKey));
        }

        private void DrawIssueTypeField(Rect lineRect)
        {
            const float HelpIconWidth = 24f;
            Rect helpIconRect = lineRect.SetWidth(HelpIconWidth).SetPosition(lineRect.xMax - HelpIconWidth, lineRect.y);
            Rect fieldRect = lineRect.AdjustWidth(-HelpIconWidth);
            EditorGUI.PropertyField(fieldRect, _typeProp, FieldLabel("Issue Type", Instruction.IssueReport_TypeTooltip));
            string tooltip = GetIssueTypeTooltip((IssueType)_typeProp.enumValueIndex);
            GUI.Label(helpIconRect, new GUIContent(EditorGUIUtility.IconContent(IconConstant.Help).image, tooltip));
        }

        private string GetIssueTypeTooltip(IssueType type) => type switch
        {
            IssueType.Editor => _instruction.GetText(Instruction.IssueReport_TypeTooltip_Editor),
            IssueType.PlayMode => _instruction.GetText(Instruction.IssueReport_TypeTooltip_PlayMode),
            IssueType.Build => _instruction.GetText(Instruction.IssueReport_TypeTooltip_Build),
            _ => "Please select an Issue Type.",
        };

        private ReorderableList CreateProblemSoundsList()
        {
            return new ReorderableList(_problemSoundsProp.serializedObject, _problemSoundsProp)
            {
                drawHeaderCallback = rect =>
                {
                    SplitRectHorizontal(rect, 0.75f, 5f, out Rect labelPart, out Rect buttonPart);
                    EditorGUI.LabelField(labelPart, FieldLabel("Problem Sounds", Instruction.IssueReport_ProblemSoundsTooltip));
                    if (GUI.Button(buttonPart, "Auto Collect"))
                    {
                        AutoCollectProblemSounds();
                    }
                },
                drawElementCallback = (rect, index, isActive, isFocused) =>
                {
                    rect.y += 2f;
                    rect.height = EditorGUIUtility.singleLineHeight;
                    EditorGUI.PropertyField(rect, _problemSoundsProp.GetArrayElementAtIndex(index), GUIContent.none);
                },
                elementHeight = EditorGUIUtility.singleLineHeight + 4f,
            };
        }

        protected override void OnGUI()
        {
            base.OnGUI();
            _serializedObject.Update();
            Offset = 0f;

            Rect drawPosition = new Rect(position) { x = 10f, y = 5f, width = position.width - 35f };
            Rect scrollViewRect = new Rect(drawPosition) { width = drawPosition.width + 10f};

            _scrollPos = BeginScrollView(scrollViewRect, _scrollPos);
            {
                DrawForm(drawPosition);
            }
            EndScrollView();

            _serializedObject.ApplyModifiedProperties();
        }

        private void DrawForm(Rect drawPosition)
        {
            DrawIssueTypeField(GetRectAndIterateLine(drawPosition));

            EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), FieldLabel("Title", Instruction.IssueReport_TitleTooltip));
            _titleProp.stringValue = EditorGUI.TextField(GetRectAndIterateLine(drawPosition), _titleProp.stringValue);

            DrawTextArea("What happened", _descriptionProp, Instruction.IssueReport_DescriptionTooltip, drawPosition, 3);
            DrawTextArea("What I expected", _expectationProp, Instruction.IssueReport_ExpectationTooltip, drawPosition, 2);

            DrawEmptyLine(1);
            EditorGUI.PropertyField(GetRectAndIterateLine(drawPosition), _targetObjectProp, new GUIContent("Target Object", _instruction.GetText(Instruction.IssueReport_TargetObjectTooltip)));

            DrawProblemSoundsList(drawPosition);
            EditorGUI.PropertyField(GetRectAndIterateLine(drawPosition), _integrationProp, new GUIContent("How it's used", _instruction.GetText(Instruction.IssueReport_IntegrationStyleTooltip)));
            using (new EditorGUI.IndentLevelScope())
            {
                DrawIntegrationSubsection(drawPosition);
            }

            DrawTextArea("Console output (optional)", _consoleOutputProp, Instruction.IssueReport_ConsoleOutputTooltip, drawPosition, 3);

            _showPreview = EditorGUI.Foldout(GetRectAndIterateLine(drawPosition), _showPreview, "Included in report", true);
            if (_showPreview)
            {
                DrawPreview(drawPosition);
            }

            DrawValidation(drawPosition);

            using (new EditorGUI.DisabledScope(!IsValid(out _)))
            {
                Rect buttonsRect = GetRectAndIterateLine(drawPosition).GetHorizontalCenterRect(410f, SingleLineSpace * 1.5f);
                SplitRectHorizontal(buttonsRect, 0.5f, 10f, out Rect saveButtonRect, out Rect issueButtonRect);
                if (GUI.Button(saveButtonRect, "Save Report"))
                {
                    SaveReport();
                }
                if (GUI.Button(issueButtonRect, "Create GitHub Issue"))
                {
                    CreateGitHubIssue();
                }
            }
            DrawEmptyLine(1);
            Rect helpBoxRect = GetRectAndIterateLine(drawPosition);
            helpBoxRect.height = SingleLineSpace * 2;
            RichTextHelpBox(helpBoxRect, _instruction.GetText(Instruction.IssueReport_CreateIssueHint), MessageType.Info);
            DrawEmptyLine(1);
        }

        private void DrawTextArea(string label, SerializedProperty prop, Instruction tooltipKey, Rect drawPosition, int lineCount)
        {
            EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), FieldLabel(label, tooltipKey));
            Rect areaRect = GetRectAndIterateLine(drawPosition);
            areaRect.height = SingleLineSpace * lineCount;
            prop.stringValue = EditorGUI.TextArea(areaRect, prop.stringValue);
            DrawEmptyLine(lineCount - 1);
        }

        private void DrawProblemSoundsList(Rect drawPosition)
        {
            Rect listRect = GetRectAndIterateLine(drawPosition);
            float listHeight = _problemSoundsList.GetHeight();
            listRect.height = listHeight;
            _problemSoundsList.DoList(listRect);
            DrawEmptyLine(Mathf.CeilToInt(listHeight / SingleLineSpace) - 2);
            Offset += 5f;
        }

        private void DrawIntegrationSubsection(Rect drawPosition)
        {
            var style = (IntegrationStyle)_integrationProp.enumValueIndex;
            if (style != IntegrationStyle.Script)
            {
                return;
            }

            Rect labelRect = GetRectAndIterateLine(drawPosition);
            SplitRectHorizontal(labelRect, 0.75f, 5f, out Rect labelPart, out Rect buttonPart);
            EditorGUI.LabelField(labelPart, new GUIContent("Script", _instruction.GetText(Instruction.IssueReport_AutoCollectTooltip)));
            if (GUI.Button(buttonPart, "Auto Collect"))
            {
                AutoCollectScript();
            }

            Rect areaRect = GetRectAndIterateLine(drawPosition);
            areaRect.height = SingleLineSpace * 6;
            DrawScrollableScriptCollectionTextArea(areaRect);
            DrawEmptyLine(5);

            Rect privacyRect = GetRectAndIterateLine(drawPosition);
            privacyRect.height = SingleLineSpace * 2;
            EditorGUI.HelpBox(privacyRect, _instruction.GetText(Instruction.IssueReport_PrivacyNotice), MessageType.Info);
            DrawEmptyLine(2);
        }

        private void DrawScrollableScriptCollectionTextArea(Rect areaRect)
        {
            string text = _scriptCollectionProp.stringValue;
            int lineCount = Mathf.Max(6, text.Split('\n').Length);
            Rect contentRect = new Rect(0f, 0f, areaRect.width - 15f, 15f * lineCount);

            _scriptCollectionScrollPos = GUI.BeginScrollView(areaRect, _scriptCollectionScrollPos, contentRect);
            _scriptCollectionProp.stringValue = EditorGUI.TextArea(contentRect, text);
            GUI.EndScrollView();
        }

        private void AutoCollectScript()
        {
            var targetObject = _targetObjectProp.objectReferenceValue;
            if (!targetObject)
            {
                _scriptCollectionProp.stringValue = "No Target Object assigned — drag in the GameObject or ScriptableObject where the problem lives first.";
                return;
            }

            var problemSoundEntities = GetProblemSoundEntities();
            string collected = IssueReportCollector.CollectScriptReferences(targetObject, problemSoundEntities, out bool noneMatched);

            if (noneMatched)
            {
                _scriptCollectionProp.stringValue = string.Format(_instruction.GetText(Instruction.IssueReport_NoSoundsMatchedTarget), targetObject.name);
            }
            else if (string.IsNullOrEmpty(collected))
            {
                string message = $"No user script with a SoundID reference found on {targetObject.name}.";
                _scriptCollectionProp.stringValue = message;
                ShowNotification(new GUIContent(message));
            }
            else
            {
                _scriptCollectionProp.stringValue = collected;
            }
        }

        private void AutoCollectProblemSounds()
        {
            var targetObject = _targetObjectProp.objectReferenceValue;
            if (!targetObject)
            {
                ShowNotification(new GUIContent("No Target Object assigned — drag in the GameObject or ScriptableObject where the problem lives first."));
                return;
            }

            var existing = new HashSet<AudioEntity>(GetProblemSoundEntities());
            existing.Remove(null);

            var found = new List<AudioEntity>();
            foreach (var obj in IssueReportCollector.ResolveTargets(targetObject))
            {
                foreach (var field in IssueReportCollector.FindSoundIDFieldsOn(obj))
                {
                    if (field.Entity && existing.Add(field.Entity))
                    {
                        found.Add(field.Entity);
                    }
                }
            }

            if (found.Count == 0)
            {
                ShowNotification(new GUIContent($"No new SoundID references found on {targetObject.name}."));
                return;
            }

            foreach (var entity in found)
            {
                int index = _problemSoundsProp.arraySize;
                _problemSoundsProp.InsertArrayElementAtIndex(index);
                var entityProp = _problemSoundsProp.GetArrayElementAtIndex(index).FindPropertyRelative(SoundID.NameOf.Entity);
                entityProp.objectReferenceValue = entity;
            }
        }

        private List<AudioEntity> GetProblemSoundEntities()
        {
            var entities = new List<AudioEntity>();
            for (int i = 0; i < _problemSoundsProp.arraySize; i++)
            {
                var element = _problemSoundsProp.GetArrayElementAtIndex(i);
                var entityProp = element.FindPropertyRelative(SoundID.NameOf.Entity);
                entities.Add(entityProp != null ? entityProp.objectReferenceValue as AudioEntity : null);
            }
            return entities;
        }

        private bool IsValid(out string missingFieldsMessage)
        {
            var missing = new List<string>();
            foreach (RequiredField field in _requiredFields)
            {
                if (field.IsMissing())
                {
                    missing.Add(field.Label);
                }
            }

            if (missing.Count > 0)
            {
                missingFieldsMessage = string.Join(", ", missing);
                return false;
            }
            missingFieldsMessage = null;
            return true;
        }

        private void DrawValidation(Rect drawPosition)
        {
            if (IsValid(out string missing))
            {
                return;
            }

            Rect boxRect = GetRectAndIterateLine(drawPosition);
            boxRect.height *= 2;
            string message = string.Format(_instruction.GetText(Instruction.IssueReport_MissingRequiredFields), missing);
            RichTextHelpBox(boxRect, message, MessageType.Warning);
            DrawEmptyLine(1);
        }

        private void DrawPreview(Rect drawPosition)
        {
            string preview = ComposePreviewText();
            int lineCount = Mathf.Clamp(preview.Split('\n').Length, 4, 24);
            Rect areaRect = GetRectAndIterateLine(drawPosition);
            areaRect.height = SingleLineSpace * lineCount;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.TextArea(areaRect, preview);
            }
            DrawEmptyLine(lineCount - 1);
            DrawEmptyLine(1);
        }

        private string ComposePreviewText()
        {
            var sb = new StringBuilder();
            var problemSoundEntities = GetProblemSoundEntities();

            if (problemSoundEntities.Count > 0)
            {
                sb.AppendLine("Sounds involved:");
                foreach (var entity in problemSoundEntities)
                {
                    sb.AppendLine("- " + (entity ? entity.Name : "(unassigned SoundID)"));
                }
                sb.AppendLine();
            }

            var targetObject = _targetObjectProp.objectReferenceValue;
            if ((IntegrationStyle)_integrationProp.enumValueIndex == IntegrationStyle.BroAudioComponent && targetObject)
            {
                string componentSettings = IssueReportCollector.CollectComponentSettings(targetObject, problemSoundEntities);
                sb.AppendLine(!string.IsNullOrEmpty(componentSettings)
                    ? componentSettings
                    : _instruction.GetText(Instruction.IssueReport_NoBroAudioComponentsFound));
                sb.AppendLine();
            }

            sb.AppendLine("Environment:");
            sb.AppendLine(IssueEnvironmentSnapshot.Compose((IssueType)_typeProp.enumValueIndex));

            return sb.ToString();
        }

        private string ComposeMarkdown(out string title)
        {
            // Flush this frame's edits into _draft — Compose() below reads the raw field,
            // not the SerializedProperty, and OnGUI's own ApplyModifiedProperties() hasn't run yet.
            _serializedObject.ApplyModifiedProperties();

            var problemSoundEntities = GetProblemSoundEntities();
            var targetObject = _targetObjectProp.objectReferenceValue;
            string collectedComponentMarkdown = (IntegrationStyle)_integrationProp.enumValueIndex == IntegrationStyle.BroAudioComponent
                ? IssueReportCollector.CollectComponentSettings(targetObject, problemSoundEntities)
                : null;

            title = IssueReportMarkdown.ComposeTitle(_draft);
            return IssueReportMarkdown.Compose(_draft, problemSoundEntities, collectedComponentMarkdown);
        }

        private bool NotifyIfBroAudioComponentsMissing()
        {
            var style = (IntegrationStyle)_integrationProp.enumValueIndex;
            if (style != IntegrationStyle.BroAudioComponent)
            {
                return false;
            }

            string collected = IssueReportCollector.CollectComponentSettings(_targetObjectProp.objectReferenceValue, GetProblemSoundEntities());
            if (string.IsNullOrEmpty(collected))
            {
                ShowNotification(new GUIContent(_instruction.GetText(Instruction.IssueReport_NoBroAudioComponentsFound)));
                return true;
            }
            return false;
        }

        private void SaveReport()
        {
            if (NotifyIfBroAudioComponentsMissing())
            {
                return;
            }

            string markdown = ComposeMarkdown(out _);
            EditorGUIUtility.systemCopyBuffer = markdown;

            string fileName = $"BroAudio_Issue_{_draft.Type}_{DateTime.Now:yyyyMMdd-HHmmss}.md";
            string prefsKey = LastSaveDirectoryPrefKey + PlayerSettings.productGUID;
            string lastDirectory = EditorPrefs.GetString(prefsKey, Directory.GetCurrentDirectory());
            string path = EditorUtility.SaveFilePanel("Save Issue Report", lastDirectory, fileName, "md");

            bool wasSaved = false;
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, markdown);
                EditorPrefs.SetString(prefsKey, Path.GetDirectoryName(path));
                wasSaved = true;
            }

            string notification = wasSaved
                ? _instruction.GetText(Instruction.IssueReport_SavedNotification)
                : _instruction.GetText(Instruction.IssueReport_CopiedNotSavedNotification);
            ShowNotification(new GUIContent(notification));
        }

        private void CreateGitHubIssue()
        {
            if (NotifyIfBroAudioComponentsMissing())
            {
                return;
            }

            string markdown = ComposeMarkdown(out string title);
            EditorGUIUtility.systemCopyBuffer = markdown;
            Application.OpenURL(IssueReportMarkdown.BuildGitHubIssueURL(title));
            ShowNotification(new GUIContent(_instruction.GetText(Instruction.IssueReport_CopiedNotSavedNotification)));
        }
    }
}
