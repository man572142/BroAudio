using System.Collections.Generic;
using UnityEngine;
using Ami.BroAudio.Data;
using static Ami.BroAudio.Utility;

namespace Ami.BroAudio.Runtime
{
    public partial class SoundManager : MonoBehaviour
    {
        private readonly Queue<IPlayable> _playbackQueue = new Queue<IPlayable>();
        private AudioPlayer.HandoverPlayerFactory _scheduleHandover;

        #region Play
        public IAudioPlayer Play(SoundID id, float fadeIn, IPlayableValidator customValidator = null)
        {
            if (IsPlayable(id, customValidator, GloballyPlayedPosition, out var entity, out var player))
            {
                var pref = new PlaybackPreference(entity).SetNextFadeIn(fadeIn);
                return PlayerToPlay(id, player, pref);
            }
            return Empty.AudioPlayer;
        }

        public IAudioPlayer Play(SoundID id, Vector3 position, float fadeIn, IPlayableValidator customValidator = null)
        {
            if (IsPlayable(id, customValidator, position, out var entity, out var player))
            {
                var pref = new PlaybackPreference(entity, position).SetNextFadeIn(fadeIn);
                return PlayerToPlay(id, player, pref);
            }
            return Empty.AudioPlayer;
        }

        public IAudioPlayer Play(SoundID id, Transform followTarget, float fadeIn, IPlayableValidator customValidator = null)
        {
            if (!followTarget)
            {
                Debug.LogError(LogTitle + $"Cannot play {id}: the follow target is null or destroyed.");
                return Empty.AudioPlayer;
            }

            if (IsPlayable(id, customValidator, followTarget.position, out var entity, out var player))
            {
                var pref = new PlaybackPreference(entity, followTarget).SetNextFadeIn(fadeIn);
                return PlayerToPlay(id, player, pref);
            }
            return Empty.AudioPlayer;
        }

        private bool IsPlayable(SoundID id, IPlayableValidator customValidator, Vector3 position, out IAudioEntity entity, out AudioPlayer player)
        {
            player = null;

            if(!TryGetEntity(id, out entity))
            {
                return false;
            }

            var validator = customValidator ?? entity.PlaybackGroup; // entity's runtime group will be set in InitBank() if it's null
            if (validator != null && !validator.IsPlayable(id, position))
            {
                return false;
            }

            player = _audioPlayerPool.Extract();
            validator?.OnGetPlayer(player);
            return true;
        }

        private IAudioPlayer PlayerToPlay(SoundID id, AudioPlayer player, PlaybackPreference pref)
        {
            var audioType = id.ToAudioType();
            var wrapper = new AudioPlayerInstanceWrapper(player);
            player.SetInstanceWrapper(wrapper);
            player.SetPlaybackData(id, pref);

            _playbackQueue.Enqueue(player);

            if (Setting.AlwaysPlayMusicAsBGM && audioType == BroAudioType.Music)
            {
                player.AsBGM().SetTransition(Setting.DefaultBGMTransition, Setting.DefaultBGMTransitionTime);
            }
            
            // Whether there's any group implementing this or not, we're tracking it anyway
            _combFilteringPreventer ??= new Dictionary<SoundID, AudioPlayer>();
            _combFilteringPreventer[id] = player;
            
            if (pref.Entity.HasLoop(out _, out _) || pref.Entity.PlayMode == MulticlipsPlayMode.Chained)
            {
                _scheduleHandover ??= ScheduleNextPlayback;
                player.RequestNextPlayer = _scheduleHandover;
            }

            // Start loading addressable clips if needed
            StartLoadingAddressableClips(pref.Entity, id);

            return wrapper;
        }

        private void StartLoadingAddressableClips(IAudioEntity entity, SoundID id)
        {
#if PACKAGE_ADDRESSABLES
            if (!Setting.AutomaticallyLoadAddressableAudioClips) // You better have preloaded them
            {
                return;
            }

            if (entity is AudioEntity audioEntity && audioEntity.UseAddressables)
            {
                // Start loading all addressable clips asynchronously if not already loaded/loading
                foreach (var clip in audioEntity.Clips)
                {
                    if (clip.IsAddressablesAvailable() && !clip.IsLoaded && !clip.IsLoading)
                    {
                        clip.LoadAssetAsync();

                        // Track that this entity has started loading
                        UpdateLoadedEntityLastPlayedTime(id);
                    }
                }
            }
#endif
        }

        private AudioPlayer ScheduleNextPlayback(PlaybackHandoverData handover)
        {
            var newPlayer = _audioPlayerPool.Extract();
            newPlayer.ReceiveHandover(handover);
            newPlayer.RequestNextPlayer = _scheduleHandover;
            return newPlayer;
        }

        private void RemoveFromPreventer(AudioPlayer target)
        {
            if(!target.IsActive)
            {
                Debug.LogWarning(LogTitle + "Tried to remove an inactive player from the comb-filtering tracker.");
                return;
            }

            if(_combFilteringPreventer.TryGetValue(target.ID, out var player) && player == target)
            {
                _combFilteringPreventer.Remove(target.ID);
            }
        }

        private void LateUpdate()
        {
            while (_playbackQueue.Count > 0)
            {
                _playbackQueue.Dequeue().Play();
            }
        }
        #endregion

        #region Stop
        public void Stop(BroAudioType targetType)
        {
            Stop(targetType,FadeData.UseClipSetting);
        }

        public void Stop(SoundID id)
        {
            Stop(id, FadeData.UseClipSetting);
        }

        public void Stop(SoundID id,float fadeTime)
        {
            StopPlayer(fadeTime, id);
        }

        public void Stop(BroAudioType targetType,float fadeTime)
        {
            targetType = targetType.ConvertEverythingFlag();

            var players = GetCurrentAudioPlayers();
            for (int i = players.Count - 1; i >= 0; i--)
            {
                var player = players[i];
                if (!player.IsActive)
                {
                    continue;
                }

                if (targetType.Contains(player.ID.ToAudioType()))
                {
                    player.Stop(fadeTime);
                }
            }
        }            

        private void StopPlayer(float fadeTime, SoundID identity)
        {
            var players = GetCurrentAudioPlayers();
            for (int i = players.Count - 1; i >= 0; i--)
            {
                var player = players[i];
                if(!player.IsActive)
                {
                    continue;
                }

                if (player.ID.Equals(identity))
                {
                    player.Stop(fadeTime);
                }
            }
        }
        #endregion

        #region Pause
        public void Pause(SoundID id, bool isPause)
        {
            Pause(id, FadeData.UseClipSetting, isPause);
        }

        public void Pause(SoundID id, float fadeTime, bool isPause)
        {
            var players = GetCurrentAudioPlayers();
            for (int i = players.Count - 1; i >= 0; i--)
            {
                var player = players[i];
                if (player.IsActive && player.ID.Equals(id))
                {
                    PausePlayer(player, isPause, fadeTime);
                }
            }
        }

        public void Pause(BroAudioType targetType, bool isPause)
        {
            Pause(targetType, FadeData.UseClipSetting, isPause);
        }

        public void Pause(BroAudioType targetType, float fadeTime, bool isPause)
        {
            targetType = targetType.ConvertEverythingFlag();
            var players = GetCurrentAudioPlayers();
            for (int i = players.Count - 1; i >= 0; i--)
            {
                var player = players[i];
                if (player.IsActive && targetType.Contains(player.ID.ToAudioType()))
                {
                    PausePlayer(player, isPause, fadeTime);
                }
            }
        }

        private static void PausePlayer(AudioPlayer player, bool isPause, float fadeTime)
        {
            if (isPause)
            {
                player.Pause(fadeTime);
            }
            else
            {
                player.UnPause(fadeTime);
            }
        }
        #endregion

        public bool TryGetPreviousPlayerFromCombFilteringPreventer(SoundID id, out AudioPlayer previousPlayer)
        {
            previousPlayer = null;
            return _combFilteringPreventer != null && _combFilteringPreventer.TryGetValue(id, out previousPlayer);
        }
    }
}