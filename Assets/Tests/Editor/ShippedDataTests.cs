using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ami.BroAudio.Editor.Setting;
using Ami.BroAudio.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Data-integrity checks on the data BroAudio ships - the BroInstruction asset and EditorSetting's factory
    /// values - rather than on in-memory fixtures. BroInstruction's <c>_dictionary</c> field is private
    /// serialized data; it is read here via <see cref="SerializedObject"/>, never mutated.
    /// <para>
    /// The BroInstruction checked is the COMMITTED one under <c>Resources~/Editor</c>, not the
    /// <c>Editor/Resources</c> copy <c>Resources.Load</c> would find: that copy is gitignored and only created
    /// from <c>Resources~</c> when absent, so it can be stale in either direction. <c>Resources~</c> is hidden
    /// from the AssetDatabase (the trailing <c>~</c>), so the file is deserialized straight from disk.
    /// </para>
    /// </summary>
    public class ShippedDataTests : BroEditorTestFixture
    {
        /// <summary>
        /// The committed asset. This suite runs in the development project, where the package lives at
        /// Assets/BroAudio (see <see cref="BroEditorTestFixture.TempFolder"/>'s note on the shipped subtree).
        /// </summary>
        private static string CommittedInstructionAssetPath =>
            Path.Combine(Application.dataPath, "BroAudio", "Resources~", "Editor", BroName.InstructionFileName + ".asset");

        private BroInstruction LoadShippedInstructionAsset()
        {
            string path = CommittedInstructionAssetPath;
            Assert.IsTrue(File.Exists(path), $"The committed {BroName.InstructionFileName} asset is missing at {path}.");

            // Loaded outside the AssetDatabase, so the objects are not assets: tracked and destroyed in TearDown.
            BroInstruction asset = null;
            foreach (UnityEngine.Object loaded in InternalEditorUtility.LoadSerializedFileAndForget(path))
            {
                Track(loaded);
                if (!asset && loaded is BroInstruction instruction)
                {
                    asset = instruction;
                }
            }
            Assert.IsTrue(asset, $"{path} did not deserialize to a {nameof(BroInstruction)}.");
            return asset;
        }

        /// <summary>
        /// Read-only walk of the private _dictionary field via SerializedObject. Fails on zero entries: every
        /// check built on this list would otherwise pass on an empty or unreadable asset.
        /// </summary>
        private static List<(int key, string value)> ReadDictionaryEntries(BroInstruction asset)
        {
            var entries = new List<(int, string)>();
            var so = new SerializedObject(asset);
            SerializedProperty dictProp = so.FindProperty(BroInstruction.NameOf.Dictionary);
            Assert.IsNotNull(dictProp, $"BroInstruction's serialized field is no longer named '{BroInstruction.NameOf.Dictionary}' - update this test.");

            for (int i = 0; i < dictProp.arraySize; i++)
            {
                SerializedProperty element = dictProp.GetArrayElementAtIndex(i);
                int key = element.FindPropertyRelative(BroInstruction.NameOf.Key).intValue;
                string value = element.FindPropertyRelative(BroInstruction.NameOf.Value).stringValue;
                entries.Add((key, value));
            }
            Assert.IsNotEmpty(entries, $"The {BroName.InstructionFileName} asset has no dictionary entries - every key-set check would pass vacuously.");
            return entries;
        }

        [Test]
        public void EveryInstructionEnumValue_HasNonEmptyTextInTheShippedAsset()
        {
            var entries = ReadDictionaryEntries(LoadShippedInstructionAsset());
            var textByKey = new Dictionary<int, string>();
            foreach ((int key, string value) entry in entries)
            {
                textByKey[entry.key] = entry.value;
            }

            var missing = new List<Instruction>();
            foreach (Instruction instruction in Enum.GetValues(typeof(Instruction)))
            {
                if (!textByKey.TryGetValue((int)instruction, out string text) || string.IsNullOrEmpty(text))
                {
                    missing.Add(instruction);
                }
            }

            Assert.IsEmpty(missing,
                $"Instruction value(s) with no shipped text: [{string.Join(", ", missing)}]. " +
                "Fix the asset to make this green; do not add an exclusion list.");
        }

        [Test]
        public void BroInstructionAsset_HasNoDuplicateKeys()
        {
            // Not expected to be red today, but worth its own test: BroInstruction.OnEnable() builds its
            // dictionary with Dictionary.Add(), which THROWS on a duplicate key. That leaves _actualDict
            // half-built, and every instruction after the throw point (plus every instruction ever, since the
            // exception is swallowed by Unity's asset-load pipeline) silently resolves to "??????????" instead
            // of failing anywhere visible. This is the failure mode nobody would diagnose from the symptom.
            var asset = LoadShippedInstructionAsset();
            var entries = ReadDictionaryEntries(asset);

            var duplicateKeys = entries
                .GroupBy(e => e.key)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.IsEmpty(duplicateKeys,
                $"Duplicate key(s) in BroInstruction's asset: [{string.Join(", ", duplicateKeys)}]. A duplicate " +
                "key makes BroInstruction.OnEnable()'s Dictionary.Add() throw, half-building _actualDict and " +
                "silently turning every GetText() call into '??????????' with no visible error.");
        }

        [Test]
        public void BroInstructionAsset_EveryKeyIsADefinedEnumValue()
        {
            var asset = LoadShippedInstructionAsset();
            var entries = ReadDictionaryEntries(asset);

            var undefinedKeys = entries
                .Select(e => e.key)
                .Where(key => !Enum.IsDefined(typeof(Instruction), key))
                .Distinct()
                .ToList();

            Assert.IsEmpty(undefinedKeys,
                $"Asset key(s) belonging to no defined Instruction member: [{string.Join(", ", undefinedKeys)}]. " +
                "Remove the stale entry to make this green; do not add an exclusion list.");
        }

        [Test]
        public void ResetToFactorySettings_CreatesAnAudioTypeSettingForEveryConcreteAudioType()
        {
            EditorSetting setting = BroEditorUtility.EditorSetting;
            setting.ResetToFactorySettings();

            // Compared against the factory colour constants rather than against TryGetAudioTypeSetting's
            // own output - GetAudioTypeColor is defined in terms of TryGetAudioTypeSetting, so checking
            // one against the other cannot detect a wrong colour or a wrong type-to-colour pairing.
            var factoryColors = new Dictionary<BroAudioType, string>
            {
                { BroAudioType.Music, EditorSetting.FactorySettings.MusicColor },
                { BroAudioType.UI, EditorSetting.FactorySettings.UIColor },
                { BroAudioType.Ambience, EditorSetting.FactorySettings.AmbienceColor },
                { BroAudioType.SFX, EditorSetting.FactorySettings.SFXColor },
                { BroAudioType.VoiceOver, EditorSetting.FactorySettings.VoiceOverColor },
            };

            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                Assert.IsTrue(setting.TryGetAudioTypeSetting(audioType, out var typeSetting),
                    $"ResetToFactorySettings did not create an AudioTypeSetting for {audioType}.");
                Assert.AreEqual(audioType, typeSetting.AudioType);

                ColorUtility.TryParseHtmlString(factoryColors[audioType], out Color expected);
                Assert.AreEqual(expected, setting.GetAudioTypeColor(audioType),
                    $"{audioType} did not get its factory colour.");
            }
        }

        [Test]
        public void GetSpectrumColor_InRange_ReturnsTheStoredColor()
        {
            // Expected values come from this test, not from the list GetSpectrumColor reads: a lookup that
            // returned the wrong index, or the factory list regardless of what is stored, would still pass a
            // check that read both sides from SpectrumBandColors. An in-memory instance keeps the write off the
            // project's EditorSetting.
            EditorSetting setting = NewScriptableObject<EditorSetting>();
            setting.SpectrumBandColors = new List<Color> { Color.red, Color.green, Color.blue };

            Assert.AreEqual(Color.red, setting.GetSpectrumColor(0));
            Assert.AreEqual(Color.green, setting.GetSpectrumColor(1));
            Assert.AreEqual(Color.blue, setting.GetSpectrumColor(2));
        }

        [Test]
        public void ResetToFactorySettings_SpectrumColors_AreTheTenFactoryBands()
        {
            EditorSetting setting = NewScriptableObject<EditorSetting>();
            setting.ResetToFactorySettings();

            // Factory literals, written out here rather than read back from the list under test. The factory
            // bands all carry alpha 150/256.
            const float FactoryBandAlpha = 150f / 256f;
            ColorUtility.TryParseHtmlString("#7CAEFF", out Color first);
            ColorUtility.TryParseHtmlString("#6CFF75", out Color last);
            first.a = FactoryBandAlpha;
            last.a = FactoryBandAlpha;
            Assert.AreEqual(10, setting.SpectrumBandColors.Count);
            Assert.AreEqual(first, setting.GetSpectrumColor(0));
            Assert.AreEqual(last, setting.GetSpectrumColor(9));
        }

        [Test]
        public void GetSpectrumColor_OutOfRange_ReturnsTheFallbackColor()
        {
            EditorSetting setting = BroEditorUtility.EditorSetting;
            setting.ResetToFactorySettings();

            Color fallback = new Color(1f, 1f, 1f, 0.2f);
            Assert.AreEqual(fallback, setting.GetSpectrumColor(-1));
            Assert.AreEqual(fallback, setting.GetSpectrumColor(setting.SpectrumBandColors.Count));
        }
    }
}