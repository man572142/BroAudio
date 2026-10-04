using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="AudioEntity.HasLoop(out LoopType, out float)"/> overload reads SoundManager.Instance.Setting,
    /// so it can't move to EditMode; the explicit-defaults overload is covered in ClipSelectionTests.
    /// </summary>
    public class ChainedLoopDefaultSettingTests : BroAudioTestFixture
    {
        [UnityTest]
        public IEnumerator HasLoop_TwoArgOverload_TracksDefaultChainedPlayModeLoopSetting()
        {
            AudioEntity entity = NewEntity("ChainedSettingSfx", BroAudioType.SFX, NewClip(2f), NewClip(2f), NewClip(2f));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode, MulticlipsPlayMode.Chained);

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

            Assert.AreEqual(0f, transitionTimeOff, "A HasLoop that returns false reports no transition time.");

            yield return null;
        }
    }
}
