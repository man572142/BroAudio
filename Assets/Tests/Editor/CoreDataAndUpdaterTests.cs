using System;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Small editor-side decision points with no other coverage: how many clip rows each play mode accepts
    /// (<see cref="BroEditorUtility.GetMaxAcceptableClipCount"/>), the legacy core-data parser
    /// (<see cref="BroEditorUtility.TryParseCoreData"/>), and the version gates inside <see cref="BroUpdater"/>
    /// that decide which upgrade steps an older install gets.
    /// <para>
    /// BroUpdater's steps are private and <c>Process</c> itself moves assets, opens a dialog and writes the
    /// version file, so the gates are driven one step at a time through reflection, against in-memory settings
    /// objects - never the project's own assets. The one gate whose open branch writes an asset to disk
    /// (<c>CreateGlobalPlaybackGroup</c>) is only driven down its closed branches.
    /// </para>
    /// </summary>
    public class CoreDataAndUpdaterTests : BroEditorTestFixture
    {
        #region GetMaxAcceptableClipCount
        // ReorderableClips disables every clip row at or past this count. Single plays only the first clip and
        // Chained only intro/loop/outro; every other mode, Localization included, takes any number.
        [TestCase(MulticlipsPlayMode.Single, 1)]
        [TestCase(MulticlipsPlayMode.Chained, 3)]
        [TestCase(MulticlipsPlayMode.Sequence, int.MaxValue)]
        [TestCase(MulticlipsPlayMode.Random, int.MaxValue)]
        [TestCase(MulticlipsPlayMode.Shuffle, int.MaxValue)]
        [TestCase(MulticlipsPlayMode.Velocity, int.MaxValue)]
        [TestCase(MulticlipsPlayMode.Localization, int.MaxValue)]
        public void GetMaxAcceptableClipCount_ReturnsTheRowLimitForEachPlayMode(MulticlipsPlayMode mode, int expected)
        {
            Assert.AreEqual(expected, mode.GetMaxAcceptableClipCount());
        }
        #endregion

        #region TryParseCoreData
        private TextAsset NewTextAsset(string text) => Track(new TextAsset(text));

        [Test]
        public void TryParseCoreData_WithNoTextAsset_ReturnsFalseAndNoData()
        {
            Assert.IsFalse(BroEditorUtility.TryParseCoreData(null, out BroEditorUtility.SerializedCoreData coreData));
            Assert.IsNull(coreData);
        }

        [Test]
        public void TryParseCoreData_WithAnEmptyTextAsset_ReturnsFalseAndNoData()
        {
            Assert.IsFalse(BroEditorUtility.TryParseCoreData(NewTextAsset(string.Empty), out BroEditorUtility.SerializedCoreData coreData));
            Assert.IsNull(coreData);
        }

        [Test]
        public void TryParseCoreData_WithSerializedCoreData_ReturnsTrueAndRoundTripsBothFields()
        {
            var written = new BroEditorUtility.SerializedCoreData("Assets/CoreDataOutput", new List<string> { "guid-a", "guid-b" });

            Assert.IsTrue(BroEditorUtility.TryParseCoreData(NewTextAsset(JsonUtility.ToJson(written)), out BroEditorUtility.SerializedCoreData read));

            Assert.AreEqual(written.AssetOutputPath, read.AssetOutputPath);
            CollectionAssert.AreEqual(written.GUIDs, read.GUIDs);
        }

        // characterizes: the only guard is "null or empty text"; anything else goes straight to
        // JsonUtility.FromJson, which throws on malformed JSON - so this Try* method throws instead of
        // returning false for a corrupted core-data file. Characterizes TEST_FINDINGS #69; a fix that returns
        // false turns the Assert.Catch red.
        [Test]
        [Category("Finding_69")]
        public void TryParseCoreData_WithMalformedText_ThrowsInsteadOfReturningFalse()
        {
            TextAsset corrupted = NewTextAsset("this is not json");

            Assert.Catch<ArgumentException>(() => BroEditorUtility.TryParseCoreData(corrupted, out _),
                "characterizes: malformed text reaches JsonUtility.FromJson, whose parse error escapes the Try* method.");
        }
        #endregion

        #region BroUpdater version gates
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>Private member names on the global-namespace <see cref="BroUpdater"/>, resolved lazily with a named failure.</summary>
        private static class ReflectedBroUpdater
        {
            public const string PlaybackGroupFirstReleasedVersion = "PlaybackGroupFirstReleasedVersion";
            public const string AssetBasedSoundIDFirstReleasedVersion = "AssetBasedSoundIDFirstReleasedVersion";
            public const string AddPlaybackGroupDrawedProperty = "AddPlaybackGroupDrawedProperty";
            public const string CreateGlobalPlaybackGroup = "CreateGlobalPlaybackGroup";

            public static MethodInfo Method(string name)
            {
                MethodInfo method = typeof(BroUpdater).GetMethod(name, PrivateStatic);
                Assert.IsNotNull(method, $"Reflection: BroUpdater.{name} could not be resolved - renamed or moved? Update CoreDataAndUpdaterTests.");
                return method;
            }

            public static Version VersionProperty(string name)
            {
                PropertyInfo property = typeof(BroUpdater).GetProperty(name, PrivateStatic);
                Assert.IsNotNull(property, $"Reflection: BroUpdater.{name} could not be resolved - renamed or moved? Update CoreDataAndUpdaterTests.");
                return (Version)property.GetValue(null);
            }
        }

        /// <summary>Invokes a BroUpdater step shaped (ref bool isDirty, Version oldVersion, TSetting setting) and returns isDirty.</summary>
        private static bool InvokeUpgradeStep(string methodName, Version oldVersion, ScriptableObject setting)
        {
            object[] args = { false, oldVersion, setting };
            ReflectedBroUpdater.Method(methodName).Invoke(null, args);
            return (bool)args[0];
        }

        // The two thresholds every gate below compares against. Process additionally only runs at all while the
        // stored version is below BroVersion.CodeBaseVersion, and runs the Sound ID upgrade only below 3.1.0.
        [Test]
        public void VersionGates_AreThePlaybackGroupAndAssetBasedSoundIdReleases()
        {
            Assert.AreEqual(new Version(2, 0, 0), ReflectedBroUpdater.VersionProperty(ReflectedBroUpdater.PlaybackGroupFirstReleasedVersion));
            Assert.AreEqual(new Version(3, 1, 0), ReflectedBroUpdater.VersionProperty(ReflectedBroUpdater.AssetBasedSoundIDFirstReleasedVersion));
        }

        // `oldVersion < 2.0.0` is strict: an install already on 2.0.0 is left alone. Below it, every audio type
        // gains the PlaybackGroup row on top of what it already drew, and the setting is reported dirty.
        [TestCase("1.9.9", true)]
        [TestCase("2.0.0", false)]
        [TestCase("3.2.3", false)]
        public void AddPlaybackGroupDrawedProperty_AddsTheRowOnlyForInstallsOlderThanPlaybackGroups(string oldVersion, bool expectAdded)
        {
            const DrawedProperty Existing = DrawedProperty.Volume | DrawedProperty.Loop;
            EditorSetting setting = NewScriptableObject<EditorSetting>();
            setting.AudioTypeSettings = new List<EditorSetting.AudioTypeSetting>
            {
                new EditorSetting.AudioTypeSetting { AudioType = BroAudioType.Music, DrawedProperty = Existing },
                new EditorSetting.AudioTypeSetting { AudioType = BroAudioType.SFX, DrawedProperty = Existing },
            };

            bool isDirty = InvokeUpgradeStep(ReflectedBroUpdater.AddPlaybackGroupDrawedProperty, Version.Parse(oldVersion), setting);

            Assert.AreEqual(expectAdded, isDirty, "The setting is reported dirty exactly when the gate opens.");
            foreach (EditorSetting.AudioTypeSetting typeSetting in setting.AudioTypeSettings)
            {
                Assert.AreEqual(expectAdded, typeSetting.CanDraw(DrawedProperty.PlaybackGroup), $"{typeSetting.AudioType}: PlaybackGroup row");
                Assert.IsTrue(typeSetting.CanDraw(DrawedProperty.Volume) && typeSetting.CanDraw(DrawedProperty.Loop),
                    $"{typeSetting.AudioType}: the rows it already drew are kept either way.");
            }
        }

        // The gate needs both an old install AND no global group yet. Either half closed leaves the setting
        // untouched and clean. (The open branch creates a DefaultPlaybackGroup asset next to the RuntimeSetting
        // asset, so it is not driven here.)
        [TestCase("2.0.0", false)]
        [TestCase("1.0.0", true)]
        public void CreateGlobalPlaybackGroup_WithEitherHalfOfItsGateClosed_LeavesTheSettingUntouched(string oldVersion, bool alreadyHasGroup)
        {
            RuntimeSetting setting = NewScriptableObject<RuntimeSetting>();
            DefaultPlaybackGroup existing = alreadyHasGroup ? NewScriptableObject<DefaultPlaybackGroup>() : null;
            setting.GlobalPlaybackGroup = existing;

            bool isDirty = InvokeUpgradeStep(ReflectedBroUpdater.CreateGlobalPlaybackGroup, Version.Parse(oldVersion), setting);

            Assert.IsFalse(isDirty);
            Assert.AreSame(existing, setting.GlobalPlaybackGroup, "The global playback group must be left exactly as it was.");
        }
        #endregion
    }
}