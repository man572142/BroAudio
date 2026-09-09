using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fails the PlayMode run when the editor image's audio device is gone, instead of letting the tests
    /// that need it quietly do nothing.
    /// <para>
    /// Two dozen and counting open with <see cref="BroAudioTestFixture.RequireRealtimeAudioClock"/> — every
    /// seamless and chained handover, most of the spectrum analyzer suite, the scheduling pins, the dominator
    /// routing tests, and the only tests that characterize TEST_FINDINGS #38-#40. (Deliberately not an exact
    /// count: one was written here and went stale within the week. `grep -rc "yield return
    /// RequireRealtimeAudioClock();" Assets/Tests/Runtime/` is the current number.) It calls <c>Assert.Ignore</c> when the DSP clock is off
    /// wall time by more than 10%, which is right for a developer machine and silent on CI: an ignored test
    /// is not a failure, so an image that lost its PulseAudio null sink reports green with the heart of the
    /// suite never executed. <c>check_test_suites.py</c> cannot catch it either — the fixtures are all
    /// present in the results, they simply ran nothing.
    /// </para>
    /// <para>
    /// This is the same hole <see cref="OptionalPackageTests"/> closes for optional packages, and it draws
    /// the same kind of line: a missing thing is only a failure where it was promised. The promise here is
    /// <c>BROAUDIO_CI_EXPECTS_AUDIO</c>, baked into .github/docker/Dockerfile — the image CI passes as
    /// <c>customImage</c> on the PlayMode leg only — so the variable is present exactly where an audio
    /// device was built in and expected. Anywhere it is unset, an unrealtime clock is a fact about the
    /// machine rather than a regression, and this test passes so a local run stays green.
    /// </para>
    /// </summary>
    public class AudioClockProbeTests : BroAudioTestFixture
    {
        /// <summary>Set by .github/docker/Dockerfile; its presence means "this image ships an audio device".</summary>
        private const string CiExpectsAudioVariable = "BROAUDIO_CI_EXPECTS_AUDIO";

        [UnityTest]
        public IEnumerator AudioClock_IsRealtime_SoTheTestsGatedOnItActuallyRan()
        {
            // The same measurement RequireRealtimeAudioClock decides on, cached for the rest of the run:
            // this probe reports the number that gate uses, it does not take a second opinion.
            yield return MeasureAudioClockRate();

            float rate = AudioClockRate;
            if (Mathf.Abs(rate - 1f) <= RealtimeAudioClockTolerance)
            {
                yield break;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CiExpectsAudioVariable)))
            {
                // No audio device was promised here, so the ignore gate is doing its job rather than
                // hiding a broken image. Pass — this must not turn a local run red.
                yield break;
            }

            Assert.Fail(
                $"The DSP clock runs at {rate:F2}x wall time, so this run had no realtime audio output device - " +
                $"but {CiExpectsAudioVariable} is set, meaning the editor image is supposed to provide one. " +
                "Every test that opens with RequireRealtimeAudioClock was silently ignored rather than run: " +
                "the seamless and chained handovers, most of the spectrum analyzer suite, the scheduling pins, " +
                "the dominator routing tests, and the TEST_FINDINGS #38-#40 characterizations. Everything else in this run reported green, so " +
                "treat that green as meaningless until this passes. Check the PulseAudio null sink in " +
                ".github/docker/Dockerfile: that the image was rebuilt after the Dockerfile last changed (the " +
                "workflow reuses an already-published tag), and that /usr/bin/unity-editor.d/00-audio.sh still " +
                "starts the daemon before the Editor launches.");
        }
    }
}