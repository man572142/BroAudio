using Ami.BroAudio.Tests;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// <see cref="AudioClipEditingHelper"/>'s sample math: a bug here corrupts a user's audio file, so every edit
    /// is checked against exact values from a ramp clip. Pinned quirks (TEST_FINDINGS) are characterized, not fixed.
    /// <para>
    /// At <see cref="SampleRate"/> one sample is one millisecond, so every <see cref="Seconds"/> value survives the
    /// float round-trip (including PrependSilence's (int) cast) and index math can be asserted exactly.
    /// </para>
    /// </summary>
    public class ClipEditingTests : BroEditorTestFixture
    {
        private const float Tolerance = 1e-4f;

        /// <summary>AudioClipEditingHelper's private sample buffer; see <see cref="EditorReflected"/>.</summary>
        private static System.Reflection.FieldInfo SampleDataField =>
            TestAudioLibrary.Reflected.Field(typeof(AudioClipEditingHelper), EditorReflected.AudioClipEditingHelper.SampleDatas);

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
        public void GetResultClip_NoEdit_ReturnsOriginalInstance()
        {
            AudioClip clip = Track(TestAudioLibrary.CreateClip(0.1f, "Untouched"));
            using var helper = new AudioClipEditingHelper(clip);

            AudioClip result = helper.GetResultClip();

            // An unedited helper hands back the SAME instance, not a copy.
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
            // GetData refuses a streamed clip: the one reliable way to make TryGetSampleData return false.
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
            // Pins TEST_FINDINGS #61. A created streamed clip has no PCM reader, so content can't be compared;
            // StreamingClip_WithoutAFailedTrim_IsNotEditable is the contrast.
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
            // Read the field: GetResultClip would re-create a STREAMED clip and SetData into it.
            float[] buffer = (float[])SampleDataField.GetValue(helper);
            Assert.IsNotNull(buffer);
            Assert.AreEqual(10, buffer.Length, "The leftover buffer is sized to the requested range (the whole 10-sample clip).");
            Assert.That(buffer, Is.All.EqualTo(0f), "The leftover buffer is zeros - the clip was never read into it.");
        }

        [Test]
        public void StreamingClip_WithoutAFailedTrim_IsNotEditable()
        {
            // Contrast for #61: the lazy read fails the same way but leaves the buffer null.
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
            // GetData wraps past the end instead of failing, so TryGetSampleData must clamp. A negative end
            // position asks for more than the clip holds.
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

        #region PrependSilence
        [Test]
        public void PrependSilence_PrependsSilenceAndShiftsOriginalDataToTail()
        {
            AudioClip clip = CreateRampClip("Ramp4", 4, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.PrependSilence(Seconds(3)); // mono => 3 silent samples

            Assert.IsTrue(helper.HasEdited);
            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            float[] expected = { 0f, 0f, 0f, 0f, 0.25f, 0.5f, 0.75f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
        }
        [Test]
        public void PrependSilence_PadLengthTruncatesInsteadOfRounding()
        {
            // This time is 3.9999 samples.
            AudioClip clip = CreateRampClip("Ramp4Trunc", 4, 1);
            using var helper = new AudioClipEditingHelper(clip);

            helper.PrependSilence(0.0039999f);

            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            Assert.AreEqual(4 + 3, actual.Length,
                "characterizes: PrependSilence's pad length truncates via a plain (int) cast, unlike FadeIn/FadeOut/" +
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
            // Pins TEST_FINDINGS #26.
            AudioClip clip = CreateRampClip("Ramp3Stereo", 3, 2);
            using var helper = new AudioClipEditingHelper(clip);

            helper.Reverse();

            float[] actual = ReadAllSamples(Track(helper.GetResultClip()));
            // Original interleaved (L0,R0,L1,R1,L2,R2) = (0, 1/3, 2/3, 1, 4/3, 5/3).
            float[] expected = { 5f / 3f, 4f / 3f, 1f, 2f / 3f, 1f / 3f, 0f };
            Assert.That(actual, Is.EqualTo(expected).Within(Tolerance));
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
            // FadeIn must return early: a zero window divides by zero and would still flip HasEdited.
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
        public void ConvertToMono_Downmixing_AveragesEachFrameButDropsTheFinalFrame()
        {
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
            // Sized by the stereo count instead, fadeSample would double and fade indices 2,3 too.
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