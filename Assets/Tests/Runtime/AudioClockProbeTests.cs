using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fails the run when the CI image's audio device is gone: <see cref="BroAudioTestFixture.RequireRealtimeAudioClock"/>
    /// ignores rather than fails, so without this probe a lost device reports green with the DSP-gated tests
    /// never run. Like <see cref="OptionalPackageTests"/>, a missing device fails only where it was promised
    /// (<c>BROAUDIO_CI_EXPECTS_AUDIO</c>); elsewhere this passes so a local run stays green.
    /// </summary>
    public class AudioClockProbeTests : BroAudioTestFixture
    {
        /// <summary>Set by .github/docker/Dockerfile; its presence means "this image ships an audio device".</summary>
        private const string CiExpectsAudioVariable = "BROAUDIO_CI_EXPECTS_AUDIO";

        [UnityTest]
        public IEnumerator AudioClock_IsRealtime_SoTheTestsGatedOnItActuallyRan()
        {
            // The cached number the gate decides on, not a second measurement.
            yield return MeasureAudioClockRate();

            float rate = AudioClockRate;
            if (Mathf.Abs(rate - 1f) <= RealtimeAudioClockTolerance)
            {
                yield break;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CiExpectsAudioVariable)))
            {
                // No device was promised here; must not turn a local run red.
                yield break;
            }

            Assert.Fail(
                $"The DSP clock runs at {rate:F2}x wall time, so this run had no realtime audio output device - " +
                $"but {CiExpectsAudioVariable} is set, meaning the editor image is supposed to provide one. " +
                "Every test that opens with RequireRealtimeAudioClock was silently ignored rather than run: " +
                "the seamless and chained handovers, most of the spectrum analyzer suite, the scheduling pins, " +
                "the dominator routing tests, and the TEST_FINDINGS #39-#40 characterizations. Everything else in this run reported green, so " +
                "treat that green as meaningless until this passes. Check the PulseAudio null sink in " +
                ".github/docker/Dockerfile: that the image was rebuilt after the Dockerfile last changed (the " +
                "workflow reuses an already-published tag), and that /usr/bin/unity-editor.d/00-audio.sh still " +
                "starts the daemon before the Editor launches.");
        }
    }
}