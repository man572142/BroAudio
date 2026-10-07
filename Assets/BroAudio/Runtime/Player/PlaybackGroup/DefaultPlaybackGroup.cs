using System;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio
{
    /// <inheritdoc cref="PlaybackGroup"/>
    [CreateAssetMenu(menuName = nameof(BroAudio) + "/Playback Group", fileName = "PlaybackGroup", order = 0)]
    public partial class DefaultPlaybackGroup : PlaybackGroup
    {
        public const float DefaultCombFilteringTime = RuntimeSetting.FactorySettings.CombFilteringPreventionInSeconds;

        #region Max Playable Count Rule
#if UNITY_EDITOR
        [CustomDrawingMethod(typeof(DefaultPlaybackGroup), nameof(DrawMaxPlayableLimitProperty))]
#endif
        [SerializeField]
        [Tooltip("The maximum number of sounds that can be played simultaneously in this group")]
        [ValueButton("Infinity", -1)]
        private MaxPlayableCountRule _maxPlayableCount = -1; 
        #endregion

        #region Comb-Filtering Rule
        [SerializeField]
        [ValueButton("Default", DefaultCombFilteringTime)]
        [Tooltip("Time interval to prevent the Comb-Filtering effect")]
        private CombFilteringRule _combFilteringTime = DefaultCombFilteringTime;

        [SerializeField]
        [DerivativeProperty]
        [InspectorName("Ignore If Same Frame")]
        [Tooltip("Ignore Comb-filtering prevention if identical sounds are played within the same frame")]
        private bool _ignoreCombFilteringIfSameFrame = false;
        
        [SerializeField]
        [DerivativeProperty]
        [InspectorName("Ignore If Distance Is Greater Than")]
        [Tooltip("Ignore Comb-filtering prevention if identical sounds are played farther apart than the specified distance")]
        private float _ignoreIfDistanceIsGreaterThan = 0.1f;

        [SerializeField]
        [DerivativeProperty(isEnd: true)]
        [InspectorName("Log Warning When Occurs")]
        [Tooltip("Log a warning message when the Comb-Filtering occurs")]
        private bool _logCombFilteringWarning = true;
        #endregion

        private int _currentPlayingCount;
        private Action<SoundID> _decreasePlayingCountDelegate;

        /// <inheritdoc cref="PlaybackGroup.InitializeRules"/>
        protected override IEnumerable<IRule> InitializeRules()
        {
            yield return Initialize(_maxPlayableCount, IsPlayableLimitNotReached);
            yield return Initialize(_combFilteringTime, HasPassedCombFilteringRule);
        }

        /// <summary>
        /// Manages the player and tracks the number of sounds currently playing.
        /// </summary>
        /// <param name="player"></param>
        public override void OnGetPlayer(IAudioPlayer player)
        {
            _currentPlayingCount++;
            _decreasePlayingCountDelegate ??= _ => _currentPlayingCount--;
            player.OnEnd(_decreasePlayingCountDelegate);
        }

        #region Rule Methods
        protected virtual bool IsPlayableLimitNotReached(SoundID id, Vector3 position)
        {
            return _maxPlayableCount <= 0 || _currentPlayingCount < _maxPlayableCount;
        }

        protected virtual bool HasPassedCombFilteringRule(SoundID id, Vector3 currentPlayPos)
        {
            if (_combFilteringTime <= 0f || !SoundManager.Instance.TryGetPreviousPlayerFromCombFilteringPreventer(id, out var previousPlayer))
            {
                return true;
            }
            
            if (!HasPassedCombFilteringRule(previousPlayer, currentPlayPos))
            {
                if (_logCombFilteringWarning)
                {
                    Debug.LogWarning(Utility.LogTitle + $"One of the plays of Audio:{id.ToString().ToWhiteBold()} was rejected by the [Comb Filtering Time] rule.");
                }
                return false;
            }
            return true;
        }

        private bool HasPassedCombFilteringRule(AudioPlayer previousPlayer, Vector3 currentPlayPos)
        {
            int time = TimeExtension.UnscaledCurrentFrameBeganTime;
            int previousPlayTime = previousPlayer.PlaybackStartingTime;
            // the previous has been added to the queue but hasn't played yet, i.e., The current and the previous will end up being played in the same frame
            bool previousIsInQueue = Mathf.Approximately(previousPlayTime, 0f); 
            float difference = time - previousPlayTime;
            bool isSameFrame = previousIsInQueue || Mathf.Approximately(difference, 0f);
            if((isSameFrame && _ignoreCombFilteringIfSameFrame) || (!isSameFrame && HasPassedCombFilteringTime()))
            {
                return true;
            }

            bool currentIsGlobal = Utility.IsPlayedGlobally(currentPlayPos);
            bool previousIsGlobal = Utility.IsPlayedGlobally(previousPlayer.PlayingPosition);
            // Both are played in the world space
            if (!currentIsGlobal && !previousIsGlobal)
            {
                var sqrDistance = (currentPlayPos - previousPlayer.PlayingPosition).sqrMagnitude;
                if (sqrDistance > Mathf.Pow(_ignoreIfDistanceIsGreaterThan, 2))
                {
                    return true;
                }
            }
            
            // Only one is played globally. A global play has no position, so the pair counts as farther apart than any threshold.
            // TODO: use the AudioListener's position as the global position?
            if ((currentIsGlobal != previousIsGlobal) && _ignoreIfDistanceIsGreaterThan > 0f)
            {
                return true;
            }
            return false;

            bool HasPassedCombFilteringTime() => difference >= TimeExtension.SecToMs(_combFilteringTime);
        }
        #endregion

        private void OnEnable()
        {
            _currentPlayingCount = 0;
        }
    }


#if UNITY_EDITOR
    public partial class DefaultPlaybackGroup : PlaybackGroup
    {
        private static object DrawMaxPlayableLimitProperty(SerializedProperty property)
        {
            float currentValue = property.intValue <= 0 ? float.PositiveInfinity : property.intValue;
            EditorGUI.BeginChangeCheck();
            float newValue = EditorGUILayout.FloatField(currentValue);
            if (EditorGUI.EndChangeCheck())
            {
                if (newValue <= 0f || float.IsInfinity(newValue) || float.IsNaN(newValue))
                {
                    property.intValue = -1;
                }
                else
                {
                    property.intValue = newValue > currentValue ? Mathf.CeilToInt(newValue) : Mathf.FloorToInt(newValue);
                }
            }
            return property.intValue <= 0;
        }

        public static class NameOf
        {
            public const string CombFilteringTime = nameof(_combFilteringTime);
        }
    }
#endif
}