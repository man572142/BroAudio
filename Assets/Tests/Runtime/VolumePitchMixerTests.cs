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
    /// Inventory 1.6-1.9 (Docs/inventory/volume-mixer.md): volume composition, per-type volume's
    /// live/future behavior, mixer track acquisition/return, and pitch via AudioSource.
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

            // characterizes: master volume is a separate mixer-graph stage, not a peer in the
            // player's own linear product (Docs/inventory/volume-mixer.md "Conflicts observed").
            Assert.AreEqual(baselineLinear, player.GetVolume(), LinearTolerance, "Master volume must never appear in IAudioPlayer.GetVolume()'s linear product.");
        }

        // Per-SoundID and per-BroAudioType volume composing multiplicatively in the linear product, and that
        // product reaching the track's mixer dB parameter, is covered by
        // AuthoredVolumeTests.SetVolume_ComposesMultiplicativelyWithTheAuthoredClipAndMasterVolume, which
        // additionally starts from a non-default authored clip*master product - a strict superset of what a
        // default-entity version of this test could prove.

        [UnityTest]
        public IEnumerator SetAudioTypeVolume_ToExactlyDefault_AppliesToLiveAndFuturePlayers()
        {
            SoundID liveId = NewSound("LiveTypeSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer livePlayer = BroAudio.Play(liveId);
            yield return WaitForPlaybackStart(livePlayer, "live playback to start");

            // Move the live player's per-type factor away from default so a push back to it is observable.
            BroAudio.SetVolume(BroAudioType.SFX, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.4f, livePlayer.GetVolume(), LinearTolerance);

            // Setting back to exactly 1f (DefaultTrackVolume) is pushed to live players.
            BroAudio.SetVolume(BroAudioType.SFX, 1f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(1f, livePlayer.GetVolume(), LinearTolerance, "Live players are pushed even when the new value equals the default.");

            // PlayControl applies the stored pref to a freshly-started player unconditionally, so the pref
            // and live players agree at every value including the default (Docs/FIXED_ISSUES.md #7).
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(1f, pref.Volume, LinearTolerance);

            SoundID futureId = NewSound("FutureTypeSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer futurePlayer = BroAudio.Play(futureId);
            yield return WaitForPlaybackStart(futurePlayer, "future playback to start");
            Assert.AreEqual(1f, futurePlayer.GetVolume(), LinearTolerance, "A fresh player should read the stored per-type volume.");
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
            // Called in the same frame Play() was enqueued - before SoundManager.LateUpdate drains the
            // queue and SetInitialPitch runs - so this must hit the deferred-fade branch, not the live one.
            player.SetPitch(2f, 0.5f);

            yield return WaitForPlaybackStart(player);

            // The very first playing frame should still read close to the entity's base pitch (1), proving
            // the fade was deferred rather than the pitch snapping straight to the target (2).
            Assert.Less(player.AudioSource.pitch, 1.9f, "SetPitch called before play should defer into a fade, not snap to the target immediately.");

            yield return WaitUntilOrTimeout(() => Mathf.Abs(player.AudioSource.pitch - 2f) < 0.01f, "the deferred pitch fade to reach its target", 2f);
            Assert.AreEqual(2f, player.AudioSource.pitch, LinearTolerance);
        }

        // SoundManager.SetPitch(float, BroAudioType, float) mirrors
        // SetAudioTypeVolume_ToExactlyDefault_AppliesToLiveAndFuturePlayers above: it both pushes the new
        // pitch to every live player of the matching type and stores it into AudioTypePlaybackPreference,
        // so a player that hasn't been played yet also picks it up via SetInitialPitch. BroAudio.SetPitch
        // with no fadeTime argument defaults to BroAdvice.FadeTime_Immediate, so this applies instantly.
        [UnityTest]
        public IEnumerator SetPitch_ByBroAudioType_AppliesToLiveAndFuturePlayersOfThatTypeOnly()
        {
            SoundID sfxId = NewSound("PitchTypeLiveSfx", BroAudioType.SFX, NewClip(3f));
            SoundID musicId = NewSound("PitchTypeLiveMusic", BroAudioType.Music, NewClip(3f));
            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);
            yield return WaitUntilOrTimeout(() => sfxPlayer.IsPlaying && musicPlayer.IsPlaying, "both players to start playing", 2f);

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

        // SoundManager.SetPitch(SoundID, ...) only pushes to live players with that ID - unlike the per-type
        // overload it stores nothing, so a later play of the same ID starts from its base pitch again.
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
    }
}