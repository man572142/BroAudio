using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Coverage gap: no other file in the suite calls <c>BroAudio.Play(SoundID, Vector3, float)</c> or
    /// <c>BroAudio.Play(SoundID, Transform, float)</c> - the two overloads that combine a spatial placement
    /// with a one-shot fade-in override in a single call. Both funnel into
    /// <c>SoundManager.Play(SoundID, Vector3/Transform, float, IPlayableValidator)</c>
    /// (SoundManager.Playback.cs), which builds a <c>PlaybackPreference</c> from the position/target and
    /// then calls <c>SetNextFadeIn(fadeIn)</c> - the same one-shot override mechanism
    /// <see cref="FadeAndTrimTests.Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting"/>
    /// already pins for the plain <c>Play(SoundID, float)</c> overload. Each test here asserts both halves
    /// of that combined contract on the same play call: the positional half (the pooled AudioSource lands at
    /// 3D and at/tracking the given position or target, per <see cref="SoundSourceTests"/> and
    /// <see cref="SpatialAndPriorityTests"/>) and the fade half (volume starts at silence and ramps to full
    /// over the given duration, per <see cref="VolumeFadeTests"/> and <see cref="FadeAndTrimTests"/>).
    /// </summary>
    public class PlayFadeInOverloadTests : BroAudioTestFixture
    {
        /// <summary>Long enough to outlive every fade-in below with seconds to spare for a slow frame clock.</summary>
        private const float ClipLength = 15f;

        private const float FadeInDuration = 5f;

        /// <summary>
        /// InCubic (the factory default fade-in ease, t^3) sampled at 2.5s into a 5s fade is
        /// (2.5/5)^3 = 0.125 of the full-volume target. The curve only clears 0.01 at
        /// 5*0.01^(1/3) ≈ 1.08s and only clears 0.5 at 5*0.5^(1/3) ≈ 3.97s, so this sample sits about 1.4s
        /// clear of either bound - comfortably past the fixture's "one slow frame is at most 0.333s" rule -
        /// while still failing decisively if the fade-in were skipped (a read at/near 1) or never started
        /// (a read stuck at 0).
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

            // Positional half: a specified position forces AudioPlayer.SetSpatial's SetSpatialBlend into its
            // SetTo3D() branch (see SpatialAndPriorityTests), and PlaybackPreference resolves the played
            // position onto the pooled player's transform (see SoundSourceTests' StayHere case).
            AudioPlayer concretePlayer = InstanceOf(player);
            Assert.IsNotNull(concretePlayer, "BroAudio.Play(id, position, fadeIn) should hand back a real pooled AudioPlayer.");
            AudioSource source = concretePlayer.GetComponent<AudioSource>();
            Assert.AreEqual(AudioConstant.SpatialBlend_3D, source.spatialBlend, LinearTolerance,
                "Playing at a position must force the voice to 3D.");
            Assert.Less(Vector3.Distance(position, concretePlayer.transform.position), LinearTolerance,
                $"The pooled player's transform should sit at the given position (expected {position}, was {concretePlayer.transform.position}).");

            // Fade half: SetupClipVolume snaps _clipVolume.Current to 0 before the fade-in coroutine starts
            // ramping it toward the entity's (default, unauthored) full-volume target.
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

            // Positional half: a follow target forces the same SetTo3D() branch as a plain position, and the
            // pooled player starts exactly on the target (see SoundSourceTests' FollowGameObject case).
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

            // Tracking: move the target and confirm the pooled player follows it, independent of the fade
            // still running. AudioPlayer.Update writes transform.position from the target on its own script
            // order, so poll rather than assuming the very next frame (mirrors SoundSourceTests).
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