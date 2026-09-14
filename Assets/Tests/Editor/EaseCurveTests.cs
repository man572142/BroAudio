using System;
using Ami.Extension;
using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Shape assertions for <see cref="EaseExtension.SetEase"/> - the only shaping function behind player
    /// volume fades (FaderModule), master fades (SoundManager) and all effect automation
    /// (EffectAutomationHelper). Pure math: no SoundManager, no MonoBehaviour, no Play Mode.
    /// <para>
    /// Every expected value below is hand-derived from the curve's closed form and written as a literal.
    /// Nothing in this file may call <c>SetEase</c> to produce an expectation - a test that re-derives its
    /// expectation from the code under test cannot catch a transcription slip (<c>Pow(v, 3)</c> becoming
    /// <c>Pow(v, 4)</c>), which is precisely the failure mode this file exists to catch. For the same reason
    /// each interior sample is chosen so that no two curves in the same family - and no curve versus
    /// Linear - share the asserted value.
    /// </para>
    /// </summary>
    public class EaseCurveTests
    {
        /// <summary>
        /// Sample points used throughout. The InOut curves branch on <c>value &lt; 0.5f</c>, so they are
        /// sampled on both sides of that seam as well as exactly on it.
        /// </summary>
        private const float Quarter = 0.25f;
        private const float Half = 0.5f;
        private const float ThreeQuarters = 0.75f;

        /// <summary>
        /// The curves are float32 Pow/Sqrt/Sin/Cos evaluations of exactly representable inputs; the dyadic
        /// expectations (0.125, 0.96875, ...) are exact and the transcendental ones are written to 8 decimals,
        /// so 1e-5 is orders of magnitude wider than the arithmetic error yet far tighter than the gap between
        /// any two curves asserted here (the closest pair, InCubic 0.125 vs InCirc 0.13397460, differ by ~9e-3).
        /// </summary>
        private const float Tolerance = 1e-5f;

        /// <summary>Drives the endpoint tests, so a newly added Ease member is covered automatically.</summary>
        private static readonly Ease[] AllEaseValues = (Ease[])Enum.GetValues(typeof(Ease));

        #region Endpoints

        [Test]
        public void SetEase_AtZero_ReturnsZero([ValueSource(nameof(AllEaseValues))] Ease ease)
        {
            // Every Ease is normalized: a fade starting at t=0 must start at the origin volume, whichever
            // curve the user picked. A curve that does not pass through (0,0) pops at the start of the fade.
            Assert.That(0f.SetEase(ease), Is.EqualTo(0f).Within(Tolerance), "Ease." + ease + " must map t=0 to 0.");
        }

        [Test]
        public void SetEase_AtOne_ReturnsOne([ValueSource(nameof(AllEaseValues))] Ease ease)
        {
            // ...and must reach the target exactly at t=1, or the fade never lands on its target volume.
            Assert.That(1f.SetEase(ease), Is.EqualTo(1f).Within(Tolerance), "Ease." + ease + " must map t=1 to 1.");
        }

        #endregion

        #region Curve shape - hand-derived interior values

        // In family, sampled at t=0.5:
        //   InQuad   0.5^2                    = 0.25
        //   InCubic  0.5^3                    = 0.125
        //   InQuart  0.5^4                    = 0.0625
        //   InQuint  0.5^5                    = 0.03125
        //   InSine   1 - cos(pi/4) = 1 - √2/2 = 0.29289322
        //   InCirc   1 - sqrt(3)/2            = 0.13397460
        [TestCase(Ease.Linear, Half, 0.5f)]
        [TestCase(Ease.InQuad, Half, 0.25f)]
        [TestCase(Ease.InCubic, Half, 0.125f)]
        [TestCase(Ease.InQuart, Half, 0.0625f)]
        [TestCase(Ease.InQuint, Half, 0.03125f)]
        [TestCase(Ease.InSine, Half, 0.29289322f)]
        [TestCase(Ease.InCirc, Half, 0.13397460f)]
        // Out family at t=0.5 - the mirror 1 - f(1 - t) of the In family:
        //   OutQuad  1 - 0.5^2      = 0.75
        //   OutCubic 1 - 0.5^3      = 0.875
        //   OutQuart 1 - 0.5^4      = 0.9375
        //   OutQuint 1 - 0.5^5      = 0.96875
        //   OutSine  sin(pi/4)      = 0.70710678
        //   OutCirc  sqrt(1 - 0.25) = 0.86602540
        [TestCase(Ease.OutQuad, Half, 0.75f)]
        [TestCase(Ease.OutCubic, Half, 0.875f)]
        [TestCase(Ease.OutQuart, Half, 0.9375f)]
        [TestCase(Ease.OutQuint, Half, 0.96875f)]
        [TestCase(Ease.OutSine, Half, 0.70710678f)]
        [TestCase(Ease.OutCirc, Half, 0.86602540f)]
        // InOut family, lower (ease-in) half at t=0.25:
        //   InOutQuad  2*0.25^2                      = 0.125
        //   InOutCubic 2*0.25^3                      = 0.03125
        //   InOutQuart 2*0.25^4                      = 0.0078125
        //   InOutQuint 2*0.25^5                      = 0.001953125
        //   InOutSine  (1 - cos(pi/4)) / 2           = 0.14644661
        //   InOutCirc  (1 - sqrt(1 - 0.5^2)) / 2     = (1 - sqrt(3)/2) / 2 = 0.06698730
        [TestCase(Ease.Linear, Quarter, 0.25f)]
        [TestCase(Ease.InOutQuad, Quarter, 0.125f)]
        [TestCase(Ease.InOutCubic, Quarter, 0.03125f)]
        [TestCase(Ease.InOutQuart, Quarter, 0.0078125f)]
        [TestCase(Ease.InOutQuint, Quarter, 0.001953125f)]
        [TestCase(Ease.InOutSine, Quarter, 0.14644661f)]
        [TestCase(Ease.InOutCirc, Quarter, 0.06698730f)]
        // InOut family, upper (ease-out) half at t=0.75, where -2t + 2 = 0.5. This is a separate branch of
        // the expression, so it needs its own row per curve:
        //   InOutQuad  1 - 0.5^2 / 2                 = 0.875
        //   InOutCubic 1 - 0.5^3 / 2                 = 0.9375
        //   InOutQuart 1 - 0.5^4 / 2                 = 0.96875
        //   InOutQuint 1 - 0.5^5 / 2                 = 0.984375
        //   InOutSine  (1 - cos(3pi/4)) / 2          = (1 + √2/2) / 2 = 0.85355339
        //   InOutCirc  (sqrt(1 - 0.5^2) + 1) / 2     = (sqrt(3)/2 + 1) / 2 = 0.93301270
        [TestCase(Ease.Linear, ThreeQuarters, 0.75f)]
        [TestCase(Ease.InOutQuad, ThreeQuarters, 0.875f)]
        [TestCase(Ease.InOutCubic, ThreeQuarters, 0.9375f)]
        [TestCase(Ease.InOutQuart, ThreeQuarters, 0.96875f)]
        [TestCase(Ease.InOutQuint, ThreeQuarters, 0.984375f)]
        [TestCase(Ease.InOutSine, ThreeQuarters, 0.85355339f)]
        [TestCase(Ease.InOutCirc, ThreeQuarters, 0.93301270f)]
        public void SetEase_InteriorPoint_MatchesHandDerivedCurveShape(Ease ease, float t, float expected)
        {
            Assert.That(t.SetEase(ease), Is.EqualTo(expected).Within(Tolerance),
                "Ease." + ease + " no longer has its documented shape at t=" + t +
                ". The expected value is hand-derived from the curve's closed form - if SetEase's expression " +
                "for this member was edited, every fade and automation ramp in the product changed with it.");
        }

        [TestCase(Ease.InOutQuad)]
        [TestCase(Ease.InOutCubic)]
        [TestCase(Ease.InOutQuart)]
        [TestCase(Ease.InOutQuint)]
        [TestCase(Ease.InOutSine)]
        [TestCase(Ease.InOutCirc)]
        public void SetEase_InOutCurves_AtMidpoint_AreExactlyHalfway(Ease ease)
        {
            // Pins the seam: all but InOutSine branch on `value < 0.5f` (strictly less), so t=0.5 evaluates
            // the ease-OUT half, and its value there must still be 0.5 or the fade steps at the midpoint.
            // InOutSine is a single expression and is symmetric about 0.5 by construction.
            Assert.That(Half.SetEase(ease), Is.EqualTo(0.5f).Within(Tolerance),
                "Ease." + ease + " must pass through (0.5, 0.5) - its two halves have to meet at the midpoint.");
        }

        #endregion

        #region Characterization - inputs SetEase does not normalize

        [TestCase(Ease.Linear, 1.5f, 1.5f)]
        [TestCase(Ease.InQuad, 1.5f, 2.25f)]
        [TestCase(Ease.Linear, -1f, -1f)]
        [TestCase(Ease.InQuad, -1f, 1f)]
        public void SetEase_OutOfRangeInput_IsNotClamped_CharacterizesDiscardedClamp01(Ease ease, float t, float expected)
        {
            // characterizes (reported as a finding, NOT fixed here): SetEase opens with a bare
            // `Mathf.Clamp01(value);` whose return value is discarded - Mathf.Clamp01 is pure, so the clamp
            // does nothing and out-of-range t flows straight into the curve. t > 1 therefore overshoots the
            // target volume and a negative t can come back POSITIVE through the even powers (-1 -> 1).
            // Callers stay in range today only because they all pass elapsed/duration ratios. If the clamp is
            // ever wired up (`value = Mathf.Clamp01(value);`), these rows are the ones that must change.
            Assert.That(t.SetEase(ease), Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void SetEase_UndefinedEaseValue_FallsBackToZero()
        {
            // characterizes the `_ => 0` switch arm: an out-of-range cast (e.g. a saved ordinal from a newer
            // build) silently yields 0 for the whole fade rather than throwing, which pins the volume at the
            // fade's origin for its entire duration.
            Assert.That(Half.SetEase((Ease)9999), Is.EqualTo(0f).Within(Tolerance));
        }

        #endregion

        #region Enum ordinals

        [TestCase(Ease.Linear, 0)]
        [TestCase(Ease.InQuad, 1)]
        [TestCase(Ease.InCubic, 2)]
        [TestCase(Ease.InQuart, 3)]
        [TestCase(Ease.InQuint, 4)]
        [TestCase(Ease.InSine, 5)]
        [TestCase(Ease.InCirc, 6)]
        [TestCase(Ease.OutQuad, 7)]
        [TestCase(Ease.OutCubic, 8)]
        [TestCase(Ease.OutQuart, 9)]
        [TestCase(Ease.OutQuint, 10)]
        [TestCase(Ease.OutSine, 11)]
        [TestCase(Ease.OutCirc, 12)]
        [TestCase(Ease.InOutQuad, 13)]
        [TestCase(Ease.InOutCubic, 14)]
        [TestCase(Ease.InOutQuart, 15)]
        [TestCase(Ease.InOutQuint, 16)]
        [TestCase(Ease.InOutSine, 17)]
        [TestCase(Ease.InOutCirc, 18)]
        public void EaseMember_KeepsItsSerializedOrdinal(Ease ease, int expectedOrdinal)
        {
            Assert.That((int)ease, Is.EqualTo(expectedOrdinal),
                "Ease." + ease + " moved from ordinal " + expectedOrdinal + " to " + (int)ease + ". " +
                "Ease has no explicit values and Unity serializes an enum BY ORDINAL, so RuntimeSetting's " +
                "DefaultFadeInEase / DefaultFadeOutEase / SeamlessFadeInEase / SeamlessFadeOutEase - and every " +
                "user's saved asset carrying them - would silently remap to a different curve. Insert nothing " +
                "into the middle of the Ease enum and reorder nothing; append new members at the end.");
        }

        [Test]
        public void EaseEnum_MemberCount_IsPinned()
        {
            Assert.That(AllEaseValues.Length, Is.EqualTo(19),
                "The Ease enum's member count changed. A member appended at the END is serialization-safe, but " +
                "it needs a row added to EaseMember_KeepsItsSerializedOrdinal and a hand-derived shape row in " +
                "SetEase_InteriorPoint_MatchesHandDerivedCurveShape - otherwise the new curve ships unasserted. " +
                "A REMOVED member is a breaking change to every saved RuntimeSetting.");
        }

        #endregion
    }
}