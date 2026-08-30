using System;
using System.Collections.Generic;
using System.Linq;
using Ami.BroAudio.Editor.Setting;
using Ami.BroAudio.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Data-integrity checks on the assets that actually ship in Editor/Resources — BroInstruction and
    /// EditorSetting — rather than on in-memory fixtures. BroInstruction's <c>_dictionary</c> field is private
    /// serialized data; it is read here via <see cref="SerializedObject"/>, never mutated.
    /// </summary>
    public class ShippedDataTests : BroEditorTestFixture
    {
        private static BroInstruction LoadShippedInstructionAsset()
        {
            var asset = Resources.Load<BroInstruction>(BroName.InstructionFileName);
            Assert.IsTrue(asset, $"Could not load the shipped {BroName.InstructionFileName} asset from Resources.");
            return asset;
        }

        /// <summary>Read-only walk of the private _dictionary field via SerializedObject.</summary>
        private static List<(int key, string value)> ReadDictionaryEntries(BroInstruction asset)
        {
            var entries = new List<(int, string)>();
            var so = new SerializedObject(asset);
            SerializedProperty dictProp = so.FindProperty("_dictionary");
            Assert.IsNotNull(dictProp, "BroInstruction's serialized field is no longer named '_dictionary' - update this test.");

            for (int i = 0; i < dictProp.arraySize; i++)
            {
                SerializedProperty element = dictProp.GetArrayElementAtIndex(i);
                int key = element.FindPropertyRelative("Key").intValue;
                string value = element.FindPropertyRelative("Value").stringValue;
                entries.Add((key, value));
            }
            return entries;
        }

        [Test]
        public void EveryInstructionEnumValue_ResolvesToNonMissingText()
        {
            var missing = new List<Instruction>();
            foreach (Instruction instruction in Enum.GetValues(typeof(Instruction)))
            {
                string text = Instructions.GetText(instruction);
                if (string.IsNullOrEmpty(text) || text == BroInstructionHelper.MissingText)
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
            EditorSetting setting = BroEditorUtility.EditorSetting;
            setting.ResetToFactorySettings();

            Assert.AreEqual(setting.SpectrumBandColors[0], setting.GetSpectrumColor(0));

            int lastIndex = setting.SpectrumBandColors.Count - 1;
            Assert.AreEqual(setting.SpectrumBandColors[lastIndex], setting.GetSpectrumColor(lastIndex));
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
