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
    /// What a mixer track looks like when it goes back to SoundManager's track pool, and which track the next
    /// Play() takes. Tracks are pooled and shared across audio types, so AudioPlayer.Recycle mutes a track
    /// before returning it (SilenceTrackBeforeReturn): otherwise its next borrower would be heard at the
    /// previous sound's level until its own first volume write lands.
    /// <para>
    /// Both tests rely on the track pool being LIFO: AudioTrackObjectPool is an ObjectPool, whose Extract and
    /// Recycle both work on the last index, and the base fixture drains every player before each test, so the
    /// only track returned during a test is the one the test returned. Track routing exists only off WebGL
    /// (AudioPlayer.SetupAudioTrack takes no track there), hence the guard.
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

            // Every later SFX Play() routes through the effect send. The base fixture's teardown removes the
            // per-type effect and resets Effect_LowPass.
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

            // Two steps get here: EndPlaying's ResetEffect mutes the send and clears the player's effect flags,
            // then Recycle's SilenceTrackBeforeReturn mutes the dry track (and would mute the send too if the
            // flags were still set). Both sides must come back muted.
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, sendName), DecibelTolerance,
                $"{sendName} should go back to the pool muted, so the track's next borrower is not heard through a stale send.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, ReadDb(mixer, trackName), DecibelTolerance,
                $"{trackName} should go back to the pool muted.");

            // The per-type effect is still set, so the next borrower of the same track routes through the send
            // again and must end up with the same split as the first one: level on the send, dry track muted.
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