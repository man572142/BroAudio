using NUnit.Framework;
using Ami.Extension;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// <see cref="EditorScriptingExtension"/>'s reflection-naming helpers: plain string arithmetic, no IMGUI context.
    /// </summary>
    public class EditorReflectionNamingTests : BroEditorTestFixture
    {
        [Test]
        public void GetBackingFieldName_WrapsPropertyNameInCompilerGeneratedPattern()
        {
            Assert.AreEqual("<Foo>k__BackingField", EditorScriptingExtension.GetBackingFieldName("Foo"));
        }

        [Test]
        public void GetFieldName_LowercasesLeadingCharAndPrefixesUnderscore()
        {
            Assert.AreEqual("_foo", EditorScriptingExtension.GetFieldName("Foo"));
        }

        [Test]
        public void GetFieldName_AlreadyLowercaseLeadingChar_StillGetsPrefixed()
        {
            Assert.AreEqual("_foo", EditorScriptingExtension.GetFieldName("foo"));
        }

        [Test]
        public void GetFieldName_LowercasesOnlyTheLeadingChar()
        {
            Assert.AreEqual("_fooF", EditorScriptingExtension.GetFieldName("FooF"));
        }

        [Test]
        public void GetFieldName_NullOrEmpty_PassesThroughUnchanged()
        {
            Assert.IsNull(EditorScriptingExtension.GetFieldName(null));
            Assert.AreEqual(string.Empty, EditorScriptingExtension.GetFieldName(string.Empty));
        }
    }
}
