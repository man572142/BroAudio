using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The EditMode half of <see cref="OptionalPackageTests"/>: defines are per-assembly, so the runtime probe
    /// says nothing about what <c>EditorTests</c> compiled. Honors
    /// <see cref="OptionalPackageTests.ExpectsNoOptionalPackages"/>.
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
        public void Addressables_IsCompiledIntoTheEditorAssemblyExactlyWhenThisRunExpectsIt()
        {
            OptionalPackageTests.AssertCompiledInAsExpected(AddressablesCompiledIn, "PACKAGE_ADDRESSABLES",
                "com.unity.addressables", "the addressable fixture tooling and the play mode catalog prebuild compiled to nothing.");
        }

        [Test]
        public void Localization_IsCompiledIntoTheEditorAssemblyExactlyWhenThisRunExpectsIt()
        {
            OptionalPackageTests.AssertCompiledInAsExpected(LocalizationCompiledIn, "PACKAGE_LOCALIZATION",
                "com.unity.localization", "LocalizationClipStrategyTests compiled to nothing and is silently absent from this run.");
        }
    }
}