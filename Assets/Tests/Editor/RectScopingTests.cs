using NUnit.Framework;
using UnityEngine;
using Ami.Extension;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-math coverage for the <see cref="EditorScriptingExtension"/> <c>Scoping</c>/<c>DeScope</c>
    /// rect-coordinate helpers. No IMGUI context is touched — every target here is plain Rect arithmetic
    /// that runs outside OnGUI.
    /// </summary>
    public class RectScopingTests : BroEditorTestFixture
    {
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
    }
}
