using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ami.Extension;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-math coverage for the <see cref="EditorScriptingExtension"/> rect-splitting helpers:
    /// <c>SplitRectHorizontal</c>/<c>SplitRectVertical</c>'s dedicated 2-way ratio overload and their
    /// <c>params float[] ratios</c> overload. No IMGUI context is touched — every target here is plain
    /// Rect arithmetic that runs outside OnGUI.
    /// </summary>
    public class RectSplitRatioTests : BroEditorTestFixture
    {
        #region Dedicated 2-way ratio overload (out Rect, out Rect)
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

        #region params float[] ratios overload
        [Test]
        [Category("Finding_21")]
        public void SplitRectHorizontal_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            var origin = new Rect(0f, 0f, 120f, 40f);
            var rects = new Rect[3];

            EditorScriptingExtension.SplitRectHorizontal(origin, 6f, rects, 0.25f, 0.25f, 0.5f);

            // First/last segments lose a full gap, the middle segment only loses half a gap.
            Assert.AreEqual(new Rect(0f, 0f, 24f, 40f), rects[0]);
            Assert.AreEqual(new Rect(30f, 0f, 27f, 40f), rects[1]);
            Assert.AreEqual(new Rect(63f, 0f, 54f, 40f), rects[2]);

            // Characterizes TEST_FINDINGS #21: with 3 segments the accounting falls short of origin.xMax by half
            // a gap (117 vs 120) — unlike the dedicated 2-way ratio overload above, this form does not land
            // exactly on the origin's far edge except at specific segment counts (N=4 lands exactly; N=2 and
            // N=3 fall short; N>=5 would overshoot past origin.xMax by this same formula).
            Assert.AreEqual(117f, rects[2].xMax, 0.0001f);
        }

        [Test]
        [Category("Finding_21")]
        public void SplitRectHorizontal_RatiosArrayForm_TwoWay_FallsShortOfOriginXMax_UnlikeTheDedicatedOverload()
        {
            // Characterizes TEST_FINDINGS #21: the same origin/gap/50-50 split as
            // SplitRectHorizontal_RatioForm_..., but through the params-ratios overload instead of the
            // dedicated (out, out) 2-way overload.
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

            LogAssert.Expect(LogType.Error, new Regex("Split ratio's sum should be 1"));
            EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_NullArray_LogsItsOwnErrorAndReturns()
        {
            // Ratios sum to 1 here, so the guard that actually fires is the inner SplitHorizontal
            // helper's own null check, not the ratio-sum check.
            LogAssert.Expect(LogType.Error, new Regex("Rects array is null!"));
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, null, 0.5f, 0.5f));
        }

        [Test]
        [Category("Finding_21")]
        public void SplitRectVertical_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            // Characterizes TEST_FINDINGS #21: the vertical twin of the horizontal three-way split above -
            // the same per-segment offset rule, so the last segment's yMax lands on 117 (63 + 54) rather
            // than the origin's 120.
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

            LogAssert.Expect(LogType.Error, new Regex("Split ratio's sum should be 1"));
            EditorScriptingExtension.SplitRectVertical(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        [Category("Finding_22")]
        public void SplitRectVertical_RatiosArrayForm_NullArray_SilentlyNoOps_UnlikeHorizontal()
        {
            // Characterizes TEST_FINDINGS #22: unlike SplitRectHorizontal's params-ratios overload, this one does
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
    }
}
