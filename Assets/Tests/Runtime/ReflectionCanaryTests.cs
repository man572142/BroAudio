using System;
using System.Collections.Generic;
using System.Reflection;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using NUnit.Framework;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Resolves every name in <see cref="TestAudioLibrary.Reflected"/> against the production type it belongs
    /// to, so a rename or a move fails here, once, naming each member - instead of failing whichever tests
    /// happen to reach it.
    /// <para>
    /// Plain NUnit, no <see cref="BroAudioTestFixture"/>: nothing here plays audio or needs a SoundManager, and
    /// it must stay green while the manager cannot bootstrap, so it can tell the two failures apart.
    /// </para>
    /// </summary>
    public class ReflectionCanaryTests
    {
        private const BindingFlags AnyInstanceMember = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// The production type each nested class of <see cref="TestAudioLibrary.Reflected"/> names members of.
        /// Every nested class must appear here; <see cref="EveryReflectedClass_MapsToAProductionType"/> fails
        /// on one that does not, so a new one cannot escape the canary.
        /// </summary>
        private static readonly Dictionary<Type, Type> ProductionTypes = new Dictionary<Type, Type>
        {
            { typeof(TestAudioLibrary.Reflected.AudioEntity), typeof(AudioEntity) },
            { typeof(TestAudioLibrary.Reflected.SoundSource), typeof(SoundSource) },
            { typeof(TestAudioLibrary.Reflected.DefaultPlaybackGroup), typeof(DefaultPlaybackGroup) },
            { typeof(TestAudioLibrary.Reflected.AudioPlayer), typeof(AudioPlayer) },
            { typeof(TestAudioLibrary.Reflected.SoundManager), typeof(SoundManager) },
        };

#if !PACKAGE_LOCALIZATION
        /// <summary>
        /// Members declared only in a <c>.Localization.cs</c> partial, which compiles to nothing without the
        /// package - their names cannot resolve then, and nothing in that build reaches them.
        /// </summary>
        private static readonly HashSet<string> LocalizationOnlyMembers = new HashSet<string>
        {
            TestAudioLibrary.Reflected.AudioEntity.LocalizedAudio,
            TestAudioLibrary.Reflected.SoundManager.LocalizedRuntime,
        };
#endif

        [Test]
        public void EveryReflectedClass_MapsToAProductionType()
        {
            List<string> unmapped = new List<string>();
            foreach (Type nested in typeof(TestAudioLibrary.Reflected).GetNestedTypes(BindingFlags.Public))
            {
                if (!ProductionTypes.ContainsKey(nested))
                {
                    unmapped.Add(nested.Name);
                }
            }

            Assert.IsEmpty(unmapped,
                "TestAudioLibrary.Reflected has nested classes the canary does not know the production type of - " +
                "add them to ReflectionCanaryTests.ProductionTypes: " + string.Join(", ", unmapped));
        }

        [Test]
        public void EveryReflectedName_ResolvesOnItsProductionType()
        {
            List<string> unresolved = new List<string>();
            int checkedCount = 0;
            foreach (KeyValuePair<Type, Type> pair in ProductionTypes)
            {
                foreach (FieldInfo constant in pair.Key.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!constant.IsLiteral || constant.FieldType != typeof(string))
                    {
                        continue;
                    }

                    string memberName = (string)constant.GetRawConstantValue();
#if !PACKAGE_LOCALIZATION
                    if (LocalizationOnlyMembers.Contains(memberName))
                    {
                        continue;
                    }
#endif
                    checkedCount++;
                    if (!Resolves(pair.Value, memberName))
                    {
                        unresolved.Add($"Reflected.{pair.Key.Name}.{constant.Name} (\"{memberName}\") on {pair.Value.FullName}");
                    }
                }
            }

            Assert.Greater(checkedCount, 0, "Non-vacuity: the canary found no Reflected constants to check.");
            Assert.IsEmpty(unresolved,
                "These reflected names no longer resolve - renamed or moved in production? Update TestAudioLibrary.Reflected " +
                "and its callers: " + string.Join("; ", unresolved));
        }

#if UNITY_EDITOR
        /// <summary>
        /// Where production does publish a name, only under UNITY_EDITOR, the suite's copy must equal it. This
        /// catches a Reflected constant pointing at the wrong member that happens to exist, which resolving
        /// alone cannot.
        /// </summary>
        [Test]
        public void ReflectedNames_MatchTheEditorOnlyProductionNames()
        {
            Assert.AreEqual(SoundSource.NameOf.SoundID, TestAudioLibrary.Reflected.SoundSource.Sound);
            Assert.AreEqual(SoundSource.NameOf.PositionModeProperty, TestAudioLibrary.Reflected.SoundSource.PositionMode);
            Assert.AreEqual(SoundSource.NameOf.PlayOnEnable, TestAudioLibrary.Reflected.SoundSource.PlayOnEnable);
            Assert.AreEqual(SoundSource.NameOf.OnlyPlayOnce, TestAudioLibrary.Reflected.SoundSource.OnlyPlayOnce);
            Assert.AreEqual(SoundSource.NameOf.StopOnDisable, TestAudioLibrary.Reflected.SoundSource.StopOnDisable);
            Assert.AreEqual(SoundSource.NameOf.OverrideFadeOut, TestAudioLibrary.Reflected.SoundSource.OverrideFadeOut);
            Assert.AreEqual(SoundSource.NameOf.Delay, TestAudioLibrary.Reflected.SoundSource.Delay);
            Assert.AreEqual(SoundSource.NameOf.OverrideGroup, TestAudioLibrary.Reflected.SoundSource.OverrideGroup);

            Assert.AreEqual(DefaultPlaybackGroup.NameOf.CombFilteringTime, TestAudioLibrary.Reflected.DefaultPlaybackGroup.CombFilteringTime);

            Assert.AreEqual(AudioEntity.EditorPropertyName.PlaybackGroup, TestAudioLibrary.Reflected.AudioEntity.Group);
            Assert.AreEqual(AudioEntity.EditorPropertyName.MulticlipsPlayMode, TestAudioLibrary.Reflected.AudioEntity.MulticlipsPlayMode);
#if PACKAGE_LOCALIZATION
            Assert.AreEqual(AudioEntity.LocalizationEditorPropertyName.LocalizedAudio, TestAudioLibrary.Reflected.AudioEntity.LocalizedAudio);
#endif
        }
#endif

        /// <summary>
        /// True when <paramref name="memberName"/> names a field (or an auto-property's backing field) or a
        /// method declared on <paramref name="type"/> or a base class - the two ways the suite uses these names
        /// (TestAudioLibrary.GetPrivateField/SetPrivateField and TestAudioLibrary.Reflected.Method).
        /// </summary>
        private static bool Resolves(Type type, string memberName)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (current.GetField(memberName, AnyInstanceMember) != null
                    || current.GetField($"<{memberName}>k__BackingField", AnyInstanceMember) != null
                    || current.GetMember(memberName, MemberTypes.Method, AnyInstanceMember).Length > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}