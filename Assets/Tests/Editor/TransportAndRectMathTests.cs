using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ami.Extension;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-math coverage for <see cref="Transport"/>'s value budget/rounding rules and the
    /// <see cref="EditorScriptingExtension"/> rect-splitting helpers. No IMGUI context is touched —
    /// every target here is plain float/Rect arithmetic that runs outside OnGUI.
    /// </summary>
    public class TransportAndRectMathTests : BroEditorTestFixture
    {
        #region Transport.SetValue — length budget
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

        [Test]
        public void SetValue_Delay_OnlyEverClampsToZero()
        {
            var transport = new Transport(10f);

            transport.SetValue(-5f, TransportType.Delay);

            Assert.AreEqual(0f, transport.Delay, 0.0001f);
        }
        #endregion

        #region Transport.SetValue — rounding
        [Test]
        public void SetValue_Start_RoundsToThreeDigits()
        {
            var transport = new Transport(1000f);

            transport.SetValue(3.4567f, TransportType.Start);

            // 4th decimal digit is 7 (unambiguous either way a midpoint rule breaks ties) so this
            // pins down "rounds to 3 digits" without depending on a float32 landing exactly on a
            // .0005 tie — such a tie is not reliably reachable at float precision (verified: values
            // like 1.2345f / 1.0005f do not round-trip to an exact .5 at the 4th decimal), so the
            // AwayFromZero-vs-banker's-rounding distinction specifically could not be pinned down here.
            Assert.AreEqual(3.457f, transport.StartPosition, 0.0001f);
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

        #region Transport.HasDifferentPosition
        [Test]
        public void HasDifferentPosition_DefaultState_IsFalse()
        {
            var transport = new Transport(10f);

            Assert.IsFalse(transport.HasDifferentPosition);
        }

        [Test]
        public void HasDifferentPosition_EndNonZero_IsTrue()
        {
            var transport = new Transport(10f);
            transport.SetValue(1f, TransportType.End);

            Assert.IsTrue(transport.HasDifferentPosition);
        }

        [Test]
        public void HasDifferentPosition_DelayGreaterThanStart_IsTrue_EvenWithStartAndEndAtZero()
        {
            // Characterized, and logged as TEST_FINDINGS #32: Start and End are both untouched (0), yet a
            // positive Delay alone flips HasDifferentPosition to true via the "Delay > StartPosition" term
            // (0 > 0 is false, but any positive Delay clears that bar). Whether a delay alone should count
            // as a different *position* is the open question; this test pins today's answer.
            var transport = new Transport(10f);
            transport.SetValue(1f, TransportType.Delay);

            Assert.IsTrue(transport.HasDifferentPosition);
        }
        #endregion

        #region EditorScriptingExtension.SplitRectHorizontal / SplitRectVertical — 2-way ratio form
        [Test]
        public void SplitRectHorizontal_RatioForm_ReachesExactlyOriginXMax_RegardlessOfGap()
        {
            var origin = new Rect(0f, 0f, 120f, 40f);

            EditorScriptingExtension.SplitRectHorizontal(origin, 0.5f, 6f, out Rect rect1, out Rect rect2);

            // halfGap is subtracted from BOTH rects' widths but the full gap is added back once between
            // them, so the two halfGap deductions net out against the one gap addition exactly.
            Assert.AreEqual(60f - 3f, rect1.width, 0.0001f);
            Assert.AreEqual(rect1.xMax + 6f, rect2.x, 0.0001f);
            Assert.AreEqual(origin.xMax, rect2.xMax, 0.0001f);
        }

        [Test]
        public void SplitRectVertical_RatioForm_ReachesExactlyOriginYMax_RegardlessOfGap()
        {
            var origin = new Rect(0f, 0f, 40f, 120f);

            EditorScriptingExtension.SplitRectVertical(origin, 0.5f, 6f, out Rect rect1, out Rect rect2);

            Assert.AreEqual(60f - 3f, rect1.height, 0.0001f);
            Assert.AreEqual(rect1.yMax + 6f, rect2.y, 0.0001f);
            Assert.AreEqual(origin.yMax, rect2.yMax, 0.0001f);
        }
        #endregion

        #region EditorScriptingExtension.SplitRectHorizontal / SplitRectVertical — params float[] ratios form
        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            var origin = new Rect(0f, 0f, 120f, 40f);
            var rects = new Rect[3];

            EditorScriptingExtension.SplitRectHorizontal(origin, 6f, rects, 0.25f, 0.25f, 0.5f);

            // First/last segments lose a full gap, the middle segment only loses half a gap.
            Assert.AreEqual(new Rect(0f, 0f, 24f, 40f), rects[0]);
            Assert.AreEqual(new Rect(30f, 0f, 27f, 40f), rects[1]);
            Assert.AreEqual(new Rect(63f, 0f, 54f, 40f), rects[2]);

            // FINDING: with 3 segments the accounting falls short of origin.xMax by half a gap (117 vs 120)
            // — unlike the dedicated 2-way ratio overload above, this form does not land exactly on the
            // origin's far edge except at specific segment counts (verified: N=4 lands exactly; N=2 and
            // N=3 fall short; N>=5 would overshoot past origin.xMax by this same formula).
            Assert.AreEqual(117f, rects[2].xMax, 0.0001f);
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_TwoWay_FallsShortOfOriginXMax_UnlikeTheDedicatedOverload()
        {
            // Same origin/gap/50-50 split as SplitRectHorizontal_RatioForm_..., but through the
            // params-ratios overload instead of the dedicated (out, out) 2-way overload.
            var origin = new Rect(0f, 0f, 120f, 40f);
            var rects = new Rect[2];

            EditorScriptingExtension.SplitRectHorizontal(origin, 6f, rects, 0.5f, 0.5f);

            // Both segments are index 0 and index (length-1) simultaneously when there are only two,
            // so BOTH take the full-gap offset instead of a halfGap each — the two overloads disagree.
            Assert.AreEqual(114f, rects[1].xMax, 0.0001f, "Expected the params-ratios 2-way split to fall short of origin.xMax by a full gap.");
            Assert.AreNotEqual(origin.xMax, rects[1].xMax, "This overload does not match the (out,out) 2-way overload's exact-edge behavior for the same inputs.");
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_RatiosNotSummingToOne_LogsErrorAndLeavesArrayUntouched()
        {
            var rects = new Rect[2];

            LogAssert.Expect(LogType.Error, "[Editor] Split ratio's sum should be 1");
            EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_NullArray_LogsItsOwnErrorAndReturns()
        {
            // Ratios sum to 1 here, so the guard that actually fires is the inner SplitHorizontal
            // helper's own null check, not the ratio-sum check.
            LogAssert.Expect(LogType.Error, "Rects array is null!");
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, null, 0.5f, 0.5f));
        }

        [Test]
        public void SplitRectVertical_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            var origin = new Rect(0f, 0f, 40f, 120f);
            var rects = new Rect[3];

            EditorScriptingExtension.SplitRectVertical(origin, 6f, rects, 0.25f, 0.25f, 0.5f);

            Assert.AreEqual(new Rect(0f, 0f, 40f, 24f), rects[0]);
            Assert.AreEqual(new Rect(0f, 30f, 40f, 27f), rects[1]);
            Assert.AreEqual(new Rect(0f, 63f, 40f, 54f), rects[2]);
        }

        [Test]
        public void SplitRectVertical_RatiosArrayForm_RatiosNotSummingToOne_LogsErrorAndLeavesArrayUntouched()
        {
            var rects = new Rect[2];

            LogAssert.Expect(LogType.Error, "[Editor] Split ratio's sum should be 1");
            EditorScriptingExtension.SplitRectVertical(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        public void SplitRectVertical_RatiosArrayForm_NullArray_SilentlyNoOps_UnlikeHorizontal()
        {
            // FINDING: unlike SplitRectHorizontal's params-ratios overload, this one does
            // `resultRects ??= new Rect[ratios.Length]` instead of logging+returning on null. That
            // reassignment is local to the method (arrays pass by reference-value, no `ref` here), so
            // the caller's own null reference is completely unaffected — the call computes into a
            // throwaway array and is, from the caller's side, an expensive no-op. No exception, no log.
            Rect[] rects = null;

            // Only the no-throw is load-bearing: no implementation without a `ref` parameter could make
            // the caller's local non-null, so asserting that would test C#, not this method.
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectVertical(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.5f));
        }
        #endregion

        #region EditorScriptingExtension.Scoping / DeScope
        [Test]
        public void ScopingThenDeScope_RoundTrips_WhenNoClampFires()
        {
            var scope = new Rect(10f, 20f, 200f, 100f);
            var originalGlobalRect = new Rect(50f, 60f, 30f, 15f);

            Rect local = originalGlobalRect.Scoping(scope);
            Rect backToGlobal = local.DeScope(scope);

            Assert.AreEqual(new Rect(40f, 40f, 30f, 15f), local, "Scoping should subtract the scope's own position.");
            Assert.AreEqual(originalGlobalRect, backToGlobal, "DeScope should be the exact inverse of Scoping.");
        }

        [Test]
        public void Scoping_ClampsXMaxAndYMax_ToScopeBounds()
        {
            var scope = new Rect(0f, 0f, 50f, 50f);
            var oversizedRect = new Rect(0f, 0f, 80f, 90f);

            Rect result = oversizedRect.Scoping(scope);

            Assert.AreEqual(50f, result.width, 0.0001f, "width should be clamped so xMax does not exceed scope.xMax.");
            Assert.AreEqual(50f, result.height, 0.0001f, "height should be clamped so yMax does not exceed scope.yMax.");
        }

        [Test]
        public void DeScope_ClampsXMaxAndYMax_ToScopeBounds()
        {
            var scope = new Rect(0f, 0f, 50f, 50f);
            var oversizedLocalRect = new Rect(10f, 10f, 60f, 70f);

            Rect result = oversizedLocalRect.DeScope(scope);

            Assert.AreEqual(40f, result.width, 0.0001f); // xMax clamped from 70 down to scope.xMax (50)
            Assert.AreEqual(40f, result.height, 0.0001f); // yMax clamped from 80 down to scope.yMax (50)
        }
        #endregion

        #region EditorScriptingExtension.GetBackingFieldName / GetFieldName
        [Test]
        public void GetBackingFieldName_WrapsPropertyNameInCompilerGeneratedPattern()
        {
            Assert.AreEqual("<Foo>k__BackingField", EditorScriptingExtension.GetBackingFieldName("Foo"));
        }

        [Test]
        public void GetFieldName_LowercasesLeadingCharAndPrefixesUnderscore()
        {
            Assert.AreEqual("_foo", EditorScriptingExtension.GetFieldName("Foo"));
        }

        [Test]
        public void GetFieldName_AlreadyLowercaseLeadingChar_StillGetsPrefixed()
        {
            Assert.AreEqual("_foo", EditorScriptingExtension.GetFieldName("foo"));
        }

        [Test]
        public void GetFieldName_ReplacesEveryOccurrenceOfTheLeadingChar_NotJustTheFirst()
        {
            // FINDING: the implementation does propertyName.Replace(firstChar, lowerFirstChar) — a
            // global string.Replace(char,char) — not a single-position substitution. Any later
            // occurrence of the same uppercase leading letter elsewhere in the name is lowercased too.
            Assert.AreEqual("_foof", EditorScriptingExtension.GetFieldName("FooF"));
        }

        [Test]
        public void GetFieldName_NullOrEmpty_PassesThroughUnchanged()
        {
            Assert.IsNull(EditorScriptingExtension.GetFieldName(null));
            Assert.AreEqual(string.Empty, EditorScriptingExtension.GetFieldName(string.Empty));
        }
        #endregion
    }
}
