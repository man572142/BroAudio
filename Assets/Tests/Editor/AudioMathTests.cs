using System.Reflection;
using System.Text.RegularExpressions;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Ami.Extension.AudioConstant;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pure conversion and comparison math: volume/dB conversion, clamp helpers, and <see cref="Effect"/>
    /// ordering (inventory 0.2-0.4, see Docs/inventory/volume-mixer.md). Plain <c>[Test]</c>s only —
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
        /// no public `Effect.Volume(...)` factory.
        /// </summary>
        private static readonly ConstructorInfo _effectCtor = typeof(Effect).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(EffectType), typeof(float), typeof(Fading), typeof(bool) },
            null);

        private static Effect CreateEffect(EffectType type, float rawValue)
        {
            Assert.IsNotNull(_effectCtor, "Reflection: Effect(EffectType, float, Fading, bool) constructor not found - " +
                "renamed or its signature changed? Update AudioMathTests.cs._effectCtor.");
            return (Effect)_effectCtor.Invoke(new object[] { type, rawValue, new Fading(type), false });
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

            Assert.That(result, Is.EqualTo(Mathf.Log10(2f) * DefaultDecibelVolumeScale).Within(0.001f));
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
            Assert.That(5f.ToDecibel(), Is.EqualTo(Mathf.Log10(5f) * DefaultDecibelVolumeScale).Within(0.001f),
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
        public void IsDefault_LowPass_ParameterlessConstructor_IsNotDefault()
        {
            // characterizes: the parameterless ctor seeds LowPass with BroAdvice.LowPassFrequency (300Hz, a
            // "recommended starting point"), but IsDefault() compares against AudioConstant.MaxFrequency
            // (22000Hz, "no filtering" / neutral). Those are two different constants, so a freshly-constructed
            // `new Effect(EffectType.LowPass)` reads as NOT default. Likely surprising — reported as a finding.
            Assert.IsFalse(new Effect(EffectType.LowPass).IsDefault());
        }

        [Test]
        public void IsDefault_LowPass_AtMaxFrequency_IsDefault()
        {
            Assert.IsTrue(Effect.LowPass(MaxFrequency).IsDefault());
            Assert.IsTrue(Effect.ResetLowPass().IsDefault());
        }

        [Test]
        public void IsDefault_HighPass_ParameterlessConstructor_IsNotDefault()
        {
            // characterizes: same mismatch as LowPass, mirrored — BroAdvice.HighPassFrequency (2000Hz) vs.
            // AudioConstant.MinFrequency (10Hz, the neutral value IsDefault() actually checks against).
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
            Assert.That(effect.Value, Is.EqualTo(0.5f.ToDecibel()).Within(0.001f));
        }

        [Test]
        public void Value_LowPass_OutOfRangeWrite_IsSilentlyDroppedKeepingPriorValue()
        {
            LogAssert.Expect(LogType.Error, new Regex("frequency"));
            Effect effect = CreateEffect(EffectType.LowPass, -100f);

            // this(type) runs first and seeds Value with BroAdvice.LowPassFrequency (a valid frequency, so it
            // passes IsValidFrequency silently); the ctor body's overwrite to -100 then fails validation and is
            // dropped, leaving the seeded value in place rather than -100 or 0.
            Assert.That(effect.Value, Is.EqualTo(300f).Within(0.001f));
        }

        [Test]
        public void Value_HighPass_OutOfRangeWrite_IsSilentlyDroppedKeepingPriorValue()
        {
            LogAssert.Expect(LogType.Error, new Regex("frequency"));
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
                LogAssert.Expect(LogType.Error, new Regex("Hz"));
            }
            Assert.That(AudioExtension.IsValidFrequency(freq), Is.EqualTo(expectedValid));
        }

        #endregion
    }
}