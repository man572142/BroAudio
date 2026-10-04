using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pins the serialized writeback layer over <see cref="Transport"/>, and the backing-field lookup the
    /// runtime suite's <see cref="TestAudioLibrary"/> reflection depends on.
    /// </summary>
    public class SerializedTransportTests : BroEditorTestFixture
    {
        private static SerializedProperty GetFirstClipProperty(SerializedObject entitySo)
        {
            SerializedProperty clips = entitySo.FindProperty(nameof(AudioEntity.Clips));
            return clips.GetArrayElementAtIndex(0);
        }

        #region SerializedTransport — property mapping + apply contract
        [Test]
        public void SetValue_EachTransportType_WritesToItsOwnDistinctClipField_NoCrossContamination()
        {
            // A swapped pair (e.g. FadeIn <-> FadeOut) would pass any single-field test but fail this one:
            // every field gets a distinct value, so a swap shows up as a mismatch on two fields at once.
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("Mapping", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(10f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);
            var transport = new SerializedTransport(clipProp, 10f);

            transport.SetValue(1f, TransportType.Start);
            transport.SetValue(2f, TransportType.End);
            transport.SetValue(0.5f, TransportType.FadeIn);
            transport.SetValue(1.5f, TransportType.FadeOut);
            transport.SetValue(3f, TransportType.Delay);

            // Read off the object itself, proving the writes were committed, not merely staged.
            BroAudioClip clip = entity.Clips[0];
            Assert.AreEqual(1f, clip.StartPosition, 0.0001f, "Start");
            Assert.AreEqual(2f, clip.EndPosition, 0.0001f, "End");
            Assert.AreEqual(0.5f, clip.FadeIn, 0.0001f, "FadeIn");
            Assert.AreEqual(1.5f, clip.FadeOut, 0.0001f, "FadeOut");
            Assert.AreEqual(3f, clip.Delay, 0.0001f, "Delay");
        }

        [Test]
        public void SetValue_Start_CommitsTheClampedValue_NotTheRawInput()
        {
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("ClampStart", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(5f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);
            var transport = new SerializedTransport(clipProp, 5f);

            transport.SetValue(999f, TransportType.Start);

            Assert.AreEqual(5f, entity.Clips[0].StartPosition, 0.0001f,
                "Transport clamps Start to FullLength when nothing else consumes the budget; the serialized field must hold that clamped value, not 999.");
        }

        [Test]
        public void SetValue_AppliesImmediately_WithoutTheCallerCallingApplyModifiedProperties()
        {
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("SelfApplies", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(10f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);
            var transport = new SerializedTransport(clipProp, 10f);

            transport.SetValue(4f, TransportType.FadeOut);
            // No entitySo.ApplyModifiedProperties() call here — deliberately.

            Assert.AreEqual(4f, entity.Clips[0].FadeOut, 0.0001f,
                "The write should already be committed to the target object without an extra ApplyModifiedProperties call.");
        }
        #endregion

        #region FindBackingFieldProperty — guards TestAudioLibrary's reflection
        [Test]
        public void FindBackingFieldProperty_ResolvesEveryAutoPropertyBackedAudioEntityMember()
        {
            // TestAudioLibrary.SetPrivateField reaches these by "<Name>k__BackingField"; this names a renamed one.
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("BackingFields", BroAudioType.SFX));
            var entitySo = new SerializedObject(entity);

            // Derived from the type, not a hand list, which drifts.
            var members = new List<string>();
            foreach (PropertyInfo property in typeof(AudioEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                FieldInfo backingField = typeof(AudioEntity).GetField($"<{property.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                if (backingField != null && backingField.IsDefined(typeof(SerializeField), false))
                {
                    members.Add(property.Name);
                }
            }

            // Floor, so an empty or partial derivation cannot pass.
            string[] reachedByTheRuntimeSuite =
            {
                nameof(AudioEntity.Loop),
                nameof(AudioEntity.SeamlessLoop),
                nameof(AudioEntity.RandomFlags),
                nameof(AudioEntity.MasterVolume),
                nameof(AudioEntity.VolumeRandomRange),
                nameof(AudioEntity.Pitch),
                nameof(AudioEntity.PitchRandomRange),
                nameof(AudioEntity.Flags),
                nameof(AudioEntity.TransitionTime),
                nameof(AudioEntity.SpatialSetting),
                nameof(AudioEntity.Priority),
            };
            CollectionAssert.IsSubsetOf(reachedByTheRuntimeSuite, members,
                "A member the runtime suite writes by its backing-field name is no longer a serialized auto-property.");

            foreach (string member in members)
            {
                SerializedProperty prop = entitySo.FindBackingFieldProperty(member);
                Assert.IsNotNull(prop, $"AudioEntity.{member}'s backing field property could not be resolved. " +
                    "The runtime PlayMode suite reaches this member by the same backing-field name and would " +
                    "break silently — TestAudioLibrary.SetPrivateField is the caller to update.");
            }
        }

        [Test]
        public void FindBackingFieldProperty_ResolvesAudioAsset_AssetName()
        {
            var asset = NewScriptableObject<AudioAsset>();
            var assetSo = new SerializedObject(asset);

            SerializedProperty prop = assetSo.FindBackingFieldProperty(nameof(AudioAsset.AssetName));

            Assert.IsNotNull(prop, "AudioAsset.AssetName's backing field property could not be resolved.");
        }
        #endregion

        #region TryFindPropertyRelative
        [Test]
        public void TryFindPropertyRelative_KnownRelativeName_ReturnsTrueWithTheProperty()
        {
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("TryTrue", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(1f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);

            bool found = clipProp.TryFindPropertyRelative(nameof(BroAudioClip.StartPosition), out SerializedProperty result);

            Assert.IsTrue(found);
            Assert.IsNotNull(result);
        }

        [Test]
        public void TryFindPropertyRelative_UnknownRelativeName_ReturnsFalseWithNullResult()
        {
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("TryFalse", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(1f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);

            bool found = clipProp.TryFindPropertyRelative("ThisFieldDoesNotExist", out SerializedProperty result);

            Assert.IsFalse(found);
            Assert.IsNull(result);
        }
        #endregion
    }
}