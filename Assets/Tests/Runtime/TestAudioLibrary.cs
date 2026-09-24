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
        /// Creates a playable entity exactly like <see cref="CreateEntity"/>, but owned by
        /// <paramref name="asset"/>, so that <see cref="AudioEntity.PlaybackGroup"/> falls back to the asset's
        /// group and from there to <see cref="RuntimeSetting.GlobalPlaybackGroup"/> - the chain every shipped
        /// entity plays through.
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
        /// Creates a playable entity exactly like <see cref="CreateEntity"/>, but with the per-clip
        /// <see cref="BroAudioClip.Volume"/> and entity <see cref="AudioEntity.MasterVolume"/> authored away
        /// from their shared default of 1f (AudioConstant.FullVolume). Neither factor can be moved off 1
        /// by any other factory here, which leaves AudioPlayer.Playback.cs's `_clip.Volume *
        /// _pref.Entity.GetMasterVolume()` product (SetupClipVolume) unable to
        /// ever read as anything but 1 * 1 in the suite - this is the smallest addition that fixes that.
        /// <para>
        /// <see cref="BroAudioClip.Volume"/> is a plain public field, so it's written directly; MasterVolume
        /// is `private set` like most of <see cref="AudioEntity"/>, so it goes through <see cref="SetPrivateField"/>
        /// against the auto-property's backing field, the same way every other authored AudioEntity field in
        /// this suite (RandomFlags, VolumeRandomRange, Loop, ...) is written.
        /// </para>
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
        /// Creates a playable entity exactly like <see cref="CreateEntity"/>, but with the entity's authored
        /// <see cref="AudioEntity.Pitch"/> moved off <see cref="AudioConstant.DefaultPitch"/>.
        /// <para>
        /// <see cref="AudioEntity.CreateNewInstance"/> always sets Pitch to exactly 1, and nothing else in this
        /// suite moves it, which leaves AudioPlayer.Pitch.cs's <c>GetBasePitch</c> unable to read as anything but
        /// 1 - the same blind spot <see cref="CreateEntityWithVolume"/> exists to remove for clip/master volume.
        /// Pitch is `private set` like most of <see cref="AudioEntity"/>, so it goes through
        /// <see cref="SetPrivateField"/> against the auto-property's backing field.
        /// </para>
        /// </summary>
        public static AudioEntity CreateEntityWithPitch(string name, BroAudioType audioType, float pitch, params AudioClip[] clips)
        {
            AudioEntity entity = CreateEntity(name, audioType, clips);
            SetPrivateField(entity, nameof(AudioEntity.Pitch), pitch);
            return entity;
        }

        /// <summary>
        /// Creates a playable entity with the per-play randomization a designer authors in the Library Manager:
        /// <see cref="AudioEntity.RandomFlags"/> plus the base value and range for each enabled flag.
        /// <para>
        /// <see cref="AudioEntity.GetRandomValue(float, RandomFlag)"/> returns
        /// <c>baseValue + Random.Range(-range * 0.5f, range * 0.5f)</c>, so the base and the range are what
        /// bound every draw; pass the two ranges as *different* values so a test can tell them apart if they
        /// were ever swapped. All four numbers are `private set`, hence <see cref="SetPrivateField"/>.
        /// </para>
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
        /// GUIDs of the suite's own addressable fixtures — <c>Assets/Tests/Fixtures/AddressableTone{A,B}.wav</c>,
        /// addressed as <c>BroAudioTest/ToneA</c> and <c>BroAudioTest/ToneB</c> in the Default Local Group.
        /// <para>
        /// These are half-second sine tones generated by <c>Tools > BroAudio > Tests > Regenerate Addressable
        /// Fixtures</c> and committed, rather than built at runtime like <see cref="CreateClip"/>: an
        /// AssetReference resolves through the AssetDatabase, so an addressable clip has to be a real asset.
        /// They deliberately live outside <c>Assets/BroAudio/Samples</c>, which ships as <c>Samples~</c> and is
        /// therefore invisible to Unity on CI.
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

        /// <summary>
        /// Reads a private field or an auto-property backing field, walking the type hierarchy.
        /// The read counterpart of <see cref="SetPrivateField"/>, for state a type exposes no getter for
        /// (e.g. <c>SoundVolume.Setting</c>'s current volume).
        /// </summary>
        public static T GetPrivateField<T>(object target, string fieldName)
        {
            return (T)GetFieldOrThrow(target, fieldName).GetValue(target);
        }

        /// <summary>
        /// Writes a private field or an auto-property backing field, walking the type hierarchy.
        /// Needed because most of <see cref="AudioEntity"/> is `private set`.
        /// </summary>
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
        /// Every genuinely private, non-auto-property member name the suite reaches by string literal - one
        /// place to update on a rename. A member that already has a compile-checked source (a public
        /// member's <c>nameof</c>, or a production <c>NameOf</c>/<c>EditorPropertyName</c> class) is NOT
        /// duplicated here - except that several of those sources (<c>SoundSource.NameOf</c>,
        /// <c>DefaultPlaybackGroup.NameOf</c>, <see cref="AudioEntity.EditorPropertyName"/>) live behind
        /// <c>#if UNITY_EDITOR</c> in production, while this file's assembly (Tests.asmdef) targets every
        /// platform - so the Runtime suite still has to reach those particular members by string, which is
        /// what the constants below centralize.
        /// <para>
        /// ReflectionCanaryTests resolves every constant here against the production type its nested class is
        /// named after, so a rename fails one test by name instead of whichever tests happen to reach the
        /// member. A new nested class needs an entry in that canary's type map, or the canary fails.
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
                public const string GetCurrentAudioPlayers = "GetCurrentAudioPlayers";
                public const string LocalizedRuntime = "_localizedRuntime";
            }

            /// <summary>
            /// Resolves a private instance method lazily, at first use, throwing the same exception as
            /// <see cref="GetPrivateField{T}"/>/<see cref="SetPrivateField"/> (see <see cref="Unresolved"/>)
            /// instead of leaving a caller to dereference a null MethodInfo.
            /// </summary>
            public static MethodInfo Method(System.Type type, string methodName)
            {
                MethodInfo method = type.GetMethod(methodName, PrivateInstance);
                if (method == null)
                {
                    throw Unresolved(type, methodName);
                }
                return method;
            }

            /// <summary>
            /// The one exception every reflection lookup in the suite throws when a member no longer resolves:
            /// a <see cref="BroAudioException"/> naming the exact type and member. Reused instead of a new
            /// exception type per CLAUDE.md - a renamed reflection target is a genuine test-scaffolding setup
            /// error, not an expected "not found" gameplay path.
            /// </summary>
            internal static BroAudioException Unresolved(System.Type type, string memberName)
                => new BroAudioException($"Reflection: {type.Name}.{memberName} could not be resolved. " +
                    "Renamed or moved? Update TestAudioLibrary.Reflected and its caller.");
        }
    }
}