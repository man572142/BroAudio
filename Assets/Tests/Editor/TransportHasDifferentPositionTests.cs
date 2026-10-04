using NUnit.Framework;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pins <see cref="Transport.HasDifferentPosition"/>, plain arithmetic over the transport's fields.
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
            // Pins TEST_FINDINGS #32.
            var transport = new Transport(10f);
            transport.SetValue(1f, TransportType.Delay);

            Assert.IsTrue(transport.HasDifferentPosition);
        }
    }
}
