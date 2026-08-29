using System.IO;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// The only tests in this suite with a real disk footprint. Everything they create lives under
    /// <see cref="BroEditorTestFixture.TempFolder"/>, which TearDown deletes.
    /// <para>
    /// The redirection that makes that true is <c>EditorSetting.AssetOutputPath</c>: AudioAssetEditor writes
    /// new entities into that path, not next to the asset they belong to. The fixture snapshot/restore puts
    /// the developer's real output path back afterwards.
    /// </para>
    /// <para>
    /// <c>BroUserDataGenerator.CheckAndGenerateUserData</c> is deliberately NOT covered here — it writes into
    /// the shipped package's own Resources folders and completes on an async ResourceRequest callback, so it
    /// cannot be exercised without breaking the isolation contract. See Docs/TEST_INVENTORY.md.
    /// </para>
    /// </summary>
    public class AssetWritingTests : BroEditorTestFixture
    {
        private const string TempResourcesFolder = TempFolder + "/Resources";

        /// <summary>
        /// CreateScriptableObjectIfNotExist checks for existence through Resources.Load, so the not-exist
        /// half only engages inside a Resources folder. Tests that need the second-call path use this.
        /// </summary>
        private string EnsureTempResourcesFolder()
        {
            EnsureTempFolder();
            if (!AssetDatabase.IsValidFolder(TempResourcesFolder))
            {
                AssetDatabase.CreateFolder(TempFolder, "Resources");
                AssetDatabase.Refresh();
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

            // New entities land in AssetOutputPath, not beside the asset they belong to. Redirect it into the
            // temp folder; the fixture restores the developer's real path in TearDown.
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
            Assert.AreEqual(EditorSetting.FactorySettings.ShowVUColorOnVolumeSlider, created.ShowVUColorOnVolumeSlider,
                "The new asset did not get factory settings.");
            Assert.AreEqual(ConcreteAudioTypes.Length, created.AudioTypeSettings.Count,
                "ResetToFactorySettings was not applied to the new EditorSetting.");
        }

        [Test]
        public void CreateScriptableObjectIfNotExist_SecondCall_ReturnsTheExistingAsset()
        {
            string path = EnsureTempResourcesFolder() + "/BroTestRuntimeSetting.asset";

            var first = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);
            AssetDatabase.Refresh();
            var second = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);

            Assert.AreSame(first, second, "The second call created a new instance instead of returning the existing asset.");
        }

        [Test]
        public void CreateScriptableObjectIfNotExist_OutsideAResourcesFolder_CreatesANewInstanceEveryTime()
        {
            // Characterized, not fixed. The existence check is Resources.Load-based rather than
            // AssetDatabase-based, so outside a Resources folder the guard never fires and the asset is
            // silently overwritten. Every production caller passes a Resources path, so this stays latent.
            string path = EnsureTempFolder() + "/BroTestNotInResources.asset";

            var first = BroEditorUtility.CreateScriptableObjectIfNotExist<RuntimeSetting>(path);
            AssetDatabase.Refresh();
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
        public void Verify_ValidName_ClearsTheInstruction()
        {
            NewAssetOnDisk("BroTestValidName", out AudioAssetEditor editor);

            editor.Verify();

            Assert.AreEqual(default(Instruction), editor.CurrInstruction);
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
