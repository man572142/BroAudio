using System;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pins <see cref="BroEditorUtility.GetMaxAcceptableClipCount"/>, <see cref="BroEditorUtility.TryParseCoreData"/>
    /// and the version gates in <see cref="BroUpdater"/>. <c>Process</c> touches the project, so each private
    /// step is invoked by reflection against in-memory settings; <c>CreateGlobalPlaybackGroup</c>'s open
    /// branch writes an asset to disk, so only its closed branches are driven.
    /// </summary>
    public class CoreDataAndUpdaterTests : BroEditorTestFixture
    {
        #region GetMaxAcceptableClipCount
        // ReorderableClips disables rows at or past this count: Single plays one clip, Chained intro/loop/outro.
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

        // Pins TEST_FINDINGS #69.
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

        [Test]
        public void VersionGates_AreThePlaybackGroupAndAssetBasedSoundIdReleases()
        {
            Assert.AreEqual(new Version(2, 0, 0), ReflectedBroUpdater.VersionProperty(ReflectedBroUpdater.PlaybackGroupFirstReleasedVersion));
            Assert.AreEqual(new Version(3, 1, 0), ReflectedBroUpdater.VersionProperty(ReflectedBroUpdater.AssetBasedSoundIDFirstReleasedVersion));
        }

        // Strict `<`: an install already on 2.0.0 is left alone.
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

        // The gate needs both an old install AND no global group yet.
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