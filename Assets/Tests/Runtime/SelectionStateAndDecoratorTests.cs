using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
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
