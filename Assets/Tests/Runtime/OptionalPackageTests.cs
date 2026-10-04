using System;
using System.Linq;
using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Fails the run when an optional package is not in the state the run promised: without
    /// <c>PACKAGE_ADDRESSABLES</c>, <see cref="AddressablesTests"/> compiles to nothing and CI stays green.
    /// These probes compile unconditionally, so they cannot vanish the same way.
    /// <para>
    /// Both packages are pinned in the manifest, so a missing one means resolution failed. The CI leg that
    /// removes them on purpose sets <see cref="ExpectsNoOptionalPackages"/>, which inverts the probes.
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
        /// The command-line form, which CI must use: game-ci's container does not forward a step's <c>env:</c>,
        /// but <c>customParameters</c> reaches the Editor's command line. Unity ignores unknown arguments.
        /// </summary>
        public const string ExpectsNoOptionalPackagesArgument = "-broaudioCiExpectsNoOptionalPackages";

        /// <summary>
        /// True when this run omits Addressables and Localization on purpose. Also read by
        /// <see cref="OptionalPackageEditorTests"/>.
        /// </summary>
        public static bool ExpectsNoOptionalPackages =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ExpectsNoOptionalPackagesVariable))
            || Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, ExpectsNoOptionalPackagesArgument, StringComparison.OrdinalIgnoreCase));

        /// <summary>Shared by the probes in both assemblies: compiled in exactly when the run expects it.</summary>
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