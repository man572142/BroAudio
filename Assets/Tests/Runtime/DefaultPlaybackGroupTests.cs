using System.Collections;
using Ami.BroAudio.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The playback group every shipped entity plays through: an entity authored in the Library Manager lives in
    /// an AudioAsset, the asset links itself to <see cref="RuntimeSetting.GlobalPlaybackGroup"/>, and a new
    /// project's global group is a <see cref="DefaultPlaybackGroup"/> with its factory values
    /// (BroUserDataGenerator creates it with CreateInstance and nothing else). So in a shipped project every Play
    /// goes through a 0.04s comb-filtering window that does not exempt same-frame plays.
    /// <para>
    /// The rest of the suite plays code-built entities with no AudioAsset, which no group reaches - that is what
    /// lets it play one ID twice in quick succession. The tests here use
    /// <see cref="BroAudioTestFixture.NewAssetBackedSound"/> to play under
    /// <see cref="BroAudioTestFixture.FactoryGlobalPlaybackGroup"/>, and contrast each rejection with the same
    /// calls on a code-built entity where that explains a difference from the rest of the suite.
    /// </para>
    /// <para>
    /// Rejection is pinned only for plays in the same frame, which is deterministic: the earlier player is still
    /// queued, so the rule counts it as the same frame whatever the frame rate. A replay one frame later but
    /// inside 0.04s depends on the frame rate and is not pinned; the replay after the window is, on a realtime
    /// audio clock.
    /// </para>
    /// </summary>
    public class DefaultPlaybackGroupTests : BroAudioTestFixture
    {
        /// <summary>The comb-filtering window a factory DefaultPlaybackGroup ships with, as a literal oracle.</summary>
        private const float FactoryCombFilteringSeconds = 0.04f;

        /// <summary>
        /// Far more than <see cref="FactoryCombFilteringSeconds"/>, so a slow frame cannot land the replay back
        /// inside the window, and well short of the clip the first play is still playing.
        /// </summary>
        private const float PastTheWindowSeconds = 1f;

        [UnityTest]
        public IEnumerator AssetBackedEntity_ResolvesToTheFactoryGlobalGroup_WhoseWindowIsFortyMilliseconds()
        {
            AudioEntity assetBacked = NewAssetBackedEntity("GlobalGroupResolveSfx");
            AudioEntity codeBuilt = NewEntity("GlobalGroupCodeBuiltSfx");

            Assert.AreSame(FactoryGlobalPlaybackGroup, assetBacked.PlaybackGroup,
                "An entity in an AudioAsset with no group of its own must fall back, through the asset, to the global playback group.");
            Assert.IsFalse((bool)codeBuilt.PlaybackGroup,
                "Contrast: an entity with no AudioAsset has no playback group at all - the global group never reaches it.");

            CombFilteringRule window = TestAudioLibrary.GetPrivateField<CombFilteringRule>(
                FactoryGlobalPlaybackGroup, TestAudioLibrary.Reflected.DefaultPlaybackGroup.CombFilteringTime);
            Assert.AreEqual(FactoryCombFilteringSeconds, window.Value, 1e-6f,
                "A factory DefaultPlaybackGroup's comb-filtering window is the value every shipped Play is held to.");
            yield break;
        }

        // Default acceptance: the global group has no voice limit, and its window is per SoundID, so distinct
        // IDs played in one frame all go through and play.
        [UnityTest]
        public IEnumerator Play_DistinctAssetBackedIdsInOneFrame_AreAllAcceptedAndPlay()
        {
            SoundID first = NewAssetBackedSound("DefaultAcceptA");
            SoundID second = NewAssetBackedSound("DefaultAcceptB");
            SoundID third = NewAssetBackedSound("DefaultAcceptC");

            IAudioPlayer firstPlayer = BroAudio.Play(first);
            IAudioPlayer secondPlayer = BroAudio.Play(second);
            IAudioPlayer thirdPlayer = BroAudio.Play(third);

            Assert.IsTrue(firstPlayer.IsActive, "The first play of an ID under the global group must be accepted.");
            Assert.IsTrue(secondPlayer.IsActive, "A different ID in the same frame must be accepted: the window is per SoundID.");
            Assert.IsTrue(thirdPlayer.IsActive, "A third ID must be accepted too: the factory group has no voice limit.");

            yield return WaitForPlaybackStart(firstPlayer, "the first accepted play to start");
            yield return WaitForPlaybackStart(secondPlayer, "the second accepted play to start");
            yield return WaitForPlaybackStart(thirdPlayer, "the third accepted play to start");
        }

        // Same-frame rejection: the first play is still queued (PlaybackStartingTime 0), which the rule counts as
        // the same frame, and the factory group does not exempt that case. Both plays are global, so no distance
        // exemption applies either. The rejection is logged as a tagged warning, since the factory group has
        // "Log Warning When Occurs" on.
        [UnityTest]
        public IEnumerator Play_SameAssetBackedIdTwiceInOneFrame_RejectsTheSecondWithATaggedWarning()
        {
            SoundID id = NewAssetBackedSound("SameFrameAssetSfx");

            IAudioPlayer first = BroAudio.Play(id);
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer second = BroAudio.Play(id);

            Assert.IsTrue(first.IsActive, "The first play must be accepted.");
            Assert.IsFalse(second.IsActive, "A same-ID replay in the same frame must be rejected by the factory comb-filtering rule.");
            Assert.AreEqual(SoundID.Invalid, second.ID, "A rejected play returns the inert empty player.");

            // Contrast: the identical calls on a code-built entity both go through, which is why the rest of the
            // suite can play one ID twice in a frame.
            SoundID codeBuilt = NewSound("SameFrameCodeBuiltSfx");
            IAudioPlayer codeBuiltFirst = BroAudio.Play(codeBuilt);
            IAudioPlayer codeBuiltSecond = BroAudio.Play(codeBuilt);
            Assert.IsTrue(codeBuiltFirst.IsActive && codeBuiltSecond.IsActive,
                "Contrast: with no AudioAsset no group applies, so both same-frame plays of a code-built entity are accepted.");

            yield return WaitFrames(1);
        }

        // The factory _ignoreIfDistanceIsGreaterThan is 0.1: two positioned plays of one ID in the same frame are
        // exempt when farther apart than that, and rejected when closer.
        [UnityTest]
        public IEnumerator Play_SameAssetBackedIdPositionedInOneFrame_IsExemptOnlyBeyondTheFactoryDistance()
        {
            SoundID farId = NewAssetBackedSound("SameFrameFarSfx");
            SoundID closeId = NewAssetBackedSound("SameFrameCloseSfx");

            IAudioPlayer farFirst = BroAudio.Play(farId, Vector3.zero);
            IAudioPlayer farSecond = BroAudio.Play(farId, new Vector3(1f, 0f, 0f));

            IAudioPlayer closeFirst = BroAudio.Play(closeId, Vector3.zero);
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer closeSecond = BroAudio.Play(closeId, new Vector3(0.05f, 0f, 0f));

            Assert.IsTrue(farFirst.IsActive && closeFirst.IsActive, "The first play of each ID must be accepted.");
            Assert.IsTrue(farSecond.IsActive,
                "Two positioned plays 1 unit apart are farther than the factory 0.1 distance, so the second is exempt.");
            Assert.IsFalse(closeSecond.IsActive,
                "Two positioned plays 0.05 units apart are within the factory 0.1 distance, so the second is rejected.");

            yield return WaitFrames(1);
        }

        // The other side of the window: once more than 0.04s has passed since the first play started, a replay
        // of the same ID is accepted. The first play must still be active when the replay is made - a recycled
        // player leaves the comb-filtering tracker, and the replay would then be accepted for that reason alone.
        // Hence the realtime gate: without an audio device the DSP clock can finish the clip within a frame.
        [UnityTest]
        public IEnumerator Play_SameAssetBackedIdAfterTheWindow_IsAccepted()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewAssetBackedSound("AfterWindowAssetSfx", BroAudioType.SFX, NewClip(5f, "AfterWindowClip"));

            IAudioPlayer first = BroAudio.Play(id);
            yield return WaitForPlaybackStart(first, "the first play to start so its start time is recorded");
            yield return new WaitForSecondsRealtime(PastTheWindowSeconds);

            Assert.IsTrue(first.IsActive,
                "Precondition: the first play must still be active, so the comb-filtering tracker still compares against it.");

            IAudioPlayer second = BroAudio.Play(id);
            Assert.IsTrue(second.IsActive,
                $"A same-ID replay {PastTheWindowSeconds}s after the first started is outside the factory {FactoryCombFilteringSeconds}s window and must be accepted.");
            yield return WaitForPlaybackStart(second, "the accepted replay to start");
        }

        // PlaybackGroup's parent fallback: a custom group whose comb-filtering rule has its override flag off
        // runs the global group's rule instead of its own, so a group that sets no window of its own still
        // rejects a same-frame replay under the shipped global group - and logs with the global group's warning
        // setting, since the rule that runs belongs to it. The same group with the flag on keeps its own
        // (disabled) window and accepts both.
        [UnityTest]
        public IEnumerator Play_CustomGroupNotOverridingCombFiltering_FallsBackToTheGlobalGroupsWindow()
        {
            DefaultPlaybackGroup inheriting = NewGroup();
            CombFilteringRule inheritedRule = TestAudioLibrary.GetPrivateField<CombFilteringRule>(
                inheriting, TestAudioLibrary.Reflected.DefaultPlaybackGroup.CombFilteringTime);
            TestAudioLibrary.SetPrivateField(inheritedRule, Rule<float>.NameOf.IsOverride, false);
            SoundID inheritingId = NewSoundInGroup(inheriting, "InheritedWindowSfx");

            DefaultPlaybackGroup overriding = NewGroup();
            SoundID overridingId = NewSoundInGroup(overriding, "OverriddenWindowSfx");

            IAudioPlayer inheritingFirst = BroAudio.Play(inheritingId);
            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            IAudioPlayer inheritingSecond = BroAudio.Play(inheritingId);

            IAudioPlayer overridingFirst = BroAudio.Play(overridingId);
            IAudioPlayer overridingSecond = BroAudio.Play(overridingId);

            Assert.IsTrue(inheritingFirst.IsActive, "The first play must be accepted.");
            Assert.IsFalse(inheritingSecond.IsActive,
                "A rule that does not override falls back to the global group's rule, whose window rejects a same-frame replay.");
            Assert.IsTrue(overridingFirst.IsActive && overridingSecond.IsActive,
                "Contrast: the same group with its rule overriding keeps its own window of 0, so both plays are accepted.");

            yield return WaitFrames(1);
        }

        /// <summary>A tracked code-built entity wired to <paramref name="group"/> through its own _group field.</summary>
        private SoundID NewSoundInGroup(DefaultPlaybackGroup group, string name)
        {
            AudioEntity entity = NewEntity(name);
            TestAudioLibrary.SetPrivateField(entity, TestAudioLibrary.Reflected.AudioEntity.Group, group);
            return IdOf(entity);
        }
    }
}