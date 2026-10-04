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
    /// Pins the authored clip-volume * MasterVolume product from <c>SetupClipVolume</c>, which entities
    /// elsewhere in the suite leave at 1 * 1.
    /// <para>
    /// Don't assert on <c>AudioSource.volume</c>: with a pooled mixer track the volume goes to the mixer's
    /// dB parameter and AudioSource.volume is never written, so it would pass whatever was computed.
    /// </para>
    /// </summary>
    public class AuthoredVolumeTests : BroAudioTestFixture
    {

        [UnityTest]
        public IEnumerator Play_WithAuthoredClipAndMasterVolume_AppliesTheirProductNotEitherFactorAlone()
        {
            // Dropping either factor reads 0.5, dropping both reads 1; both are far from 0.25.
            const float ClipVolume = 0.5f;
            const float MasterVolume = 0.5f;
            const float ExpectedProduct = ClipVolume * MasterVolume; // 0.25

            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("AuthoredVolSfx", BroAudioType.SFX, ClipVolume, MasterVolume, NewClip(3f));
            Track(entity);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            Assert.AreEqual(ExpectedProduct, player.GetVolume(), LinearTolerance,
                "A freshly played entity with a non-default authored clip volume and MasterVolume should read their product, not either factor alone or full volume.");

            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "The player must hold a pooled track for its volume parameter to be exposed.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(player.AudioSource.outputAudioMixerGroup.name, out float db));
            Assert.AreEqual(ExpectedProduct.ToDecibel(), db, DecibelTolerance,
                "The authored clip*master product must reach the track's mixer parameter in decibels, not just IAudioPlayer.GetVolume()'s linear bookkeeping.");
        }

        [UnityTest]
        public IEnumerator SetVolume_ComposesMultiplicativelyWithTheAuthoredClipAndMasterVolume()
        {
            const float ClipVolume = 0.6f;
            const float MasterVolume = 0.5f;
            const float AuthoredProduct = ClipVolume * MasterVolume; // 0.3

            AudioEntity entity = TestAudioLibrary.CreateEntityWithVolume("ComposedVolSfx", BroAudioType.SFX, ClipVolume, MasterVolume, NewClip(3f));
            Track(entity);
            SoundID id = IdOf(entity);

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            Assert.AreEqual(AuthoredProduct, player.GetVolume(), LinearTolerance,
                "The authored product must already be in the linear product before any SetVolume call.");

            BroAudio.SetVolume(id, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AuthoredProduct * 0.4f, player.GetVolume(), LinearTolerance,
                "Per-SoundID volume must multiply the authored clip*master product, not replace it.");

            BroAudio.SetVolume(BroAudioType.SFX, 0.7f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AuthoredProduct * 0.4f * 0.7f, player.GetVolume(), LinearTolerance,
                "Per-BroAudioType volume must further multiply the same running product, authored volume included.");

            Assert.IsNotNull(player.AudioSource.outputAudioMixerGroup, "The player must still hold a pooled track for its volume parameter to be exposed.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(player.AudioSource.outputAudioMixerGroup.name, out float db));
            Assert.AreEqual((AuthoredProduct * 0.4f * 0.7f).ToDecibel(), db, DecibelTolerance,
                "The composed authored*per-id*per-type product must reach the track's mixer parameter in decibels, not just IAudioPlayer.GetVolume()'s linear bookkeeping.");
        }
    }
}