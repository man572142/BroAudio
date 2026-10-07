using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
#if !UNITY_WEBGL
    /// <summary>
    /// A recycled mixer track must return to the pool muted (SilenceTrackBeforeReturn): tracks are shared across
    /// audio types, so the next borrower would otherwise be heard at the previous sound's level.
    /// <para>
    /// Both tests rely on the pool being LIFO and on the base fixture draining every player, so the only track
    /// returned is the test's own. No tracks on WebGL.
    /// </para>
    /// </summary>
    public class MixerTrackRecycleTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator Recycle_SilencesTheReturnedTrack_AndTheNextPlayTakesThatSameTrack()
        {
            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            SoundID firstId = NewSound("TrackRecycleSfx1", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer first = BroAudio.Play(firstId);
            yield return WaitForPlaybackStart(first, "the first playback to start");

            AudioPlayer firstInstance = InstanceOf(first);
            AudioMixerGroup track = HeldGenericTrack(firstInstance);
            string trackName = track.name;

            // The level is on the track before the recycle, so reading it muted afterwards is the recycle's doing.
            Assert.AreEqual(AudioConstant.FullDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"Precondition: a full-volume player should hold its level on {trackName}.");

            BroAudio.Stop(firstId, 0f);
            yield return WaitForRecycle(first, "the first player to be recycled");

            Assert.IsFalse(firstInstance.GetComponent<AudioSource>().outputAudioMixerGroup,
                "A recycled player must let go of its track.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"{trackName} should go back to the pool muted, so its next borrower does not start at the previous sound's level.");

            SoundID secondId = NewSound("TrackRecycleSfx2", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer second = BroAudio.Play(secondId);
            yield return WaitForPlaybackStart(second, "the second playback to start");

            AudioMixerGroup secondTrack = HeldGenericTrack(InstanceOf(second));
            Assert.AreEqual(track, secondTrack,
                $"The track pool is LIFO and this test returned the only track, so the next Play() should take " +
                $"{trackName} again; it took {secondTrack.name}.");
            Assert.AreEqual(AudioConstant.FullDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"The next borrower should claim {trackName} at its own level.");
        }

        [UnityTest]
        public IEnumerator Recycle_WhileRoutedThroughTheEffectSend_ReturnsTheTrackWithBothTrackAndSendMuted()
        {
            AudioMixer mixer = SoundManager.Instance.AudioMixer;

            // Routes every later SFX Play() through the effect send.
            BroAudio.SetEffect(Effect.LowPass(800f));
            yield return WaitFrames(1);

            SoundID firstId = NewSound("SendRecycleSfx1", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer first = BroAudio.Play(firstId);
            yield return WaitForPlaybackStart(first, "the first playback to start");

            AudioPlayer firstInstance = InstanceOf(first);
            Assert.IsTrue(firstInstance.IsUsingTrackEffect, "Precondition: a player started after SetEffect(LowPass) should use the effect send.");
            AudioMixerGroup track = HeldGenericTrack(firstInstance);
            string trackName = track.name;
            string sendName = trackName + BroName.EffectParaNameSuffix;
            Assert.AreEqual(AudioConstant.FullDecibelVolume, ReadDb(mixer, sendName), DecibelTolerance,
                $"Precondition: a full-volume player routed through the send should hold its level on {sendName}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"Precondition: a player routed through the send should leave {trackName} muted.");

            BroAudio.Stop(firstId, 0f);
            yield return WaitForRecycle(first, "the first player to be recycled");

            // ResetEffect mutes the send and clears the effect flags; SilenceTrackBeforeReturn then mutes the dry track.
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, sendName), DecibelTolerance,
                $"{sendName} should go back to the pool muted, so the track's next borrower is not heard through a stale send.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"{trackName} should go back to the pool muted.");

            // The effect is still set, so the next borrower must get the same split as the first.
            SoundID secondId = NewSound("SendRecycleSfx2", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer second = BroAudio.Play(secondId);
            yield return WaitForPlaybackStart(second, "the second playback to start");

            AudioPlayer secondInstance = InstanceOf(second);
            AudioMixerGroup secondTrack = HeldGenericTrack(secondInstance);
            Assert.AreEqual(track, secondTrack,
                $"The track pool is LIFO and this test returned the only track, so the next Play() should take " +
                $"{trackName} again; it took {secondTrack.name}.");
            Assert.IsTrue(secondInstance.IsUsingTrackEffect, "The next SFX player should also use the effect send.");
            Assert.AreEqual(AudioConstant.FullDecibelVolume, ReadDb(mixer, sendName), DecibelTolerance,
                $"The next borrower should hold its level on {sendName}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"The next borrower should leave {trackName} muted.");
        }

        /// <summary>
        /// The generic track the player's AudioSource is routed to. Read off the concrete player rather than
        /// the public AudioSource proxy, which logs an error once the handle has been recycled.
        /// </summary>
        private static AudioMixerGroup HeldGenericTrack(AudioPlayer player)
        {
            Assert.IsTrue(player, "Precondition: the handle should resolve to a live player.");
            AudioMixerGroup track = player.GetComponent<AudioSource>().outputAudioMixerGroup;
            Assert.IsTrue(track, "Precondition: the player should hold a pooled mixer track.");
            StringAssert.StartsWith(BroName.GenericTrackName, track.name, "Precondition: the player should hold a generic track.");
            return track;
        }

        private static float ReadDb(AudioMixer mixer, string parameterName)
        {
            Assert.IsTrue(mixer.GetFloat(parameterName, out float db), $"{parameterName} should be an exposed mixer parameter.");
            return db;
        }
    }
#endif
}