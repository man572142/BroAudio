using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Runtime-only characterization for inventory 3.6: decorator attach semantics.
    /// <see cref="AudioPlayerDecorator"/>-based modes (<see cref="MusicPlayer"/>, <see cref="DominatorPlayer"/>)
    /// are attached, not inherited - <c>AsBGM()</c>/<c>AsDominator()</c> get or create a decorator instance
    /// on the player's own list, and a repeated call must reuse it rather than stack a duplicate.
    /// </summary>
    public class DecoratorAttachmentTests : BroAudioTestFixture
    {
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

        private static List<AudioPlayerDecorator> GetDecorators(IAudioPlayer player)
            => TestAudioLibrary.GetPrivateField<List<AudioPlayerDecorator>>(InstanceOf(player), TestAudioLibrary.Reflected.AudioPlayer.Decorators);
    }
}
