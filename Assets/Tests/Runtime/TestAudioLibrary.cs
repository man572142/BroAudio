using System.Reflection;
using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
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

        /// <summary>
        /// Matches any log carrying BroAudio's <see cref="Utility.LogTitle"/> tag. The suite checks a provoked log by its
        /// LogType and this tag, never by its sentence: rewording a message is not a behavior change.
        /// </summary>
        public static readonly Regex BroAudioLogPrefix = new Regex(Regex.Escape(Utility.LogTitle));

        /// <summary>
        /// Matches any message at all. Only for a log that carries no BroAudio tag (Unity's own, or one of the untagged
        /// Editor logs in Docs/TEST_FINDINGS.md #34), where the LogType is all there is left to check.
        /// </summary>
        public static readonly Regex AnyLogMessage = new Regex(string.Empty);

        /// <summary>Concrete audio types, i.e. All without the composite flag.</summary>
        public static readonly BroAudioType[] ConcreteAudioTypes =
        {
            BroAudioType.Music, BroAudioType.UI, BroAudioType.Ambience, BroAudioType.SFX, BroAudioType.VoiceOver,
        };

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
            return broClip;
        }

        /// <summary>
        /// Creates a playable entity. Passing no clips generates one 1-second clip.
        /// <para>
        /// The entity has no <see cref="AudioAsset"/>, so <see cref="AudioEntity.PlaybackGroup"/> is null unless a
        /// test wires one onto <see cref="Reflected.AudioEntity.Group"/>, and the global playback group never
        /// applies to it. <see cref="CreateAssetBackedEntity"/> builds the shape the Library Manager produces.
        /// </para>
        /// </summary>
        public static AudioEntity CreateEntity(string name, BroAudioType audioType, params AudioClip[] clips)
            => BuildEntity(null, name, audioType, clips);

        /// <summary>
        /// An empty <see cref="AudioAsset"/>, the container the Library Manager puts every entity in. Its only
        /// role here is the playback-group chain: <see cref="AudioAsset.PlaybackGroup"/> links itself to
        /// <see cref="RuntimeSetting.GlobalPlaybackGroup"/> the first time it is read.
        /// </summary>
        public static AudioAsset CreateAudioAsset(string name = "TestAudioAsset")
        {
            AudioAsset asset = ScriptableObject.CreateInstance<AudioAsset>();
            asset.name = name;
            return asset;
        }

        /// <summary>
        /// Like <see cref="CreateEntity"/>, but owned by <paramref name="asset"/>, so
        /// <see cref="AudioEntity.PlaybackGroup"/> falls back through the asset's group to
        /// <see cref="RuntimeSetting.GlobalPlaybackGroup"/> as shipped entities do.
        /// </summary>
        public static AudioEntity CreateAssetBackedEntity(string name, BroAudioType audioType, AudioAsset asset, params AudioClip[] clips)
        {
            if (!asset)
            {
                throw new System.ArgumentNullException(nameof(asset), "Use CreateEntity for an entity without an AudioAsset.");
            }
            return BuildEntity(asset, name, audioType, clips);
        }

        private static AudioEntity BuildEntity(AudioAsset asset, string name, BroAudioType audioType, AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0)
            {
                clips = new[] { CreateClip(name: name + "Clip") };
            }

            var entity = AudioEntity.CreateNewInstance(asset, name, audioType);
            entity.Clips = new BroAudioClip[clips.Length];
            for (int i = 0; i < clips.Length; i++)
            {
                entity.Clips[i] = CreateBroClip(clips[i]);
            }
            return entity;
        }

        /// <summary>
        /// Like <see cref="CreateEntity"/>, but with per-clip <see cref="BroAudioClip.Volume"/> and
        /// <see cref="AudioEntity.MasterVolume"/> moved off their default of 1, so the clip-volume product in
        /// SetupClipVolume can read as something other than 1 * 1.
        /// </summary>
        public static AudioEntity CreateEntityWithVolume(string name, BroAudioType audioType, float clipVolume, float masterVolume, params AudioClip[] clips)
        {
            AudioEntity entity = CreateEntity(name, audioType, clips);
            foreach (BroAudioClip clip in entity.Clips)
            {
                clip.Volume = clipVolume;
            }
            SetPrivateField(entity, nameof(AudioEntity.MasterVolume), masterVolume);
            return entity;
        }

        /// <summary>
        /// Like <see cref="CreateEntity"/>, but with <see cref="AudioEntity.Pitch"/> moved off
        /// <see cref="AudioConstant.DefaultPitch"/>, so <c>GetBasePitch</c> can read as something other than 1.
        /// </summary>
        public static AudioEntity CreateEntityWithPitch(string name, BroAudioType audioType, float pitch, params AudioClip[] clips)
        {
            AudioEntity entity = CreateEntity(name, audioType, clips);
            SetPrivateField(entity, nameof(AudioEntity.Pitch), pitch);
            return entity;
        }

        /// <summary>
        /// Creates a playable entity with authored <see cref="AudioEntity.RandomFlags"/> plus the base value and
        /// range for each flag. Pass the two ranges as different values, or a swap between them goes unnoticed.
        /// </summary>
        public static AudioEntity CreateRandomizedEntity(string name, BroAudioType audioType, RandomFlag randomFlags,
            float pitch, float pitchRandomRange, float masterVolume, float volumeRandomRange, params AudioClip[] clips)
        {
            AudioEntity entity = CreateEntity(name, audioType, clips);
            SetPrivateField(entity, nameof(AudioEntity.Pitch), pitch);
            SetPrivateField(entity, nameof(AudioEntity.PitchRandomRange), pitchRandomRange);
            SetPrivateField(entity, nameof(AudioEntity.MasterVolume), masterVolume);
            SetPrivateField(entity, nameof(AudioEntity.VolumeRandomRange), volumeRandomRange);
            SetPrivateField(entity, nameof(AudioEntity.RandomFlags), randomFlags);
            return entity;
        }

#if PACKAGE_ADDRESSABLES
        /// <summary>
        /// GUIDs of <c>Assets/Tests/Fixtures/AddressableTone{A,B}.wav</c> (regenerate via
        /// <c>Tools > BroAudio > Tests > Regenerate Addressable Fixtures</c>).
        /// <para>
        /// Committed assets, not runtime clips: an AssetReference resolves through the AssetDatabase. Don't move
        /// them under <c>Assets/BroAudio/Samples</c>: it ships as <c>Samples~</c>, invisible to Unity on CI.
        /// </para>
        /// </summary>
        public static readonly string[] AddressableClipGuids =
        {
            "3466b5a562524ab2aaf09db0429c665f",
            "c3e9b7bf8cf5472d9cffbdafae49b73d",
        };

        /// <summary>
        /// Builds a clip backed by an addressable asset rather than a direct reference, so
        /// <c>IsAddressablesAvailable()</c> reports true and the load paths engage.
        /// </summary>
        public static BroAudioClip CreateAddressableBroClip(string guid)
        {
            var broClip = new BroAudioClip();
            SetPrivateField(broClip, BroAudioClip.NameOf.AudioClipAssetReference, new UnityEngine.AddressableAssets.AssetReferenceT<AudioClip>(guid));
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

        /// <summary>Reads a private field or an auto-property backing field, walking the type hierarchy.</summary>
        public static T GetPrivateField<T>(object target, string fieldName)
        {
            return (T)GetFieldOrThrow(target, fieldName).GetValue(target);
        }

        /// <summary>Writes a private field or an auto-property backing field, walking the type hierarchy.</summary>
        public static void SetPrivateField(object target, string fieldName, object value)
        {
            GetFieldOrThrow(target, fieldName).SetValue(target, value);
        }

        private static FieldInfo GetFieldOrThrow(object target, string fieldName)
        {
            System.Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, PrivateInstance)
                                  ?? type.GetField($"<{fieldName}>k__BackingField", PrivateInstance);
                if (field != null)
                {
                    return field;
                }
                type = type.BaseType;
            }
            throw Reflected.Unresolved(target.GetType(), fieldName);
        }

        /// <summary>
        /// The one home for private member names the suite reaches by string. Add a name here only when it has
        /// no cross-platform compile-checked source: prefer <c>nameof</c> or a production <c>NameOf</c>, but
        /// UNITY_EDITOR-only ones (e.g. <see cref="AudioEntity.EditorPropertyName"/>) can't be used from this
        /// all-platform assembly.
        /// <para>
        /// ReflectionCanaryTests resolves every constant here; a new nested class needs an entry in that
        /// canary's type map, or the canary fails.
        /// </para>
        /// </summary>
        public static class Reflected
        {
            /// <summary>Names on <see cref="AudioEntity"/> with no cross-platform compile-checked source.</summary>
            public static class AudioEntity
            {
                /// <summary>Plain private field (not an auto-property) - shadows its own enum type's name.</summary>
                public const string MulticlipsPlayMode = "MulticlipsPlayMode";
                public const string Group = "_group";
                public const string LocalizedAudio = "_localizedAudio";
            }

            /// <summary>Names on <c>Ami.BroAudio.SoundSource</c>; its own <c>NameOf</c> is UNITY_EDITOR-only.</summary>
            public static class SoundSource
            {
                public const string Sound = "_sound";
                public const string PositionMode = "_positionMode";
                public const string PlayOnEnable = "_playOnEnable";
                public const string OnlyPlayOnce = "_onlyPlayOnce";
                public const string StopOnDisable = "_stopOnDisable";
                public const string OverrideFadeOut = "_overrideFadeOut";
                public const string Delay = "_delay";
                public const string OverrideGroup = "_overrideGroup";
            }

            /// <summary>Names on <c>Ami.BroAudio.DefaultPlaybackGroup</c>; its own <c>NameOf</c> is UNITY_EDITOR-only.</summary>
            public static class DefaultPlaybackGroup
            {
                public const string MaxPlayableCount = "_maxPlayableCount";
                public const string CombFilteringTime = "_combFilteringTime";
                public const string IgnoreCombFilteringIfSameFrame = "_ignoreCombFilteringIfSameFrame";
                public const string IgnoreIfDistanceIsGreaterThan = "_ignoreIfDistanceIsGreaterThan";
                public const string LogCombFilteringWarning = "_logCombFilteringWarning";
            }

            /// <summary>Names on <c>Ami.BroAudio.Runtime.AudioPlayer</c>, which exposes no NameOf of its own.</summary>
            public static class AudioPlayer
            {
                public const string Decorators = "_decorators";
                public const string AddedEffects = "_addedEffects";
            }

            /// <summary>Names on <c>Ami.BroAudio.Runtime.SoundManager</c>, which exposes no NameOf of its own.</summary>
            public static class SoundManager
            {
                public const string LoadedEntityLastPlayedTime = "_loadedEntityLastPlayedTime";
                public const string LocalizedRuntime = "_localizedRuntime";
            }

            /// <summary>Resolves a private instance field lazily, at first use. See <see cref="Method"/>.</summary>
            public static FieldInfo Field(System.Type type, string fieldName)
            {
                FieldInfo field = type.GetField(fieldName, PrivateInstance);
                if (field == null)
                {
                    throw Unresolved(type, fieldName);
                }
                return field;
            }

            /// <summary>Resolves a private instance method lazily, at first use; throws <see cref="Unresolved"/> rather than return null.</summary>
            public static MethodInfo Method(System.Type type, string methodName)
            {
                MethodInfo method = type.GetMethod(methodName, PrivateInstance);
                if (method == null)
                {
                    throw Unresolved(type, methodName);
                }
                return method;
            }

            /// <summary>The exception every reflection lookup in the suite throws when a member no longer resolves.</summary>
            internal static BroAudioException Unresolved(System.Type type, string memberName)
                => new BroAudioException($"Reflection: {type.Name}.{memberName} could not be resolved. " +
                    "Renamed or moved? Update TestAudioLibrary.Reflected and its caller.");
        }
    }
}