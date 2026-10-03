#if PACKAGE_ADDRESSABLES || PACKAGE_LOCALIZATION
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.ResourceManagement.AsyncOperations;
using Ami.BroAudio.Data;
using System.Collections;
using Ami.Extension;

namespace Ami.BroAudio.Runtime
{
    public partial class SoundManager : MonoBehaviour
    {
        public IEnumerable GetAddressableKeys(SoundID id)
        {
            if (TryGetAddressableEntity(id, out var entity))
            {
                return entity.GetAllAddressableKeys();
            }
            return null;
        }

        public object GetAddressableKey(SoundID id, int index)
        {
            if (TryGetAddressableEntity(id, out var entity))
            {
                return entity.GetAddressableKey(index);
            }
            return null;
        }

        public bool IsLoaded(SoundID id)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out _))
            {
                return IsLocalizationClipLoaded(id);
            }
#endif
            return TryGetAddressableEntity(id, out var entity) && entity.IsLoaded();
        }

        public bool IsLoaded(SoundID id, int clipIndex)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out _))
            {
                return IsLocalizationClipLoaded(id);
            }
#endif
            return TryGetAddressableEntity(id, out var entity) && entity.IsLoaded(clipIndex);
        }

        public AsyncOperationHandle<IList<AudioClip>> LoadAllAssetsAsync(SoundID id)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out var localizationEntity))
            {
                return LoadAllLocalizedAssetsAsync(id, localizationEntity);
            }
#endif
            if (TryGetAddressableEntity(id, out var entity))
            {
                var result = entity.LoadAssetsAsync();
                UpdateLoadedEntityLastPlayedTime(id);
                return result;
            }

            return default;
        }

        public AsyncOperationHandle<AudioClip> LoadAssetAsync(SoundID id, int clipIndex)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out var localizationEntity))
            {
                return LoadLocalizedAssetAsync(id, localizationEntity);
            }
#endif
            if (TryGetAddressableEntity(id, out var entity) && clipIndex >= 0 && clipIndex < entity.Clips.Length)
            {
                var clip = entity.Clips[clipIndex];
                var result = clip.LoadAssetAsync();
                UpdateLoadedEntityLastPlayedTime(id);
                return result;
            }
            return default;
        }

        public void ReleaseAllAssets(SoundID id)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out _))
            {
                ReleaseLocalizationClipInternal(id);
                return;
            }
#endif
            if (TryGetAddressableEntity(id, out var entity))
            {
                entity.ReleaseAllAssets();
            }
        }

        public void ReleaseAsset(SoundID id, int clipIndex)
        {
#if PACKAGE_LOCALIZATION
            if (TryGetLocalizationEntity(id, out _))
            {
                ReleaseLocalizationClipInternal(id);
                return;
            }
#endif
            if (TryGetAddressableEntity(id, out var entity) && clipIndex >= 0 && clipIndex < entity.Clips.Length)
            {
                entity.Clips[clipIndex].ReleaseAsset();
            }
        }

        private bool TryGetAddressableEntity(SoundID id, out AudioEntity entity)
        {
            entity = null;
            if(TryGetEntity(id, out var e))
            {
                entity = e as AudioEntity;
                if(!entity.UseAddressables)
                {
                    Debug.LogError(Utility.LogTitle + $"The entity {id.ToString().ToBold()} isn’t marked as addressable. Please check its settings.");
                }
                return entity.UseAddressables;
            }
            return false;
        }
    }
} 
#endif