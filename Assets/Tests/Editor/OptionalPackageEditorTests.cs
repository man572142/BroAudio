using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The EditMode half of <see cref="OptionalPackageTests"/>. Defines are per-assembly, so the runtime probe
    /// says nothing about what <c>EditorTests</c> compiled — and EditMode lost tests the same way PlayMode did:
    /// workflow run 11 ran 209 EditMode tests instead of 214, with <c>LocalizationClipStrategyTests</c> silently
    /// compiled out, and still reported green.
    /// </summary>
    public class OptionalPackageEditorTests
    {
#if PACKAGE_ADDRESSABLES
        private const bool AddressablesCompiledIn = true;
#else
        private const bool AddressablesCompiledIn = false;
#endif
#if PACKAGE_LOCALIZATION
        private const bool LocalizationCompiledIn = true;
#else
        private const bool LocalizationCompiledIn = false;
#endif

        [Test]
        public void Addressables_IsResolved_SoItsEditorCodeIsCompiledIntoThisRun()
        {
            Assert.IsTrue(AddressablesCompiledIn,
                "PACKAGE_ADDRESSABLES is undefined, so the addressable fixture tooling and the play mode catalog " +
                "prebuild compiled to nothing. com.unity.addressables is pinned in Packages/manifest.json, so it " +
                "failed to resolve rather than being genuinely optional here.");
        }

        [Test]
        public void Localization_IsResolved_SoItsSuiteIsCompiledIntoThisRun()
        {
            Assert.IsTrue(LocalizationCompiledIn,
                "PACKAGE_LOCALIZATION is undefined, so LocalizationClipStrategyTests compiled to nothing and is " +
                "silently absent from this run. com.unity.localization is pinned in Packages/manifest.json, so it " +
                "failed to resolve rather than being genuinely optional here.");
        }
    }
}