using NUnit.Framework;
using Ami.Extension;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// Pure-string coverage for the <see cref="EditorScriptingExtension"/> reflection-naming helpers:
    /// <c>GetBackingFieldName</c> (property name -> compiler-generated backing field name) and
    /// <c>GetFieldName</c> (property name -> the suite's own <c>_camelCase</c> field-name guess). No IMGUI
    /// context is touched — every target here is plain string arithmetic.
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
        [Category("Finding_23")]
        public void GetFieldName_ReplacesEveryOccurrenceOfTheLeadingChar_NotJustTheFirst()
        {
            // Characterizes TEST_FINDINGS #23: the implementation does
            // propertyName.Replace(firstChar, lowerFirstChar) — a global string.Replace(char,char) — not a
            // single-position substitution. Any later occurrence of the same uppercase leading letter
            // elsewhere in the name is lowercased too.
            Assert.AreEqual("_foof", EditorScriptingExtension.GetFieldName("FooF"));
        }

        [Test]
        public void GetFieldName_NullOrEmpty_PassesThroughUnchanged()
        {
            Assert.IsNull(EditorScriptingExtension.GetFieldName(null));
            Assert.AreEqual(string.Empty, EditorScriptingExtension.GetFieldName(string.Empty));
        }
    }
}
