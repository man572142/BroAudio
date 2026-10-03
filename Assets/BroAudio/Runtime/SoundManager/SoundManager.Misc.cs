using UnityEngine;
using Ami.BroAudio.Data;
using Ami.Extension;
using System;
using static Ami.BroAudio.Utility;

namespace Ami.BroAudio.Runtime
{
    public partial class SoundManager : MonoBehaviour
    {
        public AudioClip GetAudioClip(SoundID id)
        {
            return GetAudioClip(id, x => x.PickNewClip());
        }

        public AudioClip GetAudioClip(SoundID id, int velocity)
        {
            return GetAudioClip(id, x => x.PickNewClip(velocity));
        }

        public AudioClip GetAudioClip(SoundID id, PlaybackStage chainedModeStage)
        {
            return GetAudioClip(id, x => x.PickNewClip((int)chainedModeStage));
        }

        private AudioClip GetAudioClip(SoundID id, Func<IAudioEntity, IBroAudioClip> onGetAudioClip)
        {
            if (TryGetEntity(id, out var entity))
            {
                var broClip = onGetAudioClip?.Invoke(entity);
                if (broClip != null)
                {
                    return broClip.GetAudioClip();
                }
            }
            return null;
        }

        public void ResetMultiClipStrategy(SoundID id)
        {
            if(TryGetEntity(id, out IAudioEntity entity))
            {
                entity.ResetMultiClipStrategy();
            }
        }

        public string GetNameByID(SoundID id)
        {
            if(!IsAvailable())
            {
                return string.Empty;
            }
            return id.ToString();
        }

        public bool TryGetEntity(SoundID id, out IAudioEntity entity, bool logError = true)
        {
            entity = id.Entity;
            if (logError)
            {
                if (!id.IsValid())
                {
                    Debug.LogError(Utility.LogTitle + $"The SoundID hasn't been assigned yet! {GetDebugObjectName()}", GetDebugObject());
                    return false;
                }
                return true;
            }

            return true;

            string GetDebugObjectName()
            {
                var obj = GetDebugObject();
                if (obj != null)
                {
                    return $"Source:{obj.name.ToBold()}";
                }
                return string.Empty;
            }

#if UNITY_EDITOR
            GameObject GetDebugObject() => id.DebugObject;
#else
            GameObject GetDebugObject() => null;
#endif
        }

        private bool IsAvailable(bool logError = true)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError(LogTitle + $"The method {"GetNameByID".ToWhiteBold()} is {"Runtime Only".ToBold().SetColor(Color.green)}");
                return false;
            }
            return true;
        }
    }
}