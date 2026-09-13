#if PACKAGE_ADDRESSABLES
using System.Collections;
using System.Collections.Generic;
using Ami.Extension;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Ami.BroAudio.Data
{
    public partial class AudioEntity : IAudioEntity
    {
        public bool UseAddressables = false;

        public IEnumerable GetAllAddressableKeys()
        {
            foreach (var clip in Clips)
            {
                yield return clip.AddressableKey;
            }
        }

        public object GetAddressableKey(int index)
        {
            if(index >= 0 && index < Clips.Length)
            {
                return Clips[index].AddressableKey;
            }
            return string.Empty;
        }

        public bool IsLoaded()
        {
            foreach (var clip in Clips)
            {
                if(!clip.IsLoaded)
                {
                    return false;
                }
            }
            return true;
        }

        public bool IsLoaded(int clipIndex)
        {

            return Clips[clipIndex].IsLoaded;
        }

        public bool IsValidClipIndex(int clipIndex, bool logError = true)
        {
            if (clipIndex < 0 || clipIndex >= Clips.Length)
            {
                Debug.LogError(Utility.LogTitle + $"Invalid clip index: {clipIndex}. The audio entity [{Name.ToWhiteBold()}] contains {Clips.Length} clips.");
                return false;
            }
            return true;
        }

        public AsyncOperationHandle<IList<AudioClip>> LoadAssetsAsync()
        {
            // Clips load individually; group their handles into one so callers still get an AsyncOperationHandle<IList<AudioClip>>.
            var handles = new List<AsyncOperationHandle>();

            foreach (var clip in Clips)
            {
                if (clip.IsAddressablesAvailable() && !clip.IsLoaded && !clip.IsLoading)
                {
                    handles.Add(clip.LoadAssetAsync());
                }
                else
                {
                    handles.Add(clip.GetCurrentOperationHandle());
                }
            }

            if (handles.Count == 0)
            {
                Debug.LogError(Utility.LogTitle + $"{Name.ToWhiteBold()} has no clips to load!");
                return default;
            }

            var groupHandle = Addressables.ResourceManager.CreateGenericGroupOperation(handles);

            var finalHandle = Addressables.ResourceManager.CreateChainOperation<IList<AudioClip>>(groupHandle, op =>
            {
                var result = new List<AudioClip>(handles.Count);
                foreach (var h in handles)
                {
                    result.Add(h.Result as AudioClip);
                }
                return Addressables.ResourceManager.CreateCompletedOperation<IList<AudioClip>>(result, string.Empty);
            });

            return finalHandle;
        }

        [System.Obsolete("Batch loading is no longer supported. Use individual clip loading instead.")]
        public void SetLoadedClipList(AsyncOperationHandle<IList<AudioClip>> handle)
        {
            handle.Completed -= SetLoadedClipList;

            if(handle.Status == AsyncOperationStatus.Succeeded && handle.Result.Count == Clips.Length)
            {
                // No longer storing batch loaded clips or calling SetLoadedAsset
                // Individual clips should be loaded separately as needed
            }
            else
            {
                throw handle.OperationException;
            }
        }

        public void ReleaseAllAssets()
        {
            // Release individual clip assets
            foreach (var clip in Clips)
            {
                clip.ReleaseAsset();
            }
        }
    }
}
#endif