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

        // The tests above keep the scope at the origin, where scope.xMax == scope.width and a clamp against
        // either edge gives the same answer. Scoping's real callers pass an EditorWindow's position (e.g.
        // LibraryManagerWindow), which is not at the origin, so the cases below move the scope off it.

        [Test]
        [Category("Finding_62")]
        public void Scoping_OffOriginScope_ClampsLocalRectAgainstGlobalEdge()
        {
            // Characterizes TEST_FINDINGS #62: Scoping converts the rect into scope-local coordinates, then
            // clamps its xMax/yMax against scope.xMax/yMax, which are GLOBAL coordinates. Local (10, 10, 80, 90)
            // has xMax 90 and yMax 100, both under the global edge (150, 150), so nothing is clamped - although
            // the rect overhangs the scope's local bounds (width 50, height 50) by 40 and 50.
            var scope = new Rect(100f, 100f, 50f, 50f);
            var oversizedGlobalRect = new Rect(110f, 110f, 80f, 90f);

            Rect result = oversizedGlobalRect.Scoping(scope);

            Assert.AreEqual(10f, result.x, 0.0001f, "Scoping should subtract the scope's x.");
            Assert.AreEqual(10f, result.y, 0.0001f, "Scoping should subtract the scope's y.");
            Assert.AreEqual(80f, result.width, 0.0001f,
                "width was clamped - if to 40 (scope.width - local x), #62 is fixed: update this pin and the finding.");
            Assert.AreEqual(90f, result.height, 0.0001f,
                "height was clamped - if to 40 (scope.height - local y), #62 is fixed: update this pin and the finding.");
        }

        [Test]
        [Category("Finding_62")]
        public void Scoping_OffOriginScope_LocalRectPastTheGlobalEdge_IsClampedToTheGlobalEdge()
        {
            // The other half of #62: the clamp does fire once the LOCAL xMax passes the GLOBAL scope.xMax, and it
            // then cuts the local rect back to that global value, not to scope.width.
            // Local (10, 10, 200, 200): xMax 210 > 150, so xMax becomes 150 -> width 140 (a correct clamp gives 40).
            var scope = new Rect(100f, 100f, 50f, 50f);
            var oversizedGlobalRect = new Rect(110f, 110f, 200f, 200f);

            Rect result = oversizedGlobalRect.Scoping(scope);

            Assert.AreEqual(140f, result.width, 0.0001f, "Local xMax was not clamped to the global scope.xMax (150).");
            Assert.AreEqual(140f, result.height, 0.0001f, "Local yMax was not clamped to the global scope.yMax (150).");
        }

        [Test]
        public void DeScope_OffOriginScope_ClampsGlobalRectAgainstGlobalEdge()
        {
            // DeScope's result is in global coordinates, so its clamp against the global scope.xMax/yMax is
            // consistent - the contrast that shows #62 is confined to Scoping.
            // Local (10, 10, 80, 90) -> global (110, 110), xMax 190 -> 150, yMax 200 -> 150.
            var scope = new Rect(100f, 100f, 50f, 50f);
            var oversizedLocalRect = new Rect(10f, 10f, 80f, 90f);

            Rect result = oversizedLocalRect.DeScope(scope);

            Assert.AreEqual(110f, result.x, 0.0001f);
            Assert.AreEqual(110f, result.y, 0.0001f);
            Assert.AreEqual(40f, result.width, 0.0001f);
            Assert.AreEqual(40f, result.height, 0.0001f);
        }
    }
}
