using System.Reflection;
using Ami.BroAudio.Data;
using UnityEngine;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Builds audio entities in code so tests never depend on authored assets.
    /// Uses <see cref="AudioEntity.CreateNewInstance"/> — a raw CreateInstance leaves MasterVolume and Pitch at 0.
    /// </summary>
    public static class TestAudioLibrary
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        public const int SampleRate = 44100;

        /// <summary>A procedurally generated sine clip of an exactly known length.</summary>
        public static AudioClip CreateClip(float seconds = 1f, string name = "TestClip")
        {
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(seconds * SampleRate));
            var clip = AudioClip.Create(name, sampleCount, 1, SampleRate, false);
            float[] data = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                data[i] = Mathf.Sin(2f * Mathf.PI * 440f * i / SampleRate) * 0.25f;
            }
            clip.SetData(data, 0);
            return clip;
        }

        public static BroAudioClip CreateBroClip(AudioClip clip)
        {
            var broClip = new BroAudioClip();
            SetPrivateField(broClip, BroAudioClip.NameOf.AudioClip, clip);
            FillNullAssetReference(broClip);
            return broClip;
        }

        /// <summary>
        /// Unity's serializer always materializes the AssetReference field; `new BroAudioClip()` leaves it null and
        /// <c>IsAddressablesAvailable()</c> then throws (see Docs/TEST_FINDINGS.md). Reflection keeps the Tests
        /// assembly free of an Addressables reference and works unchanged when the package is absent.
        /// </summary>
        private static void FillNullAssetReference(BroAudioClip broClip)
        {
            FieldInfo field = typeof(BroAudioClip).GetField(BroAudioClip.NameOf.AudioClipAssetReference, PrivateInstance);
            if (field != null && field.GetValue(broClip) == null)
            {
                field.SetValue(broClip, System.Activator.CreateInstance(field.FieldType, string.Empty));
            }
        }

        /// <summary>Creates a playable entity. Passing no clips generates one 1-second clip.</summary>
        public static AudioEntity CreateEntity(string name, BroAudioType audioType, params AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0)
            {
                clips = new[] { CreateClip(name: name + "Clip") };
            }

            var entity = AudioEntity.CreateNewInstance(null, name, audioType);
            entity.Clips = new BroAudioClip[clips.Length];
            for (int i = 0; i < clips.Length; i++)
            {
                entity.Clips[i] = CreateBroClip(clips[i]);
            }
            return entity;
        }

#if PACKAGE_ADDRESSABLES
        /// <summary>
        /// GUIDs of the two demo clips marked addressable for the suite, addressed as
        /// <c>BroAudioTest/Footstep1</c> and <c>BroAudioTest/Footstep2</c> in the Default Local Group.
        /// </summary>
        public static readonly string[] AddressableClipGuids =
        {
            "98e92f012ca7aac4cb5a10875b88f2b2",
            "81d6cc9391967574c8d850e15adec10c",
        };

        /// <summary>
        /// Builds a clip backed by an addressable asset rather than a direct reference, so
        /// <c>IsAddressablesAvailable()</c> reports true and the load paths engage.
        /// </summary>
        public static BroAudioClip CreateAddressableBroClip(string guid)
        {
            var broClip = new BroAudioClip();
            FieldInfo field = typeof(BroAudioClip).GetField(BroAudioClip.NameOf.AudioClipAssetReference, PrivateInstance);
            field.SetValue(broClip, new UnityEngine.AddressableAssets.AssetReferenceT<AudioClip>(guid));
            return broClip;
        }

        /// <summary>Creates an entity whose clips all resolve through Addressables.</summary>
        public static AudioEntity CreateAddressableEntity(string name, BroAudioType audioType, params string[] guids)
        {
            if (guids == null || guids.Length == 0)
            {
                guids = new[] { AddressableClipGuids[0] };
            }

            var entity = AudioEntity.CreateNewInstance(null, name, audioType);
            entity.UseAddressables = true;
            entity.Clips = new BroAudioClip[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                entity.Clips[i] = CreateAddressableBroClip(guids[i]);
            }
            return entity;
        }
#endif

        /// <summary>
        /// Writes a private field or an auto-property backing field, walking the type hierarchy.
        /// Needed because most of <see cref="AudioEntity"/> is `private set`.
        /// </summary>
        public static void SetPrivateField(object target, string fieldName, object value)
        {
            System.Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, PrivateInstance)
                                  ?? type.GetField($"<{fieldName}>k__BackingField", PrivateInstance);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }
            throw new System.MissingFieldException(target.GetType().Name, fieldName);
        }
    }
}
