using Ami.BroAudio.Data;
using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// E2 tier: the three <see cref="SerializedProperty"/> helpers in
    /// BroEditorUtility.SerializedProperty.cs. Clip fields are reached through a real
    /// <see cref="AudioEntity"/> (an in-memory ScriptableObject, tracked for teardown) because
    /// <see cref="BroAudioClip"/> is a plain serializable class, not an asset in its own right.
    /// <see cref="SpatialSetting"/> is used for the curve tests since it is itself a
    /// ScriptableObject with AnimationCurve fields, so the fixture's temp-object helper applies directly.
    /// </summary>
    public class SerializedPropertyResetTests : BroEditorTestFixture
    {
        // Distinctive, non-default stand-ins for every field the reset methods touch.
        private const int DistinctiveWeight = 7;
        private const float DistinctiveVolume = 0.42f;
        private const float DistinctiveStartPosition = 1.1f;
        private const float DistinctiveEndPosition = 2.2f;
        private const float DistinctiveFadeIn = 0.3f;
        private const float DistinctiveFadeOut = 0.4f;
        private const float DistinctiveDelay = 0.5f;
        private const string DistinctiveGuid = "distinctive-guid";

        private static void SetDistinctiveClipValues(SerializedProperty clipProp, string guid)
        {
            clipProp.FindPropertyRelative(nameof(BroAudioClip.Weight)).intValue = DistinctiveWeight;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue = DistinctiveVolume;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.StartPosition)).floatValue = DistinctiveStartPosition;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.EndPosition)).floatValue = DistinctiveEndPosition;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeIn)).floatValue = DistinctiveFadeIn;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeOut)).floatValue = DistinctiveFadeOut;
            clipProp.FindPropertyRelative(nameof(BroAudioClip.Delay)).floatValue = DistinctiveDelay;
#if PACKAGE_ADDRESSABLES
            AssetReferenceGuidProp(clipProp).stringValue = guid;
#endif
        }

        private static void AssertDistinctiveClipValuesUnchanged(SerializedProperty clipProp, Object expectedClip, string expectedGuid)
        {
            Assert.AreSame(expectedClip, clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClip).objectReferenceValue);
            Assert.AreEqual(DistinctiveWeight, clipProp.FindPropertyRelative(nameof(BroAudioClip.Weight)).intValue);
            Assert.AreEqual(DistinctiveVolume, clipProp.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue);
            Assert.AreEqual(DistinctiveStartPosition, clipProp.FindPropertyRelative(nameof(BroAudioClip.StartPosition)).floatValue);
            Assert.AreEqual(DistinctiveEndPosition, clipProp.FindPropertyRelative(nameof(BroAudioClip.EndPosition)).floatValue);
            Assert.AreEqual(DistinctiveFadeIn, clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeIn)).floatValue);
            Assert.AreEqual(DistinctiveFadeOut, clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeOut)).floatValue);
            Assert.AreEqual(DistinctiveDelay, clipProp.FindPropertyRelative(nameof(BroAudioClip.Delay)).floatValue);
#if PACKAGE_ADDRESSABLES
            Assert.AreEqual(expectedGuid, AssetReferenceGuidProp(clipProp).stringValue);
#endif
        }

        private static void AssertPlaybackSettingWasReset(SerializedProperty clipProp)
        {
            Assert.AreEqual(AudioConstant.FullVolume, clipProp.FindPropertyRelative(nameof(BroAudioClip.Volume)).floatValue);
            Assert.AreEqual(0f, clipProp.FindPropertyRelative(nameof(BroAudioClip.StartPosition)).floatValue);
            Assert.AreEqual(0f, clipProp.FindPropertyRelative(nameof(BroAudioClip.EndPosition)).floatValue);
            Assert.AreEqual(0f, clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeIn)).floatValue);
            Assert.AreEqual(0f, clipProp.FindPropertyRelative(nameof(BroAudioClip.FadeOut)).floatValue);
            Assert.AreEqual(0f, clipProp.FindPropertyRelative(nameof(BroAudioClip.Delay)).floatValue);
        }

#if PACKAGE_ADDRESSABLES
        private static SerializedProperty AssetReferenceGuidProp(SerializedProperty clipProp)
            => clipProp.FindPropertyRelative(BroAudioClip.NameOf.AudioClipAssetReference)
                       .FindPropertyRelative(BroEditorUtility.AssetReferenceGUIDFieldName);
#endif

        #region ResetBroAudioClipSerializedProperties
        [Test]
        public void ResetBroAudioClipSerializedProperties_ZeroesClipFields_LeavesSiblingClipAndEntityFieldsAlone()
        {
            AudioClip clip0 = Track(TestAudioLibrary.CreateClip(name: "Clip0"));
            AudioClip clip1 = Track(TestAudioLibrary.CreateClip(name: "Clip1"));
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("Entity", BroAudioType.SFX, clip0, clip1));
            var so = new SerializedObject(entity);
            SerializedProperty clips = so.FindProperty(nameof(AudioEntity.Clips));
            SerializedProperty target = clips.GetArrayElementAtIndex(0);
            SerializedProperty sibling = clips.GetArrayElementAtIndex(1);

            // clip0/clip1 are already assigned by CreateEntity, so AudioClip itself starts non-default too.
            SetDistinctiveClipValues(target, DistinctiveGuid);
            SetDistinctiveClipValues(sibling, DistinctiveGuid + "-sibling");
            so.ApplyModifiedProperties();

            BroEditorUtility.ResetBroAudioClipSerializedProperties(target);
            so.ApplyModifiedProperties();

            Assert.IsNull(target.FindPropertyRelative(BroAudioClip.NameOf.AudioClip).objectReferenceValue);
            Assert.AreEqual(0, target.FindPropertyRelative(nameof(BroAudioClip.Weight)).intValue);
            AssertPlaybackSettingWasReset(target);
#if PACKAGE_ADDRESSABLES
            Assert.AreEqual(string.Empty, AssetReferenceGuidProp(target).stringValue);
#endif

            // The reset call was scoped to a single array element - everything else must be untouched.
            AssertDistinctiveClipValuesUnchanged(sibling, clip1, DistinctiveGuid + "-sibling");
            Assert.AreEqual(AudioConstant.FullVolume, entity.MasterVolume);
            Assert.AreEqual(AudioConstant.DefaultPitch, entity.Pitch);
        }
        #endregion

        #region ResetBroClipPlaybackSetting
        [Test]
        public void ResetBroClipPlaybackSetting_ResetsPlaybackFields_LeavesAudioClipAndWeightUntouched()
        {
            AudioClip clip = Track(TestAudioLibrary.CreateClip(name: "Clip"));
            AudioEntity entity = Track(TestAudioLibrary.CreateEntity("Entity", BroAudioType.SFX, clip));
            var so = new SerializedObject(entity);
            SerializedProperty target = so.FindProperty(nameof(AudioEntity.Clips)).GetArrayElementAtIndex(0);

            SetDistinctiveClipValues(target, DistinctiveGuid);
            so.ApplyModifiedProperties();

            BroEditorUtility.ResetBroClipPlaybackSetting(target);
            so.ApplyModifiedProperties();

            // Volume resets to FullVolume, not 0 - the one field that differs from a straight zero-out.
            AssertPlaybackSettingWasReset(target);

            // This is the whole difference from ResetBroAudioClipSerializedProperties: these two survive.
            Assert.AreSame(clip, target.FindPropertyRelative(BroAudioClip.NameOf.AudioClip).objectReferenceValue);
            Assert.AreEqual(DistinctiveWeight, target.FindPropertyRelative(nameof(BroAudioClip.Weight)).intValue);
#if PACKAGE_ADDRESSABLES
            Assert.AreEqual(DistinctiveGuid, AssetReferenceGuidProp(target).stringValue);
#endif
        }
        #endregion

        #region SafeSetCurve
        private static void AssertCurveEquals(AnimationCurve expected, AnimationCurve actual)
        {
            Assert.AreEqual(expected.keys.Length, actual.keys.Length, "Key count differs.");
            for (int i = 0; i < expected.keys.Length; i++)
            {
                Assert.AreEqual(expected.keys[i].time, actual.keys[i].time, $"Key {i} time differs.");
                Assert.AreEqual(expected.keys[i].value, actual.keys[i].value, $"Key {i} value differs.");
            }
        }

        [Test]
        public void SafeSetCurve_NullCurve_LeavesPropertyUnchanged()
        {
            SerializedObject so = NewSerializedObject<SpatialSetting>();
            SerializedProperty curveProp = so.FindProperty(nameof(SpatialSetting.SpatialBlend));
            AnimationCurve original = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            curveProp.animationCurveValue = original;
            so.ApplyModifiedProperties();

            curveProp.SafeSetCurve(null);
            so.ApplyModifiedProperties();

            AssertCurveEquals(original, curveProp.animationCurveValue);
        }

        [Test]
        public void SafeSetCurve_ZeroKeyCurve_LeavesPropertyUnchanged()
        {
            SerializedObject so = NewSerializedObject<SpatialSetting>();
            SerializedProperty curveProp = so.FindProperty(nameof(SpatialSetting.SpatialBlend));
            AnimationCurve original = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            curveProp.animationCurveValue = original;
            so.ApplyModifiedProperties();

            curveProp.SafeSetCurve(new AnimationCurve());
            so.ApplyModifiedProperties();

            AssertCurveEquals(original, curveProp.animationCurveValue);
        }

        [Test]
        public void SafeSetCurve_MultiKeyCurve_IsWritten()
        {
            SerializedObject so = NewSerializedObject<SpatialSetting>();
            SerializedProperty curveProp = so.FindProperty(nameof(SpatialSetting.SpatialBlend));
            AnimationCurve replacement = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

            curveProp.SafeSetCurve(replacement);
            so.ApplyModifiedProperties();

            AssertCurveEquals(replacement, curveProp.animationCurveValue);
        }
        #endregion
    }
}