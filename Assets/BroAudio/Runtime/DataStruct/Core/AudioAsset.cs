using UnityEngine;
using System.Collections.Generic;
using Ami.BroAudio.Runtime;
using UnityEngine.Serialization;

namespace Ami.BroAudio.Data
{
    public class AudioAsset : ScriptableObject, IAudioAsset
#if UNITY_EDITOR
        , ISerializationCallbackReceiver
#endif
    {
#if UNITY_EDITOR
        [System.Obsolete("ignore obsolete warnings")]
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            upgradeEntities_setup();
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        [System.Obsolete("ignore obsolete warnings")]
        private void upgradeEntities()
        {
            if (Entities == null || Entities.Length == 0)
            {
                return;
            }

            lock (Entities)
            {
                if (Entities == null || Entities.Length == 0)
                {
                    return;
                }

                List<string> convertedPaths = new List<string>();
                UnityEditor.AssetDatabase.StartAssetEditing();

                try
                {
                    var assetOutputPath = "Assets/BroAudio/AudioAssets";

                    try
                    {
                        // reflection since we don't have a reference to editor code
                        assetOutputPath = (string)System.Type.GetType("Ami.BroAudio.Editor.BroEditorUtility, BroAudioEditor", true)
                            .GetProperty("AssetOutputPath", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                            .GetValue(null);

                        if (string.IsNullOrEmpty(assetOutputPath))
                        {
                            assetOutputPath = "Assets/BroAudio/AudioAssets";
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogException(ex);
                    }

                    foreach (var entity in Entities)
                    {
                        var path = System.IO.Path.Combine(assetOutputPath, name, entity.Name + ".asset");

                        if (!System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path)))
                        {
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                        }

                        if (System.IO.File.Exists(path))
                        {
                            Debug.LogError(Utility.LogTitle + $"Audio entity [{entity.Name}] already exists at path [{path}]!");
                        }

                        var newAsset = AudioEntity.ConvertLegacy(entity, this);

                        if (newAsset != null)
                        {
                            UnityEditor.AssetDatabase.CreateAsset(newAsset, path);
                            convertedPaths.Add(path);
                        }
                    }

                    Entities = null;
                    UnityEditor.EditorUtility.SetDirty(this);

                    UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
                }
                finally
                {
                    UnityEditor.AssetDatabase.StopAssetEditing();
                }

                UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceUpdate);

                foreach (var path in convertedPaths)
                {
                    var entity = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioEntity>(path);

                    if (entity != null)
                    {
                        if (ConvertedEntities == null)
                        {
                            ConvertedEntities = new List<AudioEntity>();
                        }

                        ConvertedEntities.Add(entity);
                        UnityEditor.EditorUtility.SetDirty(this);
                    }
                }

                UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
            }
        }

        [System.Obsolete("ignore obsolete warnings")]
        public bool ForceConvertLegacyEntities()
        {
            if (Entities == null || Entities.Length == 0)
            {
                return false;
            }
            upgradeEntities();
            return true;
        }

        [System.Obsolete("ignore obsolete warnings")]
        private void upgradeEntities_setup()
        {
            if (Entities == null || Entities.Length == 0)
            {
                return;
            }

            lock (Entities)
            {
                if (Entities == null || Entities.Length == 0)
                {
                    return;
                }

                UnityEditor.EditorApplication.delayCall += upgradeEntities;
            }
        }
#endif

        [SerializeField]
        [System.Obsolete("Entities are only here for backwards compatibility.", true)]
        private AudioEntity_LEGACY[] Entities;

        [SerializeField]
        [System.Obsolete("Here JUST in case the SoundIDs are not fully converted and need some way to be converted back")]
        private List<AudioEntity> ConvertedEntities;

        [SerializeField, FormerlySerializedAs("Group")]
        private PlaybackGroup _group;
        private PlaybackGroup _upperGroup;
        private bool _hasGroupLinked;
        
        public PlaybackGroup PlaybackGroup
        {
            get
            {
                if (!_hasGroupLinked)
                {
                    LinkPlaybackGroup(SoundManager.Instance.Setting.GlobalPlaybackGroup);
                    _hasGroupLinked = true;
                }
                return _group ? _group : _upperGroup;
            }
        }

        [System.Obsolete("Entities are only here for backwards compatibility.", true)]
        private int EntitiesCount => Entities.Length;

#if UNITY_EDITOR
        [field: SerializeField] public string AssetName { get; set; }

        [SerializeField] private string _assetGUID;

        public string AssetGUID
        {
            get
            {
                if (string.IsNullOrEmpty(_assetGUID))
                {
                    _assetGUID = UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(this));
                }
                return _assetGUID;
            }
            set
            {
                _assetGUID = value;
            }
        }
#endif

        public void LinkPlaybackGroup(PlaybackGroup upperGroup)
        {
            if (_group != null)
            {
                _group.SetParent(upperGroup);         
            }
            else
            {
                _upperGroup = upperGroup;
            }
        }

        [System.Obsolete("Should only be used during conversion")]
        public bool TryGetEntityFromId(int id, out AudioEntity entity)
        {
#if UNITY_EDITOR
            upgradeEntities();
#endif

            if (ConvertedEntities?.Count > 0)
            {
                foreach (var converted in ConvertedEntities)
                {
                    if (converted == null)
                    {
                        continue;
                    }

                    if (converted.ID == id)
                    {
                        entity = converted;
                        return true;
                    }
                }
            }

            entity = null;
            return false;
        }

        [System.Obsolete("Clearing down after ID upgrades are all done", true)]
        public void ClearStoredEntities()
        {
            if (Entities != null) 
            {
                Entities = new AudioEntity_LEGACY[0];
            }

            if (ConvertedEntities != null)
            {
                ConvertedEntities.Clear();
            }

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
#endif
        }

        public static class NameOf
        {
            public const string Group = nameof(_group);
        }
    }
}