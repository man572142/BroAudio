#if PACKAGE_ADDRESSABLES
using Ami.BroAudio.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Ami.BroAudio.Data
{
	public partial class BroAudioClip : IBroAudioClip
	{
        // Unity's serializer always materializes this field, but `new BroAudioClip()` in code does not.
        // Initialize it inline so a code-constructed clip behaves like a deserialized one instead of NRE-ing.
        [SerializeField] private AssetReferenceT<AudioClip> AudioClipAssetReference = new AssetReferenceT<AudioClip>(string.Empty);

        public IKeyEvaluator AddressableKey => AudioClipAssetReference;
        public bool IsLoaded => AudioClip != null || AudioClipAssetReference.Asset != null;

        public bool IsLoading => AudioClipAssetReference.OperationHandle.IsValid() &&
                                 !AudioClipAssetReference.OperationHandle.IsDone;

        public AsyncOperationHandle<AudioClip> LoadAssetAsync()
        {
            // Don't start loading if already loading or loaded
            if (IsLoading || IsLoaded)
            {
                return AudioClipAssetReference.OperationHandle.Convert<AudioClip>();
            }
            return AudioClipAssetReference.LoadAssetAsync();
        }

        public AsyncOperationHandle GetCurrentOperationHandle()
        {
            return AudioClipAssetReference.OperationHandle;
        }

        public void ReleaseAsset()
        {
            if(AudioClipAssetReference.OperationHandle.IsValid())
            {
                Addressables.Release(AudioClipAssetReference.OperationHandle);
            }
        }

        [System.Obsolete("You cannot set the loaded asset for an addressable clip.")]
        public void SetLoadedAsset(AudioClip clip)
        {
        }

        public AudioClip GetAudioClip()
        {
            if (AudioClip != null)
            {
                return AudioClip;
            }

            string assetIdentity = null;
            if (IsAddressablesAvailable())
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    return AudioClipAssetReference.editorAsset;
                }
                assetIdentity = AudioClipAssetReference.editorAsset.name;
#else
                assetIdentity = AudioClipAssetReference.AssetGUID;
#endif
                if (AudioClipAssetReference.Asset is AudioClip clip) // null check
                {
                    return clip;
                }

                if (AudioClipAssetReference.OperationHandle.IsValid() && !AudioClipAssetReference.OperationHandle.IsDone)
                {
                    Debug.LogWarning(Utility.LogTitle + $"AudioClip [<b>{assetIdentity}</b>] is still loading!");
                    return null;
                }

                // Synchronously load the addressable if it's not loaded
                var handle = AudioClipAssetReference.LoadAssetAsync();
                handle.WaitForCompletion();
                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    return handle.Result;
                }
                else
                {
                    throw new BroAudioException($"Failed to synchronously load AudioClip [<b>{assetIdentity}</b>]");
                }
            }
            return AudioClip;
        }

        public bool IsAddressablesAvailable() => AudioClipAssetReference != null && !string.IsNullOrEmpty(AudioClipAssetReference.AssetGUID);
    }
}
#endif