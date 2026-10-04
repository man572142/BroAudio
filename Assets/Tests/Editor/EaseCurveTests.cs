using System;
using Ami.Extension;
using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Shape of <see cref="EaseExtension.SetEase"/>, behind every fade and effect automation ramp.
    /// Expectations are hand-derived literals: never call <c>SetEase</c> to produce one, or a transcription
    /// slip (<c>Pow(v, 3)</c> to <c>Pow(v, 4)</c>) passes. Samples are chosen so no two curves in a family,
    /// nor any curve and Linear, share the asserted value.
    /// </summary>
    public class EaseCurveTests
    {
        /// <summary>
        /// The InOut curves branch on <c>value &lt; 0.5f</c>, so they are sampled on both sides of it and on it.
        /// </summary>
        private const float Quarter = 0.25f;
        private const float Half = 0.5f;
        private const float ThreeQuarters = 0.75f;

        /// <summary>
        /// Far wider than float32 error, far tighter than the closest pair of curves (InCubic vs InCirc, ~9e-3).
        /// </summary>
        private const float Tolerance = 1e-5f;

        /// <summary>So a newly added Ease member gets endpoint coverage automatically.</summary>
        private static readonly Ease[] AllEaseValues = (Ease[])Enum.GetValues(typeof(Ease));

        #region Endpoints

        [Test]
        public void SetEase_AtZero_ReturnsZero([ValueSource(nameof(AllEaseValues))] Ease ease)
        {
            // A curve off (0,0) pops at the start of the fade.
            Assert.That(0f.SetEase(ease), Is.EqualTo(0f).Within(Tolerance), "Ease." + ease + " must map t=0 to 0.");
        }

        [Test]
        public void SetEase_AtOne_ReturnsOne([ValueSource(nameof(AllEaseValues))] Ease ease)
        {
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
        // InOut family, upper (ease-out) branch at t=0.75, where -2t + 2 = 0.5:
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
            // `value < 0.5f` is strict, so t=0.5 evaluates the ease-OUT branch (InOutSine has no branch).
            Assert.That(Half.SetEase(ease), Is.EqualTo(0.5f).Within(Tolerance),
                "Ease." + ease + " must pass through (0.5, 0.5) - its two halves have to meet at the midpoint.");
        }

        #endregion

        #region Out-of-range input

        // Ramps evaluate one step past t = 1 on their last frame, so out-of-range input must clamp to the curve's ends.
        [TestCase(Ease.Linear, 1.5f, 1f)]
        [TestCase(Ease.InQuad, 1.5f, 1f)]
        [TestCase(Ease.Linear, -1f, 0f)]
        [TestCase(Ease.InQuad, -1f, 0f)]
        [TestCase(Ease.OutSine, 1.2f, 1f)]
        public void SetEase_OutOfRangeInput_IsClampedToTheCurveEnds(Ease ease, float t, float expected)
        {
            Assert.That(t.SetEase(ease), Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void SetEase_InCircPastOne_IsOne()
        {
            // Unclamped, InCirc's sqrt(1 - t^2) is NaN past 1.
            Assert.That(1.1f.SetEase(Ease.InCirc), Is.EqualTo(1f).Within(Tolerance));
        }

        #endregion

        #region Enum ordinals

        // Serialized ordinals: a moved member makes saved assets deserialize to a different or undefined Ease, which evaluates as `_ => 0`.
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