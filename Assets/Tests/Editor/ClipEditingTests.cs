using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// E3 tier: <see cref="AudioClipEditingHelper"/>'s sample math. This is the only place in the
    /// Editor assembly where a bug corrupts a user's audio file, so every edit is verified against
    /// exact expected sample values (via a ramp clip built in this file), not just "did it run".
    /// <para>
    /// Several tests pin down real, verified quirks in the production code (Downmix drops the final
    /// group, Reverse swaps stereo channels, AddSlient prepends rather than appends). These are
    /// characterized, not fixed - do not "correct" them.
    /// </para>
    /// <para>
    /// Ramp clips run at 1000 Hz, the lowest rate AudioClip.Create accepts (it caps anything lower and
    /// logs an error). One sample is therefore one millisecond, and <see cref="Seconds"/> converts a sample
    /// count back to the time argument the production code wants — every such value survives the
    /// float round-trip exactly, including AddSlient's truncating (int) cast, with no
    /// floating-point rounding risk, so index math can be asserted exactly.
    /// </para>
    /// </summary>
    public class ClipEditingTests : BroEditorTestFixture
    {
        private const float Tolerance = 1e-4f;

        /// <summary>
        /// AudioClipEditingHelper's private sample buffer. Reached through the shared lookup so a rename fails with a
        /// message naming the member; the name lives here because TestAudioLibrary.Reflected sits in the runtime
        /// test assembly, which cannot see this Editor-only type.
        /// </summary>
        private const string SampleDataFieldName = "_sampleDatas";
        private static System.Reflection.FieldInfo SampleDataField =>
            TestAudioLibrary.Reflected.Field(typeof(AudioClipEditingHelper), SampleDataFieldName);

        /// <summary>1000 Hz is the lowest rate AudioClip.Create honours; below it Unity caps and logs an error.</summary>
        private const int SampleRate = 1000;

        /// <summary>A sample count expressed as the seconds value the editing helper takes.</summary>
        private static float Seconds(int samples) => samples / (float)SampleRate;

        /// <summary>Builds a ramp clip: frame i (per-channel) holds value i/n, interleaved across channels.</summary>
        private AudioClip CreateRampClip(string name, int n, int channels, int frequency = SampleRate)
        {
            AudioClip clip = AudioClip.Create(name, n, channels, frequency, false);
            float[] data = new float[n * channels];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = i / (float)n;
            }
            clip.SetData(data, 0);
            return Track(clip);
        }

        private static float[] ReadAllSamples(AudioClip clip)
        {
            float[] buffer = new float[clip.samples * clip.channels];
            clip.GetData(buffer, 0);
            return buffer;
        }

        #region GetResultClip
        [Test]
        [Category("Finding_29")]
        public void GetResultClip_NoEdit_ReturnsOriginalInstance()
        {
            AudioClip clip = Track(TestAudioLibrary.CreateClip(0.1f, "Untouched"));
            using var helper = new AudioClipEditingHelper(clip);

            AudioClip result = helper.GetResultClip();

            // Characterizes TEST_FINDINGS #29: an unedited helper hands back the SAME instance, not a copy.
            Assert.AreSame(clip, result);
        }

        [Test]
        public void GetResultClip_NoClip_ReturnsNull()
        {
            using var helper = new AudioClipEditingHelper(null);
            Assert.IsNull(helper.GetResultClip());
        }
        #endregion

        #region Trim
        [Test]
        public void Trim_PartialRange_ReturnsExpectedWindowAndFlipsHasEdited()
        {
            AudioClip clip = CreateRampClip("Ramp10", 10, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.Trim(Seconds(2), Seconds(3)); // drop 2 samples from the start, 3 from the end

            Assert.IsTrue(helper.HasEdited);
            AudioClip result = Track(helper.GetResultClip());
            Assert.AreNotSame(clip, result);
            float[] actual = ReadAllSamples(result);
            float[] expected = { 0.2f, 0.3f, 0.4f, 0.5f, 0.6f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void Trim_OnStreamingClip_FailsAndLeavesOriginalClip()
        {
            // Per Unity's docs, AudioClip.GetData flatly refuses on a streamed clip (stream:true in
            // AudioClip.Create, or a Streaming-load-type import) - it is the one documented, reliable
            // way to make TryGetSampleData return false rather than throw. (A too-large read range
            // does not fail either - GetData wraps around - which is why TryGetSampleData clamps it;
            // see Trim_RangeLongerThanTheClip_ClampsToTheEndInsteadOfWrappingAround.)
            AudioClip clip = Track(AudioClip.Create("StreamedRamp10", 10, 1, SampleRate, stream: true));
            using var helper = new AudioClipEditingHelper(clip);

            // Two errors are expected here: the engine refusing the read, then BroAudio reporting it.
            LogAssert.Expect(LogType.Error, TestAudioLibrary.AnyLogMessage); // Unity's own AudioClip.GetData error, untagged
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            helper.Trim(0f, 0f);

            Assert.IsFalse(helper.HasEdited, "TryGetSampleData returned false, so Trim must not report an edit.");
            Assert.AreSame(clip, helper.GetResultClip(), "A failed Trim must fall back to the original clip.");
        }

        [Test]
        [Category("Finding_61")]
        public void Trim_OnStreamingClip_LeavesAZeroedBufferThatLaterEditsApplyTo()
        {
            // Characterizes TEST_FINDINGS #61: TryGetSampleData allocates its out array BEFORE GetData fails and
            // hands that array back even when it returns false, and Trim stores it in the helper's sample field
            // unconditionally. The helper then holds a buffer of zeros the size of the requested range, never
            // read from the clip, so CanEdit reads true and every later edit runs on that silence. On an
            // imported streaming clip with real audio, GetResultClip after such an edit would return silence.
            // (This test's streamed clip has no PCM reader, so its own content cannot be compared - the
            // contrast test below shows that without the failed Trim the same clip is NOT editable.)
            AudioClip clip = Track(AudioClip.Create("StreamedRamp10", 10, 1, SampleRate, stream: true));
            using var helper = new AudioClipEditingHelper(clip);

            LogAssert.Expect(LogType.Error, TestAudioLibrary.AnyLogMessage); // Unity's own AudioClip.GetData error, untagged
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            helper.Trim(0f, 0f);
            Assert.IsFalse(helper.HasEdited, "Precondition: the Trim failed.");

            Assert.IsTrue(helper.CanEdit,
                "A failed Trim left no buffer behind - if so, #61 is fixed: update this pin and the finding.");

            helper.AdjustVolume(0.5f);

            Assert.IsTrue(helper.HasEdited, "AdjustVolume ran on the leftover buffer and reported an edit.");
            // No public member exposes the buffer short of GetResultClip, which would re-create a STREAMED clip
            // (the original's load type) and SetData into it - so the buffer is read directly.
            float[] buffer = (float[])SampleDataField.GetValue(helper);
            Assert.IsNotNull(buffer);
            Assert.AreEqual(10, buffer.Length, "The leftover buffer is sized to the requested range (the whole 10-sample clip).");
            Assert.That(buffer, Is.All.EqualTo(0f), "The leftover buffer is zeros - the clip was never read into it.");
        }

        [Test]
        public void StreamingClip_WithoutAFailedTrim_IsNotEditable()
        {
            // The contrast for #61: the helper's lazy read fails the same way, but leaves the buffer null,
            // so CanEdit is false and every edit is a no-op.
            AudioClip clip = Track(AudioClip.Create("StreamedRamp10", 10, 1, SampleRate, stream: true));
            using var helper = new AudioClipEditingHelper(clip);

            LogAssert.Expect(LogType.Error, TestAudioLibrary.AnyLogMessage); // Unity's own AudioClip.GetData error, untagged
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            Assert.IsFalse(helper.CanEdit, "A streamed clip's samples cannot be read, so the helper must not be editable.");
            Assert.IsFalse(helper.HasEdited);
        }

        [Test]
        public void Trim_RangeLongerThanTheClip_ClampsToTheEndInsteadOfWrappingAround()
        {
            // AudioClip.GetData wraps back to the start of the clip when the requested range runs past
            // the end rather than failing, so an oversized range would silently splice the clip with its
            // own beginning if nothing guarded it. TryGetSampleData clamps the read to the samples that
            // remain, which is the invariant this test pins. A negative end position is the simplest way
            // to ask for more than the clip holds.
            AudioClip clip = CreateRampClip("Ramp5", 5, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.Trim(0f, -Seconds(4)); // asks for 9 samples out of a 5-sample clip

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            Assert.AreEqual(5, actual.Length, "The read must stop at the end of the clip, not wrap around it.");
            float[] expected = { 0f, 0.2f, 0.4f, 0.6f, 0.8f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        #endregion

        #region AddSlient
        [Test]
        [Category("Finding_27")]
        public void AddSlient_PrependsSilenceAndShiftsOriginalDataToTail()
        {
            // Characterizes TEST_FINDINGS #27: despite the name giving no indication, the silence goes at the FRONT.
            AudioClip clip = CreateRampClip("Ramp4", 4, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.AddSlient(Seconds(3)); // mono => 3 silent samples

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            float[] expected = { 0f, 0f, 0f, 0f, 0.25f, 0.5f, 0.75f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        [Test]
        [Category("Finding_27")]
        public void AddSlient_PadLengthTruncatesInsteadOfRounding()
        {
            // Characterizes TEST_FINDINGS #27: second half - AddSlient sizes the pad with a plain (int)
            // cast, while FadeIn/FadeOut/GetDataSample all use Math.Round(..., AwayFromZero). This time
            // computes to 3.9999 samples, so the cast yields 3 where every other path would yield 4.
            AudioClip clip = CreateRampClip("Ramp4Trunc", 4, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.AddSlient(0.0039999f);

            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            Assert.AreEqual(4 + 3, actual.Length,
                "characterizes: AddSlient's pad length truncates via a plain (int) cast, unlike FadeIn/FadeOut/" +
                "GetDataSample which round - a 3.9999-sample pad yields 3, not 4.");
        }
        #endregion

        #region AdjustVolume
        [Test]
        public void AdjustVolume_MultipliesEverySample()
        {
            AudioClip clip = CreateRampClip("Ramp4", 4, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.AdjustVolume(0.5f);

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            float[] expected = { 0f, 0.125f, 0.25f, 0.375f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        #endregion

        #region Reverse
        [Test]
        public void Reverse_Mono_ReversesSampleOrder()
        {
            AudioClip clip = CreateRampClip("Ramp5", 5, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.Reverse();

            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            float[] expected = { 0.8f, 0.6f, 0.4f, 0.2f, 0f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        [Category("Finding_26")]
        public void Reverse_Stereo_ReversesRawArraySoChannelsAreTransposed()
        {
            // Characterizes TEST_FINDINGS #26: Reverse() flips the raw interleaved array with no channel awareness.
            // On a stereo clip this doesn't just time-reverse - it also SWAPS L and R, because index 0
            // (a left slot) ends up holding what was the last RIGHT sample.
            AudioClip clip = CreateRampClip("Ramp3Stereo", 3, 2);
            using var helper = new AudioClipEditingHelper(clip);

            helper.Reverse();

            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            // Original interleaved (L0,R0,L1,R1,L2,R2) = (0, 1/3, 2/3, 1, 4/3, 5/3).
            float[] expected = { 5f / 3f, 4f / 3f, 1f, 2f / 3f, 1f / 3f, 0f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
            // Left channel (even indices) post-reverse is the original right channel, reversed - the transpose.
            Assert.AreEqual(5f / 3f, actual[0], Tolerance, "index 0 (now 'left') should hold the old last-right sample.");
        }
        #endregion

        #region FadeIn / FadeOut
        [Test]
        public void FadeIn_RampsFromSilenceOverFadeWindowOnly()
        {
            AudioClip clip = CreateRampClip("Ramp5", 5, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.FadeIn(Seconds(3)); // mono => fadeSample = 3

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            // i=0: *0; i=1: *(1/3); i=2: *(2/3); i=3,4 untouched.
            float[] expected = { 0f, 0.2f / 3f, 0.4f * (2f / 3f), 0.6f, 0.8f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void FadeIn_ZeroTime_IsANoOpAndDoesNotReportAnEdit()
        {
            // A zero fade window divides by it (volIncrement = 1f/fadeSamples), which would compute
            // Infinity and still flip HasEdited, making GetResultClip mint a copy of an unchanged clip if
            // nothing guarded it. FadeIn returns early on a zero window instead, which is the invariant
            // this test pins.
            AudioClip clip = CreateRampClip("Ramp3", 3, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.FadeIn(0f);

            Assert.IsFalse(helper.HasEdited, "Nothing was touched, so no edit must be reported.");
            AudioClip result = Track(helper.GetResultClip());
            Assert.AreSame(clip, result, "With no edit, GetResultClip hands back the original instance.");
            float[] actual = ReadAllSamples(result);
            float[] expected = { 0f, 1f / 3f, 2f / 3f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance), "the untouched range must be untouched");
        }

        [Test]
        public void FadeOut_RampsToSilenceOverFadeWindowOnly()
        {
            AudioClip clip = CreateRampClip("Ramp5", 5, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.FadeOut(Seconds(3)); // mono => fadeSample = 3, starts at index 5-3=2

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            // i=2: *1; i=3: *(2/3); i=4: *(1/3). indices 0,1 untouched.
            float[] expected = { 0f, 0.2f, 0.4f, 0.6f * (2f / 3f), 0.8f * (1f / 3f) };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        #endregion

        #region ConvertToMono
        [Test]
        [Category("Finding_25")]
        public void ConvertToMono_Downmixing_AveragesEachFrameButDropsTheFinalFrame()
        {
            // Characterizes TEST_FINDINGS #25: each frame (L,R pair) is grouped and averaged correctly - the grouping
            // is not offset - but the running sum is only flushed when the NEXT frame's boundary is reached, so the
            // final frame is never flushed: output length is (totalSamples / channels) - 1, and the last frame is lost.
            AudioClip clip = CreateRampClip("Ramp3Stereo", 3, 2); // interleaved: 0, 1/3, 2/3, 1, 4/3, 5/3
            using var helper = new AudioClipEditingHelper(clip);

            helper.ConvertToMono(MonoConversionMode.Downmixing);

            AudioClip result = Track(helper.GetResultClip());
            Assert.AreEqual(1, result.channels);
            float[] actual = ReadAllSamples(result);
            // (0+1/3)/2, (2/3+1)/2 - the third pair (4/3+5/3)/2 is dropped entirely.
            float[] expected = { 1f / 6f, 5f / 6f };
            Assert.AreEqual(2, actual.Length, "6 interleaved samples / 2 channels - 1 dropped group = 2.");
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void ConvertToMono_SelectOneChannel_TakesTheNamedChannelInFull()
        {
            AudioClip clip = CreateRampClip("Ramp3Stereo", 3, 2); // interleaved: 0, 1/3, 2/3, 1, 4/3, 5/3

            using (var leftHelper = new AudioClipEditingHelper(clip))
            {
                leftHelper.ConvertToMono(MonoConversionMode.Left);
                float[] left = ReadAllSamples(Track(leftHelper.GetResultClip()));
                float[] expectedLeft = { 0f, 2f / 3f, 4f / 3f };
                Assert.AreEqual(expectedLeft.Length, left.Length, "SelectOneChannel keeps the full frame count, unlike Downmixing.");
                for (int i = 0; i < expectedLeft.Length; i++)
                {
                    Assert.AreEqual(expectedLeft[i], left[i], Tolerance, $"left index {i}");
                }
            }

            using (var rightHelper = new AudioClipEditingHelper(clip))
            {
                rightHelper.ConvertToMono(MonoConversionMode.Right);
                float[] right = ReadAllSamples(Track(rightHelper.GetResultClip()));
                float[] expectedRight = { 1f / 3f, 1f, 5f / 3f };
                Assert.AreEqual(expectedRight.Length, right.Length, "SelectOneChannel keeps the full frame count for the right channel too.");
                for (int i = 0; i < expectedRight.Length; i++)
                {
                    Assert.AreEqual(expectedRight[i], right[i], Tolerance, $"right index {i}");
                }
            }
        }

        [Test]
        public void ConvertToMono_ThenFadeIn_SizesFadeWindowByMonoChannelCountNotOriginal()
        {
            // GetChannelCount() feeds off _isMono, which ConvertToMono flips. If FadeIn used the
            // original stereo channel count instead, fadeSample would be twice as large and this
            // test's untouched indices (2,3) would get faded too.
            AudioClip clip = CreateRampClip("Ramp4Stereo", 4, 2); // interleaved: 0,.25,.5,.75,1,1.25,1.5,1.75
            using var helper = new AudioClipEditingHelper(clip);

            helper.ConvertToMono(MonoConversionMode.Left); // -> mono [0, 0.5, 1, 1.5]
            helper.FadeIn(Seconds(2)); // now mono => fadeSample = 2

            AudioClip result = Track(helper.GetResultClip());
            Assert.AreEqual(1, result.channels);
            float[] actual = ReadAllSamples(result);
            float[] expected = { 0f, 0.25f, 1f, 1.5f }; // index0 *0, index1 *(1/2); index2,3 untouched
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        #endregion
    }
}