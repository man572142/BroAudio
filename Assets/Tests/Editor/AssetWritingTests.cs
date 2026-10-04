using System.IO;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Tests with a real disk footprint; everything must land under
    /// <see cref="BroEditorTestFixture.TempFolder"/>, which TearDown deletes.
    /// <para>
    /// Don't cover <c>BroUserDataGenerator.CheckAndGenerateUserData</c> here: it writes into the package's own
    /// Resources folders on an async callback, which breaks the isolation contract.
    /// </para>
    /// </summary>
    public class AssetWritingTests : BroEditorTestFixture
    {
        private const string TempResourcesFolder = TempFolder + "/Resources";

        /// <summary>The existence check uses Resources.Load, so it only engages inside a Resources folder.</summary>
        private string EnsureTempResourcesFolder()
        {
            EnsureTempFolder();
            if (!AssetDatabase.IsValidFolder(TempResourcesFolder))
            {
                AssetDatabase.CreateFolder(TempFolder, "Resources");
            }
            return TempResourcesFolder;
        }

        private AudioAsset NewAssetOnDisk(string assetName, out AudioAssetEditor editor)
        {
            EnsureTempFolder();
            var asset = ScriptableObject.CreateInstance<AudioAsset>();
            AssetDatabase.CreateAsset(asset, TempFolder + "/" + assetName + ".asset");

            editor = Track(UnityEditor.Editor.CreateEditor(asset, typeof(AudioAssetEditor))) as AudioAssetEditor;
            editor.SetData(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)), assetName);

            // New entities land in AssetOutputPath, not beside their asset; redirect it into the temp folder.
            BroEditorUtility.EditorSetting.AssetOutputPath = TempFolder;
            return asset;
        }

        #region CreateScriptableObjectIfNotExist
        [Test]
        public void CreateScriptableObjectIfNotExist_CreatesTheAssetWithFactorySettings()
        {
            string path = EnsureTempResourcesFolder() + "/BroTestEditorSetting.asset";

            var created = BroEditorUtility.CreateScriptableObjectIfNotExist<EditorSetting>(path);

            Assert.IsTrue(created, "No asset was created.");
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<EditorSetting>(path), "The asset is not on disk at the requested path.");
            // Don't assert a bool: they're field-initialised to the factory values, so they'd pass with the
            // reset removed. These collections are null until the reset populates them.
            Assert.IsNotNull(created.AudioTypeSettings, "ResetToFactorySettings was not applied to the new EditorSetting.");
            Assert.AreEqual(ConcreteAudioTypes.Length, created.AudioTypeSettings.Count,
                "The new asset did not get one AudioTypeSetting per concrete audio type.");
            Assert.IsNotEmpty(created.SpectrumBandColors, "The new asset did not get the default spectrum colours.");
        }

        [Test]
        public void CreateScriptableObjectIfNotExist_SecondCall_ReturnsTheExistingAsset()
        {
            string path = EnsureTempResourcesFolder() + "/BroTestRuntimeSetting.asset";

            var first = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);
            // Don't use AssetDatabase.Refresh(): it would import any .cs saved mid-run, and the domain reload
            // kills the run.
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var second = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);

            Assert.AreSame(first, second, "The second call created a new instance instead of returning the existing asset.");
        }

        [Test]
        [Category("Finding_31")]
        public void CreateScriptableObjectIfNotExist_OutsideAResourcesFolder_CreatesANewInstanceEveryTime()
        {
            // Pins TEST_FINDINGS #31.
            string path = EnsureTempFolder() + "/BroTestNotInResources.asset";

            var first = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport); // same scoped import as above
            var second = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);

            Assert.AreNotSame(first, second, "The Resources-based existence check now finds assets outside a Resources folder.");
        }
        #endregion

        #region AudioAssetEditor
        [Test]
        public void CreateNewEntity_WritesTheEntityAsset_AndNamesIt()
        {
            NewAssetOnDisk("BroTestLibrary", out AudioAssetEditor editor);

            (AudioEntity entity, AudioEntityEditor entityEditor) = editor.CreateNewEntity("Footstep", BroAudioType.SFX);
            Track(entityEditor);

            Assert.IsTrue(entity, "No entity was created.");
            Assert.AreEqual("Footstep", entity.Name);
            Assert.AreEqual(BroAudioType.SFX, entity.AudioType);

            string path = AssetDatabase.GetAssetPath(entity);
            Assert.IsNotEmpty(path, "The entity was never written to disk.");
            StringAssert.StartsWith(TempFolder, path, "The entity escaped the temp folder.");
        }

        [Test]
        public void CreateNewEntity_Twice_GivesTheSecondAUniqueName()
        {
            NewAssetOnDisk("BroTestUniqueLibrary", out AudioAssetEditor editor);

            (AudioEntity first, AudioEntityEditor firstEditor) = editor.CreateNewEntity("Footstep", BroAudioType.SFX);
            (AudioEntity second, AudioEntityEditor secondEditor) = editor.CreateNewEntity("Footstep", BroAudioType.SFX);
            Track(firstEditor);
            Track(secondEditor);

            Assert.AreNotEqual(first.Name, second.Name, "GenerateUniqueAssetPath did not disambiguate the second entity.");
            Assert.AreNotEqual(AssetDatabase.GetAssetPath(first), AssetDatabase.GetAssetPath(second),
                "The second entity overwrote the first.");
        }

        [Test]
        public void SetAssetName_RenamesTheFile_AndWritesTheBackingField()
        {
            AudioAsset asset = NewAssetOnDisk("BroTestOldName", out AudioAssetEditor editor);

            editor.SetAssetName("BroTestNewName");

            Assert.AreEqual("BroTestNewName", asset.AssetName, "The AssetName backing field was not written.");
            Assert.AreEqual("BroTestNewName", Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(asset)),
                "The asset file itself was not renamed.");
        }

        [Test]
        public void Verify_ValidName_ClearsAPreviouslyReportedInstruction()
        {
            // CurrInstruction starts at default, so dirty it with a bad name first.
            NewAssetOnDisk("BroTestValidName", out AudioAssetEditor editor);
            editor.SetData(string.Empty, "1StartsWithANumber");
            editor.Verify();
            Assert.AreNotEqual(default(Instruction), editor.CurrInstruction, "Setup failed: the bad name did not set an instruction.");

            editor.SetData(string.Empty, "BroTestValidName");
            editor.Verify();

            Assert.AreEqual(default(Instruction), editor.CurrInstruction, "A valid name did not clear the reported instruction.");
        }

        [Test]
        public void Verify_InvalidName_ReportsTheMatchingInstruction()
        {
            NewAssetOnDisk("BroTestInvalidName", out AudioAssetEditor editor);
            editor.SetData(string.Empty, "1StartsWithANumber");

            editor.Verify();

            Assert.AreEqual(Instruction.AssetNaming_StartWithNumber, editor.CurrInstruction,
                "Verify did not map the name-validation error code to its instruction.");
        }
        #endregion
    }
}