using System.Collections;
using System.Collections.Generic;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SpectrumAnalyzer"/> component: source acquisition, FFT buffer sizing, and per-band
    /// ballistics (attack / decay / smoothing).
    /// <para>
    /// Most tests play a <b>silent</b> clip on purpose: every band's target is then exactly
    /// <see cref="AudioConstant.MinDecibelVolume"/>, so a pinned band moves at a rate only the inspector
    /// fields control. Exception: a sub-bin band's target depends on whether one bin is exactly zero, so
    /// that test accepts both outcomes.
    /// </para>
    /// <para>
    /// Tests needing playback past a frame call RequireRealtimeAudioClock first: without a device the DSP
    /// clock outruns wall time and the clip ends before the analyzer sees it.
    /// </para>
    /// </summary>
    public class SpectrumAnalyzerTests : BroAudioTestFixture
    {
        private const int DefaultResolutionScale = 10;

        /// <summary>
        /// One FFT bin's width, as <see cref="SpectrumAnalyzer"/>'s Start computes it. Derive bin-aligned
        /// frequencies from this, never hardcode them: the output sample rate is a machine property.
        /// </summary>
        private static float HarmonicOf(int resolutionScale)
        {
            Assert.Greater(AudioSettings.outputSampleRate, 0, "The audio engine reported no output sample rate.");
            return AudioSettings.outputSampleRate / 2f / (1 << resolutionScale);
        }

        /// <summary>A clip of pure digital silence, so every FFT bin is exactly zero.</summary>
        private AudioClip NewSilentClip(float seconds = 20f, string name = "SilentTestClip")
            => Track(AudioClip.Create(name, Mathf.RoundToInt(seconds * TestAudioLibrary.SampleRate), 1, TestAudioLibrary.SampleRate, false));

        /// <summary>
        /// Builds a tracked SpectrumAnalyzer. The host starts inactive so the fields land before Start,
        /// which snapshots _resolutionScale and _soundSource.
        /// </summary>
        private SpectrumAnalyzer NewAnalyzer(float[] bandFrequencies, SoundSource soundSource = null,
            int resolutionScale = DefaultResolutionScale, SpectrumAnalyzer.Metering metering = SpectrumAnalyzer.Metering.Peak,
            FFTWindow windowType = FFTWindow.BlackmanHarris, int attack = 100, int decay = 1500, int smooth = 0)
        {
            GameObject host = Track(new GameObject("SpectrumAnalyzerHost"));
            host.SetActive(false);

            SpectrumAnalyzer analyzer = host.AddComponent<SpectrumAnalyzer>();
            var bands = new SpectrumAnalyzer.Band[bandFrequencies.Length];
            for (int i = 0; i < bandFrequencies.Length; i++)
            {
                bands[i] = new SpectrumAnalyzer.Band { Frequency = bandFrequencies[i] };
            }

            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.Bands, bands);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.ResolutionScale, resolutionScale);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.Metering, metering);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.WindowType, windowType);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.Attack, attack);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.Decay, decay);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.Smooth, smooth);
            TestAudioLibrary.SetPrivateField(analyzer, SpectrumAnalyzer.NameOf.SoundSource, soundSource);

            host.SetActive(true);
            return analyzer;
        }

        /// <summary>A tracked SoundSource that plays the given sound the moment it is activated.</summary>
        private SoundSource NewPlayingSource(SoundID id)
        {
            GameObject host = Track(new GameObject("SpectrumSoundSource"));
            host.SetActive(false);

            SoundSource source = host.AddComponent<SoundSource>();
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.Sound, id);
            TestAudioLibrary.SetPrivateField(source, TestAudioLibrary.Reflected.SoundSource.PlayOnEnable, true);

            host.SetActive(true);
            return source;
        }

        /// <summary>Start allocates the spectrum buffer, so nothing may be asserted before it has run.</summary>
        private static IEnumerator WaitForStart(SpectrumAnalyzer analyzer)
            => WaitUntilOrTimeout(() => analyzer.Spectrum != null, "SpectrumAnalyzer.Start to allocate the spectrum buffer", DefaultPlaybackWaitSeconds);

        /// <summary>Counts OnUpdate invocations and records the last payload the analyzer handed out.</summary>
        private sealed class UpdateRecorder
        {
            public int Count;
            public IReadOnlyList<SpectrumAnalyzer.Band> LastBands;

            public UpdateRecorder(SpectrumAnalyzer analyzer) => analyzer.OnUpdate += Record;

            private void Record(IReadOnlyList<SpectrumAnalyzer.Band> bands)
            {
                Count++;
                LastBands = bands;
            }
        }

        private static void PinBandTo(SpectrumAnalyzer.Band band, float decibel)
            => band.SetVolume(decibel.ToNormalizeVolume(), decibel);

        /// <summary>Starts a silent clip long enough to outlast any test here, and hands back its player.</summary>
        private IAudioPlayer PlaySilence(float seconds = 20f)
            => BroAudio.Play(NewSound("SilentSpectrumSfx", BroAudioType.SFX, NewSilentClip(seconds)));

        #region Setup and inert paths
        [UnityTest]
        public IEnumerator Start_SizesTheSpectrumBufferFromTheResolutionScale()
        {
            SpectrumAnalyzer coarse = NewAnalyzer(new[] { 1000f }, resolutionScale: 6);
            SpectrumAnalyzer fine = NewAnalyzer(new[] { 1000f }, resolutionScale: 12);

            yield return WaitForStart(coarse);
            yield return WaitForStart(fine);

            Assert.AreEqual(64, coarse.Spectrum.Count, "A resolution scale of 6 must allocate 2^6 samples.");
            Assert.AreEqual(4096, fine.Spectrum.Count, "A resolution scale of 12 must allocate 2^12 samples.");
        }

        [UnityTest]
        public IEnumerator Bands_BeforeAnyPlayback_RestAtTheSilenceFloor()
        {
            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 500f, 2000f, 8000f });
            yield return WaitForStart(analyzer);

            Assert.AreEqual(3, analyzer.BandCount, "BandCount must report the configured band array's length.");
            Assert.AreEqual(3, analyzer.Bands.Count, "Bands must expose the same array BandCount counts.");
            foreach (SpectrumAnalyzer.Band band in analyzer.Bands)
            {
                Assert.AreEqual(AudioConstant.MinDecibelVolume, band.DecibelVolume, DecibelTolerance, "A fresh band sits at the decibel floor.");
                Assert.AreEqual(AudioConstant.MinVolume, band.Amplitube, 1e-6f, "A fresh band sits at the normalized floor.");
            }
        }

        [UnityTest]
        public IEnumerator Update_WithNoPlayerAndNoSoundSource_StaysInert()
        {
            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 1000f });
            yield return WaitForStart(analyzer);
            var recorder = new UpdateRecorder(analyzer);

            yield return WaitFrames(5);

            Assert.AreEqual(0, recorder.Count, "An analyzer with no source must never raise OnUpdate.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, analyzer.Bands[0].DecibelVolume, DecibelTolerance,
                "An analyzer with no source must leave its bands at the floor.");
        }

        [UnityTest]
        public IEnumerator Update_AfterThePlayerIsRecycled_GoesQuiet()
        {
            SoundID id = NewSound("RecycledSpectrumSfx", BroAudioType.SFX, NewSilentClip(20f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitFrames(1);

            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 1000f });
            yield return WaitForStart(analyzer);
            analyzer.SetSource(player);

            BroAudio.Stop(BroAudioType.All, 0f);
            yield return WaitForRecycle(player);

            var recorder = new UpdateRecorder(analyzer);
            yield return WaitFrames(5);

            Assert.AreEqual(0, recorder.Count, "A recycled player is not playing, so the analyzer must stop sampling it.");
        }
        #endregion

        #region Source acquisition
        [UnityTest]
        public IEnumerator SetSource_WithAPlayingPlayer_RaisesOnUpdateEveryFrameWithTheLiveBandList()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 1000f, 8000f });
            yield return WaitForStart(analyzer);
            analyzer.SetSource(player);

            var recorder = new UpdateRecorder(analyzer);
            yield return WaitFrames(5);

            Assert.GreaterOrEqual(recorder.Count, 4, "OnUpdate fires once per frame for as long as the player is playing.");
            Assert.AreSame(analyzer.Bands, recorder.LastBands, "OnUpdate hands out the analyzer's own band array, not a copy.");
        }

        [UnityTest]
        public IEnumerator Update_TakesThePlayerFromItsSoundSource_ButOnlyIfItWasAssignedBeforeStart()
        {
            yield return RequireRealtimeAudioClock();
            SoundID id = NewSound("SourcedSpectrumSfx", BroAudioType.SFX, NewSilentClip(20f));
            SoundSource source = NewPlayingSource(id);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", DefaultPlaybackWaitSeconds);

            SpectrumAnalyzer wiredBeforeStart = NewAnalyzer(new[] { 1000f }, soundSource: source);
            SpectrumAnalyzer wiredAfterStart = NewAnalyzer(new[] { 1000f });
            yield return WaitForStart(wiredBeforeStart);
            yield return WaitForStart(wiredAfterStart);
            TestAudioLibrary.SetPrivateField(wiredAfterStart, SpectrumAnalyzer.NameOf.SoundSource, source);

            var wiredBeforeRecorder = new UpdateRecorder(wiredBeforeStart);
            var wiredAfterRecorder = new UpdateRecorder(wiredAfterStart);
            yield return WaitFrames(5);

            Assert.GreaterOrEqual(wiredBeforeRecorder.Count, 4, "An analyzer wired to a SoundSource before Start must adopt that source's player.");
            Assert.AreEqual(0, wiredAfterRecorder.Count,
                "Start caches whether a SoundSource was assigned, so one assigned afterwards is never polled.");
        }
        #endregion

        #region Ballistics
        [UnityTest]
        public IEnumerator Update_WithSilence_HoldsEveryBandAtTheFloor()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 1000f, 8000f });
            yield return WaitForStart(analyzer);
            analyzer.SetSource(player);

            yield return WaitFrames(10);

            foreach (SpectrumAnalyzer.Band band in analyzer.Bands)
            {
                // A range, not equality: the rate limiter can overshoot by one step. The lower bound separates
                // resting from the runaway drift pinned below.
                Assert.LessOrEqual(band.DecibelVolume, AudioConstant.MinDecibelVolume + DecibelTolerance,
                    "Silence resolves to the decibel floor, so no band may climb above it.");
                Assert.GreaterOrEqual(band.DecibelVolume, AudioConstant.MinDecibelVolume - 1f,
                    "A band at its target must settle there rather than keep subtracting steps.");
                Assert.AreEqual(AudioConstant.MinVolume, band.Amplitube, 1e-6f, "The normalized amplitude tracks the decibel value.");
            }
        }

        // Decay is the milliseconds to shed MaxVolumeChange (20dB).
        [UnityTest]
        public IEnumerator Update_Decay_RateLimitsTheFallAndTheDecaySettingSetsTheRate()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            SpectrumAnalyzer fast = NewAnalyzer(new[] { 1000f }, decay: 20);
            SpectrumAnalyzer slow = NewAnalyzer(new[] { 1000f }, decay: 20000);
            yield return WaitForStart(fast);
            yield return WaitForStart(slow);
            fast.SetSource(player);
            slow.SetSource(player);

            PinBandTo(fast.Bands[0], AudioConstant.FullDecibelVolume);
            PinBandTo(slow.Bands[0], AudioConstant.FullDecibelVolume);

            yield return WaitUntilOrTimeout(() => fast.Bands[0].DecibelVolume <= AudioConstant.MinDecibelVolume + DecibelTolerance,
                "the fast-decay band to reach the floor", RampConvergenceWaitSeconds);

            Assert.Less(slow.Bands[0].DecibelVolume, AudioConstant.FullDecibelVolume,
                "The slow band must still be falling - Decay limits the rate, it does not stop the fall.");
            Assert.Greater(slow.Bands[0].DecibelVolume, -20f,
                "A 20s decay sheds 20dB per 20s, so the slow band cannot have collapsed to the floor in the time the fast one took.");
        }

        [UnityTest]
        public IEnumerator Update_Attack_RateLimitsTheRiseAndTheAttackSettingSetsTheRate()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            const float StartDecibel = -200f; // below the floor, so silence itself is a rising target
            SpectrumAnalyzer fast = NewAnalyzer(new[] { 1000f }, attack: 20);
            SpectrumAnalyzer slow = NewAnalyzer(new[] { 1000f }, attack: 20000);
            yield return WaitForStart(fast);
            yield return WaitForStart(slow);
            fast.SetSource(player);
            slow.SetSource(player);

            PinBandTo(fast.Bands[0], StartDecibel);
            PinBandTo(slow.Bands[0], StartDecibel);

            yield return WaitUntilOrTimeout(() => fast.Bands[0].DecibelVolume >= AudioConstant.MinDecibelVolume - DecibelTolerance,
                "the fast-attack band to climb to the target", RampConvergenceWaitSeconds);

            Assert.AreEqual(AudioConstant.MinDecibelVolume, fast.Bands[0].DecibelVolume, DecibelTolerance,
                "The last step snaps to the target instead of overshooting it.");
            Assert.Greater(slow.Bands[0].DecibelVolume, StartDecibel, "The slow band must still be rising.");
            Assert.Less(slow.Bands[0].DecibelVolume, -190f,
                "A 20s attack gains 20dB per 20s, so the slow band cannot have caught up in the time the fast one took.");
        }

        [UnityTest]
        public IEnumerator Update_Smooth_ScalesTheStepByTheRemainingDifference()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            SpectrumAnalyzer stepped = NewAnalyzer(new[] { 1000f }, decay: 100, smooth: 0);
            SpectrumAnalyzer smoothed = NewAnalyzer(new[] { 1000f }, decay: 100, smooth: 4000);
            yield return WaitForStart(stepped);
            yield return WaitForStart(smoothed);
            stepped.SetSource(player);
            smoothed.SetSource(player);

            PinBandTo(stepped.Bands[0], AudioConstant.FullDecibelVolume);
            PinBandTo(smoothed.Bands[0], AudioConstant.FullDecibelVolume);

            yield return WaitUntilOrTimeout(() => stepped.Bands[0].DecibelVolume <= AudioConstant.MinDecibelVolume + DecibelTolerance,
                "the unsmoothed band to reach the floor", RampConvergenceWaitSeconds);

            Assert.Less(smoothed.Bands[0].DecibelVolume, AudioConstant.FullDecibelVolume, "The smoothed band must still be moving.");
            Assert.Greater(smoothed.Bands[0].DecibelVolume, -40f,
                "An 80dB difference over a smoothing of 4000 scales the step down 50x, so the smoothed band is nowhere near the floor yet.");
        }
        #endregion

        #region Band ranges
        // Pins TEST_FINDINGS #39. Whether the one covered bin is exactly zero (0/0 = NaN, falls forever) or
        // not (x/0 = +Infinity, climbs to the ceiling) is not controllable, so both branches are accepted.
        [UnityTest]
        [Category("Finding_39")]
        public IEnumerator Update_WithABandNarrowerThanOneFftBin_LeavesTheFloorUnderRmsButHoldsUnderPeak()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            // Band 1 spans (k - 0.5) to (k + 0.5) bin widths, so it starts and ends on bin k: length 0.
            float harmonic = HarmonicOf(DefaultResolutionScale);
            float[] bands = { 39.5f * harmonic, 40.5f * harmonic };

            SpectrumAnalyzer rms = NewAnalyzer(bands, metering: SpectrumAnalyzer.Metering.RMS);
            SpectrumAnalyzer peak = NewAnalyzer(bands, metering: SpectrumAnalyzer.Metering.Peak);
            yield return WaitForStart(rms);
            yield return WaitForStart(peak);
            rms.SetSource(player);
            peak.SetSource(player);

            // Far enough that no ramp sits there by accident; both runaways cover it in well under a second.
            const float FloorDistance = 10f;
            yield return WaitUntilOrTimeout(
                () => Mathf.Abs(rms.Bands[1].DecibelVolume - AudioConstant.MinDecibelVolume) > FloorDistance,
                "the RMS metering of a sub-bin band to leave the decibel floor in either direction", RampConvergenceWaitSeconds);

            if (rms.Bands[1].DecibelVolume < AudioConstant.MinDecibelVolume)
            {
                Assert.AreEqual(AudioConstant.MinVolume, rms.Bands[1].Amplitube, 1e-6f,
                    "Amplitube clamps at the floor, so nothing bound to it can see the value running away underneath.");
            }
            else
            {
                // The last step snaps to the target, so the climb ends on MaxDecibelVolume exactly.
                yield return WaitUntilOrTimeout(
                    () => rms.Bands[1].DecibelVolume >= AudioConstant.MaxDecibelVolume,
                    "the same band to finish its climb to the ceiling", RampConvergenceWaitSeconds);

                Assert.AreEqual(AudioConstant.MaxVolume, rms.Bands[1].Amplitube, 1e-4f,
                    "Amplitube clamps at the ceiling, so a meter bound to it reads full scale on silence.");
            }

            Assert.AreEqual(AudioConstant.MinDecibelVolume, rms.Bands[0].DecibelVolume, DecibelTolerance,
                "The wide band of the same analyzer holds - this is the range width, not the metering mode.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, peak.Bands[1].DecibelVolume, DecibelTolerance,
                "Peak metering never divides by the range length, so the same band rests on the floor.");
        }
        #endregion

        #region End to end
        [UnityTest]
        public IEnumerator Update_WithATone_RaisesTheBandCoveringItAndNotTheOneAboveIt()
        {
            yield return RequireRealtimeAudioClock();
            SoundID id = NewSound("ToneSpectrumSfx", BroAudioType.SFX, NewClip(20f)); // NewClip is a 440Hz sine
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            SpectrumAnalyzer analyzer = NewAnalyzer(new[] { 1000f, 12000f }, attack: 100);
            yield return WaitForStart(analyzer);
            analyzer.SetSource(player);

            yield return WaitForSpectrumData(analyzer);
            yield return WaitUntilOrTimeout(() => analyzer.Bands[0].DecibelVolume > AudioConstant.MinDecibelVolume + 20f,
                "the tone's own band to rise well clear of the floor", RampConvergenceWaitSeconds);

            Assert.Greater(analyzer.Bands[0].DecibelVolume, analyzer.Bands[1].DecibelVolume + 6f,
                "The 10Hz-1kHz band contains the 440Hz tone; the 1kHz-12kHz band above it contains only window leakage.");
            Assert.Greater(analyzer.Bands[0].Amplitube, AudioConstant.MinVolume,
                "The normalized amplitude a meter binds to must rise with the decibel value.");
        }

        // Pins TEST_FINDINGS #40. Must play a tone, not silence: on an all-zero spectrum 0 * 1 == 0 * 20, so
        // the pin would survive the fix it exists to catch.
        [UnityTest]
        [Category("Finding_40")]
        public IEnumerator Update_BandWeighting_HasNoEffectOnTheBandOutput()
        {
            yield return RequireRealtimeAudioClock();
            SoundID id = NewSound("WeightedSpectrumSfx", BroAudioType.SFX, NewClip(20f)); // NewClip is a 440Hz sine
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // One 10Hz-1kHz band - the window the test above proves the 440Hz tone lights up.
            SpectrumAnalyzer unweighted = NewAnalyzer(new[] { 1000f }, attack: 100);
            SpectrumAnalyzer weighted = NewAnalyzer(new[] { 1000f }, attack: 100);
            yield return WaitForStart(unweighted);
            yield return WaitForStart(weighted);
            TestAudioLibrary.SetPrivateField(unweighted.Bands[0], SpectrumAnalyzer.Band.NameOf.Weighted, 1f);
            TestAudioLibrary.SetPrivateField(weighted.Bands[0], SpectrumAnalyzer.Band.NameOf.Weighted, 20f);
            unweighted.SetSource(player);
            weighted.SetSource(player);

            yield return WaitForSpectrumData(unweighted);
            yield return WaitUntilOrTimeout(() => unweighted.Bands[0].DecibelVolume > AudioConstant.MinDecibelVolume + 20f,
                "the tone's own band to rise well clear of the floor", RampConvergenceWaitSeconds);
            // Past the 20dB-per-100ms attack ramp, so both bands sit on the tone's level where a weight would show.
            yield return new WaitForSeconds(0.5f); // a frame-clock ramp, so wall time is the right clock

            Assert.Greater(weighted.Bands[0].DecibelVolume, AudioConstant.MinDecibelVolume + 20f,
                "Precondition: both analyzers are metering the tone, not resting on a floor where any weighting would cancel out.");
            Assert.AreEqual(unweighted.Bands[0].DecibelVolume, weighted.Bands[0].DecibelVolume, 0.5f,
                "Characterizes TEST_FINDINGS #40: a band's Weighted field is written by the inspector and read by nothing - "
                + "a 20x weight on a band metering a real tone moves its output by less than half a dB, which is to say not at all.");
        }

        /// <summary>
        /// Fails unless the engine fills the spectrum buffer. Don't make it Assert.Ignore: callers already
        /// passed RequireRealtimeAudioClock, so zero data is a real failure, and ignoring would silently
        /// disable the #40 pin.
        /// </summary>
        private static IEnumerator WaitForSpectrumData(SpectrumAnalyzer analyzer, float timeout = 2f)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < deadline)
            {
                for (int i = 0; i < analyzer.Spectrum.Count; i++)
                {
                    if (analyzer.Spectrum[i] > 0f)
                    {
                        yield break;
                    }
                }
                yield return null;
            }
            Assert.Fail("The spectrum stayed all-zero for " + timeout + "s while a 440Hz tone was playing on a realtime " +
                "audio clock. The device is mixing (RequireRealtimeAudioClock passed), so the analyzer is not reading " +
                "the player it was given: check SpectrumAnalyzer.SetSource and its IAudioPlayer.GetSpectrumData call.");
        }
        #endregion
    }
}
