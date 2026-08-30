using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Inventory slice 3.1-3.4: PlaybackGroup voice limiting and comb-filtering rejection, and how a custom
    /// IPlayableValidator overrides the group entirely. See Docs/inventory/selection-policy.md,
    /// "Playback-group voice limiting and rejection".
    /// <para>
    /// A code-built AudioEntity has no PlaybackGroup (_group is null, no AudioAsset), so every test here wires
    /// one explicitly via <see cref="NewGroup"/>/<see cref="NewGroupedSound"/> before the first Play call -
    /// PlaybackGroup caches its rule list lazily on first use.
    /// </para>
    /// </summary>
    public class PlaybackGroupTests : BroAudioTestFixture
    {
        /// <summary>Always allows the play - the opposite of PlaybackLifecycleTests' RejectingValidator.</summary>
        private class AllowingValidator : IPlayableValidator
        {
            public bool IsPlayable(SoundID id, Vector3 position) => true;
            public void OnGetPlayer(IAudioPlayer player) { }
        }

        /// <summary>
        /// Builds a fresh, tracked DefaultPlaybackGroup with only the rule(s) a test cares about enabled.
        /// _logCombFilteringWarning is always off - the warning is log noise, not the behavior under test.
        /// </summary>
        private DefaultPlaybackGroup NewGroup(int maxPlayableCount = -1, float combFilteringTime = 0f,
            bool ignoreSameFrame = false, float ignoreDistanceGreaterThan = 0f)
        {
            DefaultPlaybackGroup group = Track(ScriptableObject.CreateInstance<DefaultPlaybackGroup>());
            TestAudioLibrary.SetPrivateField(group, "_maxPlayableCount", (MaxPlayableCountRule)maxPlayableCount);
            TestAudioLibrary.SetPrivateField(group, "_combFilteringTime", (CombFilteringRule)combFilteringTime);
            TestAudioLibrary.SetPrivateField(group, "_ignoreCombFilteringIfSameFrame", ignoreSameFrame);
            TestAudioLibrary.SetPrivateField(group, "_ignoreIfDistanceIsGreaterThan", ignoreDistanceGreaterThan);
            TestAudioLibrary.SetPrivateField(group, "_logCombFilteringWarning", false);
            return group;
        }

        /// <summary>Creates a tracked entity wired to the given group and returns its SoundID.</summary>
        private SoundID NewGroupedSound(DefaultPlaybackGroup group, string name, float clipSeconds = 2f)
        {
            AudioEntity entity = NewEntity(name, BroAudioType.SFX, NewClip(clipSeconds, name + "Clip"));
            TestAudioLibrary.SetPrivateField(entity, "_group", group);
            return IdOf(entity);
        }

        // 3.1 - the (N+1)th concurrent play is rejected once the limit is reached; when an accepted play ends
        // (here via Stop, which recycles and fires OnEnd same as natural completion) the count decrements and
        // a new play succeeds again.
        [UnityTest]
        public IEnumerator Play_BeyondMaxPlayableCount_RejectsThenAcceptsAfterASlotFrees()
        {
            DefaultPlaybackGroup group = NewGroup(maxPlayableCount: 2);
            SoundID id1 = NewGroupedSound(group, "VoiceLimitA");
            SoundID id2 = NewGroupedSound(group, "VoiceLimitB");
            SoundID id3 = NewGroupedSound(group, "VoiceLimitC");
            SoundID id4 = NewGroupedSound(group, "VoiceLimitD");

            IAudioPlayer player1 = BroAudio.Play(id1);
            IAudioPlayer player2 = BroAudio.Play(id2);
            IAudioPlayer player3 = BroAudio.Play(id3);

            Assert.IsTrue(player1.IsActive, "1st concurrent play within the limit must be accepted.");
            Assert.IsTrue(player2.IsActive, "2nd concurrent play within the limit must be accepted.");
            Assert.IsFalse(player3.IsActive, "The (N+1)th concurrent play must be rejected once the limit is reached.");
            Assert.AreEqual(SoundID.Invalid, player3.ID, "A rejected play returns the inert empty player.");

            yield return WaitFrames(2);

            player1.Stop(0f);
            yield return WaitUntilOrTimeout(() => !player1.IsActive, "the stopped player to recycle and free its slot", 3f);

            IAudioPlayer player4 = BroAudio.Play(id4);
            Assert.IsTrue(player4.IsActive, "Once a slot frees (OnEnd decrements the group's count), a new play must succeed again.");
        }

        // 3.2 - characterizes: MaxPlayableCountRule.OnGetPlayer increments inside SoundManager.IsPlayable,
        // synchronously during Play(), before LateUpdate drains the queue. Two plays issued in the same frame -
        // neither yet audible - already exhaust the limit.
        [UnityTest]
        public IEnumerator Play_TwoPlaysInSameFrame_BothCountAgainstLimitBeforeEitherStartsPlaying()
        {
            DefaultPlaybackGroup group = NewGroup(maxPlayableCount: 1);
            SoundID id1 = NewGroupedSound(group, "EnqueueLimitA");
            SoundID id2 = NewGroupedSound(group, "EnqueueLimitB");

            IAudioPlayer player1 = BroAudio.Play(id1);
            IAudioPlayer player2 = BroAudio.Play(id2); // no yield in between - both land in the same frame's queue

            Assert.IsTrue(player1.IsActive, "The first play must be accepted.");
            Assert.IsFalse(player1.IsPlaying, "The accepted play must not be audible yet - LateUpdate hasn't drained the queue.");
            Assert.IsFalse(player2.IsActive,
                "The second play is already rejected, even though neither play has started audible playback yet.");

            yield return WaitFrames(1);
        }

        // 3.3 - baseline: two plays of the same SoundID within _combFilteringTime, in different frames
        // (neither exemption applies), reject the second.
        [UnityTest]
        public IEnumerator Play_SameID_WithinCombFilteringWindow_RejectsSecond()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 1f);
            SoundID id = NewGroupedSound(group, "CombWindowSfx");

            IAudioPlayer player1 = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player1.IsPlaying, "the first play to start so PlaybackStartingTime is recorded", 2f);
            yield return WaitFrames(2); // move to a later frame - no longer "still queued"

            IAudioPlayer player2 = BroAudio.Play(id);

            Assert.IsFalse(player2.IsActive, "A same-ID replay inside the comb-filtering window must be rejected.");
        }

        // 3.3 - _ignoreCombFilteringIfSameFrame == true: two same-ID plays enqueued in the same frame
        // (still queued, neither started) are exempt.
        [UnityTest]
        public IEnumerator Play_SameID_SameFrameWithIgnoreFlagTrue_BothSucceed()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 1f, ignoreSameFrame: true);
            SoundID id = NewGroupedSound(group, "CombSameFrameIgnoreSfx");

            IAudioPlayer player1 = BroAudio.Play(id);
            IAudioPlayer player2 = BroAudio.Play(id); // no yield - both still queued in the same frame

            Assert.IsTrue(player1.IsActive);
            Assert.IsTrue(player2.IsActive,
                "With _ignoreCombFilteringIfSameFrame on, two same-ID plays enqueued in the same frame are exempt.");

            yield return WaitFrames(1);
        }

        // 3.3 - _ignoreCombFilteringIfSameFrame == false: the same same-frame scenario now rejects the second
        // play. characterizes: "still queued" (PlaybackStartingTime == 0) counts as the same frame internally
        // regardless of the flag - the flag only controls whether that same-frame case is forgiven.
        [UnityTest]
        public IEnumerator Play_SameID_SameFrameWithIgnoreFlagFalse_RejectsSecond()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 1f, ignoreSameFrame: false);
            SoundID id = NewGroupedSound(group, "CombSameFrameStrictSfx");

            IAudioPlayer player1 = BroAudio.Play(id);
            IAudioPlayer player2 = BroAudio.Play(id); // no yield - both still queued in the same frame

            Assert.IsTrue(player1.IsActive);
            Assert.IsFalse(player2.IsActive,
                "With the same-frame flag off, an immediate same-ID replay is rejected even though neither play has started yet.");

            yield return WaitFrames(1);
        }

        // 3.3 - positional asymmetry, part 1: two positioned plays farther apart than
        // _ignoreIfDistanceIsGreaterThan are exempt even inside the time window.
        [UnityTest]
        public IEnumerator Play_PositionedFarApart_WithinCombFilteringWindow_BothSucceed()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 1f, ignoreDistanceGreaterThan: 5f);
            SoundID id = NewGroupedSound(group, "CombDistanceSfx");

            IAudioPlayer player1 = BroAudio.Play(id, Vector3.zero);
            yield return WaitUntilOrTimeout(() => player1.IsPlaying, "the first play to start", 2f);
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, new Vector3(100f, 0f, 0f));

            Assert.IsTrue(player2.IsActive,
                "Two positioned plays farther apart than _ignoreIfDistanceIsGreaterThan are exempt from comb-filtering even inside the time window.");
        }

        // 3.3 - positional asymmetry, part 2: a global (2D) play has no position to compare against a positioned
        // one. characterizes: DefaultPlaybackGroup skips the distance check entirely for a global/positioned
        // mix and instead exempts the pair purely because _ignoreIfDistanceIsGreaterThan > 0 - even when the
        // positioned play sits at the exact same origin, i.e. not actually "far apart" at all.
        [UnityTest]
        public IEnumerator Play_GlobalThenPositioned_WithinCombFilteringWindow_ExemptedRegardlessOfActualDistance()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 1f, ignoreDistanceGreaterThan: 5f);
            SoundID id = NewGroupedSound(group, "CombGlobalMixSfx");

            IAudioPlayer player1 = BroAudio.Play(id); // global (2D) play - no position
            yield return WaitUntilOrTimeout(() => player1.IsPlaying, "the first play to start", 2f);
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, Vector3.zero); // positioned, but at the exact same origin

            Assert.IsTrue(player2.IsActive,
                "A global/positioned mix is exempted purely because _ignoreIfDistanceIsGreaterThan > 0, with no actual distance comparison possible.");
        }

        // 3.4 - a custom IPlayableValidator passed to Play() replaces the entity's own PlaybackGroup entirely
        // (SoundManager.IsPlayable: customValidator ?? entity.PlaybackGroup) - it wins outright, even when it
        // allows a play the group would have rejected.
        [UnityTest]
        public IEnumerator Play_WithCustomValidator_OverridesGroupEntirely()
        {
            DefaultPlaybackGroup group = NewGroup(maxPlayableCount: 1);
            SoundID id = NewGroupedSound(group, "ValidatorOverrideSfx");

            IAudioPlayer player1 = BroAudio.Play(id);
            Assert.IsTrue(player1.IsActive, "First play fills the group's only slot.");

            IAudioPlayer player2 = BroAudio.Play(id);
            Assert.IsFalse(player2.IsActive, "Baseline: the group itself rejects a second concurrent play here.");

            IAudioPlayer player3 = BroAudio.Play(id, new AllowingValidator());
            Assert.IsTrue(player3.IsActive,
                "A custom IPlayableValidator passed to Play() overrides the group entirely - even one that allows a play the group would have rejected.");

            yield return WaitFrames(1);
        }
    }
}