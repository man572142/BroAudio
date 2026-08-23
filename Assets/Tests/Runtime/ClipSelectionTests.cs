using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// EditMode-pure characterization tests for the clip selection strategies and their supporting
    /// <see cref="AudioEntity"/> helpers. No SoundManager, no Play Mode: strategies and clip arrays
    /// are constructed directly. Deliberately does not derive from BroAudioTestFixture.
    /// </summary>
    public class ClipSelectionTests
    {
        private readonly System.Collections.Generic.List<Object> _createdObjects = new System.Collections.Generic.List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _createdObjects)
            {
                if (obj)
                {
                    Object.DestroyImmediate(obj);
                }
            }
            _createdObjects.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _createdObjects.Add(obj);
            return obj;
        }

        private AudioClip NewClip(string name = "Clip") => Track(TestAudioLibrary.CreateClip(name: name));

        /// <summary>Builds <paramref name="count"/> clips, all with a real AudioClip assigned (IsSet == true).</summary>
        private BroAudioClip[] NewSetClips(int count)
        {
            var clips = new BroAudioClip[count];
            for (int i = 0; i < count; i++)
            {
                clips[i] = TestAudioLibrary.CreateBroClip(NewClip($"Clip{i}"));
            }
            return clips;
        }

        /// <summary>A BroAudioClip with no AudioClip/addressable assigned — non-null but IsSet == false.</summary>
        private static BroAudioClip UnsetClip() => TestAudioLibrary.CreateBroClip(null);

        private AudioEntity NewEntity() => Track(TestAudioLibrary.CreateEntity("TestEntity", BroAudioType.SFX));

        #region SingleClipStrategy (0.1)

        [Test]
        public void SelectClip_WithSetClips_AlwaysReturnsFirstClip()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SingleClipStrategy();

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.AreEqual(0, index);
            Assert.AreSame(clips[0], result);
        }

        [Test]
        public void SelectClip_WithNullClipsArray_LogsErrorAndReturnsNull()
        {
            var strategy = new SingleClipStrategy();
            LogAssert.Expect(LogType.Error, new Regex("clips array is empty or null"));

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(0, index);
        }

        [Test]
        public void SelectClip_WithNullFirstClipReference_LogsErrorAndReturnsNull()
        {
            var clips = new BroAudioClip[] { null };
            var strategy = new SingleClipStrategy();
            LogAssert.Expect(LogType.Error, new Regex("first clip is null"));

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
        }

        [Test]
        public void SelectClip_WithUnsetFirstClip_ReturnsUnsetClipWithoutLoggingError()
        {
            // characterizes: unlike Sequence/Shuffle, Single only rejects a reference-null entry
            // (clips[0] == null). An unset BroAudioClip (non-null, IsSet == false) passes through
            // unchecked and is returned as-is; the failure is deferred to whatever plays it.
            var clips = new[] { UnsetClip() };
            var strategy = new SingleClipStrategy();

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.AreSame(clips[0], result);
            Assert.IsFalse(result.IsSet);
        }

        #endregion

        #region SequenceClipStrategy (0.1)

        [Test]
        public void SelectClip_Repeatedly_CyclesThroughClipsAndWrapsToStart()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SequenceClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i0);
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i1);
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i2);
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i3);

            Assert.AreEqual(0, i0);
            Assert.AreEqual(1, i1);
            Assert.AreEqual(2, i2);
            Assert.AreEqual(0, i3, "Sequence should wrap back to index 0 after the last clip.");
        }

        [Test]
        public void SelectClip_WithSingleClip_AlwaysReturnsIndexZero()
        {
            BroAudioClip[] clips = NewSetClips(1);
            var strategy = new SequenceClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i0);
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int i1);

            Assert.AreEqual(0, i0);
            Assert.AreEqual(0, i1);
        }

        [Test]
        public void SelectClip_OnFirstCallWithUnsetFirstClip_LogsErrorAndReturnsNegativeOne()
        {
            var clips = new[] { UnsetClip(), TestAudioLibrary.CreateBroClip(NewClip()) };
            var strategy = new SequenceClipStrategy();
            LogAssert.Expect(LogType.Error, new Regex("No valid clip is set for sequence index"));

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(-1, index);
        }

        [Test]
        public void SelectClip_WithUnsetClipMidSequence_LogsErrorThenRestartsFromZero()
        {
            var clips = new[] { TestAudioLibrary.CreateBroClip(NewClip()), UnsetClip(), TestAudioLibrary.CreateBroClip(NewClip()) };
            var strategy = new SequenceClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(0), out int first);
            Assert.AreEqual(0, first);

            LogAssert.Expect(LogType.Error, new Regex("No valid clip is set for sequence index"));
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int second);
            Assert.AreEqual(-1, second, "The hole at index 1 should fail this call.");

            // characterizes: the failure resets the cursor to -1, so the following call restarts the
            // scan from clips[0] rather than resuming at the clip after the hole (index 2).
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int third);
            Assert.AreEqual(0, third);
        }

        [Test]
        public void Reset_RestartsDefaultSequenceFromZero()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SequenceClipStrategy();
            strategy.SelectClip(clips, new ClipSelectionContext(0), out _);
            strategy.SelectClip(clips, new ClipSelectionContext(0), out _);

            strategy.Reset();
            strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.AreEqual(0, index);
        }

        [Test]
        public void SelectClip_WithTwoSequenceIds_AdvancesIndependently()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SequenceClipStrategy();
            var contextA = new ClipSelectionContext(0) { SequenceId = "a" };
            var contextB = new ClipSelectionContext(0) { SequenceId = "b" };

            strategy.SelectClip(clips, contextA, out int a0);
            strategy.SelectClip(clips, contextA, out int a1);
            strategy.SelectClip(clips, contextB, out int b0);

            Assert.AreEqual(0, a0);
            Assert.AreEqual(1, a1);
            Assert.AreEqual(0, b0, "Sequence 'b' should start fresh at index 0, unaffected by 'a'.");
        }

        [Test]
        public void SelectClip_WithNullSequenceId_SharesDefaultCursor()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SequenceClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(0), out int viaDefault);
            strategy.SelectClip(clips, new ClipSelectionContext(0) { SequenceId = null }, out int viaExplicitNull);

            Assert.AreEqual(0, viaDefault);
            Assert.AreEqual(1, viaExplicitNull, "An explicit null SequenceId routes to the same default cursor, not a distinct one.");
        }

        [Test]
        public void Reset_WithSequenceId_OnlyResetsThatNamedCursor()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new SequenceClipStrategy();
            var contextA = new ClipSelectionContext(0) { SequenceId = "a" };
            var contextB = new ClipSelectionContext(0) { SequenceId = "b" };
            strategy.SelectClip(clips, contextA, out _); // a -> 0
            strategy.SelectClip(clips, contextA, out _); // a -> 1
            strategy.SelectClip(clips, contextB, out _); // b -> 0
            strategy.SelectClip(clips, contextB, out _); // b -> 1

            strategy.Reset("a");

            strategy.SelectClip(clips, contextA, out int aAfterReset);
            strategy.SelectClip(clips, contextB, out int bAfterReset);

            Assert.AreEqual(0, aAfterReset, "'a' was reset and should restart.");
            Assert.AreEqual(2, bAfterReset, "'b' was untouched and should keep advancing.");
        }

        #endregion

        #region ShuffleClipStrategy (0.1)

        [Test]
        public void SelectClip_AlwaysReturnsASetClipFromTheArrayInValidRange()
        {
            BroAudioClip[] clips = NewSetClips(4);
            var strategy = new ShuffleClipStrategy();

            for (int i = 0; i < 50; i++)
            {
                IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.GreaterOrEqual(index, 0);
                Assert.Less(index, clips.Length);
                Assert.Contains(result, clips, "The returned clip must come from the given array.");
                Assert.IsTrue(result.IsSet);
            }
        }

        [Test]
        public void SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip()
        {
            // characterizes: in the fallback scan, the loop keeps advancing `index` while probing for an
            // unused clip, then returns `result` — the clip found at the *earlier* index. So `clips[index]`
            // is not necessarily the clip that was returned. Same class of defect as
            // SelectClip_AboveEveryThreshold_LeavesOutIndexStale in VelocityClipStrategy.
            // See Docs/TEST_FINDINGS.md. Only the out-index overload is affected, and its only consumers
            // are Editor preview/inspector code, so runtime playback picks the right clip regardless.
            BroAudioClip[] clips = NewSetClips(4);
            var strategy = new ShuffleClipStrategy();

            bool sawDisagreement = false;
            for (int i = 0; i < 200 && !sawDisagreement; i++)
            {
                IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                sawDisagreement = !ReferenceEquals(clips[index], result);
            }

            Assert.IsTrue(sawDisagreement,
                "Expected the fallback scan to return a clip that does not match its own out index. " +
                "If this now fails, the mismatch was fixed — update Docs/TEST_FINDINGS.md and delete this test.");
        }

        [Test]
        public void SelectClip_WithSingleClip_AlwaysReturnsSameClipAcrossCalls()
        {
            // characterizes: with only one clip, the fallback scan always lands back on the same
            // index (there's nowhere else to go), so Shuffle degrades to "always the same clip"
            // without erroring — this is real behavior, not an error path.
            BroAudioClip[] clips = NewSetClips(1);
            var strategy = new ShuffleClipStrategy();

            for (int i = 0; i < 10; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.AreEqual(0, index);
            }
        }

        [Test]
        public void SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent()
        {
            // characterizes: MulticlipsPlayMode.Shuffle's doc comment promises "not repeating with
            // the previous one", but ShuffleClipStrategy.Use() only ever rejects a pick that equals
            // `_lastUsed`, and `_lastUsed` is only refreshed when the pool is exhausted (or via the
            // fallback scan) — never after an ordinary in-cycle hit. So a direct Random.Range hit
            // mid-cycle is never checked against the clip just returned, and two consecutive calls
            // can return the same clip. This test proves the gap exists rather than asserting the
            // (false) "never repeats" guarantee.
            BroAudioClip[] clips = NewSetClips(2);
            bool foundRepeat = false;

            for (int trial = 0; trial < 500 && !foundRepeat; trial++)
            {
                var strategy = new ShuffleClipStrategy();
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int first);
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int second);
                foundRepeat = first == second;
            }

            Assert.IsTrue(foundRepeat, "Expected at least one trial where Shuffle repeated the immediately-previous clip.");
        }

        #endregion

        #region RandomClipStrategy (0.1)

        [Test]
        public void SelectClip_WithAllWeightsZero_ReturnsIndexWithinRange()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new RandomClipStrategy();

            for (int i = 0; i < 30; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.GreaterOrEqual(index, 0);
                Assert.Less(index, clips.Length);
            }
        }

        [Test]
        public void SelectClip_WithAnyNonzeroWeight_NeverSelectsZeroWeightClips()
        {
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 0;
            clips[1].Weight = 10;
            clips[2].Weight = 0;
            var strategy = new RandomClipStrategy();

            for (int i = 0; i < 50; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.AreEqual(1, index, "The only nonzero-weight clip should always win once any clip has weight.");
            }
        }

        [Test]
        public void SelectClip_WithSingleClip_AlwaysReturnsThatClipRegardlessOfWeight()
        {
            BroAudioClip[] clips = NewSetClips(1);
            clips[0].Weight = 7;
            var strategy = new RandomClipStrategy();

            for (int i = 0; i < 10; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.AreEqual(0, index);
            }
        }

        #endregion

        #region VelocityClipStrategy (0.1)

        [Test]
        public void SelectClip_WithValueBelowEveryThreshold_ReturnsIndexZero()
        {
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 10;
            clips[1].Weight = 40;
            clips[2].Weight = 80;
            var strategy = new VelocityClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(-5), out int index);

            Assert.AreEqual(0, index, "The i == 0 ? 0 : i - 1 guard keeps this in range rather than negative.");
        }

        [Test]
        public void SelectClip_WithValueBetweenThresholds_ReturnsPrecedingClip()
        {
            BroAudioClip[] clips = NewSetClips(4);
            clips[0].Weight = 0;
            clips[1].Weight = 40;
            clips[2].Weight = 80;
            clips[3].Weight = 127;
            var strategy = new VelocityClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(50), out int index);

            Assert.AreEqual(1, index, "50 sits between the 40 and 80 thresholds, so index 1 (the 40 threshold) should win.");
        }

        [Test]
        public void SelectClip_WithValueAboveEveryThreshold_ReturnsLastClipButLeavesIndexStaleAtZero()
        {
            // characterizes: when Value exceeds every threshold, the loop falls through to
            // `return clips[clips.Length - 1]` without ever reassigning `index` — the out
            // parameter stays at its initial 0 even though the returned clip is actually the last
            // one. Callers that trust `index` here would disagree with the returned clip.
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 0;
            clips[1].Weight = 40;
            clips[2].Weight = 80;
            var strategy = new VelocityClipStrategy();

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(200), out int index);

            Assert.AreSame(clips[2], result);
            Assert.AreEqual(0, index, "index is left stale at its initial value; it does not reflect the actually-returned clip.");
        }

        [Test]
        public void SelectClip_WithNonMonotonicWeights_DoesNotValidateAscendingOrder()
        {
            // characterizes: VelocityClipStrategy is a naive linear scan with no ordering check.
            // An out-of-order Weight array selects whatever the first threshold-exceeding entry
            // happens to be, not the "intended" nearest threshold.
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 80;
            clips[1].Weight = 0;
            clips[2].Weight = 40;
            var strategy = new VelocityClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext(10), out int index);

            Assert.AreEqual(0, index, "clips[0].Weight(80) > 10 is hit first, regardless of the later, smaller thresholds.");
        }

        #endregion

        #region ChainedClipStrategy (0.1)

        [Test]
        public void SelectClip_AtStartStage_ReturnsFirstClip()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new ChainedClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.Start), out int index);

            Assert.AreEqual(0, index);
        }

        [Test]
        public void SelectClip_AtLoopStage_ReturnsSecondClip()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new ChainedClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.Loop), out int index);

            Assert.AreEqual(1, index);
        }

        [Test]
        public void SelectClip_AtEndStage_ReturnsThirdClip()
        {
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new ChainedClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.End), out int index);

            Assert.AreEqual(2, index);
        }

        [Test]
        public void SelectClip_AtNoneStage_FloorsToIndexZero()
        {
            // characterizes: Math.Max(context.Value - 1, 0) floors PlaybackStage.None (0) to index 0
            // too, the same as Start — there's no distinct "no stage" outcome.
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new ChainedClipStrategy();

            strategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.None), out int index);

            Assert.AreEqual(0, index);
        }

        [Test]
        public void SelectClip_WithTooFewClipsForStage_LogsErrorAndReturnsNull()
        {
            BroAudioClip[] clips = NewSetClips(2);
            var strategy = new ChainedClipStrategy();
            LogAssert.Expect(LogType.Error, new Regex("There's no clip for Chained Play Mode Stage"));

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.End), out int index);

            Assert.IsNull(result);
        }

        #endregion

        #region AudioEntity.GetRandomValue / RandomFlag (0.5)

        [Test]
        public void GetRandomValueStatic_WithZeroRange_ReturnsBaseValueExactly()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(5f, AudioEntity.GetRandomValue(5f, 0f));
            }
        }

        [Test]
        public void GetRandomValueStatic_WithRange_StaysWithinHalfRangeBoundsAndVaries()
        {
            const float baseValue = 10f;
            const float range = 4f;
            bool sawDifferentValue = false;

            for (int i = 0; i < 30; i++)
            {
                float result = AudioEntity.GetRandomValue(baseValue, range);
                Assert.GreaterOrEqual(result, baseValue - range * 0.5f);
                Assert.LessOrEqual(result, baseValue + range * 0.5f);
                sawDifferentValue |= !Mathf.Approximately(result, baseValue);
            }

            Assert.IsTrue(sawDifferentValue, "A nonzero range should produce jitter across enough samples.");
        }

        [Test]
        public void GetRandomValue_WithFlagOff_ReturnsBaseValueRegardlessOfRange()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "RandomFlags", RandomFlag.None);
            TestAudioLibrary.SetPrivateField(entity, "PitchRandomRange", 100f);

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(2f, entity.GetRandomValue(2f, RandomFlag.Pitch), "RandomFlags.None short-circuits before Random is ever consulted.");
            }
        }

        [Test]
        public void GetRandomValue_WithFlagOnAndZeroRange_ReturnsBaseValueExactly()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "RandomFlags", RandomFlag.Volume);
            TestAudioLibrary.SetPrivateField(entity, "VolumeRandomRange", 0f);

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(0.5f, entity.GetRandomValue(0.5f, RandomFlag.Volume), "Flag on but zero range is a legitimate no-op state.");
            }
        }

        [Test]
        public void GetRandomValue_WithFlagsSet_JitterStaysWithinConfiguredRangePerFlag()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "RandomFlags", RandomFlag.Pitch | RandomFlag.Volume);
            TestAudioLibrary.SetPrivateField(entity, "PitchRandomRange", 4f);
            TestAudioLibrary.SetPrivateField(entity, "VolumeRandomRange", 10f);

            for (int i = 0; i < 20; i++)
            {
                float pitch = entity.GetRandomValue(1f, RandomFlag.Pitch);
                float volume = entity.GetRandomValue(0.8f, RandomFlag.Volume);
                Assert.That(pitch, Is.InRange(1f - 2f, 1f + 2f));
                Assert.That(volume, Is.InRange(0.8f - 5f, 0.8f + 5f));
            }
        }

        #endregion

        #region AudioEntity.HasLoop (4-arg) (0.5)

        [Test]
        public void HasLoop_WithLoopFlag_ReturnsLoopType()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "Loop", true);

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.None, 0f);

            Assert.IsTrue(hasLoop);
            Assert.AreEqual(LoopType.Loop, loopType);
            Assert.AreEqual(0f, transitionTime);
        }

        [Test]
        public void HasLoop_WithSeamlessLoopFlag_ReturnsSeamlessLoopAndOwnTransitionTime()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "SeamlessLoop", true);
            TestAudioLibrary.SetPrivateField(entity, "TransitionTime", 2.5f);

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.None, 0f);

            Assert.IsTrue(hasLoop);
            Assert.AreEqual(LoopType.SeamlessLoop, loopType);
            Assert.AreEqual(2.5f, transitionTime, "Uses the entity's own TransitionTime, not the passed-in default.");
        }

        [Test]
        public void HasLoop_WithChainedModeAndNoFlags_FallsBackToProvidedDefaults()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, "MulticlipsPlayMode", MulticlipsPlayMode.Chained);

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.SeamlessLoop, 3f);

            Assert.IsTrue(hasLoop);
            Assert.AreEqual(LoopType.SeamlessLoop, loopType);
            Assert.AreEqual(3f, transitionTime);
        }

        [Test]
        public void HasLoop_WithNoFlagsAndNotChained_ReturnsFalse()
        {
            AudioEntity entity = NewEntity();

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.Loop, 5f);

            Assert.IsFalse(hasLoop);
            Assert.AreEqual(LoopType.None, loopType);
            Assert.AreEqual(0f, transitionTime);
        }

        #endregion
    }
}
