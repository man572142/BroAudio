using UnityEditor;
using UnityEngine;
using Ami.Extension;
using System;
using System.Collections.Generic;
using static Ami.Extension.EditorScriptingExtension;
using static Ami.BroAudio.Editor.IconConstant;
using static Ami.BroAudio.Editor.PropertyClipboardDataFactory;

namespace Ami.BroAudio.Editor
{
    public class DrawClipPropertiesHelper
    {
        private struct DraggablePoint
        {
            public Rect Rect;
            public Texture Image;
            public Vector4 ImageBorder;
            public Color ColorTint;
            public Action<float> OnSetPlaybackPosition;

            public DraggablePoint(Rect position) : this()
            {
                Rect = position;
            }

            public bool IsDefault() => Rect == default;

            public void SetPlaybackPosition(float value)
            {
                OnSetPlaybackPosition?.Invoke(value);
            }
        }

        private const float DragPointSizeLength = 20f;

        private readonly GUIContent PlaybackMainLabel = new GUIContent("Playback Position");
        private readonly GUIContent FadeMainLabel = new GUIContent("Fade");
        private readonly GUIContent[] FadeLabels = { new GUIContent("   In"), new GUIContent("Out") };
        private readonly GUIContent[] PlaybackLabels = { new GUIContent("Start"), new GUIContent("End") , new GUIContent("Delay") };
        
        private readonly TransportType[] _allTransportType = Enum.GetValues(typeof(TransportType)) as TransportType[];
        private readonly WaveformRenderHelper _waveformHelper = new WaveformRenderHelper();
        private readonly Dictionary<string, Dictionary<TransportType, DraggablePoint>> _clipDraggablePointsDict = new Dictionary<string, Dictionary<TransportType, DraggablePoint>>();
        private KeyValuePair<string, DraggablePoint> _currDraggedPoint;
        private float _clipPreviewHeight;
        
        private static Color SilentMaskColor => new Color(0.2f, 0.2f, 0.2f, 0.8f);
        private static Color FadingMaskColor => new Color(0.2f, 0.2f, 0.2f, 0.5f);
        private static Color StartEndColor => Color.white;
        private static Color FadingLineColor => Color.green;

        public void DrawPlaybackPositionField(Rect position, ITransport transport)
        {
            transport.Update();
            EditorGUI.BeginChangeCheck();
            DrawMultiFloatField(position, PlaybackMainLabel, PlaybackLabels, transport.PlaybackValues);
            var data = GetPlaybackPosData(transport);
            if (EditorGUI.EndChangeCheck())
            {
                SetPlaybackPositions(transport, data);
            }
            PropertyClipboard.HandleClipboardContextMenu(position, transport, data, SetPlaybackPositions);
            
        }
        
        public static void SetPlaybackPositions(ITransport transport, PlaybackPosData data)
        {
            transport.SetValue(data.StartPosition, TransportType.Start);
            transport.SetValue(data.EndPosition, TransportType.End);
            transport.SetValue(data.Delay, TransportType.Delay);
        }

        public void DrawFadingField(Rect position, ITransport transport)
        {
            transport.Update();
            EditorGUI.BeginChangeCheck();
            DrawMultiFloatField(position, FadeMainLabel, FadeLabels, transport.FadingValues);
            var data = GetFadingData(transport);
            if (EditorGUI.EndChangeCheck())
            {
                SetFadingValues(transport, data);
            }
            
            PropertyClipboard.HandleClipboardContextMenu(position, transport, data, SetFadingValues);
        }
        
        public static void SetFadingValues(ITransport transport, FadingData data)
        {
            transport.SetValue(data.FadeIn, TransportType.FadeIn);
            transport.SetValue(data.FadeOut, TransportType.FadeOut);
        }

        // this nasty workaround is used to get the instant repainting when a draggable point is hovered
        public void DrawDraggableHiddenButton(Rect[] rects, EditorSetting.AudioTypeSetting setting)
        {
            if (Event.current.type == EventType.Repaint && setting.CanDraw(DrawedProperty.ClipPreview))
            {
                for(int i = 0;i < rects.Length;i++)
                {
                    TransportType transportType = (TransportType)i;
                    if(!setting.CanDraw(transportType.GetDrawedProperty()))
                    {
                        GUI.skin.button.Draw(rects[i], false, false, false, false);
                    }
                }
            }
        }

        public void DrawClipWaveformAndVisualEditing(Rect previewRect, ITransport transport, AudioClip audioClip, string clipPath, RequestClipPreview requestClipPreview, Action<ITransport, TransportType, Rect> onDrawValuePeeking = null)
        {
            _clipPreviewHeight = previewRect.height;
            Event currEvent = Event.current;
            float exceedTime = transport.GetExceedTime();
            var draggablePoints = GetOrCreateDraggablePoints(clipPath);

            var points = transport.GetVectorPoints(new Vector2(previewRect.width, _clipPreviewHeight), audioClip);
            DrawWaveformPreview();
            DrawClipPlaybackLine();
            DrawExtraSilence();
            DrawDraggable();
            DrawClipLengthLabel();
            HandleDraggable();
            PreviewAudioOnClickedPoint();

            void DrawWaveformPreview()
            {
                if (currEvent.type == EventType.Repaint)
                {
                    GUI.skin.window.Draw(previewRect, false, false, false, false);

                    Rect waveformRect = new Rect(previewRect);
                    // Offset measured by eye; it isn't GUI.skin.window's padding or margin.
                    waveformRect.x += 2f;
                    waveformRect.width -= 2f;
                    if (transport.Delay > transport.StartPosition)
                    {
                        float exceedTimeInPixels = exceedTime / (exceedTime + audioClip.length) * waveformRect.width;
                        waveformRect.width -= exceedTimeInPixels;
                        waveformRect.x += exceedTimeInPixels;
                    }
                    _waveformHelper.RenderClipWaveform(waveformRect, audioClip);
                }
            }

            void DrawClipPlaybackLine()
            {
                GUI.BeginClip(previewRect);
                {
                    Handles.color = FadingLineColor;
                    Handles.DrawAAPolyLine(2f, points.GetVectorsClockwise());

                    Handles.color = StartEndColor;
                    Handles.DrawAAPolyLine(1f, points.Start, new Vector3(points.Start.x, 0f));
                    Handles.DrawAAPolyLine(1f, points.End, new Vector3(points.End.x, 0f));

                    Handles.color = SilentMaskColor;
                    Vector3[] silentToStart = new Vector3[4];
                    silentToStart[0] = Vector3.zero;
                    silentToStart[1] = new Vector3(points.Start.x, 0f);
                    silentToStart[2] = points.Start;
                    silentToStart[3] = new Vector3(0f, _clipPreviewHeight);
                    Handles.DrawAAConvexPolygon(silentToStart);

                    Handles.color = FadingMaskColor;
                    Vector3[] startToFadeIn = new Vector3[3];
                    startToFadeIn[0] = new Vector3(points.Start.x, 0f);
                    startToFadeIn[1] = points.FadeIn;
                    startToFadeIn[2] = points.Start;
                    Handles.DrawAAConvexPolygon(startToFadeIn);

                    Handles.color = FadingMaskColor;
                    Vector3[] fadeOutToEnd = new Vector3[3];
                    fadeOutToEnd[0] = points.FadeOut;
                    fadeOutToEnd[1] = new Vector3(points.End.x, 0f);
                    fadeOutToEnd[2] = points.End;
                    Handles.DrawAAConvexPolygon(fadeOutToEnd);

                    Handles.color = SilentMaskColor;
                    Vector3[] endToSilent = new Vector3[4];
                    endToSilent[0] = new Vector3(points.End.x, 0f);
                    endToSilent[1] = new Vector3(previewRect.width, 0f, 0f);
                    endToSilent[2] = new Vector3(previewRect.width, _clipPreviewHeight, 0f);
                    endToSilent[3] = points.End;
                    Handles.DrawAAConvexPolygon(endToSilent);
                }
                GUI.EndClip();
            }

            void DrawDraggable()
            {
                if (currEvent.type == EventType.Repaint)
                {
                    ScaleMode scaleMode = ScaleMode.ScaleToFit;
                    foreach (var transportType in _allTransportType)
                    {
                        if (transportType != TransportType.Delay) // Delay dragging is not supported
                        {
                            DraggablePoint point = GetDraggablePoint(previewRect, points, transport, transportType);
                            draggablePoints[transportType] = point;
                            EditorGUIUtility.AddCursorRect(point.Rect, MouseCursor.SlideArrow);
                            GUI.DrawTexture(point.Rect, point.Image, scaleMode, true, 0f, point.ColorTint, point.ImageBorder, 0f);
                            onDrawValuePeeking?.Invoke(transport, transportType, point.Rect);
                        }
                    }
                }
            }

            void DrawClipLengthLabel()
            {
                Rect labelRect = new Rect(previewRect);
                labelRect.height = EditorGUIUtility.singleLineHeight;
                labelRect.y = previewRect.yMax - labelRect.height;
                float currentLength = audioClip.length - transport.StartPosition - transport.EndPosition;
                string text = currentLength.ToString("0.000");
                text += transport.Delay > 0 ? " + " + transport.Delay.ToString("0.000") : string.Empty;
                EditorGUI.DropShadowLabel(labelRect, text + "s");
            }

            void HandleDraggable()
            {
                if (currEvent.type == EventType.MouseDown)
                {
                    foreach (DraggablePoint point in draggablePoints.Values)
                    {
                        if (point.Rect.Contains(currEvent.mousePosition))
                        {
                            _currDraggedPoint = new KeyValuePair<string, DraggablePoint>(clipPath, point);
                            currEvent.Use();
                            break;
                        }
                    }
                }
                else if (currEvent.type == EventType.MouseDrag && _currDraggedPoint.Key == clipPath && !_currDraggedPoint.Value.IsDefault())
                {
                    float posInSeconds = currEvent.mousePosition.Scoping(previewRect).x / previewRect.width * audioClip.length;
                    _currDraggedPoint.Value.SetPlaybackPosition(posInSeconds);
                    currEvent.Use();
                }
                else if (currEvent.type == EventType.MouseUp)
                {
                    _currDraggedPoint = default;
                }

#if BroAudio_DevOnly && BroAudio_ShowClipDraggableArea
                foreach (var point in draggablePoints.Values)
                {
                    EditorGUI.DrawRect(point.Rect, new Color(1f, 1f, 1f, 0.3f));
                }
#endif
            }

            void DrawExtraSilence()
            {
                float delayInPixels = (transport.Delay / (audioClip.length + exceedTime)) * previewRect.width;
                Rect silentRect = new Rect(previewRect);
                silentRect.width = delayInPixels;
                silentRect.x += (transport.StartPosition + exceedTime) / (exceedTime + audioClip.length) * previewRect.width - delayInPixels;
                EditorGUI.DrawRect(silentRect, SilentMaskColor);
                EditorGUI.DropShadowLabel(silentRect, "Add Silence");
            }

            void PreviewAudioOnClickedPoint()
            {
                if ((currEvent.type == EventType.MouseDown || currEvent.type == EventType.MouseDrag)
                    && previewRect.Contains(currEvent.mousePosition))
                {
                    float clickedPoint = currEvent.mousePosition.Scoping(previewRect).x / previewRect.width;
                    var req = currEvent.CreatePreviewRequest(audioClip);
                    req.StartPosition = clickedPoint * audioClip.length;
                    requestClipPreview?.Invoke(clipPath, req);
                    EditorAudioPreviewer.Instance.PlaybackIndicator.SetClipInfo(previewRect, req);
                    currEvent.Use();
                }
            }
        }

        private Dictionary<TransportType, DraggablePoint> GetOrCreateDraggablePoints(string clipPath)
        {
            if (!_clipDraggablePointsDict.TryGetValue(clipPath, out var draggablePoints))
            {
                draggablePoints = new Dictionary<TransportType, DraggablePoint>()
                    {
                        { TransportType.Start,default},{ TransportType.FadeIn,default},{ TransportType.FadeOut,default},{ TransportType.End,default},
                    };
                _clipDraggablePointsDict.Add(clipPath, draggablePoints);
            }

            return draggablePoints;
        }

        private static DraggablePoint GetDraggablePoint(Rect waveformRect,TransportVectorPoints points, ITransport transport,TransportType transportType)
        {
            Rect rect = GetDraggableRect(waveformRect, points, transportType);
            return transportType switch
            {
                TransportType.Start => new DraggablePoint(rect)
                {
                    Image = EditorGUIUtility.IconContent(PlaybackPosIcon).image,
                    ImageBorder = new Vector4(DragPointSizeLength * 0.5f, 0f, 0f, 0f),
                    ColorTint = StartEndColor,
                    OnSetPlaybackPosition = posInSec => transport.SetValue(posInSec, transportType),
                },
                TransportType.FadeIn => new DraggablePoint(rect)
                {
                    Image = EditorGUIUtility.IconContent(FadeInIcon).image,
                    ColorTint = FadingLineColor,
                    OnSetPlaybackPosition = posInSec => transport.SetValue(posInSec - transport.StartPosition, transportType),
                },
                TransportType.FadeOut => new DraggablePoint(rect)
                {
                    Image = EditorGUIUtility.IconContent(FadeOutIcon).image,
                    ColorTint = FadingLineColor,
                    OnSetPlaybackPosition = posInSec => transport.SetValue(transport.FullLength - transport.EndPosition - posInSec, transportType),
                },
                TransportType.End => new DraggablePoint(rect)
                {
                    Image = EditorGUIUtility.IconContent(PlaybackPosIcon).image,
                    ImageBorder = new Vector4(0f, 0f, DragPointSizeLength * 0.5f, 0f),
                    ColorTint = StartEndColor,
                    OnSetPlaybackPosition = posInSec => transport.SetValue(transport.FullLength - posInSec, transportType),
                },
                _ => throw new NotImplementedException(),
            };
        }

        private static Rect GetDraggableRect(Rect waveformRect,TransportVectorPoints points, TransportType transportType)
        {
            Vector2 offset = new Vector2(-DragPointSizeLength * 0.5f, -DragPointSizeLength);
            Vector2 dragPointSize = new Vector2(DragPointSizeLength, DragPointSizeLength);
            Vector2 position = transportType switch 
            {
                TransportType.Start => new Vector2(points.Start.x, 0f).DeScope(waveformRect, offset),
                TransportType.FadeIn => new Vector2(points.FadeIn.x, dragPointSize.y).DeScope(waveformRect, offset),
                TransportType.FadeOut => new Vector2(points.FadeOut.x, dragPointSize.y).DeScope(waveformRect, offset),
                TransportType.End => new Vector2(points.End.x, 0f).DeScope(waveformRect, offset),
                _ => throw new NotImplementedException(),
            };
            return new Rect(position, dragPointSize);
        }
    }
}
