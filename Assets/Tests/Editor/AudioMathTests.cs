using System.Reflection;
using Ami.BroAudio.Editor.Tests;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Ami.Extension.AudioConstant;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pure conversion and comparison math: volume/dB conversion, clamp helpers, and <see cref="Effect"/>
    /// ordering (see Docs/inventory/volume-mixer.md). Plain <c>[Test]</c>s only —
    /// no SoundManager, no MonoBehaviour, no Play Mode.
    /// </summary>
    public class AudioMathTests
    {
        /// <summary>
        /// Round-trip tolerance for x.ToDecibel().ToNormalizeVolume() ≈ x. Log10/Pow round-tripping through
        /// float32 accumulates roughly 1e-5..1e-4 relative error; 1% comfortably covers that while still
        /// failing loudly on a real regression (e.g. a dropped allowBoost flag changes the result by 10x+).
        /// </summary>
        private const float RoundTripTolerancePercent = 1f;

        /// <summary>
        /// Effect's (type, value, fading, isDominator) constructor is internal and this assembly has no
        /// InternalsVisibleTo (tests use reflection throughout, see TestAudioLibrary). Reflection is the only
        /// way to build a Volume-type Effect at an arbitrary value — unlike LowPass/HighPass/Custom there is
        /// no public `Effect.Volume(...)` factory. Resolved through <see cref="EditorReflected"/>, which throws
        /// naming the signature if it changes.
        /// </summary>
        private static ConstructorInfo EffectCtor =>
            EditorReflected.Constructor(typeof(Effect), typeof(EffectType), typeof(float), typeof(Fading), typeof(bool));

        private static Effect CreateEffect(EffectType type, float rawValue)
        {
            return (Effect)EffectCtor.Invoke(new object[] { type, rawValue, new Fading(type), false });
        }

        #region ToDecibel / ToNormalizeVolume (0.2)

        [TestCase(0f, true)]
        [TestCase(0f, false)]
        public void ToDecibel_Zero_ClampsToMinVolumeDecibel_WithoutNaNOrInfinity(float vol, bool allowBoost)
        {
            float result = vol.ToDecibel(allowBoost);

            Assert.That(result, Is.EqualTo(MinDecibelVolume).Within(0.01f));
            Assert.IsFalse(float.IsNaN(result), "0 volume must not produce NaN.");
            Assert.IsFalse(float.IsNegativeInfinity(result), "0 volume must clamp to MinVolume before Log10, not hit Log10(0).");
        }

        [Test]
        public void ToDecibel_One_ReturnsZeroDecibels()
        {
            Assert.That(1f.ToDecibel(), Is.EqualTo(FullDecibelVolume).Within(0.001f));
        }

        [Test]
        public void ToDecibel_AboveOne_WithAllowBoostTrue_ComputesBoostedDecibelAboveZero()
        {
            float result = 2f.ToDecibel(allowBoost: true);

            // Hardcoded literal, not Mathf.Log10(2f) * DefaultDecibelVolumeScale: that expression is
            // production's own formula, so it would still pass if DefaultDecibelVolumeScale silently
            // regressed from 20 to 10 (an audible defect) - both sides would shrink together. 20 * log10(2)
            // = 6.0206 is computed independently here so a scale regression is caught. Do not "simplify"
            // this back into the formula.
            Assert.That(result, Is.EqualTo(6.0206f).Within(0.001f));
            Assert.That(result, Is.GreaterThan(FullDecibelVolume));
        }

        [Test]
        public void ToDecibel_AboveOne_WithAllowBoostFalse_ClampsToFullVolumeBeforeConversion()
        {
            Assert.That(2f.ToDecibel(allowBoost: false), Is.EqualTo(FullDecibelVolume).Within(0.001f));
        }

        [Test]
        public void ToDecibel_AboveMaxVolume_WithAllowBoostTrue_ClampsAtMaxDecibelVolume()
        {
            Assert.That(50f.ToDecibel(allowBoost: true), Is.EqualTo(MaxDecibelVolume).Within(0.001f));
        }

        [TestCase(MaxDecibelVolume)]
        [TestCase(MaxDecibelVolume + 10f)]
        public void ToNormalizeVolume_AtOrAboveMaxDecibelVolume_WithAllowBoostTrue_EarlyReturnsMaxVolume(float dB)
        {
            // The dB >= maxVol branch is a hard early return of MaxVolume, not a converging Pow() calculation —
            // exercising a value well past the boundary (not just the boundary itself) proves that.
            Assert.That(dB.ToNormalizeVolume(allowBoost: true), Is.EqualTo(MaxVolume).Within(0.0001f));
        }

        [TestCase(FullDecibelVolume)]
        [TestCase(FullDecibelVolume + 5f)]
        public void ToNormalizeVolume_AtOrAboveFullDecibelVolume_WithAllowBoostFalse_EarlyReturnsFullVolume(float dB)
        {
            Assert.That(dB.ToNormalizeVolume(allowBoost: false), Is.EqualTo(FullVolume).Within(0.0001f));
        }

        [Test]
        public void ToNormalizeVolume_BelowMaxDecibel_ComputesPow()
        {
            Assert.That((-20f).ToNormalizeVolume(), Is.EqualTo(0.1f).Within(0.0001f));
        }

        [TestCase(MinVolume)]
        [TestCase(0.001f)]
        [TestCase(0.01f)]
        [TestCase(0.1f)]
        [TestCase(0.25f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(5f)]
        [TestCase(7.5f)]
        [TestCase(MaxVolume)]
        public void ToDecibel_ThenToNormalizeVolume_RoundTripsApproximately(float x)
        {
            float roundTripped = x.ToDecibel().ToNormalizeVolume();
            Assert.That(roundTripped, Is.EqualTo(x).Within(RoundTripTolerancePercent).Percent);
        }

        #endregion

        #region Slider <-> volume (SoundVolume's slider models)

        // Literal oracles, derived by hand below, for the three slider models SoundVolume and the inspectors
        // use. SoundVolumeTests reads its expected values back through these same functions, so only here is
        // the mapping itself pinned. Do not replace a literal with a call to the function under test.
        //
        // Constants: MinVolume 0.0001, FullVolume 1, MaxVolume 10; MinLogValue -4 (log10 0.0001),
        // FullVolumeLogValue 0, MaxLogValue 1; dB = 20 * log10(volume).
        // BroVolume split points (dB): -80, -60, -36, -24, -12, -6, 0, 6, 20.
        //   allowBoost true : 9 points, 8 segments, step 1/8 = 0.125 per segment.
        //   allowBoost false: points up to 0 dB only (7 points), 6 segments, step 1/6.
        //   Slider = step * segmentIndex + step * (dB - segmentStart) / segmentWidth.

        // BroVolume, volume -> slider:
        //   1.0,   boost : 0 dB, segment 6 [0, 6)            -> 6 * 0.125                         = 0.75
        //   1.0,   !boost: 0 dB is the last point            -> 1
        //   0.1,   boost : -20 dB, segment 3 [-24, -12)      -> 3 * 0.125 + (4 / 12) * 0.125      = 0.4166667
        //   0.001, boost : -60 dB, segment 1 start           -> 1 * 0.125                         = 0.125
        //   2.0,   boost : 6.0206 dB, segment 7 [6, 20)      -> 7 * 0.125 + (0.0206 / 14) * 0.125 = 0.8751839
        //   0.5,   !boost: -6.0206 dB, segment 4 [-12, -6)   -> 4/6 + (5.9794 / 6) / 6            = 0.8327611
        //   10,    boost : 20 dB is the last point           -> 1
        [TestCase(SliderType.BroVolume, 1f, true, 0.75f)]
        [TestCase(SliderType.BroVolume, 1f, false, 1f)]
        [TestCase(SliderType.BroVolume, 0.1f, true, 0.4166667f)]
        [TestCase(SliderType.BroVolume, 0.001f, true, 0.125f)]
        [TestCase(SliderType.BroVolume, 2f, true, 0.8751839f)]
        [TestCase(SliderType.BroVolume, 0.5f, false, 0.8327611f)]
        [TestCase(SliderType.BroVolume, 10f, true, 1f)]
        [TestCase(SliderType.BroVolumeNoField, 0.1f, true, 0.4166667f)] // same model, no numeric field
        // Logarithmic, volume -> slider = InverseLerp(-4, max, log10 volume):
        //   1.0,  boost (max 1) : (0 + 4) / 5  = 0.8      0.1, boost: (-1 + 4) / 5 = 0.6
        //   0.01, !boost (max 0): (-2 + 4) / 4 = 0.5      10, !boost: log 1 is past max, clamped = 1
        [TestCase(SliderType.Logarithmic, 1f, true, 0.8f)]
        [TestCase(SliderType.Logarithmic, 0.1f, true, 0.6f)]
        [TestCase(SliderType.Logarithmic, 0.01f, false, 0.5f)]
        [TestCase(SliderType.Logarithmic, 10f, false, 1f)]
        // Linear, volume -> slider = InverseLerp(0.0001, max, volume):
        //   2.5, boost (max 10): 2.4999 / 9.9999 = 0.2499925      0.5, !boost (max 1): 0.4999 / 0.9999 = 0.49995
        [TestCase(SliderType.Linear, 2.5f, true, 0.2499925f)]
        [TestCase(SliderType.Linear, 0.5f, false, 0.49995f)]
        public void VolumeToSlider_MatchesTheHandDerivedValue(SliderType sliderType, float volume, bool allowBoost, float expectedSlider)
        {
            Assert.That(Utility.VolumeToSlider(sliderType, volume, allowBoost), Is.EqualTo(expectedSlider).Within(0.0001f));
        }

        // BroVolume, slider -> volume (boost only: its 0.125 step is exact in binary, so the segment index the
        // production code truncates to is not at the mercy of float division, as 1/6 would be):
        //   0.75   : segment 6, progress 0   -> 0 dB                        -> 1
        //   0.5    : segment 4, progress 0   -> -12 dB  -> 10^(-12/20)      -> 0.2511886
        //   0.4375 : segment 3, progress 0.5 -> -24 + 12 * 0.5 = -18 dB     -> 10^(-0.9) = 0.1258925
        //   1.0    : the top of the slider   -> MaxVolume (boost)           -> 10
        [TestCase(SliderType.BroVolume, 0.75f, true, 1f)]
        [TestCase(SliderType.BroVolume, 0.5f, true, 0.2511886f)]
        [TestCase(SliderType.BroVolume, 0.4375f, true, 0.1258925f)]
        [TestCase(SliderType.BroVolume, 1f, true, 10f)]
        [TestCase(SliderType.BroVolume, 1f, false, 1f)] // the top of the slider without boost is FullVolume
        [TestCase(SliderType.BroVolumeNoField, 0.5f, true, 0.2511886f)]
        // Logarithmic, slider -> volume = 10^Lerp(-4, max, slider):
        //   0.8, boost : -4 + 5 * 0.8 = 0 -> 1        0.5, boost : -1.5 -> 0.0316228     1, boost: 1 -> 10
        //   0.5, !boost: -4 + 4 * 0.5 = -2 -> 0.01    0.75, !boost: -1 -> 0.1
        [TestCase(SliderType.Logarithmic, 0.8f, true, 1f)]
        [TestCase(SliderType.Logarithmic, 0.5f, true, 0.0316228f)]
        [TestCase(SliderType.Logarithmic, 1f, true, 10f)]
        [TestCase(SliderType.Logarithmic, 0.5f, false, 0.01f)]
        [TestCase(SliderType.Logarithmic, 0.75f, false, 0.1f)]
        // Linear, slider -> volume = slider * (boost ? 10 : 1): 0.25 -> 2.5 / 0.25.
        [TestCase(SliderType.Linear, 0.25f, true, 2.5f)]
        [TestCase(SliderType.Linear, 0.25f, false, 0.25f)]
        public void SliderToVolume_MatchesTheHandDerivedValue(SliderType sliderType, float slider, bool allowBoost, float expectedVolume)
        {
            Assert.That(Utility.SliderToVolume(sliderType, slider, allowBoost), Is.EqualTo(expectedVolume).Within(0.0001f));
        }

        [Test]
        public void BroVolumeToSlider_DefaultAllowBoost_IsTrue()
        {
            // 1.0 sits at 0.75 on the boosted slider and at 1 on the unboosted one (derivations above).
            Assert.That(Utility.BroVolumeToSlider(1f), Is.EqualTo(0.75f).Within(0.0001f));
        }

        #endregion

        #region TempoToTime

        // SeamlessType.Tempo is purely an Editor-authoring convenience: AudioEntityEditor writes
        // TempoToTime(bpm, beats) into the entity's ordinary TransitionTime float, and playback then
        // treats it identically to a Time-authored seamless loop - there is no separate runtime path.
        // So the conversion is the only thing a Tempo loop adds, and it is pinned here rather than by a
        // second PlayMode crossfade test that would re-run an existing scenario.
        [TestCase(120f, 2, 1f)]        // 60/120 * 2
        [TestCase(60f, 1, 1f)]
        [TestCase(120f, 4, 2f)]
        [TestCase(0f, 4, 0f)]          // guards the bpm == 0 early-out rather than dividing by zero
        public void TempoToTime_ConvertsBpmAndBeatsToSeconds(float bpm, int beats, float expected)
        {
            Assert.That(AudioExtension.TempoToTime(bpm, beats), Is.EqualTo(expected).Within(0.0001f));
        }

        #endregion

        #region ClampNormalize / ClampDecibel (0.3)

        [TestCase(-5f, false, MinVolume)]
        [TestCase(0f, false, MinVolume)]
        [TestCase(0.5f, false, 0.5f)]
        [TestCase(1f, false, FullVolume)]
        [TestCase(5f, false, FullVolume)]
        [TestCase(5f, true, 5f)]
        [TestCase(15f, true, MaxVolume)]
        [TestCase(15f, false, FullVolume)]
        public void ClampNormalize_ClampsToExpectedRange(float vol, bool allowBoost, float expected)
        {
            Assert.That(vol.ClampNormalize(allowBoost), Is.EqualTo(expected).Within(0.0001f));
        }

        [TestCase(-100f, false, MinDecibelVolume)]
        [TestCase(-100f, true, MinDecibelVolume)]
        [TestCase(0f, false, FullDecibelVolume)]
        [TestCase(10f, false, FullDecibelVolume)]
        [TestCase(10f, true, 10f)]
        [TestCase(25f, true, MaxDecibelVolume)]
        [TestCase(25f, false, FullDecibelVolume)]
        public void ClampDecibel_ClampsToExpectedRange(float dB, bool allowBoost, float expected)
        {
            Assert.That(dB.ClampDecibel(allowBoost), Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void ClampNormalize_DefaultAllowBoost_IsFalse_UnlikeToDecibelsDefaultOfTrue()
        {
            // Pins the asymmetry flagged in the inventory: ClampNormalize/ClampDecibel default allowBoost to
            // false, but ToDecibel/ToNormalizeVolume default it to true. A "harmonize the defaults" refactor
            // on either side would silently change behavior for every caller that relies on the omitted arg —
            // this test fails loudly on that instead of passing silently.
            Assert.That(5f.ClampNormalize(), Is.EqualTo(FullVolume).Within(0.0001f),
                "ClampNormalize() with no args must clamp an above-unity volume down (allowBoost defaults false).");
        }

        [Test]
        public void ToDecibel_DefaultAllowBoost_IsTrue_UnlikeClampNormalizesDefaultOfFalse()
        {
            // Hardcoded literal (see ToDecibel_AboveOne_WithAllowBoostTrue_ComputesBoostedDecibelAboveZero
            // for why): Mathf.Log10(5f) * DefaultDecibelVolumeScale is production's own formula and would
            // stay green through a DefaultDecibelVolumeScale regression. 20 * log10(5) = 13.9794.
            Assert.That(5f.ToDecibel(), Is.EqualTo(13.9794f).Within(0.001f),
                "ToDecibel() with no args must let an above-unity volume through boosted (allowBoost defaults true).");
        }

        [Test]
        public void ClampDecibel_DefaultAllowBoost_IsFalse_ClampsPositiveDecibelToZero()
        {
            Assert.That(10f.ClampDecibel(), Is.EqualTo(FullDecibelVolume).Within(0.0001f));
        }

        #endregion

        #region Effect.CompareTo / IsMoreIntenseThan (0.4)

        [Test]
        public void CompareTo_Volume_HigherLinearVolumeIsMoreIntense()
        {
            Effect quiet = CreateEffect(EffectType.Volume, 0.5f);
            Effect loud = CreateEffect(EffectType.Volume, 1.5f);

            Assert.That(loud.CompareTo(quiet), Is.GreaterThan(0));
            Assert.IsTrue(loud.IsMoreIntenseThan(quiet));
            Assert.IsFalse(quiet.IsMoreIntenseThan(loud));
        }

        [Test]
        public void CompareTo_HighPass_HigherCutoffIsMoreIntense()
        {
            Effect low = Effect.HighPass(500f);
            Effect high = Effect.HighPass(5000f);

            Assert.That(high.CompareTo(low), Is.GreaterThan(0));
            Assert.IsTrue(high.IsMoreIntenseThan(low));
            Assert.IsFalse(low.IsMoreIntenseThan(high));
        }

        [Test]
        public void CompareTo_LowPass_LowerCutoffIsMoreIntense_SignIsInvertedVsHighPassAndVolume()
        {
            // characterizes: LowPass.CompareTo deliberately flips the sign (Value.CompareTo(other.Value) * -1)
            // because a LOWER cutoff removes more high end, i.e. is the more intense filter — the opposite
            // ordering of HighPass and Volume, where a HIGHER raw value is more intense. This is the single
            // most valuable assertion in this file: a refactor that "fixes" LowPass to match the other two
            // would silently invert audible ducking/filtering behavior.
            Effect narrow = Effect.LowPass(500f);
            Effect wide = Effect.LowPass(5000f);

            Assert.That(narrow.CompareTo(wide), Is.GreaterThan(0), "A lower LowPass cutoff must compare as MORE intense.");
            Assert.IsTrue(narrow.IsMoreIntenseThan(wide));
            Assert.IsFalse(wide.IsMoreIntenseThan(narrow));
        }

        [Test]
        public void CompareTo_SameValue_IsEqual()
        {
            Assert.That(Effect.HighPass(1000f).CompareTo(Effect.HighPass(1000f)), Is.EqualTo(0));
            Assert.That(Effect.LowPass(1000f).CompareTo(Effect.LowPass(1000f)), Is.EqualTo(0));
        }

        #endregion

        #region Effect.IsDefault (0.4)

        [Test]
        public void IsDefault_Volume_ParameterlessConstructor_IsDefault()
        {
            Assert.IsTrue(new Effect(EffectType.Volume).IsDefault());
        }

        [Test]
        public void IsDefault_Volume_NonUnityValue_IsNotDefault()
        {
            Assert.IsFalse(CreateEffect(EffectType.Volume, 2f).IsDefault());
        }

        [Test]
        [Category("Finding_8")]
        public void IsDefault_LowPass_ParameterlessConstructor_IsNotDefault()
        {
            // Characterizes TEST_FINDINGS #8: the parameterless ctor seeds LowPass with
            // BroAdvice.LowPassFrequency (300Hz, a "recommended starting point"), but IsDefault() compares
            // against AudioConstant.MaxFrequency (22000Hz, "no filtering" / neutral). Those are two different
            // constants, so a freshly-constructed `new Effect(EffectType.LowPass)` reads as NOT default.
            // Likely surprising.
            Assert.IsFalse(new Effect(EffectType.LowPass).IsDefault());
        }

        [Test]
        public void IsDefault_LowPass_AtMaxFrequency_IsDefault()
        {
            Assert.IsTrue(Effect.LowPass(MaxFrequency).IsDefault());
            Assert.IsTrue(Effect.ResetLowPass().IsDefault());
        }

        [Test]
        [Category("Finding_8")]
        public void IsDefault_HighPass_ParameterlessConstructor_IsNotDefault()
        {
            // Characterizes TEST_FINDINGS #8: same mismatch as LowPass, mirrored — BroAdvice.HighPassFrequency
            // (2000Hz) vs. AudioConstant.MinFrequency (10Hz, the neutral value IsDefault() actually checks
            // against).
            Assert.IsFalse(new Effect(EffectType.HighPass).IsDefault());
        }

        [Test]
        public void IsDefault_HighPass_AtMinFrequency_IsDefault()
        {
            Assert.IsTrue(Effect.HighPass(MinFrequency).IsDefault());
            Assert.IsTrue(Effect.ResetHighPass().IsDefault());
        }

        #endregion

        #region Effect.Value setter (0.4)

        [Test]
        public void Value_Volume_IsStoredPreConvertedToDecibelAtConstruction()
        {
            Effect effect = CreateEffect(EffectType.Volume, 0.5f);

            // Hardcoded literal, not 0.5f.ToDecibel(): that would make the function under test its own
            // oracle, so it would still pass if the dB conversion regressed (e.g. DefaultDecibelVolumeScale
            // 20 -> 10) since both sides would move together. 20 * log10(0.5) = -6.0206, computed
            // independently here so such a regression is caught. Do not "simplify" this back to .ToDecibel().
            Assert.That(effect.Value, Is.EqualTo(-6.0206f).Within(0.001f));
        }

        [Test]
        public void Value_LowPass_OutOfRangeWrite_IsSilentlyDroppedKeepingPriorValue()
        {
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Effect effect = CreateEffect(EffectType.LowPass, -100f);

            // this(type) runs first and seeds Value with BroAdvice.LowPassFrequency (a valid frequency, so it
            // passes IsValidFrequency silently); the ctor body's overwrite to -100 then fails validation and is
            // dropped, leaving the seeded value in place rather than -100 or 0.
            Assert.That(effect.Value, Is.EqualTo(300f).Within(0.001f));
        }

        [Test]
        public void Value_HighPass_OutOfRangeWrite_IsSilentlyDroppedKeepingPriorValue()
        {
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Effect effect = CreateEffect(EffectType.HighPass, 999999f);

            Assert.That(effect.Value, Is.EqualTo(2000f).Within(0.001f));
        }

        #endregion

        #region AudioExtension.IsValidFrequency bounds (0.4)

        [TestCase(MinFrequency, true)]
        [TestCase(MaxFrequency, true)]
        [TestCase(MinFrequency - 0.01f, false)]
        [TestCase(MaxFrequency + 0.01f, false)]
        public void IsValidFrequency_AtAndOutsideBounds(float freq, bool expectedValid)
        {
            if (!expectedValid)
            {
                LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            }
            Assert.That(AudioExtension.IsValidFrequency(freq), Is.EqualTo(expectedValid));
        }

        #endregion
    }
}