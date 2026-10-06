using Ami.BroAudio.Data;
using Ami.BroAudio.Editor.Tests;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Characterizes the clip selection strategies and their <see cref="AudioEntity"/> helpers, built
    /// directly with no SoundManager, so they run in EditMode.
    /// </summary>
    public class ClipSelectionTests : BroEditorTestFixture
    {
        /// <summary>Any value works; it only has to be the same every run.</summary>
        private const int DeterministicSeed = 918273645;
        private Random.State _priorRandomState;

        /// <summary>
        /// Seeds <see cref="Random"/> and restores the prior state afterward: the generator is process-wide,
        /// so a leaked seed would make other tests' randomness reproducible by accident.
        /// </summary>
        protected override void OnSetUp()
        {
            _priorRandomState = Random.state;
            Random.InitState(DeterministicSeed);
        }

        protected override void OnTearDown()
        {
            Random.state = _priorRandomState;
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
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

            IBroAudioClip result = strategy.SelectClip(null, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
            Assert.AreEqual(0, index);
        }

        [Test]
        public void SelectClip_WithNullFirstClipReference_LogsErrorAndReturnsNull()
        {
            var clips = new BroAudioClip[] { null };
            var strategy = new SingleClipStrategy();
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
        }

        [Test]
        public void SelectClip_WithUnsetFirstClip_LogsErrorAndReturnsNull()
        {
            var clips = new[] { UnsetClip() };
            var strategy = new SingleClipStrategy();
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);

            Assert.IsNull(result);
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
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

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

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
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
        [Category("Finding_10")]
        public void SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip()
        {
            // Pins TEST_FINDINGS #10 (fallback-scan half).
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
                "If this now fails, the mismatch was fixed — update Docs/Tests/TEST_FINDINGS.md and delete this test.");
        }

        [Test]
        public void SelectClip_WithSingleClip_AlwaysReturnsSameClipAcrossCalls()
        {
            BroAudioClip[] clips = NewSetClips(1);
            var strategy = new ShuffleClipStrategy();

            for (int i = 0; i < 10; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                Assert.AreEqual(0, index);
            }
        }

        [Test]
        [Category("Finding_9")]
        public void SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent()
        {
            // Pins TEST_FINDINGS #9.
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

        [Test]
        public void SelectClip_OverManyDraws_ReturnsEveryClipAboutEquallyOften()
        {
            // Draws aren't independent, but the strategy is symmetric under rotating the array, so long-run
            // shares are still uniform. Use IndexOf, not the out index: TEST_FINDINGS #10.
            const int ClipCount = 4;
            BroAudioClip[] clips = NewSetClips(ClipCount);
            var strategy = new ShuffleClipStrategy();

            int[] counts = new int[ClipCount];
            for (int i = 0; i < WeightedDrawCount; i++)
            {
                IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(0), out _);
                counts[System.Array.IndexOf(clips, (BroAudioClip)result)]++;
            }

            for (int i = 0; i < ClipCount; i++)
            {
                Assert.AreEqual(1f / ClipCount, counts[i] / (float)WeightedDrawCount, 0.03f, $"Clip {i} should win about a quarter of the draws.");
            }
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
        public void SelectClip_WithAllWeightsZero_DrawsEveryClipAboutEquallyOften()
        {
            // Also catches the Random.Range(int, int) exclusive-max slip that never reaches the last slot.
            BroAudioClip[] clips = NewSetClips(3);
            var strategy = new RandomClipStrategy();

            int[] counts = new int[clips.Length];
            for (int i = 0; i < WeightedDrawCount; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                counts[index]++;
            }

            for (int i = 0; i < clips.Length; i++)
            {
                Assert.AreEqual(1f / 3f, counts[i] / (float)WeightedDrawCount, 0.03f, $"Clip {i} should win about a third of the draws.");
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

        /// <summary>
        /// At this N every tested share's binomial std-dev is under 0.008, so the ±0.03 bands are over 4σ: the
        /// fixed seed can't miss by chance, while a uniform, swapped or off-by-one pick lands far outside.
        /// </summary>
        private const int WeightedDrawCount = 4000;

        [Test]
        public void SelectClip_WithWeightsOneAndThree_ObservedShareMatchesWeightOverTotal()
        {
            BroAudioClip[] clips = NewSetClips(2);
            clips[0].Weight = 1;
            clips[1].Weight = 3;
            var strategy = new RandomClipStrategy();

            int[] counts = new int[clips.Length];
            for (int i = 0; i < WeightedDrawCount; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                counts[index]++;
            }

            Assert.AreEqual(0.25f, counts[0] / (float)WeightedDrawCount, 0.03f, "Weight 1 of 4 should win about a quarter of the draws.");
            Assert.AreEqual(0.75f, counts[1] / (float)WeightedDrawCount, 0.03f, "Weight 3 of 4 should win about three quarters of the draws.");
        }

        [Test]
        public void SelectClip_WithWeightsOneTwoAndFive_ObservedShareMatchesWeightOverTotal()
        {
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 1;
            clips[1].Weight = 2;
            clips[2].Weight = 5;
            var strategy = new RandomClipStrategy();

            int[] counts = new int[clips.Length];
            for (int i = 0; i < WeightedDrawCount; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                counts[index]++;
            }

            Assert.AreEqual(0.125f, counts[0] / (float)WeightedDrawCount, 0.03f, "Weight 1 of 8 should win about one eighth of the draws.");
            Assert.AreEqual(0.25f, counts[1] / (float)WeightedDrawCount, 0.03f, "Weight 2 of 8 should win about a quarter of the draws.");
            Assert.AreEqual(0.625f, counts[2] / (float)WeightedDrawCount, 0.03f, "Weight 5 of 8 should win about five eighths of the draws.");
        }

        [Test]
        public void SelectClip_WithAZeroWeightBetweenTwoNonzeroOnes_NeverSelectsItAndSharesStillMatchWeightOverTotal()
        {
            // The cumulative sum doesn't advance across a zero weight, so index 1's slice is empty.
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 1;
            clips[1].Weight = 0;
            clips[2].Weight = 3;
            var strategy = new RandomClipStrategy();

            int[] counts = new int[clips.Length];
            for (int i = 0; i < WeightedDrawCount; i++)
            {
                strategy.SelectClip(clips, new ClipSelectionContext(0), out int index);
                counts[index]++;
            }

            Assert.AreEqual(0, counts[1], "A zero-weight clip sitting between two nonzero ones must never be picked.");
            Assert.AreEqual(0.25f, counts[0] / (float)WeightedDrawCount, 0.03f, "Weight 1 of 4 should win about a quarter of the draws even with a zero-weight clip in between.");
            Assert.AreEqual(0.75f, counts[2] / (float)WeightedDrawCount, 0.03f, "Weight 3 of 4 should win about three quarters of the draws even with a zero-weight clip in between.");
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
        public void SelectClip_WithValueAboveEveryThreshold_ReturnsLastClipAndItsIndex()
        {
            BroAudioClip[] clips = NewSetClips(3);
            clips[0].Weight = 0;
            clips[1].Weight = 40;
            clips[2].Weight = 80;
            var strategy = new VelocityClipStrategy();

            IBroAudioClip result = strategy.SelectClip(clips, new ClipSelectionContext(200), out int index);

            Assert.AreSame(clips[2], result);
            Assert.AreEqual(2, index, "index must point at the returned clip.");
        }

        [Test]
        public void SelectClip_WithNonMonotonicWeights_DoesNotValidateAscendingOrder()
        {
            // characterizes: a linear scan with no ordering check; the first threshold exceeded wins.
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
            // characterizes: None floors to the same index as Start; there is no distinct "no stage" outcome.
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
            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);

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
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.RandomFlags), RandomFlag.None);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.PitchRandomRange), 100f);

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(2f, entity.GetRandomValue(2f, RandomFlag.Pitch), "RandomFlags.None short-circuits before Random is ever consulted.");
            }
        }

        [Test]
        public void GetRandomValue_WithFlagOnAndZeroRange_ReturnsBaseValueExactly()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.RandomFlags), RandomFlag.Volume);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.VolumeRandomRange), 0f);

            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(0.5f, entity.GetRandomValue(0.5f, RandomFlag.Volume), "Flag on but zero range is a legitimate no-op state.");
            }
        }

        [Test]
        public void GetRandomValue_WithFlagsSet_JitterStaysWithinConfiguredRangePerFlag()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.RandomFlags), RandomFlag.Pitch | RandomFlag.Volume);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.PitchRandomRange), 4f);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.VolumeRandomRange), 10f);

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
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.None, 0f);

            Assert.IsTrue(hasLoop);
            Assert.AreEqual(LoopType.Loop, loopType);
            Assert.AreEqual(0f, transitionTime);
        }

        [Test]
        public void HasLoop_WithSeamlessLoopFlag_ReturnsSeamlessLoopAndOwnTransitionTime()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.SeamlessLoop), true);
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.TransitionTime), 2.5f);

            bool hasLoop = entity.HasLoop(out LoopType loopType, out float transitionTime, LoopType.None, 0f);

            Assert.IsTrue(hasLoop);
            Assert.AreEqual(LoopType.SeamlessLoop, loopType);
            Assert.AreEqual(2.5f, transitionTime, "Uses the entity's own TransitionTime, not the passed-in default.");
        }

        [Test]
        public void HasLoop_WithChainedModeAndNoFlags_FallsBackToProvidedDefaults()
        {
            AudioEntity entity = NewEntity();
            TestAudioLibrary.SetPrivateField(entity, AudioEntity.EditorPropertyName.MulticlipsPlayMode, MulticlipsPlayMode.Chained);

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