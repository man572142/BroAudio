using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins <c>AudioPlayer.SetSpatial</c> landing on the AudioSource, what ResetSpatial clears on recycle,
    /// and <see cref="AudioEntity.Priority"/> reaching <c>AudioSource.priority</c>.
    /// <para>
    /// SetSpatial/ResetSpatial write the AudioSource directly, bypassing <c>AudioSourceProxy</c>'s
    /// modified-flags, so it's ResetSpatial, not the proxy's Dispose, that clears spatial state between uses.
    /// </para>
    /// </summary>
    public class SpatialAndPriorityTests : BroAudioTestFixture
    {
        private const float FloatTolerance = 0.001f;

        private const float CurveTolerance = 0.001f;

        /// <summary>Key by key: AnimationCurve has no usable Equals, and GetCustomCurve() returns a copy.</summary>
        private static void AssertCurveEquals(AnimationCurve expected, AnimationCurve actual, string message)
        {
            Assert.AreEqual(expected.length, actual.length, $"{message} (key count: expected {expected.length}, was {actual.length})");
            for (int i = 0; i < expected.length; i++)
            {
                Assert.AreEqual(expected[i].time, actual[i].time, CurveTolerance, $"{message} (key {i} time)");
                Assert.AreEqual(expected[i].value, actual[i].value, CurveTolerance, $"{message} (key {i} value)");
            }
        }

        #region SetSpatial lands values on the AudioSource
        // Every authored value is off its AudioConstant default, which a fresh source already reads.
        [UnityTest]
        public IEnumerator Play_WithPositionAndSpatialSetting_LandsPanDopplerDistanceRolloffAndReverbCurveOnTheAudioSource()
        {
            SpatialSetting setting = Track(ScriptableObject.CreateInstance<SpatialSetting>());
            setting.StereoPan = -0.6f;
            setting.DopplerLevel = 2.5f;
            setting.MinDistance = 3f;
            setting.MaxDistance = 50f;
            setting.RolloffMode = AudioRolloffMode.Linear;
            setting.ReverbZoneMix = new AnimationCurve(new Keyframe(0f, 0.2f), new Keyframe(1f, 0.8f));

            AudioEntity entity = NewEntity("SpatialLandingSfx", BroAudioType.SFX, NewClip(3f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SpatialSetting), setting);
            SoundID id = IdOf(entity);

            // Without a position, SetSpatialBlend skips SetTo3D (next test).
            IAudioPlayer player = BroAudio.Play(id, new Vector3(10f, 2f, -5f));
            yield return WaitForPlaybackStart(player);

            AudioSource source = InstanceOf(player).GetComponent<AudioSource>();
            Assert.AreEqual(setting.StereoPan, source.panStereo, FloatTolerance, "SetSpatial should write StereoPan straight to AudioSource.panStereo.");
            Assert.AreEqual(setting.DopplerLevel, source.dopplerLevel, FloatTolerance, "SetSpatial should write DopplerLevel straight to AudioSource.dopplerLevel.");
            Assert.AreEqual(setting.MinDistance, source.minDistance, FloatTolerance, "SetSpatial should write MinDistance straight to AudioSource.minDistance.");
            Assert.AreEqual(setting.MaxDistance, source.maxDistance, FloatTolerance, "SetSpatial should write MaxDistance straight to AudioSource.maxDistance.");
            Assert.AreEqual(AudioRolloffMode.Linear, source.rolloffMode, "SetSpatial should write RolloffMode straight to AudioSource.rolloffMode.");
            AssertCurveEquals(setting.ReverbZoneMix, source.GetCustomCurve(AudioSourceCurveType.ReverbZoneMix),
                "A non-default ReverbZoneMix curve should reach the AudioSource via SetCustomCurve rather than being resolved to a flat default.");
        }

        [UnityTest]
        public IEnumerator Play_WithoutAPosition_StaysTwoDimensionalEvenWithANonDefaultSpatialBlendCurveAuthored()
        {
            // Constant 1, so an unguarded SetTo3D would visibly move spatialBlend off the 2D default.
            SpatialSetting setting = Track(ScriptableObject.CreateInstance<SpatialSetting>());
            setting.SpatialBlend = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));

            AudioEntity entity = NewEntity("SpatialBlendGuardSfx", BroAudioType.SFX, NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SpatialSetting), setting);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // No position, no follow target.
            yield return WaitForPlaybackStart(player);

            AudioSource source = InstanceOf(player).GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, source.spatialBlend, FloatTolerance,
                "Without a position or follow target, SetSpatial must leave the source 2D even though the entity authored a fully-3D SpatialBlend curve.");
        }
        #endregion

        #region Recycle: what actually resets, and what does not
        // Pins TEST_FINDINGS #46: a 3D sound, recycled, then a plain 2D sound on the same pooled source.
        [UnityTest]
        [Category("Finding_46")]
        public IEnumerator Recycle_AfterA3DSound_ResetsScalarSpatialStateButLeavesTheCustomRolloffCurveBehind()
        {
            SpatialSetting setting3D = Track(ScriptableObject.CreateInstance<SpatialSetting>());
            setting3D.StereoPan = -0.75f;
            setting3D.DopplerLevel = 3f;
            setting3D.MinDistance = 5f;
            setting3D.MaxDistance = 80f;
            setting3D.ReverbZoneMix = new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(1f, 0.9f));
            setting3D.Spread = new AnimationCurve(new Keyframe(0f, 10f), new Keyframe(1f, 90f));
            setting3D.RolloffMode = AudioRolloffMode.Custom;
            setting3D.CustomRolloff = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.3f, 0.6f), new Keyframe(1f, 0.05f));

            AudioEntity entityA = NewEntity("RecycleSpatialA", BroAudioType.SFX, NewClip(3f));
            TestAudioLibrary.SetPrivateField(entityA, nameof(AudioEntity.SpatialSetting), setting3D);
            SoundID idA = IdOf(entityA);

            IAudioPlayer playerA = BroAudio.Play(idA, new Vector3(3f, 0f, 4f));
            yield return WaitForPlaybackStart(playerA, "the 3D player to start");
            AudioPlayer concreteA = InstanceOf(playerA);
            AudioSource sourceA = concreteA.GetComponent<AudioSource>();

            // Without these, a broken SetSpatial would pass the recycle assertions vacuously.
            Assert.AreEqual(AudioRolloffMode.Custom, sourceA.rolloffMode, "Precondition: entityA should have started with Custom rolloff.");
            AssertCurveEquals(setting3D.CustomRolloff, sourceA.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                "Precondition: the authored CustomRolloff curve should have landed on the AudioSource.");

            BroAudio.Stop(idA, 0f);
            yield return WaitForRecycle(concreteA, "the 3D player to recycle after Stop");

            // The player pool is LIFO and nothing else borrows a player here, so the next Play() reuses this instance.
            AudioEntity entityB = NewEntity("RecycleSpatialB", BroAudioType.SFX, NewClip(2f));
            SoundID idB = IdOf(entityB); // No SpatialSetting: the plain 2D "UI click" from the task description.

            IAudioPlayer playerB = BroAudio.Play(idB); // No position.
            yield return WaitForPlaybackStart(playerB, "the reused player's second playback to start");
            AudioPlayer concreteB = InstanceOf(playerB);
            Assert.AreSame(concreteA, concreteB, "The pool should hand the just-recycled player back on the very next Play().");
            AudioSource sourceB = concreteB.GetComponent<AudioSource>();

            // entityB has no SpatialSetting and no position, so its own play writes none of these: what they
            // read is purely ResetSpatial's residue from entityA.
            Assert.AreEqual(AudioConstant.DefaultPanStereo, sourceB.panStereo, FloatTolerance, "panStereo should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultDoppler, sourceB.dopplerLevel, FloatTolerance, "dopplerLevel should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.AttenuationMinDistance, sourceB.minDistance, FloatTolerance, "minDistance should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.AttenuationMaxDistance, sourceB.maxDistance, FloatTolerance, "maxDistance should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultReverZoneMix, sourceB.reverbZoneMix, FloatTolerance, "reverbZoneMix should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultSpread, sourceB.spread, FloatTolerance, "spread should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultRolloffMode, sourceB.rolloffMode, "rolloffMode should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, sourceB.spatialBlend, FloatTolerance, "spatialBlend should have been reset by ResetSpatial (entityB was also played without a position).");

            AssertCurveEquals(setting3D.CustomRolloff, sourceB.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                "characterizes a defect: CustomRolloff curve DATA survives recycling untouched even though rolloffMode itself was correctly reset - see AudioPlayer.ResetSpatial().");
        }
        #endregion

        #region Priority
        [UnityTest]
        public IEnumerator Play_WithNonDefaultPriority_ReachesAudioSourcePriority()
        {
            // Neither DefaultPriority nor HighestPriority, so a skipped assignment can't pass by coincidence.
            const int NonDefaultPriority = 40;
            AudioEntity entity = NewEntity("PrioritySfx", BroAudioType.SFX, NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Priority), NonDefaultPriority);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioSource source = InstanceOf(player).GetComponent<AudioSource>();
            Assert.AreEqual(NonDefaultPriority, source.priority, "entity.Priority should reach AudioSource.priority on play, via PlayControl's assignment.");
        }

        [UnityTest]
        public IEnumerator AsBGM_OverridesEntityPriorityToHighestPriorityRegardlessOfTheEntitysOwnValue()
        {
            // Neither HighestPriority nor DefaultPriority, so only the BGM override can produce the asserted value.
            const int EntityOwnPriority = 200;
            AudioEntity entity = NewEntity("PriorityBgm", BroAudioType.Music, NewClip(3f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Priority), EntityOwnPriority);
            SoundID id = IdOf(entity);

            // AsBGM() must be called in the same frame: Play only enqueues, so PlayControl sees the decorator.
            IAudioPlayer player = BroAudio.Play(id);
            player.AsBGM();
            yield return WaitForPlaybackStart(player);

            AudioSource source = InstanceOf(player).GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.HighestPriority, source.priority,
                "A BGM player must always play at HighestPriority, overriding whatever the entity itself authored.");
        }
        #endregion
    }
}