using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// <see cref="EditorScriptingExtension"/> rect-splitting overloads; plain Rect math, no IMGUI context.
    /// </summary>
    public class RectSplitRatioTests : BroEditorTestFixture
    {
        #region Dedicated 2-way ratio overload (out Rect, out Rect)
        [Test]
        public void SplitRectHorizontal_RatioForm_ReachesExactlyOriginXMax_RegardlessOfGap()
        {
            var origin = new Rect(0f, 0f, 120f, 40f);

            EditorScriptingExtension.SplitRectHorizontal(origin, 0.5f, 6f, out Rect rect1, out Rect rect2);

            // Two halfGap deductions net out against the one gap between them.
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
        public void SplitRectHorizontal_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            var origin = new Rect(0f, 0f, 120f, 40f);
            var rects = new Rect[3];

            EditorScriptingExtension.SplitRectHorizontal(origin, 6f, rects, 0.25f, 0.25f, 0.5f);

            // First/last segments lose a full gap, the middle segment only loses half a gap.
            Assert.AreEqual(new Rect(0f, 0f, 24f, 40f), rects[0]);
            Assert.AreEqual(new Rect(30f, 0f, 27f, 40f), rects[1]);
            Assert.AreEqual(new Rect(63f, 0f, 54f, 40f), rects[2]);

            // Half a gap short of origin.xMax.
            Assert.AreEqual(117f, rects[2].xMax, 0.0001f);
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_TwoWay_FallsShortOfOriginXMax_UnlikeTheDedicatedOverload()
        {
            // Same inputs as SplitRectHorizontal_RatioForm_..., other overload.
            var origin = new Rect(0f, 0f, 120f, 40f);
            var rects = new Rect[2];

            EditorScriptingExtension.SplitRectHorizontal(origin, 6f, rects, 0.5f, 0.5f);

            // With two segments each is both first and last, so both take the full-gap offset.
            Assert.AreEqual(114f, rects[1].xMax, 0.0001f, "Expected the params-ratios 2-way split to fall short of origin.xMax by a full gap.");
            Assert.AreNotEqual(origin.xMax, rects[1].xMax, "This overload does not match the (out,out) 2-way overload's exact-edge behavior for the same inputs.");
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_RatiosNotSummingToOne_LogsErrorAndLeavesArrayUntouched()
        {
            var rects = new Rect[2];

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        public void SplitRectHorizontal_CountForm_NullArray_LogsTheNullGuardsError()
        {
            // No ratio-sum check here, so the error can only be the shared SplitHorizontal null guard's.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 2, 4f, null));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SplitRectHorizontal_RatiosArrayForm_NullArray_LogsItsOwnErrorAndReturns()
        {
            // Ratios sum to 1, so the one error is the null guard's; NoUnexpectedReceived fails if both guards fire.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectHorizontal(new Rect(0f, 0f, 100f, 50f), 4f, null, 0.5f, 0.5f));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SplitRectVertical_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule()
        {
            // Vertical twin of the three-way split above.
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

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            EditorScriptingExtension.SplitRectVertical(new Rect(0f, 0f, 100f, 50f), 4f, rects, 0.5f, 0.4f);

            Assert.AreEqual(default(Rect), rects[0]);
            Assert.AreEqual(default(Rect), rects[1]);
        }

        [Test]
        public void SplitRectVertical_RatiosArrayForm_NullArray_LogsItsOwnErrorAndReturns()
        {
            // Ratios sum to 1, so the one error is the null guard's; NoUnexpectedReceived fails if both guards fire.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Assert.DoesNotThrow(() =>
                EditorScriptingExtension.SplitRectVertical(new Rect(0f, 0f, 100f, 50f), 4f, null, 0.5f, 0.5f));
            LogAssert.NoUnexpectedReceived();
        }
        #endregion
    }
}
