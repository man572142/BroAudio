using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins PlaybackGroup voice limiting, comb-filtering rejection, and a custom IPlayableValidator overriding
    /// the group. Each test wires its group via <see cref="NewGroup"/>/<see cref="NewGroupedSound"/> before the
    /// first Play (the rule list is cached on first use). Every rule NewGroup writes overrides, so the parent
    /// <see cref="BroAudioTestFixture.FactoryGlobalPlaybackGroup"/> is never consulted.
    /// <para>
    /// An acceptance-inside-the-window test uses the same 10s window as its rejecting twin: with a short
    /// window, a stall between the plays would expire it and the acceptance would pass for the wrong reason.
    /// </para>
    /// <para>
    /// The AudioAsset-group tests use asset-backed entities, which resolve entity group, then asset group, then
    /// the global group. They play distinct IDs in one frame, which the global group always accepts (its window
    /// is per SoundID and it has no voice limit), so only the asset's own voice limit can explain a rejection.
    /// </para>
    /// </summary>
    public class PlaybackGroupTests : BroAudioTestFixture
    {
        /// <summary>Always allows the play.</summary>
        private class AllowingValidator : IPlayableValidator
        {
            public bool IsPlayable(SoundID id, Vector3 position) => true;
            public void OnGetPlayer(IAudioPlayer player) { }
        }

        /// <summary>Creates a tracked entity wired to the given group and returns its SoundID.</summary>
        private SoundID NewGroupedSound(DefaultPlaybackGroup group, string name, float clipSeconds = 2f)
        {
            AudioEntity entity = NewEntity(name, BroAudioType.SFX, NewClip(clipSeconds, name + "Clip"));
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.Group, group);
            return IdOf(entity);
        }

        /// <summary>Creates a tracked, empty AudioAsset whose own group is <paramref name="group"/>.</summary>
        private AudioAsset NewAssetWithGroup(DefaultPlaybackGroup group, string name)
        {
            AudioAsset asset = Track(TestAudioLibrary.CreateAudioAsset(name));
            TestAudioLibrary.SetPrivateField(asset, AudioAsset.NameOf.Group, group);
            return asset;
        }

        /// <summary>Creates a tracked entity owned by <paramref name="asset"/>, with no group of its own.</summary>
        private AudioEntity NewEntityInAsset(AudioAsset asset, string name, float clipSeconds = 2f)
            => Track(TestAudioLibrary.CreateAssetBackedEntity(name, BroAudioType.SFX, asset, NewClip(clipSeconds, name + "Clip")));

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
            yield return WaitForRecycle(player1, "the stopped player to recycle and free its slot", RampConvergenceWaitSeconds);

            IAudioPlayer player4 = BroAudio.Play(id4);
            Assert.IsTrue(player4.IsActive, "Once a slot frees (OnEnd decrements the group's count), a new play must succeed again.");
        }

        // Characterizes: the count increments synchronously inside Play(), before LateUpdate starts any voice.
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

        // Baseline: different frames, so neither exemption applies.
        [UnityTest]
        public IEnumerator Play_SameID_WithinCombFilteringWindow_RejectsSecond()
        {
            // 10s, so a stall between the plays cannot expire the window.
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 10f);
            SoundID id = NewGroupedSound(group, "CombWindowSfx");

            IAudioPlayer player1 = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player1, "the first play to start so PlaybackStartingTime is recorded");
            yield return WaitFrames(2); // move to a later frame - no longer "still queued"

            IAudioPlayer player2 = BroAudio.Play(id);

            Assert.IsFalse(player2.IsActive, "A same-ID replay inside the comb-filtering window must be rejected.");
        }

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

        // Characterizes: "still queued" (PlaybackStartingTime == 0) counts as same-frame regardless of the
        // flag; the flag only decides whether that case is forgiven.
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

        [UnityTest]
        public IEnumerator Play_PositionedFarApart_WithinCombFilteringWindow_BothSucceed()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 10f, ignoreDistanceGreaterThan: 5f);
            SoundID id = NewGroupedSound(group, "CombDistanceSfx");

            IAudioPlayer player1 = BroAudio.Play(id, Vector3.zero);
            yield return WaitForPlaybackStart(player1, "the first play to start");
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, new Vector3(100f, 0f, 0f));

            Assert.IsTrue(player2.IsActive,
                "Two positioned plays farther apart than _ignoreIfDistanceIsGreaterThan are exempt from comb-filtering even inside the time window.");
        }

        // Negative control for the test above.
        [UnityTest]
        public IEnumerator Play_PositionedCloseTogether_WithinCombFilteringWindow_RejectsSecond()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 10f, ignoreDistanceGreaterThan: 5f);
            SoundID id = NewGroupedSound(group, "CombDistanceCloseSfx");

            IAudioPlayer player1 = BroAudio.Play(id, Vector3.zero);
            yield return WaitForPlaybackStart(player1, "the first play to start");
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, new Vector3(1f, 0f, 0f));

            Assert.IsFalse(player2.IsActive,
                "Two positioned plays closer than _ignoreIfDistanceIsGreaterThan get no exemption inside the time window.");
        }

        [UnityTest]
        public IEnumerator Play_GlobalThenPositioned_WithinCombFilteringWindow_ExemptedRegardlessOfActualDistance()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 10f, ignoreDistanceGreaterThan: 5f);
            SoundID id = NewGroupedSound(group, "CombGlobalMixSfx");

            IAudioPlayer player1 = BroAudio.Play(id); // global (2D) play - no position
            yield return WaitForPlaybackStart(player1, "the first play to start");
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, Vector3.zero); // positioned, but at the exact same origin

            Assert.IsTrue(player2.IsActive,
                "A global/positioned mix is exempted purely because _ignoreIfDistanceIsGreaterThan > 0, with no actual distance comparison possible.");
        }

        // Negative control for the global/positioned exemption above.
        [UnityTest]
        public IEnumerator Play_GlobalThenPositioned_WithDistanceExemptionOff_RejectsSecond()
        {
            DefaultPlaybackGroup group = NewGroup(combFilteringTime: 10f, ignoreDistanceGreaterThan: 0f);
            SoundID id = NewGroupedSound(group, "CombGlobalMixNoDistanceSfx");

            IAudioPlayer player1 = BroAudio.Play(id); // global (2D) play - no position
            yield return WaitForPlaybackStart(player1, "the first play to start");
            yield return WaitFrames(2);

            IAudioPlayer player2 = BroAudio.Play(id, Vector3.zero);

            Assert.IsFalse(player2.IsActive,
                "With _ignoreIfDistanceIsGreaterThan at 0, a global/positioned mix inside the window gets no exemption.");
        }

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

        [UnityTest]
        public IEnumerator Play_TwoIdsInAnAssetWhoseGroupAllowsOneVoice_RejectsTheSecond()
        {
            AudioAsset limited = NewAssetWithGroup(NewGroup(maxPlayableCount: 1), "AssetLimitAsset");
            SoundID limitedA = IdOf(NewEntityInAsset(limited, "AssetLimitA"));
            SoundID limitedB = IdOf(NewEntityInAsset(limited, "AssetLimitB"));

            AudioAsset ungrouped = Track(TestAudioLibrary.CreateAudioAsset("AssetNoGroupAsset"));
            SoundID ungroupedA = IdOf(NewEntityInAsset(ungrouped, "AssetNoGroupA"));
            SoundID ungroupedB = IdOf(NewEntityInAsset(ungrouped, "AssetNoGroupB"));

            IAudioPlayer limitedFirst = BroAudio.Play(limitedA);
            IAudioPlayer limitedSecond = BroAudio.Play(limitedB);
            IAudioPlayer ungroupedFirst = BroAudio.Play(ungroupedA);
            IAudioPlayer ungroupedSecond = BroAudio.Play(ungroupedB);

            Assert.IsTrue(limitedFirst.IsActive, "The first play takes the asset group's only voice.");
            Assert.IsFalse(limitedSecond.IsActive,
                "An entity with no group of its own plays under its AudioAsset's group, so a second ID from that asset is rejected once the one voice is taken.");
            Assert.AreEqual(SoundID.Invalid, limitedSecond.ID, "A rejected play returns the inert empty player.");
            Assert.IsTrue(ungroupedFirst.IsActive && ungroupedSecond.IsActive,
                "Contrast: the same two IDs in an asset with no group fall back to the global group, which has no voice limit, so both are accepted.");

            yield return WaitFrames(1);
        }

        [UnityTest]
        public IEnumerator Play_EntityWithItsOwnGroupInAFullAsset_IsJudgedByItsOwnGroup()
        {
            AudioAsset limited = NewAssetWithGroup(NewGroup(maxPlayableCount: 1), "PrecedenceAsset");
            SoundID assetGroupedA = IdOf(NewEntityInAsset(limited, "PrecedenceAssetGroupedA"));
            SoundID assetGroupedB = IdOf(NewEntityInAsset(limited, "PrecedenceAssetGroupedB"));
            AudioEntity ownGroupedEntity = NewEntityInAsset(limited, "PrecedenceOwnGrouped");
            TestAudioLibrary.SetPrivateField(ownGroupedEntity, TestAudioLibrary.Reflected.AudioEntity.Group, NewGroup());
            SoundID ownGrouped = IdOf(ownGroupedEntity);

            IAudioPlayer first = BroAudio.Play(assetGroupedA);
            IAudioPlayer second = BroAudio.Play(assetGroupedB);
            IAudioPlayer third = BroAudio.Play(ownGrouped);

            Assert.IsTrue(first.IsActive, "The first play takes the asset group's only voice.");
            Assert.IsFalse(second.IsActive, "Baseline: the asset group rejects a second entity that has no group of its own.");
            Assert.IsTrue(third.IsActive,
                "An entity's own group takes precedence over its AudioAsset's group, so the full asset group does not reject it.");

            yield return WaitFrames(1);
        }
    }
}