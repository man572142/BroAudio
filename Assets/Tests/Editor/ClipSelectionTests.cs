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
    /// Characterization tests for the clip selection strategies and their supporting
    /// <see cref="AudioEntity"/> helpers. No SoundManager, no Play Mode: strategies and clip arrays
    /// are constructed directly, so this lives in the EditMode assembly (<c>EditorTests.asmdef</c>)
    /// and runs without entering Play Mode. It derives from the EditMode fixture (not the PlayMode
    /// BroAudioTestFixture, which would force Play Mode setup) for the suite-wide isolation contract.
    /// </summary>
    public class ClipSelectionTests : BroEditorTestFixture
    {
        /// <summary>
        /// Fixed so every probabilistic test below reproduces identically on a failure. Any int works -
        /// nothing here depends on its value, only on it being the same seed every run.
        /// </summary>
        private const int DeterministicSeed = 918273645;
        private Random.State _priorRandomState;

        /// <summary>
        /// Seeds <see cref="Random"/> before every test in this file and restores whatever state the rest
        /// of the Editor session was relying on afterward - this fixture shares the process-wide
        /// <see cref="Random"/> generator with everything else EditMode runs, so leaking a reseeded state
        /// would make other tests' own "varies across samples" assertions reproducible (or not) by
        /// accident. Overrides <see cref="BroEditorTestFixture.OnSetUp"/> rather than adding a second
        /// <c>[SetUp]</c>/<c>[TearDown]</c> pair, which is the extension point the base fixture already
        /// wraps its own isolation snapshot/restore around.
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
            // Single rejects an unset BroAudioClip (non-null, IsSet == false) the same way
            // Sequence and Shuffle do, rather than deferring the failure to whatever plays it.
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
            // Characterizes TEST_FINDINGS #10: in the fallback scan, the loop keeps advancing `index` while
            // probing for an unused clip, then returns `result` — the clip found at the *earlier* index. So
            // `clips[index]` is not necessarily the clip that was returned. Same class of defect as
            // SelectClip_WithValueAboveEveryThreshold_ReturnsLastClipButLeavesIndexStaleAtZero in VelocityClipStrategy.
            // Only the out-index overload is affected, and its only consumers are Editor preview/inspector
            // code, so runtime playback picks the right clip regardless.
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
        [Category("Finding_9")]
        public void SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent()
        {
            // Characterizes TEST_FINDINGS #9: MulticlipsPlayMode.Shuffle's doc comment promises "not repeating
            // with the previous one", but ShuffleClipStrategy.Use() only ever rejects a pick that equals
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

        [Test]
        public void SelectClip_OverManyDraws_ReturnsEveryClipAboutEquallyOften()
        {
            // Everything in ShuffleClipStrategy is symmetric under rotating the clip array (a uniform draw, then a
            // +1/-1 neighbour step chosen by a fair coin), so its long-run shares are uniform whatever the order
            // of individual picks. Draws are not independent - a pick right after a cycle reset is steered off the
            // previous clip - but that steering favours no slot over another. At N=4000 the i.i.d. std-dev for
            // p=0.25 is ≈0.0068, so ±0.03 is over 4 of them, while a strategy stuck on one clip, or one that never
            // reached a slot, misses the band by far. The returned clip is located with IndexOf rather than read
            // from the out index, which TEST_FINDINGS #10 shows can disagree with it.
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
            // The all-zero branch is `index = Random.Range(0, clips.Length)`: a uniform pick, so over N=4000 seeded
            // draws each of three clips should take about a third. Std-dev for p=1/3 is sqrt((1/3)(2/3)/4000) ≈ 0.0075,
            // so ±0.03 is 4 of them - a pick collapsed onto one clip (or an off-by-one that never reaches the last
            // slot, the classic Random.Range(int, int) exclusive-max slip) lands far outside.
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

        /// <summary>Draws large enough that a fixed seed's sampling noise is negligible next to the gap
        /// between a correct weighted pick and a uniform or off-by-one one - see each test's own comment
        /// for the exact arithmetic.</summary>
        private const int WeightedDrawCount = 4000;

        [Test]
        public void SelectClip_WithWeightsOneAndThree_ObservedShareMatchesWeightOverTotal()
        {
            // clips[0].Weight=1, clips[1].Weight=3, total=4: RandomClipStrategy draws
            // targetWeight = Random.Range(0, 4) (i.e. 0..3) and walks the cumulative sum [1, 4], returning
            // the first index whose running sum exceeds targetWeight. targetWeight==0 hits index 0 (1 of 4
            // values); targetWeight in {1,2,3} hits index 1 (3 of 4 values) - so the true shares are exactly
            // 0.25 and 0.75.
            // Binomial std-dev at N=4000 for p=0.25 (same for the complementary p=0.75) is
            // sqrt(0.25*0.75/4000) ≈ 0.0068, so a ±0.03 band is >4 std devs - the fixed seed below cannot
            // miss it by chance, while a uniform implementation (both shares ~0.5) or one that swapped the
            // weight lookup (shares reversed to ~0.75/0.25) would land far outside it.
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
            // Same reasoning as the two-clip test above, with a third bucket: weights {1,2,5}, total=8, so
            // the true shares are 1/8=0.125, 2/8=0.25 and 5/8=0.625. Worst-case std-dev among the three
            // (p=0.625: sqrt(0.625*0.375/4000) ≈ 0.0076) is still comfortably under a ±0.03 band at N=4000,
            // while a uniform implementation (~0.333 each) misses every one of the three bands.
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
            // clips[1].Weight=0 sits strictly between two nonzero weights (1 and 3, total=4). Because the
            // cumulative sum does not advance across a zero-weight entry, no draw of
            // targetWeight = Random.Range(0, 4) can ever land in index 1's (empty) slice of the sum - the
            // sum jumps straight from 1 (after index 0) to 4 (after index 2). So index 1 must never be
            // picked, and indices 0/2 should still show the same 0.25/0.75 shares as the two-clip case above.
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
        [Category("Finding_10")]
        public void SelectClip_WithValueAboveEveryThreshold_ReturnsLastClipButLeavesIndexStaleAtZero()
        {
            // Characterizes TEST_FINDINGS #10: when Value exceeds every threshold, the loop falls through to
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
            // This file lives in the Editor assembly (moved from Runtime), so the UNITY_EDITOR-gated
            // EditorPropertyName accessor is always available here - it makes a rename a compile error
            // instead of a reflection-time failure, unlike the runtime-suite call sites that must stay
            // string literals because they also compile in Player test builds.
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