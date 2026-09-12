using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// TEST_INVENTORY 1.6 claims the per-sound volume a designer authors in the Library Manager - the
    /// product of <see cref="BroAudioClip.Volume"/> and <see cref="AudioEntity.MasterVolume"/> computed in
    /// AudioPlayer.Playback.cs:276's <c>SetupClipVolume</c> (<c>_clip.Volume * _pref.Entity.GetMasterVolume()</c>)
    /// - is covered. It isn't: every entity built by <see cref="TestAudioLibrary.CreateEntity"/> defaults both
    /// factors to 1, so nothing in the suite would notice if that multiplication were deleted entirely. These
    /// tests use <see cref="TestAudioLibrary.CreateEntityWithVolume"/> to move both factors off 1 at once, in
    /// a fixture chosen so the three ways line 276 could break (drop the clip factor, drop the master factor,
    /// drop the whole expression) each read back as a different, wrong number.
    /// <para>
    /// Observable: <c>player.GetVolume()</c> (<c>_clipVolume.Current * _trackVolume.Current *
    /// _audioTypeVolume.Current</c>, AudioPlayer.Volume.cs), the same linear-product bookkeeping
    /// VolumePitchMixerTests and SoundVolumeTests read. Not <c>AudioSource.volume</c>: these plays acquire a
    /// pooled mixer track, so <c>UpdateVolume</c> (AudioPlayer.Volume.cs) writes the composed volume to the
    /// mixer's dB parameter via <c>TrySetMixerDecibelVolume</c> and never touches <c>AudioSource.volume</c> at
    /// all - asserting on it would silently pass no matter what SetupClipVolume computed.
    /// </para>
    /// </summary>
    public class AuthoredVolumeTests : BroAudioTestFixture
    {
        // Uses the fixture's shared LinearTolerance/DecibelTolerance. Every dropped-factor value computed
        // in the comments below misses its assertion's expected value by several times LinearTolerance
        // (worst case 0.056, in SetVolume_.../after both extra layers are applied) - tight enough to fail
        // loudly rather than slip through a loose band.

        [UnityTest]
        public IEnumerator Play_WithAuthoredClipAndMasterVolume_AppliesTheirProductNotEitherFactorAlone()
        {
            // 0.5 * 0.5 = 0.25. Equal factors, as suggested: dropping *either* single factor from line 276
            // reads back as 0.5 (the other factor alone), and dropping the whole expression - defaulting to
            // AudioConstant.FullVolume/DefaultTrackVolume like every other volume test's "freshly played
            // default entity" - reads back as 1. So the three ways this line can break collapse to two wrong
            // numbers {0.5, 1}, both clearly distinguishable from the correct 0.25 at a 0.01 tolerance; using
            // unequal factors would separate the two single-factor-dropped cases too, but isn't needed to
            // catch the defect this file exists for.
            const float ClipVolume = 0.5f;
            const float MasterVolume = 0.5f;
            const float ExpectedProduct = ClipVolume * MasterVolume; // 0.25

            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("AuthoredVolSfx", BroAudioType.SFX, ClipVolume, MasterVolume, NewClip(3f));
            Track(entity);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // Would this pass if `* _pref.Entity.GetMasterVolume()` were deleted from line 276? No - the
            // player would read 0.5 (ClipVolume alone), not 0.25. That is the whole point of this test.
            Assert.AreEqual(ExpectedProduct, player.GetVolume(), LinearTolerance,
                "A freshly played entity with a non-default authored clip volume and MasterVolume should read their product, not either factor alone or full volume.");

            // Prove the product reaches the actual mixer output too, not just AudioPlayer's own linear
            // bookkeeping - mirrors VolumePitchMixerTests.SetVolume_PerSoundIdAndPerType_ComposeMultiplicativelyInLinearProduct.
            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "The player must hold a pooled track for its volume parameter to be exposed.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(player.AudioSource.outputAudioMixerGroup.name, out float db));
            Assert.AreEqual(ExpectedProduct.ToDecibel(), db, DecibelTolerance,
                "The authored clip*master product must reach the track's mixer parameter in decibels, not just IAudioPlayer.GetVolume()'s linear bookkeeping.");
        }

        [UnityTest]
        public IEnumerator SetVolume_ComposesMultiplicativelyWithTheAuthoredClipAndMasterVolume()
        {
            // Different factors here (0.6 * 0.5 = 0.3) so this test's own baseline assertion is distinguishable
            // from the companion test above by inspection, while still exercising the same product.
            const float ClipVolume = 0.6f;
            const float MasterVolume = 0.5f;
            const float AuthoredProduct = ClipVolume * MasterVolume; // 0.3

            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("ComposedVolSfx", BroAudioType.SFX, ClipVolume, MasterVolume, NewClip(3f));
            Track(entity);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // Baseline, before any layer above it is touched: would this pass if line 276 dropped either
            // factor? No - it would read 0.6 or 0.5 instead of 0.3.
            Assert.AreEqual(AuthoredProduct, player.GetVolume(), LinearTolerance,
                "The authored product must already be in the linear product before any SetVolume call.");

            // Per-SoundID volume (_trackVolume) must multiply the authored product, not replace it - if
            // SetVolume(id, ...) overwrote rather than multiplied, this would read 0.4 instead of 0.12.
            BroAudio.SetVolume(id, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AuthoredProduct * 0.4f, player.GetVolume(), LinearTolerance,
                "Per-SoundID volume must multiply the authored clip*master product, not replace it.");

            // Per-BroAudioType volume (_audioTypeVolume) stacks on top of both of the above - if either the
            // authored product or the per-id factor had been dropped anywhere upstream, this final number
            // (0.3 * 0.4 * 0.7 = 0.084) would not match.
            BroAudio.SetVolume(BroAudioType.SFX, 0.7f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AuthoredProduct * 0.4f * 0.7f, player.GetVolume(), LinearTolerance,
                "Per-BroAudioType volume must further multiply the same running product, authored volume included.");
        }

        [UnityTest]
        public IEnumerator Play_WithFadeIn_RampsFromZeroToTheAuthoredProductNotFullVolume()
        {
            // 0.5 * 0.6 = 0.3, clearly short of both a single dropped factor (0.5 or 0.6) and of full
            // volume (1) - the value SetupClipVolume's fade-in branch snaps to instead of 0 if the
            // HasFadeIn(...) check itself were broken.
            const float ClipVolume = 0.5f;
            const float MasterVolume = 0.6f;
            const float AuthoredProduct = ClipVolume * MasterVolume; // 0.3
            const float FadeInSeconds = 0.3f;

            // A long clip relative to FadeInSeconds so the fade-in has room to be observed mid-ramp and to
            // finish well before playback ends. FadeIn is on the clip itself (BroAudioClip.FadeIn), so it's
            // written after CreateEntityWithVolume the same way any other per-clip field would be.
            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("FadeInVolSfx", BroAudioType.SFX, ClipVolume, MasterVolume, NewClip(3f));
            Track(entity);
            entity.Clips[0].FadeIn = FadeInSeconds;
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // No RequireRealtimeAudioClock: SetupClipVolume's fade-in branch (AudioPlayer.Playback.cs:277)
            // makes PlayControl block on `while (_clipVolume.IsFading) yield return null;` (:179) *before* it
            // ever reaches the DSP-time-gated end-of-clip loop, and Fader.Update (FaderModule.cs) advances
            // purely on Utility.GetDeltaTime() (Time.deltaTime/unscaledDeltaTime) - never AudioSettings.dspTime.
            // So a decoupled/fast DSP clock on a device-less CI runner cannot race this player to EndPlaying
            // before the fade is observed, unlike SetPitch_BeforePlaybackStarts_DefersFadeRatherThanSnapping's
            // pitch fade, which has no such fade-in blocking the DSP-gated loop ahead of it.
            //
            // Would this pass if HasFadeIn(clipFade) always returned false, i.e. SetupClipVolume always took
            // the snap branch? No - the very first playing frame would already read ~0.3, not "well below it".
            Assert.Less(player.GetVolume(), AuthoredProduct * 0.5f,
                "A fade-in must start near 0, not snap straight to the authored target on the first playing frame.");

            yield return WaitUntilOrTimeout(() => Mathf.Abs(player.GetVolume() - AuthoredProduct) < LinearTolerance,
                "the clip-volume fade-in to reach the authored target", 2f);

            // Would this pass if line 276 were deleted, leaving SetupClipVolume's target at full volume (1)
            // or at a single dropped factor (0.5 or 0.6)? No - only the real product (0.3) satisfies both
            // this assertion and the WaitUntilOrTimeout above.
            Assert.AreEqual(AuthoredProduct, player.GetVolume(), LinearTolerance,
                "A fade-in must land on clip.Volume * entity.MasterVolume, not on full volume or on either factor alone.");
        }
    }
}