using System.Collections.Generic;
using NUnit.Framework;

namespace Ami.BroAudio.Editor.Tests
{
    /// <summary>
    /// E0 tier: pure functions on <see cref="BroEditorUtility"/> that touch no Unity state.
    /// No fixture behavior is exercised here, but every test still derives from
    /// <see cref="BroEditorTestFixture"/> per the suite's contract.
    /// </summary>
    public class EditorUtilityPureTests : BroEditorTestFixture
    {
        #region IsInvalidName - error code precedence
        [Test]
        public void IsInvalidName_EmptyOrWhitespace_ReportsIsNullOrEmpty()
        {
            Assert.IsTrue(BroEditorUtility.IsInvalidName("", out ValidationErrorCode code));
            Assert.AreEqual(ValidationErrorCode.IsNullOrEmpty, code);

            Assert.IsTrue(BroEditorUtility.IsInvalidName("   ", out code));
            Assert.AreEqual(ValidationErrorCode.IsNullOrEmpty, code);

            Assert.IsTrue(BroEditorUtility.IsInvalidName(null, out code));
            Assert.AreEqual(ValidationErrorCode.IsNullOrEmpty, code);
        }

        [Test]
        public void IsInvalidName_LeadingDigit_OutranksInvalidWordAndWhitespace()
        {
            // '1' as the first character short-circuits before the invalid-word('-') or whitespace(' ') checks ever run.
            bool isInvalid = BroEditorUtility.IsInvalidName("1-a b", out ValidationErrorCode code);
            Assert.IsTrue(isInvalid);
            Assert.AreEqual(ValidationErrorCode.StartWithNumber, code);
        }

        [Test]
        public void IsInvalidName_InvalidWordBeforeWhitespace_ReportsContainsInvalidWord()
        {
            // '-' at index 1 is hit before the space at index 3, so ContainsInvalidWord wins.
            bool isInvalid = BroEditorUtility.IsInvalidName("a-b c", out ValidationErrorCode code);
            Assert.IsTrue(isInvalid);
            Assert.AreEqual(ValidationErrorCode.ContainsInvalidWord, code);
        }

        [Test]
        public void IsInvalidName_EmbeddedWhitespace_ReportsContainsWhiteSpace()
        {
            // Quirk: IsValidWord(' ') returns true, so a bare space never trips ContainsInvalidWord -
            // it falls through to the separate whitespace check instead.
            bool isInvalid = BroEditorUtility.IsInvalidName("a b", out ValidationErrorCode code);
            Assert.IsTrue(isInvalid);
            Assert.AreEqual(ValidationErrorCode.ContainsWhiteSpace, code);
        }

        [Test]
        public void IsInvalidName_ValidName_ReportsNoError()
        {
            bool isInvalid = BroEditorUtility.IsInvalidName("Valid_Name123", out ValidationErrorCode code);
            Assert.IsFalse(isInvalid);
            Assert.AreEqual(ValidationErrorCode.NoError, code);
        }
        #endregion

        #region Combine
        [Test]
        public void Combine_ThreeArgForm_JoinsWithSlash()
        {
            Assert.AreEqual("a/b/c", BroEditorUtility.Combine("a", "b", "c"));
        }

        [Test]
        public void Combine_ThreeArgForm_TrailingSlashOnInput_YieldsDoubleSlash()
        {
            // Characterized quirk: naked "+ "/" +" concatenation does not strip a trailing slash.
            Assert.AreEqual("a//b/c", BroEditorUtility.Combine("a/", "b", "c"));
        }

        [Test]
        public void Combine_ParamsForm_JoinsWithSlash()
        {
            Assert.AreEqual("a/b/c/d", BroEditorUtility.Combine("a", "b", "c", "d"));
        }

        [Test]
        public void Combine_ParamsForm_TrailingSlashOnInput_YieldsDoubleSlash()
        {
            // Same quirk as the 3-arg form, characterized rather than fixed.
            Assert.AreEqual("a//b", BroEditorUtility.Combine("a/", "b"));
        }

        [Test]
        public void Combine_ParamsForm_SingleElement_ReturnsItUnchanged()
        {
            Assert.AreEqual("a", BroEditorUtility.Combine("a"));
        }

        [Test]
        public void Combine_ParamsForm_NoElements_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, BroEditorUtility.Combine());
        }
        #endregion

        #region ForeachConcreteDrawedProperty / DrawedProperty.Contains
        private static readonly DrawedProperty[] ConcreteDrawedProperties =
        {
            DrawedProperty.Volume, DrawedProperty.PlaybackPosition, DrawedProperty.Fade, DrawedProperty.ClipPreview,
            DrawedProperty.MasterVolume, DrawedProperty.Loop, DrawedProperty.Priority, DrawedProperty.SpatialSettings,
            DrawedProperty.Pitch, DrawedProperty.PlaybackGroup,
        };

        [Test]
        public void ForeachConcreteDrawedProperty_VisitsEveryConcreteFlagExactlyOnce()
        {
            var visited = new List<DrawedProperty>();
            BroEditorUtility.ForeachConcreteDrawedProperty(flag => visited.Add(flag));

            Assert.AreEqual(ConcreteDrawedProperties.Length, visited.Count,
                "Iteration visited a different number of flags than the concrete set - it stopped early, ran long, or duplicated an entry.");
            CollectionAssert.AreEquivalent(ConcreteDrawedProperties, visited);
            CollectionAssert.AllItemsAreUnique(visited);
        }

        [Test]
        public void DrawedPropertyAll_IsExactlyTheConcreteFlagsCombined()
        {
            // The stop condition of ForeachConcreteDrawedProperty is All itself. If a new flag is added
            // without folding it into All, iteration stops short of it and nothing else in the suite notices.
            int combined = 0;
            foreach (DrawedProperty flag in ConcreteDrawedProperties)
            {
                combined |= (int)flag;
            }
            Assert.AreEqual((int)DrawedProperty.All, combined,
                "DrawedProperty.All no longer equals the concrete flags combined - a flag was added without updating All.");
        }

        [Test]
        public void Contains_SingleFlag_MatchesItself()
        {
            Assert.IsTrue(DrawedProperty.Fade.Contains(DrawedProperty.Fade));
        }

        [Test]
        public void Contains_CompositeFlags_MatchesEachMember()
        {
            DrawedProperty composite = DrawedProperty.Fade | DrawedProperty.Volume;
            Assert.IsTrue(composite.Contains(DrawedProperty.Fade));
            Assert.IsTrue(composite.Contains(DrawedProperty.Volume));
        }

        [Test]
        public void Contains_NonMatchingFlag_ReturnsFalse()
        {
            Assert.IsFalse(DrawedProperty.Fade.Contains(DrawedProperty.Pitch));
        }
        #endregion
    }
}
