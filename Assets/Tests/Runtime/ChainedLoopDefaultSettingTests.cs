using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Runtime-only characterization for inventory 3.7: the <see cref="AudioEntity.HasLoop(out LoopType, out float)"/>
    /// 2-arg overload - the one SoundManager.Playback.cs actually calls to decide whether Play() schedules a
    /// handover - reads SoundManager.Instance.Setting live, so it needs a real SoundManager and cannot move
    /// to the EditMode suite: SoundManager.Instance returns null outside Play Mode
    /// (Runtime/SoundManager/SoundManager.cs), so this overload throws a NullReferenceException there. The
    /// 4-arg overload with explicit defaults is covered EditMode-side by ClipSelectionTests.cs.
    /// </summary>
    public class ChainedLoopDefaultSettingTests : BroAudioTestFixture
    {
        [UnityTest]
        [Category("Finding_13")]
        public IEnumerator HasLoop_TwoArgOverload_TracksDefaultChainedPlayModeLoopSetting()
        {
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

            // Characterizes TEST_FINDINGS #13: HasLoop's Chained branch writes transitionTime *before* deciding
            // the return value, so a false return still hands back the configured transition time rather than 0.
            // Callers must not read the out parameter unless the method returned true.
            Assert.AreEqual(1.5f, transitionTimeOff,
                "The out parameter is populated even though HasLoop returned false.");

            yield return null;
        }
    }
}
