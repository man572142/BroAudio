#if PACKAGE_LOCALIZATION
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory 0.6 (Docs/inventory/selection-policy.md): <see cref="LocalizationClipStrategy"/> in isolation.
    /// <para>
    /// The project has three Locale assets but no AssetTable, so the full
    /// Play() -> SoundManager -> resolved clip path is untestable as authored. <c>Inject()</c> sidesteps that
    /// entirely: setting the table references to plain strings makes them non-empty without any table existing,
    /// and supplying a cached clip means <c>LoadAssetAsync</c> is never reached.
    /// </para>
    /// </summary>
    public class LocalizationClipStrategyTests
    {
        private AudioClip _clip;

        [SetUp]
        public void SetUp() => _clip = TestAudioLibrary.CreateClip(0.1f, "LocalizedClip");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_clip);

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
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("table is not set"));
            LocalizationClipStrategy strategy = CreateStrategy(null, _clip);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(-1, index, "A failed selection reports -1, not 0.");
        }

        [Test]
        public void SelectClip_WithUnsetTableEntry_LogsErrorAndReturnsNullWithNegativeIndex()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("entry is not set"));
            var localizedAudio = new LocalizedAudioClip { TableReference = "TestTable" };
            LocalizationClipStrategy strategy = CreateStrategy(localizedAudio, _clip);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(-1, index);
        }

        [Test]
        public void SelectClip_WithCachedClipAndNoMatchingRow_WrapsTheResolvedClipAtIndexZero()
        {
            // characterizes: with no BroAudioClip row for the active locale, the strategy still succeeds —
            // it warns, reports index 0, and returns a wrapper carrying the resolved clip with default
            // playback properties. Passing clips: null skips the per-row locale scan, so this is
            // deterministic regardless of which locale the project happens to have selected.
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
            // The cached-clip lambda is the only thing standing between this test and a synchronous
            // Addressables load against a table that does not exist. If a refactor stops consulting it,
            // this test hangs or errors rather than passing quietly.
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