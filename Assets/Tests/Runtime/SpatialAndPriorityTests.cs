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
    /// Closes two coverage gaps: the spatial half of <c>AudioPlayer.SetSpatial</c> (pan, doppler, min/max
    /// distance, the ReverbZoneMix/Spread/CustomRolloff curves, and the 2D/3D branch in its local
    /// SetSpatialBlend), and <see cref="AudioEntity.Priority"/> reaching <c>AudioSource.priority</c>.
    /// <para>
    /// The headline scenario is the recycle test below. SetSpatial and its counterpart ResetSpatial write
    /// straight to the live <c>UnityEngine.AudioSource</c> component - they never go through
    /// <c>Ami.Extension.AudioSourceProxy</c>'s per-property "was modified" flags, because that proxy is only
    /// instantiated lazily behind the public <c>IAudioPlayer.AudioSource</c> handle (used by a consumer's own
    /// OnStart/OnUpdate callbacks), not by AudioPlayer's own internal playback code. So the proxy's Dispose()
    /// (run from Recycle) is not what resets pan/doppler/distance/rolloff between pooled uses - ResetSpatial,
    /// called unconditionally from EndPlaying() before every Recycle(), is. See the recycle test for exactly
    /// what it resets and what it leaves behind.
    /// </para>
    /// </summary>
    public class SpatialAndPriorityTests : BroAudioTestFixture
    {
        // Scalar AudioSource properties compared against small authored decimals (e.g. -0.6, 2.5, 3, 50).
        private const float FloatTolerance = 0.001f;

        // AnimationCurve keyframes compared key-by-key (AnimationCurve has no value-equality of its own).
        private const float CurveTolerance = 0.001f;

        /// <summary>Reaches through the wrapper BroAudio.Play() returns to the concrete MonoBehaviour, exactly
        /// like AudioEffectTests' own Underlying() - duplicated here rather than shared, since this file may
        /// only touch its own contents. Safe for the same reason: SoundManager.Playback always hands back a
        /// fresh AudioPlayerInstanceWrapper(player) for a plain (non-BGM) play, and AsBGM() decorates the same
        /// underlying instance rather than replacing it.</summary>
        private static AudioPlayer Underlying(IAudioPlayer player) => (AudioPlayer)(AudioPlayerInstanceWrapper)player;

        /// <summary>Key-by-key AnimationCurve comparison - Trap noted in the task brief: AnimationCurve has no
        /// usable Equals, and GetCustomCurve() hands back a copy, not the original reference.</summary>
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
        // Every authored value here is off its AudioConstant default (0, 1, 1, 500, Logarithmic, flat 1.0
        // curve) - see AudioConstant.cs's "Base on AudioSource default values" comment. A fresh or
        // just-reset AudioSource already reads those defaults on its own, so matching them would prove
        // nothing; these values only appear on the source if SetSpatial actually wrote them.
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

            // A specified position is what makes SetSpatialBlend's local SetTo3D() run at all (see the next
            // test for what happens without one).
            IAudioPlayer player = BroAudio.Play(id, new Vector3(10f, 2f, -5f));
            yield return WaitForPlaybackStart(player);

            AudioSource source = Underlying(player).GetComponent<AudioSource>();
            Assert.AreEqual(setting.StereoPan, source.panStereo, FloatTolerance, "SetSpatial should write StereoPan straight to AudioSource.panStereo.");
            Assert.AreEqual(setting.DopplerLevel, source.dopplerLevel, FloatTolerance, "SetSpatial should write DopplerLevel straight to AudioSource.dopplerLevel.");
            Assert.AreEqual(setting.MinDistance, source.minDistance, FloatTolerance, "SetSpatial should write MinDistance straight to AudioSource.minDistance.");
            Assert.AreEqual(setting.MaxDistance, source.maxDistance, FloatTolerance, "SetSpatial should write MaxDistance straight to AudioSource.maxDistance.");
            Assert.AreEqual(AudioRolloffMode.Linear, source.rolloffMode, "SetSpatial should write RolloffMode straight to AudioSource.rolloffMode.");
            AssertCurveEquals(setting.ReverbZoneMix, source.GetCustomCurve(AudioSourceCurveType.ReverbZoneMix),
                "A non-default ReverbZoneMix curve should reach the AudioSource via SetCustomCurve rather than being resolved to a flat default.");
        }

        // characterizes: SetSpatial's local SetSpatialBlend() (AudioPlayer.cs:138-155) is an if/else-if with
        // no else - it only calls SetTo3D() when the play specified a position or a follow target. A fully
        // non-default SpatialBlend curve authored on the entity is simply never consulted otherwise, so the
        // source stays 2D no matter how "3D" the entity's own curve looks.
        [UnityTest]
        public IEnumerator Play_WithoutAPosition_StaysTwoDimensionalEvenWithANonDefaultSpatialBlendCurveAuthored()
        {
            // Constant 1 across the whole domain: if the guard above were deleted and SetTo3D ran
            // unconditionally, this curve would push spatialBlend to (approximately) 1, not leave it at the
            // 2D default - so the assertion below is a real check on the guard, not a default-value coincidence.
            SpatialSetting setting = Track(ScriptableObject.CreateInstance<SpatialSetting>());
            setting.SpatialBlend = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 1f));

            AudioEntity entity = NewEntity("SpatialBlendGuardSfx", BroAudioType.SFX, NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SpatialSetting), setting);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id); // No position, no follow target.
            yield return WaitForPlaybackStart(player);

            AudioSource source = Underlying(player).GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, source.spatialBlend, FloatTolerance,
                "Without a position or follow target, SetSpatial must leave the source 2D even though the entity authored a fully-3D SpatialBlend curve.");
        }
        #endregion

        #region Recycle: what actually resets, and what does not
        // The valuable test in this file. Plays a fully-configured 3D sound, recycles it, then plays a plain
        // 2D sound on the same pooled AudioSource - the exact "pooled player keeps a previous sound's 3D
        // attenuation and serves a 2D UI click" scenario from the task. Whatever the reused source carries is
        // pinned as-is, including the part that looks like a bug.
        [UnityTest]
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
            AudioPlayer concreteA = Underlying(playerA);
            AudioSource sourceA = concreteA.GetComponent<AudioSource>();

            // Precondition, not the point of the test: confirm the 3D configuration actually landed, so a
            // broken SetSpatial couldn't make the recycle assertions below pass for the wrong reason.
            Assert.AreEqual(AudioRolloffMode.Custom, sourceA.rolloffMode, "Precondition: entityA should have started with Custom rolloff.");
            AssertCurveEquals(setting3D.CustomRolloff, sourceA.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                "Precondition: the authored CustomRolloff curve should have landed on the AudioSource.");

            BroAudio.Stop(idA, 0f);
            yield return WaitForRecycle(concreteA, "the 3D player to recycle after Stop");

            // The player pool (ObjectPool<T>) is a plain List<T> where Extract()/Recycle() both operate on
            // the last index - LIFO (see AudioEffectTests' own recycle test for the same reasoning) - and
            // this test is the only thing borrowing/returning a player, so the very next Play() must hand
            // this exact instance back. Without that guarantee "what does the reused source carry" would not
            // be testable at all.
            AudioEntity entityB = NewEntity("RecycleSpatialB", BroAudioType.SFX, NewClip(2f));
            SoundID idB = IdOf(entityB); // No SpatialSetting: the plain 2D "UI click" from the task description.

            IAudioPlayer playerB = BroAudio.Play(idB); // No position.
            yield return WaitForPlaybackStart(playerB, "the reused player's second playback to start");
            AudioPlayer concreteB = Underlying(playerB);
            Assert.AreSame(concreteA, concreteB, "The pool should hand the just-recycled player back on the very next Play().");
            AudioSource sourceB = concreteB.GetComponent<AudioSource>();

            // ResetSpatial() (AudioPlayer.cs:172-184) runs at the end of every playback, before Recycle(). It
            // resets every property it touches back to AudioConstant's own defaults, which mirror Unity's own
            // AudioSource defaults. entityB carries no SpatialSetting of its own, and it was played without a
            // position, so SetSpatial() for entityB returns immediately after its position-less
            // SetSpatialBlend() no-op (AudioPlayer.cs:110-116) - nothing about entityB's own play could have
            // written any of these. Whatever they read as here is purely ResetSpatial's residue (or its
            // absence) from entityA's teardown.
            Assert.AreEqual(AudioConstant.DefaultPanStereo, sourceB.panStereo, FloatTolerance, "panStereo should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultDoppler, sourceB.dopplerLevel, FloatTolerance, "dopplerLevel should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.AttenuationMinDistance, sourceB.minDistance, FloatTolerance, "minDistance should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.AttenuationMaxDistance, sourceB.maxDistance, FloatTolerance, "maxDistance should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultReverZoneMix, sourceB.reverbZoneMix, FloatTolerance, "reverbZoneMix should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultSpread, sourceB.spread, FloatTolerance, "spread should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.DefaultRolloffMode, sourceB.rolloffMode, "rolloffMode should have been reset by ResetSpatial.");
            Assert.AreEqual(AudioConstant.SpatialBlend_2D, sourceB.spatialBlend, FloatTolerance, "spatialBlend should have been reset by ResetSpatial (entityB was also played without a position).");

            // The actual finding. ResetSpatial resets AudioSource.rolloffMode away from Custom (asserted
            // above as passing), but it never calls SetCustomCurve(CustomRolloff, ...) to clear the curve
            // DATA underneath, and there is no scalar shortcut for it: Utility.SetCustomCurveOrResetDefault
            // (Utility.cs:80-84) explicitly refuses to touch AudioSourceCurveType.CustomRolloff and says to
            // use RolloffMode to detect "is default" instead. So entityA's raw CustomRolloff keyframes are
            // still sitting on the AudioSource entityB now plays through - inert today only because
            // rolloffMode itself no longer reads Custom. This is pinning the actual (leaky) behavior, not the
            // intended one; flagged in the report as a finding for TEST_FINDINGS.md.
            AssertCurveEquals(setting3D.CustomRolloff, sourceB.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                "characterizes a defect: CustomRolloff curve DATA survives recycling untouched even though rolloffMode itself was correctly reset - see AudioPlayer.cs:172-184's ResetSpatial().");
        }
        #endregion

        #region Priority
        [UnityTest]
        public IEnumerator Play_WithNonDefaultPriority_ReachesAudioSourcePriority()
        {
            // 40 is neither AudioConstant.DefaultPriority (128, also Unity's own AudioSource component
            // default) nor AudioConstant.HighestPriority (0, the BGM override exercised below) - so this
            // fails if AudioPlayer.Playback.cs:114's assignment were deleted, leaving the source at 128.
            const int NonDefaultPriority = 40;
            AudioEntity entity = NewEntity("PrioritySfx", BroAudioType.SFX, NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Priority), NonDefaultPriority);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioSource source = Underlying(player).GetComponent<AudioSource>();
            Assert.AreEqual(NonDefaultPriority, source.priority, "entity.Priority should reach AudioSource.priority on play (AudioPlayer.Playback.cs:114).");
        }

        [UnityTest]
        public IEnumerator AsBGM_OverridesEntityPriorityToHighestPriorityRegardlessOfTheEntitysOwnValue()
        {
            // 200 sits far from both AudioConstant.HighestPriority (0, the value under test) and
            // AudioConstant.DefaultPriority (128), so this only passes if the BGM-only override at
            // AudioPlayer.Playback.cs:142 actually runs - deleting it would leave the source at 200, the
            // value the plain (non-BGM) assignment on :114 already wrote moments earlier.
            const int EntityOwnPriority = 200;
            AudioEntity entity = NewEntity("PriorityBgm", BroAudioType.Music, NewClip(3f));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Priority), EntityOwnPriority);
            SoundID id = IdOf(entity);

            // AsBGM() attaches the MusicPlayer decorator before SoundManager.LateUpdate drains the Play
            // queue (BroAudio.Play only enqueues), so PlayControl sees it once it actually runs - the same
            // ordering SchedulingAndMusicTests relies on. No prior BGM is active at the start of a test (the
            // base fixture's teardown fully stops everything first), so DoTransition takes its immediate
            // "no prior BGM" path and never waits on a transition.
            IAudioPlayer player = BroAudio.Play(id);
            player.AsBGM();
            yield return WaitForPlaybackStart(player);

            AudioSource source = Underlying(player).GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.HighestPriority, source.priority,
                "A BGM player must always play at HighestPriority, overriding whatever the entity itself authored.");
        }
        #endregion
    }
}