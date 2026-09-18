using NUnit.Framework;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-math coverage for <see cref="Transport.HasDifferentPosition"/>. No IMGUI context is touched —
    /// this is plain boolean arithmetic over the transport's own fields.
    /// </summary>
    public class TransportHasDifferentPositionTests : BroEditorTestFixture
    {
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
        [Category("Finding_32")]
        public void HasDifferentPosition_DelayGreaterThanStart_IsTrue_EvenWithStartAndEndAtZero()
        {
            // Characterizes TEST_FINDINGS #32: Start and End are both untouched (0), yet a
            // positive Delay alone flips HasDifferentPosition to true via the "Delay > StartPosition" term
            // (0 > 0 is false, but any positive Delay clears that bar). Whether a delay alone should count
            // as a different *position* is the open question; this test pins today's answer.
            var transport = new Transport(10f);
            transport.SetValue(1f, TransportType.Delay);

            Assert.IsTrue(transport.HasDifferentPosition);
        }
    }
}
