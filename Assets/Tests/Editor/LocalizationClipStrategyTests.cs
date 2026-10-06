#if PACKAGE_LOCALIZATION
using Ami.BroAudio.Data;
using Ami.BroAudio.Editor.Tests;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// <see cref="LocalizationClipStrategy"/> in isolation (Docs/Tests/inventory/selection-policy.md). With no AssetTable
    /// the Play() path is untestable; <c>Inject()</c> with string table references and a cached clip never reaches
    /// <c>LoadAssetAsync</c>.
    /// </summary>
    public class LocalizationClipStrategyTests : BroEditorTestFixture
    {
        private AudioClip _clip;

        protected override void OnSetUp() => _clip = Track(TestAudioLibrary.CreateClip(0.1f, "LocalizedClip"));

        private static LocalizationClipStrategy CreateStrategy(LocalizedAudioClip localizedAudio, AudioClip cached)
        {
            var strategy = new LocalizationClipStrategy();
            strategy.Inject(localizedAudio, "TestEntity", () => cached);
            return strategy;
        }

        /// <summary>A reference pair that reads as "set" without a real table behind it.</summary>
        private static LocalizedAudioClip NewLocalizedAudio()
        {
            return new LocalizedAudioClip
            {
                TableReference = "TestTable",
                TableEntryReference = "TestEntry",
            };
        }

        [Test]
        public void SelectClip_WithNullLocalizedAudio_LogsErrorAndReturnsNullWithNegativeIndex()
        {
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            LocalizationClipStrategy strategy = CreateStrategy(null, _clip);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(-1, index, "A failed selection reports -1, not 0.");
        }

        [Test]
        public void SelectClip_WithUnsetTableEntry_LogsErrorAndReturnsNullWithNegativeIndex()
        {
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            var localizedAudio = new LocalizedAudioClip { TableReference = "TestTable" };
            LocalizationClipStrategy strategy = CreateStrategy(localizedAudio, _clip);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(-1, index);
        }

        [Test]
        public void SelectClip_WithCachedClipAndNoMatchingRow_WrapsTheResolvedClipAtIndexZero()
        {
            // characterizes: no row for the active locale still succeeds, with a warning. clips: null skips the
            // per-row locale scan, so the selected locale doesn't matter.
            LocalizationClipStrategy strategy = CreateStrategy(NewLocalizedAudio(), _clip);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, index);
            Assert.AreSame(_clip, result.GetAudioClip());
            Assert.IsTrue(result.IsSet);
        }

        [Test]
        public void SelectClip_WhenTheCachedClipIsAlreadyResolved_DoesNotTouchTheAddressablesLoadPath()
        {
            // The cache is all that stands between this test and a synchronous Addressables load of a missing
            // table: a refactor that bypasses it hangs or errors here.
            int cacheHits = 0;
            var strategy = new LocalizationClipStrategy();
            strategy.Inject(NewLocalizedAudio(), "TestEntity", () =>
            {
                cacheHits++;
                return _clip;
            });

            strategy.SelectClip(null, new ClipSelectionContext(0), out _);

            Assert.AreEqual(1, cacheHits, "The strategy should consult the cache exactly once per selection.");
        }
    }
}
#endif