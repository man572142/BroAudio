using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Runtime-only characterization for inventory 3.5 (clip-selection state ownership), 3.6 (decorator
    /// attach semantics) and 3.7 (RuntimeSetting toggles that shape Play()). The pure per-strategy behavior
    /// already lives in ClipSelectionTests.cs (EditMode) — this file only covers what needs a live SoundManager.
    /// </summary>
    public class SelectionStateAndDecoratorTests : BroAudioTestFixture
    {
        #region 3.5 Clip-selection state lives on the AudioEntity, not the player

        [UnityTest]
        public IEnumerator Play_SameSequenceEntityPlayedTwice_AdvancesSharedCursorAcrossPlayers()
        {
            AudioClip clip0 = NewClip(3f, "SeqClip0");
            AudioClip clip1 = NewClip(3f, "SeqClip1");
            AudioClip clip2 = NewClip(3f, "SeqClip2");
            AudioEntity entity = NewEntity("SeqSfx", BroAudioType.SFX, clip0, clip1, clip2);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            SoundID id = IdOf(entity);

            IAudioPlayer player1 = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player1.IsPlaying, "the first play to start", 2f);
            Assert.AreEqual("SeqClip0", player1.AudioSource.clip.name);

            // characterizes: AudioEntity._clipSelectionStrategy is one field shared by every Play() on this
            // SoundID — a second concurrent play advances the same cursor rather than starting its own at 0.
            IAudioPlayer player2 = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player2.IsPlaying, "the second play to start", 2f);
            Assert.AreEqual("SeqClip1", player2.AudioSource.clip.name,
                "The second concurrent play should advance the same shared Sequence cursor, not restart at clip 0.");

            Assert.AreNotSame(player1.AudioSource, player2.AudioSource, "The two plays should be on different pooled players.");
            Assert.IsTrue(player1.IsPlaying, "The first player should still be playing concurrently with the second.");
        }

        [UnityTest]
        public IEnumerator ResetMultiClipStrategy_AfterAdvancingSequenceCursor_RestartsFromClipZero()
        {
            AudioClip clip0 = NewClip(2f, "SeqResetClip0");
            AudioClip clip1 = NewClip(2f, "SeqResetClip1");
            AudioEntity entity = NewEntity("SeqResetSfx", BroAudioType.SFX, clip0, clip1);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            SoundID id = IdOf(entity);

            IAudioPlayer first = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => first.IsPlaying, "the first play to start", 2f);
            Assert.AreEqual("SeqResetClip0", first.AudioSource.clip.name);

            IAudioPlayer second = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => second.IsPlaying, "the second play to start", 2f);
            Assert.AreEqual("SeqResetClip1", second.AudioSource.clip.name, "Cursor should have advanced from the first play.");

            // The state is never reset automatically between plays — only this explicit call clears it.
            BroAudio.ResetMultiClipStrategy(id);

            IAudioPlayer third = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => third.IsPlaying, "the third play to start", 2f);
            Assert.AreEqual("SeqResetClip0", third.AudioSource.clip.name,
                "ResetMultiClipStrategy is the only thing that clears the shared cursor.");
        }

        // The strategies themselves are exhaustively covered in ClipSelectionTests.cs (EditMode). The two
        // tests below only cover the wiring: IAudioPlayer.SetVelocity/SetSequenceId write a field on the
        // PlaybackPreference struct, and that write has to land before SoundManager.LateUpdate drains the
        // queued Play() and calls PickNewClip — the same same-frame seam VolumePitchMixerTests exercises
        // for SetPitch.
        [UnityTest]
        public IEnumerator SetVelocity_CalledBeforeQueueDrains_SelectsTheVelocityMatchedClip()
        {
            AudioClip low = NewClip(3f, "LowVelocityClip");
            AudioClip mid = NewClip(3f, "MidVelocityClip");
            AudioClip high = NewClip(3f, "HighVelocityClip");
            AudioEntity entity = NewEntity("VelocityWiringSfx", BroAudioType.SFX, low, mid, high);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Velocity);
            entity.Clips[0].Weight = 0;
            entity.Clips[1].Weight = 40;
            entity.Clips[2].Weight = 80;
            SoundID id = IdOf(entity);

            // Play only enqueues; SetVelocity lands in the same frame, before the clip is picked.
            IAudioPlayer player = BroAudio.Play(id);
            player.SetVelocity(50);

            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            Assert.AreSame(mid, player.AudioSource.clip,
                "SetVelocity(50) called in the same frame as Play() must still steer PickNewClip to the clip at the 40 threshold.");
        }

        [UnityTest]
        public IEnumerator SetSequenceId_WithDifferentIds_AdvancesEachNamedCursorIndependently()
        {
            AudioClip clip0 = NewClip(3f, "SeqIdClip0");
            AudioClip clip1 = NewClip(3f, "SeqIdClip1");
            AudioClip clip2 = NewClip(3f, "SeqIdClip2");
            AudioEntity entity = NewEntity("SequenceWiringSfx", BroAudioType.SFX, clip0, clip1, clip2);
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Sequence);
            SoundID id = IdOf(entity);

            IAudioPlayer firstA = BroAudio.Play(id);
            firstA.SetSequenceId("a");
            yield return WaitUntilOrTimeout(() => firstA.IsPlaying, "the first 'a' play to start", 2f);
            Assert.AreSame(clip0, firstA.AudioSource.clip, "The 'a' cursor's first pick should be index 0.");

            IAudioPlayer secondA = BroAudio.Play(id);
            secondA.SetSequenceId("a");
            yield return WaitUntilOrTimeout(() => secondA.IsPlaying, "the second 'a' play to start", 2f);
            Assert.AreSame(clip1, secondA.AudioSource.clip, "The 'a' cursor should have advanced to index 1.");

            // characterizes: a named sequence id gets its own cursor, unlike the default shared one that
            // Play_SameSequenceEntityPlayedTwice_AdvancesSharedCursorAcrossPlayers pins down above.
            IAudioPlayer firstB = BroAudio.Play(id);
            firstB.SetSequenceId("b");
            yield return WaitUntilOrTimeout(() => firstB.IsPlaying, "the first 'b' play to start", 2f);
            Assert.AreSame(clip0, firstB.AudioSource.clip,
                "A different sequence id must start fresh at index 0, unaffected by 'a' already sitting at index 1.");
        }

        #endregion

        #region 3.6 Decorators: AsBGM / AsDominator

        [UnityTest]
        public IEnumerator AsBGM_CalledTwice_ReturnsTheSameMusicPlayerDecoratorInstance()
        {
            // SFX, not Music: avoids RuntimeSetting.AlwaysPlayMusicAsBGM's implicit auto-attach interfering
            // with this explicit-attach characterization (see selection-policy.md's decorator section).
            SoundID id = NewSound("DecoratorSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);

            player.AsBGM();
            List<AudioPlayerDecorator> afterFirst = GetDecorators(player);
            Assert.AreEqual(1, afterFirst.Count, "The first AsBGM() should create exactly one decorator.");
            AudioPlayerDecorator firstInstance = afterFirst[0];
            Assert.IsInstanceOf<MusicPlayer>(firstInstance);

            player.AsBGM();
            List<AudioPlayerDecorator> afterSecond = GetDecorators(player);

            // characterizes: Utility.GetOrCreateDecorator is idempotent per type — a second AsBGM() on the
            // same player reuses the existing MusicPlayer decorator rather than stacking a duplicate.
            Assert.AreEqual(1, afterSecond.Count, "A second AsBGM() must not stack a duplicate decorator.");
            Assert.AreSame(firstInstance, afterSecond[0], "The second AsBGM() must reuse the same decorator instance.");

            yield return null;
        }

        [UnityTest]
        public IEnumerator AsBGM_AndAsDominator_CoexistOnTheSamePlayer()
        {
            SoundID id = NewSound("DecoratorSfx2", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);

            player.AsBGM();
            player.AsDominator(); // #if !UNITY_WEBGL in source — available here, running in the Editor.

            List<AudioPlayerDecorator> decorators = GetDecorators(player);
            Assert.AreEqual(2, decorators.Count, "Both decorators should live side by side in the same list.");
            Assert.IsTrue(decorators.Exists(d => d is MusicPlayer), "MusicPlayer decorator should be present.");
            Assert.IsTrue(decorators.Exists(d => d is DominatorPlayer), "DominatorPlayer decorator should be present.");

            yield return null;
        }

        #endregion

        #region 3.6 Dominator effect parameters (DominatorPlayer.LowPassOthers / HighPassOthers)

        [UnityTest]
        public IEnumerator LowPassOthers_MovesDominatorLowPassParameter_LeavesEffectLowPassParameterUntouched()
        {
            SoundID dominatorId = NewSound("DominatorLowPassSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitUntilOrTimeout(() => dominatorPlayer.IsPlaying, "the dominator to start playing", 2f);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float effectLowPassBefore);

            // characterizes: a dominator writes BroName.Dominator_LowPassParaName ("Main_LowPass"), a
            // completely separate exposed parameter from BroName.LowPassParaName ("Effect_LowPass") that
            // BroAudio.SetEffect uses. The two effect paths never touch the same mixer parameter.
            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            dominator.LowPassOthers(2000f, 0f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float v);
                return Mathf.Approximately(v, 2000f);
            }, "Main_LowPass to reach the requested frequency", 2f);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float effectLowPassAfter);
            Assert.AreEqual(effectLowPassBefore, effectLowPassAfter,
                "LowPassOthers must not move Effect_LowPass — that parameter belongs to BroAudio.SetEffect.");
        }

        [UnityTest]
        public IEnumerator HighPassOthers_MovesDominatorHighPassParameter_LeavesEffectHighPassParameterUntouched()
        {
            SoundID dominatorId = NewSound("DominatorHighPassSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitUntilOrTimeout(() => dominatorPlayer.IsPlaying, "the dominator to start playing", 2f);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float effectHighPassBefore);

            IPlayerEffect dominator = dominatorPlayer.AsDominator();
            dominator.HighPassOthers(5000f, 0f);

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_HighPassParaName, out float v);
                return Mathf.Approximately(v, 5000f);
            }, "Main_HighPass to reach the requested frequency", 2f);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float effectHighPassAfter);
            Assert.AreEqual(effectHighPassBefore, effectHighPassAfter,
                "HighPassOthers must not move Effect_HighPass — that parameter belongs to BroAudio.SetEffect.");
        }

        [UnityTest]
        public IEnumerator LowPassOthers_InvalidFrequency_LogsErrorAndLeavesParameterUnchanged_UnlikeQuietOthersWarning()
        {
            SoundID dominatorId = NewSound("InvalidFreqDominatorSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer dominatorPlayer = BroAudio.Play(dominatorId);
            yield return WaitUntilOrTimeout(() => dominatorPlayer.IsPlaying, "the dominator to start playing", 2f);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float before);
            IPlayerEffect dominator = dominatorPlayer.AsDominator();

            // characterizes: this is NOT silent. AudioExtension.IsValidFrequency itself calls Debug.LogError
            // (with the standard Utility.LogTitle prefix, per TEST_FINDINGS #15) before
            // DominatorPlayer.LowPassOthers even reaches SetAllEffectExceptDominator.
            LogAssert.Expect(LogType.Error, new Regex("frequency should be in"));
            dominator.LowPassOthers(0f, 0f);
            yield return WaitFrames(2);

            SoundManager.Instance.AudioMixer.GetFloat(BroName.Dominator_LowPassParaName, out float after);
            Assert.AreEqual(before, after, "An invalid frequency must leave the mixer parameter untouched.");

            // Contrast: QuietOthers' own range guard also logs with the Utility.LogTitle prefix, but at
            // Warning instead of Error — the two "invalid input" guards still differ in log level.
            LogAssert.Expect(LogType.Warning, new Regex("othersVol should be less than 1 and greater than 0"));
            dominator.QuietOthers(0f, 0f);
            yield return WaitFrames(2);
        }

        #endregion

        #region 3.7 RuntimeSetting toggles that change Play behavior

        [UnityTest]
        public IEnumerator AudioSource_AccessedAfterRecycle_ResolvesToNullWhicheverWayTheWarningIsSet()
        {
            // LogAccessRecycledPlayerWarning changes nothing but whether a warning is emitted, and asserting
            // on log text is an anti-goal here — so this pins the behavior that matters (a recycled wrapper
            // resolves AudioSource to null either way) and merely consumes the warning rather than testing it.
            // LogAssert.NoUnexpectedReceived() is deliberately not used: it also catches Unity's own
            // "There are no audio listeners in the scene" message, which the PlayMode test scene always emits.
            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = true;
            SoundID id = NewSound("RecycledSfx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            BroAudio.Stop(id, 0f);
            yield return WaitUntilOrTimeout(() => !player.IsActive, "the player to become inactive and recycle", 2f);

            LogAssert.Expect(LogType.Warning, new Regex("has been recycled after playback"));
            Assert.IsNull(player.AudioSource, "A recycled wrapper resolves AudioSource to null.");

            SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning = false;
            Assert.IsNull(player.AudioSource, "Still null with the warning turned off.");
        }

        [UnityTest]
        public IEnumerator HasLoop_TwoArgOverload_TracksDefaultChainedPlayModeLoopSetting()
        {
            // Runtime-only gap: ClipSelectionTests.cs (EditMode) covers the 4-arg HasLoop overload with
            // explicit defaults. The 2-arg overload — the one SoundManager.Playback.cs actually calls to
            // decide whether Play() schedules handover — reads SoundManager.Instance.Setting live, so it
            // needs a real SoundManager and belongs here instead.
            AudioEntity entity = NewEntity("ChainedSettingSfx", BroAudioType.SFX, NewClip(2f), NewClip(2f), NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Chained);

            SoundManager.Instance.Setting.DefaultChainedPlayModeLoop = LoopType.SeamlessLoop;
            SoundManager.Instance.Setting.DefaultChainedPlayModeTransitionTime = 1.5f;
            bool hasLoopWhenOn = entity.HasLoop(out LoopType loopTypeOn, out float transitionTimeOn);
            Assert.IsTrue(hasLoopWhenOn, "A Chained entity with no explicit Loop/SeamlessLoop flag should fall back to the setting's default.");
            Assert.AreEqual(LoopType.SeamlessLoop, loopTypeOn);
            Assert.AreEqual(1.5f, transitionTimeOn);

            SoundManager.Instance.Setting.DefaultChainedPlayModeLoop = LoopType.None;
            bool hasLoopWhenOff = entity.HasLoop(out LoopType loopTypeOff, out float transitionTimeOff);
            Assert.IsFalse(hasLoopWhenOff, "With the default turned off and no explicit flag, a Chained entity has no loop at all.");
            Assert.AreEqual(LoopType.None, loopTypeOff);

            // characterizes: HasLoop's Chained branch writes transitionTime *before* deciding the return
            // value, so a false return still hands back the configured transition time rather than 0.
            // Callers must not read the out parameter unless the method returned true.
            Assert.AreEqual(1.5f, transitionTimeOff,
                "The out parameter is populated even though HasLoop returned false.");

            yield return null;
        }

        #endregion

        private static List<AudioPlayerDecorator> GetDecorators(IAudioPlayer player)
        {
            AudioPlayer instance = (AudioPlayer)(AudioPlayerInstanceWrapper)player;
            FieldInfo field = typeof(AudioPlayer).GetField("_decorators", BindingFlags.NonPublic | BindingFlags.Instance);
            return (List<AudioPlayerDecorator>)field.GetValue(instance);
        }
    }
}