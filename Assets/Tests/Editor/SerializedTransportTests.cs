using Ami.BroAudio.Data;
using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Covers the serialized writeback layer on top of <see cref="Transport"/> (already covered plain
    /// in <see cref="TransportAndRectMathTests"/>) and the two reflection helpers the runtime suite's
    /// <see cref="TestAudioLibrary"/> leans on to reach private/auto-property state.
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

            // Read straight off the actual object, bypassing SerializedProperty entirely, to prove the
            // writes were committed and not merely staged on the SerializedObject.
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
        public void SetValue_Delay_ClampsOnlyToZero_ViaTheClipField()
        {
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("ClampDelay", BroAudioType.SFX, Track(TestAudioLibrary.CreateClip(5f))));
            var entitySo = new SerializedObject(entity);
            SerializedProperty clipProp = GetFirstClipProperty(entitySo);
            var transport = new SerializedTransport(clipProp, 5f);

            transport.SetValue(-5f, TransportType.Delay);

            Assert.AreEqual(0f, entity.Clips[0].Delay, 0.0001f);
        }

        [Test]
        public void SetValue_AppliesImmediately_WithoutTheCallerCallingApplyModifiedProperties()
        {
            // Contract check: SerializedTransport.SetValue calls ApplyModifiedProperties itself
            // (SerializedTransport.cs line 50) — a caller that also calls it is redundant, not required.
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
            // The runtime suite reaches these same names via reflection (TestAudioLibrary.SetPrivateField
            // falls back to "<Name>k__BackingField") with no test of its own that would notice a rename.
            // A failure here should name the exact member so a rename is caught loudly, in one place.
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("BackingFields", BroAudioType.SFX));
            var entitySo = new SerializedObject(entity);

            string[] members =
            {
                nameof(AudioEntity.Loop),
                nameof(AudioEntity.SeamlessLoop),
                nameof(AudioEntity.RandomFlags),
                nameof(AudioEntity.MasterVolume),
                nameof(AudioEntity.VolumeRandomRange),
                nameof(AudioEntity.Pitch),
                nameof(AudioEntity.PitchRandomRange),
                nameof(AudioEntity.Flags),
            };

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