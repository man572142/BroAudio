using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Volume composition, per-type volume's live/future behavior, mixer track acquisition/return, and
    /// pitch via AudioSource. See Docs/Tests/inventory/volume-mixer.md.
    /// </summary>
    public class VolumePitchMixerTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator SetVolume_Master_WritesDirectlyToMixerAndNeverEntersLinearProduct()
        {
            SoundID id = NewSound("MasterVolSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            float baselineLinear = player.GetVolume();

            BroAudio.SetVolume(0.25f, 0f); // no id/type => master, per BroAudio.SetVolume(vol, fadeTime) forwarding to BroAudioType.All
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float db));
            Assert.AreEqual(0.25f.ToDecibel(), db, DecibelTolerance, "Master volume should write vol.ToDecibel() straight to the mixer's Master parameter.");

            // characterizes: master is a separate mixer stage (Docs/Tests/inventory/volume-mixer.md "Conflicts observed").
            Assert.AreEqual(baselineLinear, player.GetVolume(), LinearTolerance, "Master volume must never appear in IAudioPlayer.GetVolume()'s linear product.");
        }

        // Per-id * per-type composition lives in AuthoredVolumeTests.SetVolume_ComposesMultiplicativelyWithTheAuthoredClipAndMasterVolume.

        [UnityTest]
        public IEnumerator SetAudioTypeVolume_ToExactlyDefault_PushesLive_AndNonDefaultAppliesToFuturePlayersAndMixer()
        {
            SoundID liveId = NewSound("LiveTypeSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer livePlayer = BroAudio.Play(liveId);
            yield return WaitForPlaybackStart(livePlayer, "live playback to start");

            BroAudio.SetVolume(BroAudioType.SFX, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.4f, livePlayer.GetVolume(), LinearTolerance);

            BroAudio.SetVolume(BroAudioType.SFX, 1f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(1f, livePlayer.GetVolume(), LinearTolerance, "Live players are pushed even when the new value equals the default.");

            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(1f, pref.Volume, LinearTolerance);

            // Non-default, or a dropped pref would read the same as an applied one.
            const float NonDefaultTypeVolume = 0.4f;
            BroAudio.SetVolume(BroAudioType.SFX, NonDefaultTypeVolume, 0f);
            yield return WaitFrames(1);

            SoundID futureId = NewSound("FutureTypeSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer futurePlayer = BroAudio.Play(futureId);
            yield return WaitForPlaybackStart(futurePlayer, "future playback to start");
            Assert.AreEqual(NonDefaultTypeVolume, futurePlayer.GetVolume(), LinearTolerance,
                "A fresh player should read the stored per-type volume, not silently fall back to full volume.");

            Assert.IsNotNull(futurePlayer.AudioSource.outputAudioMixerGroup, "The player must hold a pooled track for its volume parameter to be exposed.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(futurePlayer.AudioSource.outputAudioMixerGroup.name, out float db));
            Assert.AreEqual(NonDefaultTypeVolume.ToDecibel(), db, DecibelTolerance,
                "The stored per-type volume must reach the new player's track parameter in decibels, not just GetVolume()'s linear bookkeeping.");
        }

        [UnityTest]
        public IEnumerator Play_AcquiresPooledMixerTrackAndReusesOneAfterRecycle()
        {
            SoundID id1 = NewSound("TrackSfx1", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player1 = BroAudio.Play(id1);
            yield return WaitForPlaybackStart(player1, "first playback to start");

            Assert.IsNotNull(player1.AudioSource.outputAudioMixerGroup, "A player should acquire a pooled mixer group once play starts.");
            StringAssert.StartsWith(BroName.GenericTrackName, player1.AudioSource.outputAudioMixerGroup.name);

            BroAudio.Stop(id1, 0f);
            yield return WaitForRecycle(player1, "the player to recycle after Stop");

            SoundID id2 = NewSound("TrackSfx2", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player2 = BroAudio.Play(id2);
            yield return WaitForPlaybackStart(player2, "second playback to start");

            Assert.IsNotNull(player2.AudioSource.outputAudioMixerGroup, "A subsequent play should reuse a group from the pool rather than getting null.");
            StringAssert.StartsWith(BroName.GenericTrackName, player2.AudioSource.outputAudioMixerGroup.name);
        }

        [UnityTest]
        public IEnumerator SetPitch_WithOutOfRangeValue_ClampsToAudioSourceRange()
        {
            SoundID id = NewSound("PitchClampSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.SetPitch(10f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AudioConstant.MaxAudioSourcePitch, player.AudioSource.pitch, LinearTolerance);

            player.SetPitch(-10f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(AudioConstant.MinAudioSourcePitch, player.AudioSource.pitch, LinearTolerance);
        }

        [UnityTest]
        public IEnumerator SetPitch_BeforePlaybackStarts_DefersFadeRatherThanSnapping()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("DeferredPitchSfx", BroAudioType.SFX, NewClip(3f));

            IAudioPlayer player = BroAudio.Play(id);
            // Must be the same frame as Play(), before the queue drains, to hit the deferred-fade branch.
            player.SetPitch(2f, 0.5f);

            yield return WaitForPlaybackStart(player);

            Assert.Less(player.AudioSource.pitch, 1.9f, "SetPitch called before play should defer into a fade, not snap to the target immediately.");

            yield return WaitUntilOrTimeout(() => Mathf.Abs(player.AudioSource.pitch - 2f) < 0.01f, "the deferred pitch fade to reach its target", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(2f, player.AudioSource.pitch, LinearTolerance);
        }

        [UnityTest]
        public IEnumerator SetPitch_ByBroAudioType_AppliesToLiveAndFuturePlayersOfThatTypeOnly()
        {
            SoundID sfxId = NewSound("PitchTypeLiveSfx", BroAudioType.SFX, NewClip(3f));
            SoundID musicId = NewSound("PitchTypeLiveMusic", BroAudioType.Music, NewClip(3f));
            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);
            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", DefaultPlaybackWaitSeconds);

            BroAudio.SetPitch(BroAudioType.SFX, 0.5f);
            yield return WaitFrames(1);

            Assert.AreEqual(0.5f, sfxPlayer.AudioSource.pitch, LinearTolerance, "SetPitch by type must reach the live SFX player's AudioSource.");
            Assert.AreEqual(1f, musicPlayer.AudioSource.pitch, LinearTolerance, "SetPitch(SFX) must never touch a Music player.");

            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(0.5f, pref.Pitch, LinearTolerance, "The per-type pref must also store the new pitch for future players.");

            SoundID futureSfxId = NewSound("PitchTypeFutureSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer futureSfxPlayer = BroAudio.Play(futureSfxId);
            yield return WaitForPlaybackStart(futureSfxPlayer, "future playback to start");
            Assert.AreEqual(0.5f, futureSfxPlayer.AudioSource.pitch, LinearTolerance, "A freshly played SFX entity should pick up the stored per-type pitch.");
        }

        [UnityTest]
        public IEnumerator SetPitch_BySoundId_AppliesToThatInstanceOnly()
        {
            const float IdPitch = 1.5f;
            SoundID targetId = NewSound("PitchTargetSfx", BroAudioType.SFX, NewClip(4f));
            SoundID otherId = NewSound("PitchOtherSfx", BroAudioType.SFX, NewClip(4f));

            IAudioPlayer targetA = BroAudio.Play(targetId);
            IAudioPlayer targetB = BroAudio.Play(targetId);
            IAudioPlayer other = BroAudio.Play(otherId);
            yield return WaitForPlaybackStart(targetA);
            yield return WaitForPlaybackStart(targetB);
            yield return WaitForPlaybackStart(other);

            BroAudio.SetPitch(targetId, IdPitch);

            Assert.AreEqual(IdPitch, targetA.AudioSource.pitch, LinearTolerance, "Every live instance of the SoundID should take the pitch.");
            Assert.AreEqual(IdPitch, targetB.AudioSource.pitch, LinearTolerance, "Every live instance of the SoundID should take the pitch.");
            Assert.AreEqual(AudioConstant.DefaultPitch, other.AudioSource.pitch, LinearTolerance, "A different SoundID of the same type must be untouched.");
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(AudioConstant.DefaultPitch, pref.Pitch, LinearTolerance, "A per-SoundID pitch must not leak into the per-type pref.");

            BroAudio.Stop(targetId, 0f);
            yield return WaitForRecycle(targetA);
            IAudioPlayer later = BroAudio.Play(targetId);
            yield return WaitForPlaybackStart(later);
            Assert.AreEqual(AudioConstant.DefaultPitch, later.AudioSource.pitch, LinearTolerance,
                "A per-SoundID pitch is not stored: the next play of that ID starts from its base pitch.");
        }

        // The ramp and WaitForSeconds share the scaled frame clock, so fade time plus two frames is past the
        // ramp's last pass, which evaluates the ease past t = 1, by construction, not by margin.
        // At the half-way read the ramp has run 1s +/- one frame, and Time.deltaTime is capped at the project's
        // 0.33s Maximum Allowed Timestep, so t/F is within [1/3, 2/3]. From -6.02dB, InCirc then reads
        // -6.02 * sqrt(1 - t^2): -5.68dB to -4.49dB (-5.21dB at t = 0.5). A ramp that ignores the ease (Linear, never
        // below -4.01dB there) or picks FadeOutEase (OutSine, never below -3.01dB) reads above -4.25dB even at
        // that worst case, and an instant fade reads 0dB.
        [UnityTest]
        public IEnumerator SetVolume_MasterFadeWithInCircEase_LandsOnTheTarget()
        {
            const float StartVolume = 0.5f;
            const float FadeSeconds = 2f;
            const float InCircHalfwayCeilingDb = -4.25f;
            SoundManager.Instance.Setting.DefaultFadeInEase = Ease.InCirc;

            BroAudio.SetVolume(StartVolume, 0f);
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float startDb));
            Assert.AreEqual(StartVolume.ToDecibel(), startDb, DecibelTolerance, "Precondition: the immediate set must land before the fade starts.");

            // Rising, so SetMasterVolume's coroutine picks FadeInEase.
            BroAudio.SetVolume(AudioConstant.FullVolume, FadeSeconds);
            yield return new WaitForSeconds(FadeSeconds / 2f);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float halfwayDb));
            Assert.Less(halfwayDb, InCircHalfwayCeilingDb,
                "Half-way through, a rising master fade should still be on InCirc's slow start (DefaultFadeInEase), " +
                "not already at its target or on a faster curve.");

            yield return new WaitForSeconds(FadeSeconds / 2f);
            yield return WaitFrames(2);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float endDb));
            Assert.AreEqual(AudioConstant.FullDecibelVolume, endDb, DecibelTolerance,
                "The InCirc master fade must end on its target, not on InCirc's NaN past t = 1.");
        }
    }
}