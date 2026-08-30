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
        // Linear volume products are exact float multiplication (fadeTime 0 uses Fader.Complete), so a
        // tight tolerance is fine. dB values go through a log conversion plus mixer round-trip, so they
        // need a looser tolerance.
        private const float LinearTolerance = 0.01f;
        private const float DecibelTolerance = 0.1f;

        [UnityTest]
        public IEnumerator SetVolume_Master_WritesDirectlyToMixerAndNeverEntersLinearProduct()
        {
            SoundID id = NewSound("MasterVolSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            float baselineLinear = player.GetVolume();

            BroAudio.SetVolume(0.25f, 0f); // no id/type => master, per BroAudio.cs:159-160
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float db));
            Assert.AreEqual(0.25f.ToDecibel(), db, DecibelTolerance, "Master volume should write vol.ToDecibel() straight to the mixer's Master parameter.");

            // characterizes: master volume is a separate mixer-graph stage, not a peer in the
            // player's own linear product (Docs/inventory/volume-mixer.md "Conflicts observed").
            Assert.AreEqual(baselineLinear, player.GetVolume(), LinearTolerance, "Master volume must never appear in IAudioPlayer.GetVolume()'s linear product.");
        }

        [UnityTest]
        public IEnumerator SetVolume_PerSoundIdAndPerType_ComposeMultiplicativelyInLinearProduct()
        {
            SoundID id = NewSound("CompositionSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            // Fresh play: clip.Volume(1) * entity.MasterVolume(1) baked into _clipVolume, _trackVolume
            // and _audioTypeVolume both still at their default of 1.
            Assert.AreEqual(1f, player.GetVolume(), LinearTolerance, "A freshly played default entity should read full linear volume.");

            BroAudio.SetVolume(id, 0.5f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.5f, player.GetVolume(), LinearTolerance, "Per-SoundID volume should multiply into the linear product.");

            BroAudio.SetVolume(BroAudioType.SFX, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.5f * 0.4f, player.GetVolume(), LinearTolerance, "Per-BroAudioType volume should further multiply the same linear product (0.5 * 0.4).");
        }

        [UnityTest]
        public IEnumerator SetAudioTypeVolume_ToExactlyDefault_AppliesToLiveAndFuturePlayers()
        {
            SoundID liveId = NewSound("LiveTypeSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer livePlayer = BroAudio.Play(liveId);
            yield return WaitUntilOrTimeout(() => livePlayer.IsPlaying, "live playback to start", 2f);

            // Move the live player's per-type factor away from default so a push back to it is observable.
            BroAudio.SetVolume(BroAudioType.SFX, 0.4f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(0.4f, livePlayer.GetVolume(), LinearTolerance);

            // Setting back to exactly 1f (DefaultTrackVolume) is pushed to live players.
            BroAudio.SetVolume(BroAudioType.SFX, 1f, 0f);
            yield return WaitFrames(1);
            Assert.AreEqual(1f, livePlayer.GetVolume(), LinearTolerance, "Live players are pushed even when the new value equals the default.");

            // PlayControl applies the stored pref to a freshly-started player unconditionally, so the pref
            // and live players agree at every value including the default (Docs/TEST_FINDINGS.md finding #7).
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(1f, pref.Volume, LinearTolerance);

            SoundID futureId = NewSound("FutureTypeSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer futurePlayer = BroAudio.Play(futureId);
            yield return WaitUntilOrTimeout(() => futurePlayer.IsPlaying, "future playback to start", 2f);
            Assert.AreEqual(1f, futurePlayer.GetVolume(), LinearTolerance, "A fresh player should read the stored per-type volume.");
        }

        [UnityTest]
        public IEnumerator Play_AcquiresPooledMixerTrackAndReusesOneAfterRecycle()
        {
            SoundID id1 = NewSound("TrackSfx1", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player1 = BroAudio.Play(id1);
            yield return WaitUntilOrTimeout(() => player1.IsPlaying, "first playback to start", 2f);

            Assert.IsNotNull(player1.AudioSource.outputAudioMixerGroup, "A player should acquire a pooled mixer group once play starts.");
            StringAssert.StartsWith(BroName.GenericTrackName, player1.AudioSource.outputAudioMixerGroup.name);

            BroAudio.Stop(id1, 0f);
            yield return WaitUntilOrTimeout(() => !player1.IsActive, "the player to recycle after Stop", 2f);

            SoundID id2 = NewSound("TrackSfx2", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player2 = BroAudio.Play(id2);
            yield return WaitUntilOrTimeout(() => player2.IsPlaying, "second playback to start", 2f);

            Assert.IsNotNull(player2.AudioSource.outputAudioMixerGroup, "A subsequent play should reuse a group from the pool rather than getting null.");
            StringAssert.StartsWith(BroName.GenericTrackName, player2.AudioSource.outputAudioMixerGroup.name);
        }

        [UnityTest]
        public IEnumerator SetPitch_WithOutOfRangeValue_ClampsToAudioSourceRange()
        {
            SoundID id = NewSound("PitchClampSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

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
            SoundID id = NewSound("DeferredPitchSfx", BroAudioType.SFX, NewClip(3f));

            IAudioPlayer player = BroAudio.Play(id);
            // Called in the same frame Play() was enqueued - before SoundManager.LateUpdate drains the
            // queue and SetInitialPitch runs - so this must hit the deferred-fade branch, not the live one.
            player.SetPitch(2f, 0.5f);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            // The very first playing frame should still read close to the entity's base pitch (1), proving
            // the fade was deferred rather than the pitch snapping straight to the target (2).
            Assert.Less(player.AudioSource.pitch, 1.9f, "SetPitch called before play should defer into a fade, not snap to the target immediately.");

            yield return WaitUntilOrTimeout(() => Mathf.Abs(player.AudioSource.pitch - 2f) < 0.01f, "the deferred pitch fade to reach its target", 2f);
            Assert.AreEqual(2f, player.AudioSource.pitch, LinearTolerance);
        }
    }
}