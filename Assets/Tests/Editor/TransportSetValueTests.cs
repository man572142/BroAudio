using NUnit.Framework;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-math coverage for <see cref="Transport"/>'s SetValue value-shaping rules: the length budget
    /// each transport type clamps against, and the rounding SetValue applies on top of that clamp. No
    /// IMGUI context is touched — every target here is plain float arithmetic that runs outside OnGUI.
    /// </summary>
    public class TransportSetValueTests : BroEditorTestFixture
    {
        #region Length budget
        // GetLengthLimit sums only Start+End+FadeIn+FadeOut (zeroing out whichever of those four is
        // being modified) and subtracts that from FullLength. Delay never participates in this sum,
        // in either direction: it doesn't consume budget, and its own limit isn't computed this way.

        [Test]
        public void SetValue_Start_ClampsToFullLengthMinusEndFadeInFadeOut_IgnoringDelay()
        {
            var transport = new Transport(10f);
            transport.SetValue(3f, TransportType.End);
            transport.SetValue(1f, TransportType.FadeIn);
            transport.SetValue(1f, TransportType.FadeOut);
            transport.SetValue(100f, TransportType.Delay); // large Delay must NOT shrink the budget

            transport.SetValue(999f, TransportType.Start);

            Assert.AreEqual(10f - 3f - 1f - 1f, transport.StartPosition, 0.0001f);
        }

        [Test]
        public void SetValue_End_ClampsToFullLengthMinusStartFadeInFadeOut()
        {
            var transport = new Transport(10f);
            transport.SetValue(2f, TransportType.Start);
            transport.SetValue(1f, TransportType.FadeIn);
            transport.SetValue(1f, TransportType.FadeOut);

            transport.SetValue(999f, TransportType.End);

            Assert.AreEqual(10f - 2f - 1f - 1f, transport.EndPosition, 0.0001f);
        }

        [Test]
        public void SetValue_FadeIn_ClampsToFullLengthMinusStartEndFadeOut()
        {
            var transport = new Transport(10f);
            transport.SetValue(2f, TransportType.Start);
            transport.SetValue(2f, TransportType.End);
            transport.SetValue(1f, TransportType.FadeOut);

            transport.SetValue(999f, TransportType.FadeIn);

            Assert.AreEqual(10f - 2f - 2f - 1f, transport.FadeIn, 0.0001f);
        }

        [Test]
        public void SetValue_FadeOut_ClampsToFullLengthMinusStartEndFadeIn()
        {
            var transport = new Transport(10f);
            transport.SetValue(2f, TransportType.Start);
            transport.SetValue(2f, TransportType.End);
            transport.SetValue(1f, TransportType.FadeIn);

            transport.SetValue(999f, TransportType.FadeOut);

            Assert.AreEqual(10f - 2f - 2f - 1f, transport.FadeOut, 0.0001f);
        }

        [Test]
        public void SetValue_Delay_IsNeverLengthClamped_EvenWithNoRemainingBudget()
        {
            // FullLength is tiny and fully consumed by Start alone; a normal budget member would clamp to 0.
            var transport = new Transport(2f);
            transport.SetValue(2f, TransportType.Start);

            transport.SetValue(500f, TransportType.Delay);

            Assert.AreEqual(500f, transport.Delay, 0.0001f, "Delay must ignore FullLength entirely.");
        }

        // This is the sole pin of Transport.SetValue's Delay clamp rule (`Mathf.Max(newValue, 0f)`,
        // Transport.cs) - the only production code that computes it. SerializedTransport inherits the rule
        // unchanged rather than overriding it, and SerializedTransportTests already covers the separate
        // concern of the write committing through to the serialized clip field, so pinning the clamp rule
        // itself a second time there would only add indirection, not a second production code path.
        [Test]
        public void SetValue_Delay_OnlyEverClampsToZero()
        {
            var transport = new Transport(10f);

            transport.SetValue(-5f, TransportType.Delay);

            Assert.AreEqual(0f, transport.Delay, 0.0001f);
        }
        #endregion

        #region Rounding
        [Test]
        public void SetValue_Start_RoundsToThreeDigits()
        {
            var transport = new Transport(1000f);

            transport.SetValue(3.4567f, TransportType.Start);

            // 4th decimal digit is 7, so this pins "rounds to 3 digits" independently of how a midpoint
            // tie is broken. The tie rule has its own pin below.
            Assert.AreEqual(3.457f, transport.StartPosition, 0.0001f);
        }

        [Test]
        public void SetValue_Start_ExactMidpoint_RoundsAwayFromZero()
        {
            var transport = new Transport(1000f);

            // A tie IS reachable at float precision when the value is a dyadic fraction: 0.0625f is exactly
            // 1/16, it widens to the double 0.0625 exactly, and 0.0625 * 1000 is exactly 62.5. ClampAndRound
            // uses MidpointRounding.AwayFromZero, so 62.5 -> 63 -> 0.063; banker's rounding (the Math.Round
            // default) would give 62 -> 0.062. The tolerance is below half the 0.001 gap between the two.
            transport.SetValue(0.0625f, TransportType.Start);

            Assert.AreEqual(0.063f, transport.StartPosition, 0.0001f,
                "An exact .0005 tie no longer rounds away from zero - the midpoint rule changed (0.062 means banker's rounding).");
        }

        [Test]
        public void SetValue_Delay_IsNeverRounded()
        {
            var transport = new Transport(1000f);

            transport.SetValue(3.456789f, TransportType.Delay);

            // Delay's case in SetValue calls Mathf.Max only — it never routes through ClampAndRound.
            Assert.AreEqual(3.456789f, transport.Delay, 0.0000001f);
        }
        #endregion
    }
}
