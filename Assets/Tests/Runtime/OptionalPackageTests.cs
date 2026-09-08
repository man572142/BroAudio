using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fails the PlayMode run when an optional package is missing, instead of letting its suite disappear.
    /// <para>
    /// <see cref="AddressablesTests"/> compiles behind <c>PACKAGE_ADDRESSABLES</c>, which this assembly's
    /// <c>versionDefines</c> raise only while <c>com.unity.addressables</c> is resolved. When it is not, the
    /// whole file compiles to nothing: the suite is absent from the results, every remaining test passes, and
    /// CI reports green. Workflow run 11 passed exactly that way — 124 tests instead of 132, with the eight
    /// addressable tests silently gone, on a cold <c>Library</c> whose package resolution came up short.
    /// </para>
    /// <para>
    /// These probes are compiled unconditionally, so they cannot vanish the same way. Both packages are pinned
    /// in <c>Packages/manifest.json</c>, so a false here means the project did not resolve — not that the
    /// package is optional for this project.
    /// </para>
    /// </summary>
    public class OptionalPackageTests
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
        public void Addressables_IsResolved_SoItsSuiteIsCompiledIntoThisRun()
        {
            Assert.IsTrue(AddressablesCompiledIn,
                "PACKAGE_ADDRESSABLES is undefined, so AddressablesTests compiled to nothing and is silently " +
                "absent from this run. com.unity.addressables is pinned in Packages/manifest.json, so it failed " +
                "to resolve rather than being genuinely optional here.");
        }

        [Test]
        public void Localization_IsResolved_SoItsCodeIsCompiledIntoThisRun()
        {
            Assert.IsTrue(LocalizationCompiledIn,
                "PACKAGE_LOCALIZATION is undefined, so every localization-gated path in the runtime assembly " +
                "compiled to nothing and nothing in this run covers it. com.unity.localization is pinned in " +
                "Packages/manifest.json, so it failed to resolve rather than being genuinely optional here.");
        }
    }
}