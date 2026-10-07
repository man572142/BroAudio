using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins <c>BroAudio.Play(SoundID, Vector3, float)</c> and <c>BroAudio.Play(SoundID, Transform, float)</c>:
    /// one call must both place the voice in 3D (at / tracking the target) and apply the one-shot fade-in
    /// override, ramping from silence to full.
    /// </summary>
    public class PlayFadeInOverloadTests : BroAudioTestFixture
    {
        /// <summary>Long enough to outlive every fade-in below with seconds to spare for a slow frame clock.</summary>
        private const float ClipLength = 15f;

        private const float FadeInDuration = 5f;

        /// <summary>
        /// The factory InCubic fade reads 0.125 at 2.5s of 5s, and passes 0.01 at ~1.08s and 0.5 at ~3.97s -
        /// ~1.4s clear of the sample. A skipped fade reads ~1, an unstarted one 0.
        /// </summary>
        private const float MidFadeSampleTime = 2.5f;
        private const float MidFadeFloor = 0.01f;
        private const float MidFadeCeiling = 0.5f;

        /// <summary>Volume must start this far below target for "starts at silence" to be meaningful.</summary>
        private const float NearSilenceCeiling = 0.05f;

        /// <summary>How much longer than the fade-in itself the completion poll may take.</summary>
        private const float ArrivalSlack = 1.5f;

        [UnityTest]
        public IEnumerator Play_WithPositionAndFadeIn_PlacesTheVoiceAtThePositionAndRampsVolumeFromSilenceToFull()
        {
            yield return RequireRealtimeAudioClock();

            Vector3 position = new Vector3(6f, -1f, 3f);
            SoundID id = NewSound("PositionFadeInSfx", BroAudioType.SFX, NewClip(ClipLength));

            IAudioPlayer player = BroAudio.Play(id, position, FadeInDuration);
            yield return WaitForPlaybackStart(player);

            // Positional half.
            AudioPlayer concretePlayer = InstanceOf(player);
            Assert.IsNotNull(concretePlayer, "BroAudio.Play(id, position, fadeIn) should hand back a real pooled AudioPlayer.");
            AudioSource source = concretePlayer.GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.spatialBlend, LinearTolerance,
                "Playing at a position must force the voice to 3D.");
            Assert.Less(Vector3.Distance(position, concretePlayer.transform.position), LinearTolerance,
                $"The pooled player's transform should sit at the given position (expected {position}, was {concretePlayer.transform.position}).");

            // Fade half.
            Assert.Less(player.GetVolume(), NearSilenceCeiling,
                "Volume should start at silence in the frame playback begins when a fadeIn override is given.");

            yield return new WaitForSeconds(MidFadeSampleTime);
            float midVolume = player.GetVolume();
            Assert.Greater(midVolume, MidFadeFloor,
                "Mid-fade the volume should already be ramping above silence - a read at 0 means the fadeIn override never started.");
            Assert.Less(midVolume, MidFadeCeiling,
                "Mid-fade the volume should still be well short of full - a read near 1 means the fadeIn override was ignored and the volume snapped to target.");

            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - LinearTolerance,
                "the position overload's fadeIn override to reach full volume", FadeInDuration + ArrivalSlack);
            Assert.IsTrue(player.IsPlaying, "The player must still be live once the fade-in completes.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "The fadeIn override should land the player exactly on full (unauthored) volume once its duration elapses.");
        }

        [UnityTest]
        public IEnumerator Play_WithFollowTargetAndFadeIn_TracksTheTargetAndRampsVolumeFromSilenceToFull()
        {
            yield return RequireRealtimeAudioClock();

            Vector3 start = new Vector3(-4f, 2f, 8f);
            Vector3 moved = new Vector3(11f, 0f, -6f);
            GameObject targetHost = Track(new GameObject("PlayFadeInFollowTarget"));
            targetHost.transform.position = start;

            SoundID id = NewSound("FollowFadeInSfx", BroAudioType.SFX, NewClip(ClipLength));

            IAudioPlayer player = BroAudio.Play(id, targetHost.transform, FadeInDuration);
            yield return WaitForPlaybackStart(player);

            // Positional half.
            AudioPlayer concretePlayer = InstanceOf(player);
            Assert.IsNotNull(concretePlayer, "BroAudio.Play(id, followTarget, fadeIn) should hand back a real pooled AudioPlayer.");
            AudioSource source = concretePlayer.GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.spatialBlend, LinearTolerance,
                "Playing with a follow target must force the voice to 3D.");
            Assert.Less(Vector3.Distance(start, concretePlayer.transform.position), LinearTolerance,
                $"The pooled player's transform should start on the follow target (expected {start}, was {concretePlayer.transform.position}).");

            // Fade half: identical shape and bounds to the position-overload test above.
            Assert.Less(player.GetVolume(), NearSilenceCeiling,
                "Volume should start at silence in the frame playback begins when a fadeIn override is given.");

            yield return new WaitForSeconds(MidFadeSampleTime);
            float midVolume = player.GetVolume();
            Assert.Greater(midVolume, MidFadeFloor,
                "Mid-fade the volume should already be ramping above silence - a read at 0 means the fadeIn override never started.");
            Assert.Less(midVolume, MidFadeCeiling,
                "Mid-fade the volume should still be well short of full - a read near 1 means the fadeIn override was ignored and the volume snapped to target.");

            // Polled: AudioPlayer.Update follows the target in its own script order, not necessarily next frame.
            targetHost.transform.position = moved;
            yield return WaitUntilOrTimeout(() => Vector3.Distance(concretePlayer.transform.position, moved) < LinearTolerance,
                "the player to catch up with its moved follow target");
            Assert.Less(Vector3.Distance(moved, concretePlayer.PlayingPosition), LinearTolerance,
                "PlaybackPreference resolves Position from the live follow target, so it should move with the host too.");

            yield return WaitUntilOrTimeout(() => player.GetVolume() >= AudioConstant.FullVolume - LinearTolerance,
                "the follow-target overload's fadeIn override to reach full volume", FadeInDuration + ArrivalSlack);
            Assert.IsTrue(player.IsPlaying, "The player must still be live once the fade-in completes.");
            Assert.AreEqual(AudioConstant.FullVolume, player.GetVolume(), LinearTolerance,
                "The fadeIn override should land the player exactly on full (unauthored) volume once its duration elapses.");
        }
    }
}