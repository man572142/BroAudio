using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using Ami.Extension.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using static Ami.BroAudio.Editor.Setting.BroAudioGUISetting;
using static Ami.BroAudio.Tools.BroName;
using static Ami.Extension.EditorScriptingExtension;

namespace Ami.BroAudio.Editor
{
    public class InfoEditorWindow : MiEditorWindow
    {
        public const float Gap = 50f;
        public const float CreditsPrefixWidth = 70f;
        public const int CreditsFieldCount = 6;
        public const float DemoReferenceFieldWidth = 200f;
        public const float ParagraphWidth = 350f;
        public const float ButtonWidth = 150f;

        public const string GitURL = "https://github.com/man572142/Bro_Audio";
        public const string DocURL = "https://man572142s-organization.gitbook.io/broaudio";
        public const string KnownIssueURL = "https://man572142s-organization.gitbook.io/broaudio/others/known-issues";
		public const string DiscordURL = "https://discord.gg/z6uNmz6Z3A";

        private UnityEngine.Object[] _creditsObjects = null;
        private BroInstructionHelper _instruction = new BroInstructionHelper();
        private Vector2 _scrollPos = default;

        public override float SingleLineSpace => EditorGUIUtility.singleLineHeight + 5f;


        [MenuItem(InfoWindowMenuPath, false, InfoWindowMenuIndex)]
        [MenuItem(InfoWindowMenuPath_Window, false, InfoWindowMenuIndex)]
        public static void ShowWindow()
        {
            EditorWindow window = GetWindow(typeof(InfoEditorWindow));
            window.minSize = new Vector2(640f, 480f);
            window.titleContent = new GUIContent(MenuItem_Info);
            window.Show();       
        }

        protected override void OnGUI()
        {
            base.OnGUI();

            GUIStyle middleCenterStyle = GUIStyleHelper.MiddleCenterText;
            middleCenterStyle.normal.textColor = GUIStyleHelper.DefaultLabelColor;
            GUIStyle middleCenterRichText = GUIStyleHelper.MiddleCenterRichText;

            Rect drawPosition = new Rect(position) { x = 0f, y = 0f};
            Rect scrollViewRect = new Rect(drawPosition);
            scrollViewRect.x += 20f;
            scrollViewRect.width = position.width - 40f;

            DrawEmptyLine(1);
            _scrollPos = BeginScrollView(scrollViewRect, _scrollPos);
            {
                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Documentation".ToWhiteBold().SetSize(20), middleCenterRichText);
                DrawUrlLink(GetRectAndIterateLine(drawPosition), DocURL, DocURL, TextAnchor.MiddleCenter);

                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Found a bug?".ToBold().SetSize(14).SetColor(FalseColor), middleCenterRichText);
                DrawParagraph(drawPosition, "Refer to the known issues page for updates and solutions.");
                DrawUrlLink(GetRectAndIterateLine(drawPosition), KnownIssueURL, KnownIssueURL, TextAnchor.MiddleCenter);
                DrawEmptyLine(1);
                Rect reportIssueButtonRect = GetRectAndIterateLine(drawPosition).GetHorizontalCenterRect(ButtonWidth, SingleLineSpace * 1.5f);
                if (GUI.Button(reportIssueButtonRect, MenuItem_IssueReport))
                {
                    IssueReportWindow.ShowWindow();
                }
                DrawEmptyLine(1);

                DrawEmptyLine(1);
                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Support & Community".ToWhiteBold().SetSize(20), middleCenterRichText);
                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "GitHub Page", middleCenterStyle);
                DrawUrlLink(GetRectAndIterateLine(drawPosition), GitURL, GitURL, TextAnchor.MiddleCenter);
                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Discord", middleCenterStyle);
                DrawUrlLink(GetRectAndIterateLine(drawPosition), DiscordURL, DiscordURL, TextAnchor.MiddleCenter);
                DrawEmptyLine(2);

                DrawDemoReference(middleCenterRichText, drawPosition);
                DrawAssetCredits(drawPosition, middleCenterRichText);
            }
            EndScrollView();
        }

        private void DrawDemoReference(GUIStyle middleCenterRichText, Rect drawPosition)
        {
            if(_instruction.DemoScene)
            {
                EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Play The Demo!".ToWhiteBold().SetSize(20), middleCenterRichText);
                Rect demoRect = GetRectAndIterateLine(drawPosition).GetHorizontalCenterRect(DemoReferenceFieldWidth, SingleLineSpace);
                Rect demoURPRect = GetRectAndIterateLine(drawPosition).GetHorizontalCenterRect(DemoReferenceFieldWidth, SingleLineSpace);

                EditorGUI.ObjectField(demoRect, _instruction.DemoScene, typeof(SceneAsset), false);
                EditorGUI.ObjectField(demoURPRect, _instruction.URPDemoScene, typeof(SceneAsset), false);
                DrawParagraph(drawPosition, _instruction.GetText(Instruction.PlayDemo), 2);
            }
        }

        private void RemoveDuckVolume()
        {
            GameObject managerObj = Resources.Load(nameof(SoundManager)) as GameObject;
            if(managerObj && managerObj.TryGetComponent<SoundManager>(out var soundManager) && soundManager.AudioMixer)
            {
                AudioMixerGroup masterGroup = soundManager.AudioMixer.FindMatchingGroups(MasterTrackName)?.FirstOrDefault();
                if(masterGroup)
                {
                    BroAudioReflection.RemoveAudioEffect(soundManager.AudioMixer, BroAudioReflection.DuckVolumeEffect, masterGroup);
                }
            }
            else
            {
                Debug.LogError(Utility.LogTitle + $"Removing {BroAudioReflection.DuckVolumeEffect} is failed. The {nameof(SoundManager)} asset or the BroAudioMixer asset is missing");
            }
        }

        private void DrawParagraph(Rect drawPosition, string text, int lineCount = 1)
        {
            Rect rect = new Rect(GetRectAndIterateLine(drawPosition));
            rect.x = (drawPosition.width * 0.5f) - (ParagraphWidth * 0.5f); ;
            rect.width = ParagraphWidth;
            rect.height *= lineCount;

            GUIStyle wordWrapStyle = EditorStyles.label;
            wordWrapStyle.wordWrap = true;
            EditorGUI.LabelField(rect, text, wordWrapStyle);
        }

        private void DrawAssetCredits(Rect drawPosition, GUIStyle middleCenterRichText)
        {
            if (_creditsObjects == null)
            {
                _creditsObjects = Resources.LoadAll(string.Empty, typeof(AssetCredits));
                _creditsObjects ??= Array.Empty<UnityEngine.Object>();
            }

            if (_creditsObjects.Length == 0)
            {
                return;
            }

            drawPosition.x += drawPosition.width * 0.1f;
            drawPosition.xMax -= drawPosition.width * 0.2f;

            DrawEmptyLine(2);
            EditorGUI.LabelField(GetRectAndIterateLine(drawPosition), "Demo Credits".ToWhiteBold().SetSize(20), middleCenterRichText);
            DrawEmptyLine(1);

            foreach (var obj in _creditsObjects)
            {
                if (obj is AssetCredits creditsObj)
                {
                    foreach (var credit in creditsObj.Credits)
                    {
                        if (Event.current.type == EventType.Repaint)
                        {
                            Rect boxRect = GetNextLineRect(this, drawPosition);
                            boxRect.y -= 10f;
                            boxRect.height = SingleLineSpace * CreditsFieldCount + 10f;
                            GUI.skin.box.Draw(boxRect, false, false, false, false);
                        }
                        EditorGUI.ObjectField(GetRectAndIterateLine(drawPosition), credit.Source, typeof(AudioClip), false);
                        DrawSelectableLabelWithPrefix(GetRectAndIterateLine(drawPosition), new GUIContent("Type"), credit.Type.ToString(), CreditsPrefixWidth);
                        DrawSelectableLabelWithPrefix(GetRectAndIterateLine(drawPosition), new GUIContent("Name"), credit.Name, CreditsPrefixWidth);
                        DrawSelectableLabelWithPrefix(GetRectAndIterateLine(drawPosition), new GUIContent("License"), credit.License, CreditsPrefixWidth);
                        DrawSelectableLabelWithPrefix(GetRectAndIterateLine(drawPosition), new GUIContent("Author"), credit.Author, CreditsPrefixWidth);
                        DrawUrlLink(GetRectAndIterateLine(drawPosition), credit.Link, credit.Link);
                        DrawEmptyLine(1);
                    }
                }
            }
        }
    }
}