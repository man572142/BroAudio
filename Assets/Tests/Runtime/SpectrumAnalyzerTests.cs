using System.Collections;
using System.Collections.Generic;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SpectrumAnalyzer"/> no-code component: where it gets its player, how it sizes its FFT
    /// buffer, and the per-band ballistics (attack / decay / smoothing) that turn raw spectrum magnitudes
    /// into the values a meter is drawn from.
    /// <para>
    /// Most of these drive a deliberately <b>silent</b> clip. That is not a shortcut - it is what makes the
    /// ballistics assertions deterministic: with an all-zero spectrum every band's target is exactly the
    /// floor (<c>0f.ToDecibel()</c> == <see cref="AudioConstant.MinDecibelVolume"/>), so a band pinned above
    /// the floor must fall and a band pinned below it must rise, at a rate the inspector fields control and
    /// nothing else. <see cref="SpectrumAnalyzer.Band.SetVolume"/> is public, so pinning a band is done
    /// through the component's own API rather than by reflection. That holds for any band wide enough to
    /// meter over - residual noise from the mixer is orders of magnitude below <c>MinVolume</c> and clamps
    /// away - but not for the divide-by-zero band below, whose target is decided by whether one bin is
    /// exactly zero or merely near it, so that one test asserts on both outcomes.
    /// </para>
    /// <para>
    /// One test plays a real tone end to end. It is the only one whose result depends on the engine actually
    /// producing spectrum data, so it says so and ignores itself when the buffer never leaves zero. Every
    /// test that needs playback to survive more than a frame first calls RequireRealtimeAudioClock: without
    /// an audio output device the DSP clock runs hundreds of times faster than wall time and a clip is over
    /// before the analyzer ever sees it.
    /// </para>
    /// </summary>
    public class SpectrumAnalyzerTests : BroAudioTestFixture
    {
        private const int DefaultResolutionScale = 10;
        private const float DecibelTolerance = 0.1f;

        // SoundSource.NameOf is compiled only under UNITY_EDITOR, but Tests.asmdef targets every platform,
        // so the field names are spelled out here the same way SoundSourceTests spells them out.
        private const string SoundField = "_sound";
        private const string PlayOnEnableField = "_playOnEnable";

        /// <summary>
        /// The width of one FFT bin, computed exactly as <see cref="SpectrumAnalyzer"/>'s Start does. Band
        /// frequencies that have to land on a particular bin are derived from this rather than hardcoded -
        /// the output sample rate is a machine/driver property, not a project one.
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
        /// Builds a tracked SpectrumAnalyzer with its serialized fields already written. The host starts
        /// deactivated so the fields land before Start reads _resolutionScale and _soundSource; both are
        /// snapshotted there and never re-read.
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
            TestAudioLibrary.SetPrivateField(source, SoundField, id);
            TestAudioLibrary.SetPrivateField(source, PlayOnEnableField, true);

            host.SetActive(true);
            return source;
        }

        /// <summary>Start allocates the spectrum buffer, so nothing may be asserted before it has run.</summary>
        private static IEnumerator WaitForStart(SpectrumAnalyzer analyzer)
            => WaitUntilOrTimeout(() => analyzer.Spectrum != null, "SpectrumAnalyzer.Start to allocate the spectrum buffer", 2f);

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
        // Start turns the 6..13 resolution scale into a 2^scale FFT buffer - the size Unity requires
        // GetSpectrumData's array to be, and the divisor for the bin width every band range is measured in.
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

        // A band starts at the bottom of the scale rather than at zero, so a meter bound to Amplitube reads
        // silence rather than an uninitialized value before the first spectrum ever arrives.
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

        // With neither a SetSource call nor a serialized SoundSource there is nothing to sample: Update must
        // fall straight through, leaving the bands untouched and the event silent.
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

        // The gate is IsPlaying, not "was ever given a player": once the player behind the handle has been
        // recycled the analyzer goes quiet again rather than reading a dead source.
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
        // SetSource is the scripted entry point: from the moment a playing player is handed over, the
        // analyzer samples it once per frame and hands the live band array to every OnUpdate subscriber.
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

        // The serialized SoundSource is polled every frame until it yields a player, which is what lets a
        // Play On Enable source and an analyzer be wired up in the inspector with no script. But whether
        // that polling happens at all is decided once, in Start, from whether the field was already
        // assigned - so a SoundSource attached later is ignored for the object's whole life.
        [UnityTest]
        public IEnumerator Update_TakesThePlayerFromItsSoundSource_ButOnlyIfItWasAssignedBeforeStart()
        {
            yield return RequireRealtimeAudioClock();
            SoundID id = NewSound("SourcedSpectrumSfx", BroAudioType.SFX, NewSilentClip(20f));
            SoundSource source = NewPlayingSource(id);
            yield return WaitUntilOrTimeout(() => source.IsPlaying, "the SoundSource's playback to start", 2f);

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
                "Characterizes TEST_FINDINGS #38: Start caches whether a SoundSource was assigned, so one assigned afterwards is never polled.");
        }
        #endregion

        #region Ballistics
        // The baseline every other ballistics test measures against: a silent clip drives every band's
        // target to the floor, and a band already there stays put instead of drifting.
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
                // Bounded on both sides rather than asserted equal: the rate limiter can overshoot a target
                // by up to one step before snapping back, so "rests here" is a band around the floor, not a
                // point. The lower bound is what separates resting from the runaway drift pinned below.
                Assert.LessOrEqual(band.DecibelVolume, AudioConstant.MinDecibelVolume + DecibelTolerance,
                    "Silence resolves to the decibel floor, so no band may climb above it.");
                Assert.GreaterOrEqual(band.DecibelVolume, AudioConstant.MinDecibelVolume - 1f,
                    "A band at its target must settle there rather than keep subtracting steps.");
                Assert.AreEqual(AudioConstant.MinVolume, band.Amplitube, 1e-6f, "The normalized amplitude tracks the decibel value.");
            }
        }

        // A falling band is rate-limited: Decay is the milliseconds it takes to shed MaxVolumeChange (20dB),
        // so a 20ms decay empties a band within a few frames while a 20s one has barely started.
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

            // Pinned after the analyzers' Update has already run this frame, so the first decay step lands
            // on the next one.
            PinBandTo(fast.Bands[0], AudioConstant.FullDecibelVolume);
            PinBandTo(slow.Bands[0], AudioConstant.FullDecibelVolume);

            yield return WaitUntilOrTimeout(() => fast.Bands[0].DecibelVolume <= AudioConstant.MinDecibelVolume + DecibelTolerance,
                "the fast-decay band to reach the floor", 3f);

            Assert.Less(slow.Bands[0].DecibelVolume, AudioConstant.FullDecibelVolume,
                "The slow band must still be falling - Decay limits the rate, it does not stop the fall.");
            Assert.Greater(slow.Bands[0].DecibelVolume, -20f,
                "A 20s decay sheds 20dB per 20s, so the slow band cannot have collapsed to the floor in the time the fast one took.");
        }

        // The mirror image: Attack is the milliseconds it takes to gain 20dB. A band pinned below the floor
        // rises towards it, fast or slow, and settles exactly on the target rather than overshooting.
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
                "the fast-attack band to climb to the target", 3f);

            Assert.AreEqual(AudioConstant.MinDecibelVolume, fast.Bands[0].DecibelVolume, DecibelTolerance,
                "The last step snaps to the target instead of overshooting it.");
            Assert.Greater(slow.Bands[0].DecibelVolume, StartDecibel, "The slow band must still be rising.");
            Assert.Less(slow.Bands[0].DecibelVolume, -190f,
                "A 20s attack gains 20dB per 20s, so the slow band cannot have caught up in the time the fast one took.");
        }

        // Smooth turns the fixed 20dB-per-changeTime step into one proportional to how far the band still
        // has to travel, so the same Decay setting produces a decelerating approach instead of a ramp.
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
                "the unsmoothed band to reach the floor", 3f);

            Assert.Less(smoothed.Bands[0].DecibelVolume, AudioConstant.FullDecibelVolume, "The smoothed band must still be moving.");
            Assert.Greater(smoothed.Bands[0].DecibelVolume, -40f,
                "An 80dB difference over a smoothing of 4000 scales the step down 50x, so the smoothed band is nowhere near the floor yet.");
        }
        // TEST_FINDINGS #40. Every Band carries a serialized, inspector-drawn "Weighted" value that
        // UpdateSpectrum never reads, so two analyzers that differ only in it produce the same numbers. The
        // rise is deliberately slow enough that both are still mid-travel when they are compared - two bands
        // resting on the same target would agree whether or not the field did anything.
        [UnityTest]
        public IEnumerator Update_BandWeighting_HasNoEffectOnTheBandOutput()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            const float StartDecibel = -200f;
            SpectrumAnalyzer unweighted = NewAnalyzer(new[] { 1000f }, attack: 2000);
            SpectrumAnalyzer weighted = NewAnalyzer(new[] { 1000f }, attack: 2000);
            yield return WaitForStart(unweighted);
            yield return WaitForStart(weighted);
            TestAudioLibrary.SetPrivateField(unweighted.Bands[0], SpectrumAnalyzer.Band.NameOf.Weighted, 1f);
            TestAudioLibrary.SetPrivateField(weighted.Bands[0], SpectrumAnalyzer.Band.NameOf.Weighted, 20f);
            unweighted.SetSource(player);
            weighted.SetSource(player);

            PinBandTo(unweighted.Bands[0], StartDecibel);
            PinBandTo(weighted.Bands[0], StartDecibel);

            yield return new WaitForSeconds(0.5f); // a frame-clock ramp, so wall time is the right clock

            Assert.Greater(unweighted.Bands[0].DecibelVolume, StartDecibel, "Precondition: the bands are mid-rise, not parked on a shared target.");
            Assert.Less(unweighted.Bands[0].DecibelVolume, AudioConstant.MinDecibelVolume, "Precondition: neither band has reached the floor yet.");
            Assert.AreEqual(unweighted.Bands[0].DecibelVolume, weighted.Bands[0].DecibelVolume, 0.5f,
                "Characterizes TEST_FINDINGS #40: a band's Weighted field is written by the inspector and read by nothing.");
        }
        #endregion

        #region Band ranges
        // TEST_FINDINGS #39. A band whose frequency window is narrower than one FFT bin has start == end, so
        // RangeInt.length is 0, and RMS/Average divide the summed magnitude by it. Which way that breaks is
        // decided by the one bin the band covers, and the test may not assume either: an exactly-zero bin
        // gives 0/0 = NaN, which loses every comparison in the ballistics block and leaves the band
        // subtracting a step forever, while a bin holding any energy at all gives x/0 = +Infinity, which
        // ClampNormalize pins to MaxVolume and the band climbs to the ceiling instead. Both ends are wrong in
        // the same way - the band stops reporting the signal - so the assertion is that it leaves the floor,
        // and then that whichever end it ran to is the end the ballistics block makes it run to.
        [UnityTest]
        public IEnumerator Update_WithABandNarrowerThanOneFftBin_LeavesTheFloorUnderRmsButHoldsUnderPeak()
        {
            yield return RequireRealtimeAudioClock();
            IAudioPlayer player = PlaySilence();
            yield return WaitForPlaybackStart(player);

            // Band 1 spans (k - 0.5) to (k + 0.5) bin widths: it both starts and ends on bin k, so it covers
            // exactly one sample and RangeInt.length lands on 0. Band 0 keeps a normal, wide range.
            float harmonic = HarmonicOf(DefaultResolutionScale);
            float[] bands = { 39.5f * harmonic, 40.5f * harmonic };

            SpectrumAnalyzer rms = NewAnalyzer(bands, metering: SpectrumAnalyzer.Metering.RMS);
            SpectrumAnalyzer peak = NewAnalyzer(bands, metering: SpectrumAnalyzer.Metering.Peak);
            yield return WaitForStart(rms);
            yield return WaitForStart(peak);
            rms.SetSource(player);
            peak.SetSource(player);

            // 10dB is far enough out that no ballistics ramp can be sitting there by accident, and both
            // runaways cover it in well under a second: the decay step is 20dB/1500ms, the attack 20dB/100ms.
            const float FloorDistance = 10f;
            yield return WaitUntilOrTimeout(
                () => Mathf.Abs(rms.Bands[1].DecibelVolume - AudioConstant.MinDecibelVolume) > FloorDistance,
                "the RMS metering of a sub-bin band to leave the decibel floor in either direction", 3f);

            if (rms.Bands[1].DecibelVolume < AudioConstant.MinDecibelVolume)
            {
                // 0/0: the NaN target fails even "close enough, snap to it", so the band never stops falling.
                Assert.AreEqual(AudioConstant.MinVolume, rms.Bands[1].Amplitube, 1e-6f,
                    "Amplitube clamps at the floor, so nothing bound to it can see the value running away underneath.");
            }
            else
            {
                // x/0: ClampNormalize turns the +Infinity target into MaxVolume, so the band settles on the
                // ceiling and reports full scale for a signal that is not there.
                // The ballistics block snaps to the target once one step covers what is left, so the climb
                // ends on MaxDecibelVolume exactly rather than approaching it.
                yield return WaitUntilOrTimeout(
                    () => rms.Bands[1].DecibelVolume >= AudioConstant.MaxDecibelVolume,
                    "the same band to finish its climb to the ceiling", 3f);

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
        // The whole point of the component, with real audio in it: a 440Hz tone must light up the band that
        // covers 440Hz and leave the band above it alone.
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
                "the tone's own band to rise well clear of the floor", 3f);

            Assert.Greater(analyzer.Bands[0].DecibelVolume, analyzer.Bands[1].DecibelVolume + 6f,
                "The 10Hz-1kHz band contains the 440Hz tone; the 1kHz-12kHz band above it contains only window leakage.");
            Assert.Greater(analyzer.Bands[0].Amplitube, AudioConstant.MinVolume,
                "The normalized amplitude a meter binds to must rise with the decibel value.");
        }

        /// <summary>
        /// Ignores the calling test unless the engine actually fills the spectrum buffer. Everything else in
        /// this file is written to hold on an all-zero spectrum; this is the one assertion that cannot be.
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
            Assert.Ignore("The audio engine returned an all-zero spectrum for a playing tone - this test needs a device that actually mixes.");
        }
        #endregion
    }
}
