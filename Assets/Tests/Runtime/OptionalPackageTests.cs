using System;
using System.Linq;
using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fails the PlayMode run when an optional package is not in the state the run promised, instead of
    /// letting a suite quietly disappear or a configuration quietly go untested.
    /// <para>
    /// <see cref="AddressablesTests"/> compiles behind <c>PACKAGE_ADDRESSABLES</c>, which this assembly's
    /// <c>versionDefines</c> raise only while <c>com.unity.addressables</c> is resolved. When it is not, the
    /// whole file compiles to nothing: the suite is absent from the results, every remaining test passes, and
    /// CI reports green.
    /// </para>
    /// <para>
    /// These probes are compiled unconditionally, so they cannot vanish the same way. Both packages are pinned
    /// in <c>Packages/manifest.json</c>, so by default a false here means the project did not resolve — not that
    /// the package is optional for this project.
    /// </para>
    /// <para>
    /// The one run that removes both packages on purpose — the CI leg that proves the package compiles and
    /// passes without them (CLAUDE.md's Definition of Done #3) — says so with <see cref="ExpectsNoOptionalPackages"/>,
    /// and there the probes invert: a package that is still compiled in means the removal did not happen and the
    /// leg is re-testing the full configuration under a false name.
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

        /// <summary>The environment variable form of the switch, for a local run.</summary>
        public const string ExpectsNoOptionalPackagesVariable = "BROAUDIO_CI_EXPECTS_NO_OPTIONAL_PACKAGES";

        /// <summary>
        /// The command-line form of the switch, which is what CI uses: game-ci's unity-test-runner starts the
        /// Editor inside a container that receives only a fixed set of environment variables, so a step's
        /// <c>env:</c> never reaches it, while <c>customParameters</c> is appended to the Editor's command line.
        /// Unity ignores command-line arguments it does not recognise.
        /// </summary>
        public const string ExpectsNoOptionalPackagesArgument = "-broaudioCiExpectsNoOptionalPackages";

        /// <summary>
        /// True when this run was started without Addressables and Localization on purpose, by either the
        /// command-line argument or the environment variable. <see cref="OptionalPackageEditorTests"/> reads it too.
        /// </summary>
        public static bool ExpectsNoOptionalPackages =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ExpectsNoOptionalPackagesVariable))
            || Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, ExpectsNoOptionalPackagesArgument, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The one assertion both probes make, in both assemblies: the package is compiled in exactly when the
        /// run expects it.
        /// </summary>
        public static void AssertCompiledInAsExpected(bool compiledIn, string define, string package, string whatIsLost)
        {
            if (ExpectsNoOptionalPackages)
            {
                Assert.IsFalse(compiledIn,
                    define + " is defined, but this run was started with " + ExpectsNoOptionalPackagesArgument +
                    " (or " + ExpectsNoOptionalPackagesVariable + "), which promises " + package + " was removed. " +
                    "The removal did not take effect, so this run is testing the full configuration, not the one " +
                    "without optional packages. Check the step that strips Packages/manifest.json and packages-lock.json.");
                return;
            }

            Assert.IsTrue(compiledIn,
                define + " is undefined, so " + whatIsLost + " " + package + " is pinned in Packages/manifest.json, " +
                "so it failed to resolve rather than being genuinely optional here. A run that removes it on " +
                "purpose passes " + ExpectsNoOptionalPackagesArgument + " to say so.");
        }

        [Test]
        public void Addressables_IsCompiledInExactlyWhenThisRunExpectsIt()
        {
            AssertCompiledInAsExpected(AddressablesCompiledIn, "PACKAGE_ADDRESSABLES", "com.unity.addressables",
                "AddressablesTests compiled to nothing and is silently absent from this run.");
        }

        [Test]
        public void Localization_IsCompiledInExactlyWhenThisRunExpectsIt()
        {
            AssertCompiledInAsExpected(LocalizationCompiledIn, "PACKAGE_LOCALIZATION", "com.unity.localization",
                "every localization-gated path in the runtime assembly compiled to nothing and nothing in this run covers it.");
        }
    }
}