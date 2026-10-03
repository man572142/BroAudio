using UnityEngine;
using Ami.BroAudio.Data;
using Ami.Extension;

namespace Ami.BroAudio.Runtime
{
    public struct PlaybackPreference
    {
        public readonly IAudioEntity Entity;
        private readonly Vector3? _position;
        private readonly Transform _followTarget;

        private FadeData _fadeInData;
        private FadeData _fadeOutData;
        private int _contextValue;
        public string SequenceId { get; private set; }
        public double ScheduledStartTime { get; set; }
        public double ScheduledEndTime { get; set; }

        public PlaybackStage ChainedModeStage
        {
            get => (PlaybackStage)_contextValue;
            set => _contextValue = (int)value;
        }

        public Vector3 Position
        {
            get
            {
                if (!_position.HasValue)
                {
                    return _followTarget != null ? _followTarget.position : Utility.GloballyPlayedPosition;
                }
                return _position.Value;
            }
        }

        public PlaybackPreference(IAudioEntity entity, Vector3 position) : this(entity)
        {
            _position = position;
        }

        public PlaybackPreference(IAudioEntity entity, Transform followTarget) : this(entity)
        {
            _followTarget = followTarget;
            // Leave _position unset so Position resolves from the live follow target each frame.
            // Otherwise the base constructor's sentinel makes follow-target plays read as global,
            // which bypasses the distance-based comb-filtering rule.
            _position = null;
        }

        public PlaybackPreference(IAudioEntity entity)
        {
            Entity = entity;
            _fadeInData = new FadeData(entity.GetFadeInEase(), SoundManager.FadeInEase);
            _fadeOutData = new FadeData(entity.GetFadeOutEase(), SoundManager.FadeOutEase);
            SequenceId = null;
            ScheduledStartTime = 0;
            ScheduledEndTime = 0;
            _position = Utility.GloballyPlayedPosition;
            _followTarget = null;
            _contextValue = GetContextValue(entity);
        }

        public void SetFadeInEase(Ease ease)
        {
            _fadeInData.SetEase(ease);
        }

        public void SetFadeOutEase(Ease ease)
        {
            _fadeOutData.SetEase(ease);
        }

        public bool TryGetFadeIn(float clipFade, out float fadeIn, out Ease ease)
        {
            return TryGetOrConsumeOverride(clipFade, SoundManager.FadeInEase, ref _fadeInData, out fadeIn, out ease);
        }

        // Matches whether TryGetFadeIn would actually run a fade (override/base/clip setting > 0) without consuming the override.
        public bool HasFadeIn(float clipFade) => _fadeInData.ResolveFade(clipFade) > FadeData.Immediate;
        public bool HasFadeOutOverride => _fadeOutData.HasPendingOverride;

        public bool TryGetFadeOut(float clipFade, out float fadeOut, out Ease ease)
        {
            return TryGetOrConsumeOverride(clipFade, SoundManager.FadeOutEase, ref _fadeOutData, out fadeOut, out ease);
        }

        private static bool TryGetOrConsumeOverride(float clipFade, Ease clipEase, ref FadeData overrideData, out float fadeIn, out Ease ease)
        {
            fadeIn = clipFade;
            ease = clipEase;
            if (overrideData.TryGetOrConsumeOverride(out var overrideFade, out var overrideEase))
            {
                fadeIn = overrideFade;
                ease = overrideEase;
            }
            return fadeIn > FadeData.Immediate;
        }

        public IBroAudioClip PickNewClip()
        {
            var context = new ClipSelectionContext(_contextValue) { SequenceId = SequenceId };
            return Entity.PickNewClip(context);
        }

        public void ApplySeamlessFade()
        {
            if (Entity.HasLoop(out var loopType, out var transitionTime) && loopType == LoopType.SeamlessLoop)
            {
                _fadeInData.Base = transitionTime;
                _fadeOutData.Base = transitionTime;
            }
        }

        public PlaybackPreference SetNextFadeIn(float fadeTime)
        {
            _fadeInData.Next = fadeTime;
            return this;
        }

        public PlaybackPreference SetNextFadeOut(float fadeTime)
        {
            _fadeOutData.Next = fadeTime;
            return this;
        }

        public void SetVelocity(int velocity)
        {
            if (Entity.PlayMode != MulticlipsPlayMode.Velocity)
            {
                Debug.LogError(Utility.LogTitle + $"Cannot set velocity on [{Entity}] because it's not using VelocityPlayMode. (current : {Entity.PlayMode})");
                return;
            }
            _contextValue = velocity;
        }

        public void SetSequenceId(string sequenceId)
        {
            if (Entity.PlayMode != MulticlipsPlayMode.Sequence)
            {
                Debug.LogError(Utility.LogTitle + $"Cannot set sequence id on [{Entity}] because it's not using SequencePlayMode. (current : {Entity.PlayMode})");
                return;
            }
            SequenceId = sequenceId;
        }

        public bool HasFollowTarget(out Transform target)
        {
            target = _followTarget;
            return target != null;
        }

        public bool HasSpecifiedPosition(out Vector3 position)
        {
            position = _position.GetValueOrDefault();
            return _position.HasValue && !Utility.IsPlayedGlobally(position);
        }
        
        public bool CanHandoverToLoop()
        {
            // A default/reset pref (Entity == null) belongs to a torn-down player; it has nothing to hand
            // over. This is reachable when pause/resume lets a recycled player fall into BeginHandover.
            if (Entity == null)
            {
                return false;
            }

            if (IsChainedMode() && (ChainedModeStage == PlaybackStage.End || ChainedModeStage == PlaybackStage.None))
            {
                return false;
            }
            return Entity.HasLoop(out _, out _);
        }

        public bool CanHandoverToEnd()
        {
            if (Entity == null)
            {
                return false;
            }

            if (!IsChainedMode() ||
                (Entity is AudioEntity entity && entity.Clips.Length < (int)PlaybackStage.End))
            {
                return false;
            }

            return ChainedModeStage != PlaybackStage.End && ChainedModeStage != PlaybackStage.None;
        }

        public bool IsChainedMode()
        {
            return Entity.PlayMode == MulticlipsPlayMode.Chained;
        }

        public bool HasLoop()
        {
            return Entity.HasLoop(out _, out _);
        }
        
        public bool IsLoop(LoopType targetType)
        {
            return Entity.HasLoop(out var loopType, out _) && loopType == targetType;
        }

        private static int GetContextValue(IAudioEntity entity) => entity.PlayMode switch
        {
            MulticlipsPlayMode.Chained => (int)PlaybackStage.Start,
            _ => 0,
        };
    }
}