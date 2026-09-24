# BroAudio Test Inventory

Ranked plan for the regression suite. Goal: **maximum behavioral confidence per test.**
Coverage is not the highest priority, and coverage percentage is not a target.

Detail lives in four section files, which describe each behavior and how it can be observed. This file
is the index, the ranking, and the only place coverage status is recorded: the tier ledger below and the
[per-behavior ledger](#per-behavior-ledger).

| Section | File |
|---|---|
| Play / Stop / Pause lifecycle | [inventory/lifecycle.md](inventory/lifecycle.md) |
| Volume, pitch, mixer, effects | [inventory/volume-mixer.md](inventory/volume-mixer.md) |
| Time-dependent behavior | [inventory/time-dependent.md](inventory/time-dependent.md) |
| Selection, policy, decorators | [inventory/selection-policy.md](inventory/selection-policy.md) |

Status values: **covered** · **partial** · **deferred** · **out of scope**.
**covered** = the core contract is pinned by a test; **partial** = pinned for some inputs, with the gap
named; **deferred** = not pinned, and testable; **out of scope** = deliberately not tested, with the reason.
The tier sections below still carry the phase they were planned in, which records the build order.

## Coverage ledger

| Tier | Status | Test files |
|---|---|---|
| 0 — EditMode units | **covered** (0.1–0.6) | `ClipSelectionTests.cs`, `AudioMathTests.cs`, `LocalizationClipStrategyTests.cs`, `EaseCurveTests.cs` |
| 1 — Core playback | **partial** (1.1–1.11) | `PlaybackLifecycleTests.cs`, `VolumePitchMixerTests.cs`, `VolumeFadeTests.cs`, `MixerTrackRecycleTests.cs`, `PlaybackEdgeCaseTests.cs`, `ErrorPathTests.cs` |
| 2 — Time-dependent | **partial** (2.1-2.11) | `FadeAndTrimTests.cs`, `LoopHandoverTests.cs`, `ClipDelayAndSchedulingTests.cs`, `ScheduledPlaybackContractTests.cs`, `BGMTransitionTests.cs`, `AlwaysPlayMusicAsBGMTests.cs`, `BGMChangedEventTests.cs`, `BGMEdgeCaseTests.cs` |
| 3 — Selection and policy | **partial** (3.1-3.7) | `PlaybackGroupTests.cs`, `DefaultPlaybackGroupTests.cs`, `ClipSelectionCursorTests.cs`, `LocalizedAudioChangedSubscriptionTests.cs`, `LocalizationRuntimeGuardTests.cs`, `DecoratorAttachmentTests.cs`, `DominatorEffectParameterTests.cs`, `DominatorTrackRoutingTests.cs`, `ChainedLoopDefaultSettingTests.cs` |
| 5 — Addressables | **covered** | `AddressablesTests.cs`, `OptionalPackageTeardownTests.cs` |
| 6 — MonoComponents | **covered** | `SoundSourceTests.cs`, `SoundVolumeTests.cs`, `SpectrumAnalyzerTests.cs` |
| 7 — Structural blind spots | **covered** | `TeardownTests.cs`, `OptionalPackageTeardownTests.cs`, `UpdateModeClockTests.cs`, `AuthoredVolumeTests.cs`, `AuthoredPitchAndRandomizationTests.cs`, `SpatialAndPriorityTests.cs` |
| Suite guards | **covered** | `OptionalPackageTests.cs`, `AudioClockProbeTests.cs`, `ReflectionCanaryTests.cs` (PlayMode); `OptionalPackageEditorTests.cs`, `FindingCoverageTests.cs`, `EditorRunIsolationGuard.cs` (EditMode) |

Tier status is the summary, and a tier is **partial** when any of its rows below is; the **per-behavior**
ledger required by the plan's Definition of Done is [below](#per-behavior-ledger), one table per section
file, where every inventoried behavior is marked covered / partial / deferred / out of scope with the test
that pins it.

Per-file detail beyond the tier ledger above:

- `AudioEffectTests.cs` covers the per-player Unity filter surface (`AddChorusEffect`/`AddLowPassEffect`/etc.
  — attach, duplicate, remove, recycle cleanup, the `OnAudioFilterRead` callback, exposed parameter writes),
  the `GetOutputData` tap,
  and the mixer-routed `BroAudio.SetEffect` automation.
- `SoundSourceTests.cs` covers the `SoundSource` no-code component: the three `PositionMode`s (global stays
  2D; StayHere snapshots the transform; FollowGameObject tracks it — also this suite's only coverage of
  `BroAudio.Play(SoundID, Transform)`), the Play On Enable / Only Play Once / Stop On Disable / Override
  Fade Out / Delay / Override Playback Group inspector toggles, Play's replace-don't-layer semantics, and
  the guard clauses that keep Stop/Pause/UnPause/SetVolume/SetPitch inert without a live player.
- `SoundVolumeTests.cs` covers both halves of the component's contract: the system half (Apply On Enable,
  Only Apply Once, Reset On Disable's record-at-enable / restore-at-disable pairing, a composite `[Flags]`
  audio type fanning out over the types it contains) and the slider half (volume ↔ slider mapping per
  `SliderType`, rounding to `RoundingDigits`, Allow Boost, and listener add/remove across enable/disable).
- `SpectrumAnalyzerTests.cs` covers source acquisition (`SetSource`, the serialized `SoundSource` fallback,
  going quiet once the player is recycled), `Start`-time buffer sizing, and per-band ballistics (attack,
  decay, smoothing), plus an end-to-end tone test. Most of its tests drive a **silent** clip on purpose:
  with an all-zero spectrum every band's target is exactly the decibel floor, so the ballistics stay
  deterministic regardless of what the machine's actual audio device produces.
- `TeardownTests.cs` covers the `SoundManager.Instance` / `BroAudio.Manager` asymmetry that CLAUDE.md calls
  load-bearing: the facade's release verbs are silent no-ops with the manager destroyed, play verbs throw
  by contrast, and a player handle held across the manager's destruction stays inert. The contract does
  **not** extend to `SetEffect` or to handle-level release verbs, both of which throw. It also pins the
  manual-init contract: `SoundManager.Init`'s auto-bootstrap attribute tracks `BroAudio_InitManually`, and
  `Init()` on an absent manager yields one that plays.
- `UpdateModeClockTests.cs` covers `RuntimeSetting.UpdateMode` and `Utility.GetDeltaTime` — the clock behind
  every fade, pitch tween and scheduled start: under `Time.timeScale == 0` a fade progresses in
  `UnscaledTime` and freezes in `Normal`.
- `AuthoredVolumeTests.cs` covers `clip.Volume * entity.MasterVolume` (`AudioPlayer.Playback`) — both
  factors default to 1 everywhere else in the suite, so this product previously had no other observer.
- `SpatialAndPriorityTests.cs` covers spatial settings and `entity.Priority`, including what a pooled
  player carries into its next sound.
- `PlayFadeInOverloadTests.cs` covers `Play(id, position, fadeIn)` and `Play(id, followTarget, fadeIn)`: each
  call places a 3D voice at (or tracking) its target and ramps it from silence over the given fade.
- `VolumeFadeTests.cs` covers the timed half of `SetVolume` — per-type, per-`SoundID` and per-handle ramps on
  the frame clock, a ramp reversed mid-flight, and a `Stop` fade that starts from the current level.
- `AuthoredPitchAndRandomizationTests.cs` covers the entity's authored pitch (and a per-type pitch replacing
  it, TEST_FINDINGS #55), per-play volume and pitch randomization, and master `SetPitch` (#56).
- `EaseCurveTests.cs` (EditMode) covers `EaseExtension.SetEase` — the curve behind every fade — against
  hand-derived literal values, never against `SetEase` itself, including out-of-range input (#53) and an
  undefined `Ease` (#54).
- `MixerTrackRecycleTests.cs` covers what a mixer track looks like when it goes back to the pool — muted by
  `SilenceTrackBeforeReturn`, on its dry side and its effect send — and that the next play takes that same
  track back (behind `!UNITY_WEBGL`, where tracks exist).
- `DefaultPlaybackGroupTests.cs` runs `AudioAsset`-backed entities (`NewAssetBackedSound`) under the
  factory global playback group every shipped entity plays under — its 0.04 s comb-filtering window with
  same-frame plays not exempt — and `PlaybackGroup`'s parent fallback to it.
- `ErrorPathTests.cs` covers misuse the rest of the suite never drives: a clip slot with no `AudioClip`, a
  null follow target (TEST_FINDINGS #60), `UnPause` on a player that is not paused or is fading out (#65), and
  per-type `SetVolume`/`SetPitch` with `BroAudioType.None` or Unity's "Everything" (-1).
- `PlaybackEdgeCaseTests.cs` covers the far side of the comb-filtering window, a pause that outlasts the rest
  of the clip, a looping entity with a clip `Delay`, and the one place BroAudio writes `AudioSource.volume`
  (a player with no mixer track).
- `BGMEdgeCaseTests.cs` covers `OnBGMChanged` staying quiet across a looping BGM's handover seam, and what
  `StopMode.Mute` amounts to: the muted player keeps running silently and is never recycled until stopped
  (TEST_FINDINGS #67).
- `LocalizationRuntimeGuardTests.cs` (behind `PACKAGE_LOCALIZATION`) covers load, release and play of a
  Localization entity with no table — every path reachable without an `AssetTable` fixture.
- `OptionalPackageTeardownTests.cs` extends `TeardownTests`' sweep to the Addressables/Localization facade
  verbs: release verbs no-op with the manager gone, load/query verbs throw `BroAudioException`.
- The suite guards fail a run that silently covers less than it claims. `OptionalPackageTests.cs` (PlayMode)
  and `OptionalPackageEditorTests.cs` (EditMode) fail when `PACKAGE_ADDRESSABLES` or `PACKAGE_LOCALIZATION`
  is not compiled in exactly when the run expects it: undefined on an ordinary run, since a suite behind that
  define would otherwise compile to nothing, and still defined on the CI leg that removed both packages on
  purpose (`-broaudioCiExpectsNoOptionalPackages`).
  `AudioClockProbeTests.cs` fails when the DSP clock is not realtime on a machine that promises an audio
  device (`BROAUDIO_CI_EXPECTS_AUDIO`), where the tests gated on `RequireRealtimeAudioClock` would
  otherwise all be ignored. `FindingCoverageTests.cs` (EditMode) reconciles `[Category("Finding_N")]` tags
  on runnable tests in both assemblies with the findings in TEST_FINDINGS.md, in both directions, and holds
  that file's summary table to its sections and FIXED_ISSUES.md to disjoint numbers.
  `ReflectionCanaryTests.cs` resolves every name in `TestAudioLibrary.Reflected` against its production type
  in one place, so a production rename fails once, by name, instead of in whichever tests reach it.
  `EditorRunIsolationGuard.cs` is the EditMode assembly's `[SetUpFixture]`: it compares the settings
  assets' bytes on disk, the EditorPrefs keys BroAudio writes, the clipboard and the temp folder before the
  first test and after the last.

Every PlayMode test runs against factory `RuntimeSetting` values: `BroAudioTestFixture` resets the asset after
snapshotting it, since the asset is gitignored and a developer's copy may differ from the one CI generates.
That includes `GlobalPlaybackGroup`, which the fixture sets to a fresh factory `DefaultPlaybackGroup` per
test — the configuration users ship. It reaches only `AudioAsset`-backed entities (`NewAssetBackedSound`);
code-built ones (`NewSound`) have no asset and play outside any group, which is what lets most tests play
one ID twice in quick succession. Under `BroAudio_InitManually` the fixture calls `BroAudio.Init()` once
itself, as a project on that define must.

TearDown drains every player, then destroys the objects the test tracked — before any global state is put
back, since a component's `OnDisable` (a `SoundVolume` with Reset On Disable) is itself a writer of it —
then resets effect parameters and volumes, and finally verifies, by polling, that the per-type volume and
pitch prefs, the per-type LowPass/HighPass effect bits and the dominator's `Main_LowPass` / `Main_HighPass`
read their defaults. Anything left over is reported under the test's name.
Provoked logs are checked by `LogType` and BroAudio's tag (`TestAudioLibrary.BroAudioLogPrefix`), never by
their sentence; a log with no tag is checked by its `LogType` alone. CI enforces this with
`.github/scripts/check_log_expectations.py`, whose short allowlist names each untagged Unity log a test may
expect.

Findings any of these files surfaced are logged in [TEST_FINDINGS.md](TEST_FINDINGS.md), keyed to the
file that found them. Every fixture passes in isolation, so no test depends on another having run, and a
nightly CI run executes every leg in a random order (`-randomOrderSeed`) to catch a test that only passes
after another. The only tests allowed to report Inconclusive under a random order are listed in
`.github/test-results-policy.json`.

**Tier 0 lives in the EditMode assembly.** `ClipSelectionTests.cs`, `AudioMathTests.cs` and
`LocalizationClipStrategyTests.cs` are plain `[Test]`s with no `[UnityTest]` and no `SoundManager`, so they
need no Play Mode. They live in `Assets/Tests/Editor/` and compile into `EditorTests.asmdef`, running in
the EditMode lane rather than alongside the PlayMode suite.

**Caveat:** a file authored without a connected Unity Editor to compile and run it is not proven until it
has been. Before trusting this ledger for a given file, confirm it has actually run green.

`RuntimeSetting.DefaultAudioPlayerPoolSize` is **deferred**: it is read when `SoundManager` bootstraps, so
mutating it on the live manager has no effect. It is still testable — `TeardownTests` already destroys the
manager and calls `SoundManager.Init()`, which bootstraps a fresh one that reads the setting.

`Tests.asmdef` references `Unity.Localization` (needed for tier 0.6) and `UnityEngine.UI` (needed once
tier 6 reached `SoundVolume`, which holds a `UnityEngine.UI.Slider` directly) — asmdef references are not
transitive, so referencing `BroAudio` does not bring either into scope for the test assembly.

Both test asmdefs reference the **optional** packages (`Unity.Addressables`, `Unity.ResourceManager`,
`Unity.Localization`, `Unity.Addressables.Editor`) by GUID rather than by name, matching the shipped
`BroAudio.asmdef`: an unresolved by-name reference makes Unity refuse to build the whole assembly, so with
a package absent the suite would compile to nothing and every `#if PACKAGE_*` inside it would be moot. A
GUID reference is dropped quietly instead, and the `versionDefines` do their job. Non-optional references
(`BroAudio`, the test runners, `UnityEngine.UI`) stay by name — they always resolve.

---

## Per-behavior ledger

The status of every behavior the four section files describe, with the tests that pin it. The section
files say what each behavior is and how it can be observed; the status lives only here.

### Play / Stop / Pause lifecycle

Behaviors: [inventory/lifecycle.md](inventory/lifecycle.md).

| Behavior | Status | Pinned by |
|---|---|---|
| Play — global / positioned / follow-target | covered | `PlaybackLifecycleTests.IsActiveAndIsPlaying_AroundQueueDrain_TrackDifferentWindows` (global); `PlaybackGroupTests.Play_PositionedFarApart_*` (positioned); `SoundSourceTests.Play_WithFollowGameObjectPositionMode_KeepsTheVoiceOnTheMovingHost` (follow-target, through `SoundSource`); the fade-in overloads of both by `PlayFadeInOverloadTests`. |
| Play returns Empty.AudioPlayer when the sound is not playable | covered | `PlaybackLifecycleTests.Play_RejectedByValidator_ReturnsInertEmptyPlayer` |
| Play with a null follow target | covered | `ErrorPathTests.Play_WithANullFollowTarget_ThrowsNullReferenceExceptionBeforeAnyValidation` — throws before any validation, even for `SoundID.Invalid` (TEST_FINDINGS #60) |
| Play of an entity whose clip slot holds no AudioClip | covered | `ErrorPathTests.Play_SingleModeEntityWhoseSlotHasNoAudioClip_IsAcceptedThenLogsOneErrorAndRecyclesSilently`, `Play_RandomModeEntityWhoseSlotsHaveNoAudioClip_SelectsAnEmptySlotThenLogsOneErrorAndRecycles` |
| Stop by SoundID | covered | `PlaybackLifecycleTests.Stop_BySoundID_StopsEveryInstanceOfThatIdAndLeavesOtherIdsPlaying` — every live instance of the ID stops while another ID of the same `BroAudioType` keeps playing, so a match by type would fail it. |
| Stop by BroAudioType, including the All flag | covered | `PlaybackLifecycleTests.Stop_WithAllFlag_DeactivatesEveryConcreteType`, `Stop_WithSingleFlag_LeavesOtherTypesPlaying`; with a fade across one-shots and a loop, `LoopHandoverTests.Stop_ByTypeWithFade_FadesOneShotsButALoopFallsSilentAtItsCurrentIterationEnd` (TEST_FINDINGS #58) |
| Stop with a completion callback | covered | `PlaybackLifecycleTests.Stop_WithOnFinishedCallback_FiresAfterTheFadeButIsDroppedByARecycledHandle` (TEST_FINDINGS #41) |
| Pause / UnPause by SoundID and by BroAudioType | covered | `PlaybackLifecycleTests.Pause_ThenUnPause_FreezesAndResumesFromSamePosition`, `Pause_BySoundID_*`, `Pause_ByBroAudioType_*`, `Pause_ByTypeWithFadeTime_*`; the clip fade-in on resume, `UnPause_OnClipWithFadeIn_RestartsTheFadeInFromSilenceUnlessOverridden`; a pause longer than the rest of the clip, `PlaybackEdgeCaseTests.Pause_LongerThanTheRemainingClip_ResumesFromThePausedSampleAndPlaysTheRemainder`; `Pause()` with no fade using the clip's own `FadeOut`, `FadeAndTrimTests.Pause_WithoutAFade_FadesOutOverTheClipsAuthoredFadeOutThenFreezes` |
| UnPause misuse — on a player that is not paused, during a faded Stop, during a Pause fade-out | covered | `ErrorPathTests.UnPause_OnAPlayerThatIsNotPaused_WarnsAndLeavesPlaybackUntouched`, `UnPause_WhileAFadedStopIsInProgress_WarnsAndTheStopStillCompletes`, `UnPause_DuringAPauseFadeOut_ResumesButLeavesIsStoppingSet_SoALaterFadedStopIsIgnored` (the last pins that `IsStopping` stays set, so a later faded `Stop` is discarded; TEST_FINDINGS #65) |
| StopMode.Mute — reachable only as a BGM transition stop mode | covered | `BGMTransitionTests.SetTransition_WithStopModeMute_MutesOutgoingBGMButLeavesItAudiblyPlaying`; what "until it's played again" amounts to by `BGMEdgeCaseTests.StopModeMute_PlayingTheSameSoundAgainStartsANewPlayerAndLeavesTheMutedOneRunningSilently` and `StopModeMute_TheMutedPlayerIsNeverRecycledWhenItsClipEnds_OnlyAnExplicitStopFreesIt` (TEST_FINDINGS #67) |
| Player recycling and the stale-handle contract | covered | `PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal` (both the inert-handle and the `AudioSource`-resolves-to-null dimensions) |
| Same pooled AudioPlayer instance is reused across independent Play calls | covered | `SpatialAndPriorityTests.Recycle_AfterA3DSound_ResetsScalarSpatialStateButLeavesTheCustomRolloffCurveBehind` asserts the next `Play` gets the just-recycled `AudioPlayer` back (`Assert.AreSame`). `VolumePitchMixerTests.Play_AcquiresPooledMixerTrackAndReusesOneAfterRecycle` is about the mixer track, not the player, and does not check identity. |
| OnStart / OnUpdate / OnPause / OnEnd callbacks | covered | `PlaybackLifecycleTests.Callbacks_OnStartOnUpdateOnPause_FireWithExpectedCounts`, `OnEnd_WhenPlaybackFinishes_FiresOnceWithOriginalID` |
| IsActive vs IsPlaying | covered | `PlaybackLifecycleTests.IsActiveAndIsPlaying_AroundQueueDrain_TrackDifferentWindows` |
| Empty.AudioPlayer — the null-object path | covered | `PlaybackLifecycleTests.Play_RejectedByValidator_ReturnsInertEmptyPlayer` |
| Comb-filtering preventer bookkeeping | covered | `PlaybackGroupTests.Play_SameID_WithinCombFilteringWindow_RejectsSecond` and its sibling cases, including the two rejecting negative controls for the distance exemption; the far side of the window by `PlaybackEdgeCaseTests.Play_SameIdAfterTheCombFilteringWindowExpires_IsAcceptedAgain`; under the shipped global group by `DefaultPlaybackGroupTests` (see the selection-policy table below) |
| Release verbs and load/query verbs of the optional packages once the manager is gone | covered | `TeardownTests` for the core verbs; `OptionalPackageTeardownTests.ReleaseVerbs_ForOptionalPackages_WithManagerDestroyed_AreSilentNoOps` and `LoadAndIsLoadedVerbs_ForOptionalPackages_WithManagerDestroyed_ThrowBroAudioException` for the Addressables/Localization ones |

### Selection, policy, decorators

Behaviors: [inventory/selection-policy.md](inventory/selection-policy.md).

| Behavior | Status | Pinned by |
|---|---|---|
| Single mode always plays clips[0] | covered | `ClipSelectionTests.SelectClip_WithSetClips_AlwaysReturnsFirstClip` plus the null-array, null-reference and unset-clip cases |
| Sequence mode cycles 0..N-1 and wraps | covered | `ClipSelectionTests.SelectClip_Repeatedly_CyclesThroughClipsAndWrapsToStart`, `_WithSingleClip_AlwaysReturnsIndexZero`, `_WithUnsetClipMidSequence_LogsErrorThenRestartsFromZero`, `Reset_RestartsDefaultSequenceFromZero` |
| Sequence mode: named SequenceIds run independent cursors | covered | `ClipSelectionTests.SelectClip_WithTwoSequenceIds_AdvancesIndependently`, `_WithNullSequenceId_SharesDefaultCursor`, `Reset_WithSequenceId_OnlyResetsThatNamedCursor`; through `BroAudio.ResetMultiClipStrategy(id, sequenceId)` on a live manager by `ClipSelectionCursorTests.SetSequenceId_WithDifferentIds_*` |
| `IAudioPlayer.CurrentPlayingClip` is the entity row picked for that play | covered | `ClipSelectionCursorTests.Play_SameSequenceEntityPlayedTwice_*`; null on a recycled handle in `PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal` |
| Random mode: uniform when all Weights are 0, weighted otherwise | covered | Weighted: `ClipSelectionTests._WithAnyNonzeroWeight_NeverSelectsZeroWeightClips`, and the share of each non-zero weight, under a fixed seed, by `_WithWeightsOneAndThree_*`, `_WithWeightsOneTwoAndFive_*` and `_WithAZeroWeightBetweenTwoNonzeroOnes_*`. Uniform: `ClipSelectionTests.SelectClip_WithAllWeightsZero_DrawsEveryClipAboutEquallyOften` checks each clip's share over 4000 seeded draws, alongside the range check `SelectClip_WithAllWeightsZero_ReturnsIndexWithinRange`. |
| Shuffle never repeats the previous clip, and cycles the pool | covered | `ClipSelectionTests.SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent` — the test pins the actual behavior, which contradicts the documented intent (TEST_FINDINGS #9). That the long-run shares are uniform, by `SelectClip_OverManyDraws_ReturnsEveryClipAboutEquallyOften`. Playing each clip once per cycle is not part of Shuffle's contract (withdrawn #68), so nothing pins it either way. |
| Shuffle vs Random: the guarantee Random does not make | covered | Same pair, plus `SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip` |
| Velocity mode selects by highest Weight threshold not exceeded | covered | `ClipSelectionTests.SelectClip_WithValueBelowEveryThreshold_*`, `_WithValueBetweenThresholds_*`, `_WithValueAboveEveryThreshold_*`, `_WithNonMonotonicWeights_*` |
| Chained mode maps PlaybackStage to a fixed clip index | covered | `ClipSelectionTests.SelectClip_AtStartStage/AtLoopStage/AtEndStage/AtNoneStage_*`, `_WithTooFewClipsForStage_*` |
| Localization mode selects the row matching the active locale | covered | `LocalizationClipStrategyTests` (behind `PACKAGE_LOCALIZATION`) |
| `SubscribeLocalizedAudioChanged` / `SoundID.LocalizedAudioChanged` | partial | `LocalizedAudioChangedSubscriptionTests` pins the guards (not Localization mode, no table/entry set). A handler actually firing needs an `AssetTable` fixture — deferred, see [Deferred](#deferred) below. |
| Localization-mode load / release / play without a table | covered | `LocalizationRuntimeGuardTests` (behind `PACKAGE_LOCALIZATION`): `LoadAssetAsync_ForALocalizationEntityWithoutATable_WarnsAndReturnsAnInvalidHandle`, `ReleaseVerbs_ForALocalizationEntityThatWasNeverLoaded_AreSilentNoOps`, `Play_ForALocalizationEntityWithoutATable_LogsOneErrorAndRecyclesWithoutSounding`. Everything past those guards — a resolved locale clip, the preload cache, a locale switch — stays deferred until an `AssetTable` fixture exists. |
| ChangeClipPerLoop re-picks a clip on every loop iteration | covered | `LoopHandoverTests.Loop_WithChangeClipPerLoopAndSequence_AdvancesClipAtEachSeam` |
| RandomFlag.Volume / RandomFlag.Pitch apply ± half-range jitter | covered | `ClipSelectionTests.GetRandomValueStatic_*` and `GetRandomValue_*` |
| MaxPlayableCountRule rejects Play at the limit | covered | `PlaybackGroupTests.Play_BeyondMaxPlayableCount_RejectsThenAcceptsAfterASlotFrees` |
| The voice-limit count increments at enqueue, not at audible start | covered | `PlaybackGroupTests.Play_TwoPlaysInSameFrame_BothCountAgainstLimitBeforeEitherStartsPlaying` |
| CombFilteringRule rejects a same-ID replay in the window | covered | `PlaybackGroupTests.Play_SameID_WithinCombFilteringWindow_RejectsSecond` and the flag/position variants, whose exemption pins each have a rejecting negative control (`Play_PositionedCloseTogether_WithinCombFilteringWindow_RejectsSecond`, `Play_GlobalThenPositioned_WithDistanceExemptionOff_RejectsSecond`; TEST_FINDINGS #12); acceptance once the window has passed by `PlaybackEdgeCaseTests.Play_SameIdAfterTheCombFilteringWindowExpires_IsAcceptedAgain` |
| The shipped global playback group (`RuntimeSetting.GlobalPlaybackGroup`) and `PlaybackGroup`'s parent fallback | covered | `DefaultPlaybackGroupTests`, on entities built with `NewAssetBackedSound` so they play under the fixture's factory global group as a Library Manager entity does: `AssetBackedEntity_ResolvesToTheFactoryGlobalGroup_WhoseWindowIsFortyMilliseconds`, `Play_DistinctAssetBackedIdsInOneFrame_AreAllAcceptedAndPlay`, `Play_SameAssetBackedIdTwiceInOneFrame_RejectsTheSecondWithATaggedWarning`, `Play_SameAssetBackedIdPositionedInOneFrame_IsExemptOnlyBeyondTheFactoryDistance`, `Play_SameAssetBackedIdAfterTheWindow_IsAccepted`; the parent fallback of a custom group that does not override the rule by `Play_CustomGroupNotOverridingCombFiltering_FallsBackToTheGlobalGroupsWindow`. A replay one frame later but still inside 0.04 s depends on the frame rate and is not pinned. |
| A custom IPlayableValidator overrides the entity's PlaybackGroup | covered | `PlaybackGroupTests.Play_WithCustomValidator_OverridesGroupEntirely` |
| AsBGM() attaches a MusicPlayer decorator — composition, not a subtype swap | covered | `DecoratorAttachmentTests.AsBGM_CalledTwice_ReturnsTheSameMusicPlayerDecoratorInstance` (asserts via the private `_decorators` list) |
| Calling AsBGM() twice returns the same decorator instance | covered | Same test |
| AsDominator() attaches independently of AsBGM() | covered | `DecoratorAttachmentTests.AsBGM_AndAsDominator_CoexistOnTheSamePlayer` |
| AlwaysPlayMusicAsBGM auto-attaches the BGM decorator | covered | `AlwaysPlayMusicAsBGMTests.AlwaysPlayMusicAsBGM_Enabled_*` and `_Disabled_*` |
| Every chaining method is null-safe on a recycled/invalid player | partial | `PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal` covers the stale-handle path; the full fluent surface is not swept method by method. |
| SetVelocity and SetSequenceId are guarded no-ops outside their own mode | deferred | No test calls either outside its own mode, so the guard never fires. `ClipSelectionCursorTests.SetVelocity_CalledBeforeQueueDrains_*` and `SetSequenceId_WithDifferentIds_*` run in the matching mode and pin the in-mode behavior instead. |
| `RuntimeSetting.AutomaticallyLoadAddressableAudioClips` | covered | The on-state by `AddressablesTests.Play_WithAutomaticLoadingEnabled_LoadsTheAddressableClipAndPlaysIt` against the committed addressable tones; the factory off-state (logs an error, then loads and plays anyway) by `Play_WithTheFactoryDefaultAutomaticLoadingOff_LogsAnErrorThenLoadsAndPlaysAnyway`, and the log level's only effect by `Play_WithTheNonPreloadedLogLevelAtWarning_WarnsInsteadAndStillLoadsAndPlays`. A key that cannot load: `LoadAssetAsync_WithAKeyNoCatalogResolves_CompletesFailedAndBroAudioLogsNothingOfItsOwn`, and `Play_WithAKeyThatCannotLoad_ThrowsOutOfPlayControlAndStrandsThePlayerActiveAndSilent` (TEST_FINDINGS #66) |
| RuntimeSetting toggles that change Play behavior | partial | `AlwaysPlayMusicAsBGM` is covered above; `AutomaticallyLoadAddressableAudioClips` both ways by `AddressablesTests` (row above); `GlobalPlaybackGroup` by `DefaultPlaybackGroupTests` (row above). `DefaultAudioPlayerPoolSize` is **deferred**: it is read when `SoundManager` bootstraps, and a test can force a fresh bootstrap the way `TeardownTests` does (destroy the manager, call `SoundManager.Init()`), so it is testable. |

### Time-dependent behavior

Behaviors: [inventory/time-dependent.md](inventory/time-dependent.md).

| Behavior | Status | Pinned by |
|---|---|---|
| Fade in — from clip's own FadeIn setting | covered | `FadeAndTrimTests.Play_WithClipFadeIn_RampsVolumeUpFromSilence` |
| Fade in — on resume from pause | covered | `PlaybackLifecycleTests.UnPause_OnClipWithFadeIn_RestartsTheFadeInFromSilenceUnlessOverridden` |
| Fade in — explicit override argument | covered | `FadeAndTrimTests.Play_WithExplicitFadeInOverride_IsConsumedOnceThenFallsBackToClipSetting` — pins the one-shot consume |
| Fade in — easing curve (`SetFadeInEase`) | covered | `FadeAndTrimTests.SetFadeInEase_AndSetFadeOutEase_ShapeExplicitFades` samples an explicit fade-in a third of the way in, where the chosen ease and the factory ease sit far apart. The setter shapes only explicit fades (`Play(id, fadeIn)`); a clip's own `FadeIn` and `FadeOut` keep the factory ease, pinned by `SetFadeInEase_AndSetFadeOutEase_DoNotShapeTheClipsOwnAuthoredFades` (TEST_FINDINGS #70). The curves themselves are pinned against literals by `EaseCurveTests`. |
| Fade out — from clip's own FadeOut setting, natural end | covered | `FadeAndTrimTests.Play_WithClipFadeOut_RampsVolumeDownBeforeNaturalEnd` |
| Fade out — explicit `Stop(fadeOut)` override | covered | `VolumeFadeTests.Stop_WithFade_DuringFadeIn_RampsDownFromCurrentLevelNotFromFull` samples the ramp; `LoopHandoverTests.Stop_ByTypeWithFade_*` fades one-shots out. `FadeAndTrimTests.Stop_SecondNonImmediateCall_*` and `Stop_WithImmediateFade_*` pin the `IsStopping` guard around it. |
| Fade out easing (`SetFadeOutEase`) | covered | Same test as fade-in easing, sampling an explicit `Stop(fadeOut)` a third of the way in. |
| Clip StartPosition (trim from the front) | covered | `FadeAndTrimTests.Play_WithClipStartPosition_BeginsPlaybackPartwayIntoClip` |
| Clip EndPosition (trim from the back) | covered | `FadeAndTrimTests.Play_WithClipEndPosition_EndsPlaybackBeforeClipLength` |
| Clip Delay (per-clip, not per-call) | covered | `ClipDelayAndSchedulingTests.Play_WithClipDelayOnly_PostponesAudibleStartButNotIsPlaying` |
| Scheduled start time — `SetScheduledStartTime` / `SetDelay` | covered | `ClipDelayAndSchedulingTests.SetScheduledStartTime_CalledBeforeQueueDrains_OverridesClipDelay`, `SetDelay_CalledBeforeQueueDrains_*`; `ScheduledPlaybackContractTests.SetScheduledStartTime_OnAlreadyPlayingSource_StallsPlayheadWithoutChangingIsPlaying` |
| Scheduled end time — `SetScheduledEndTime` | covered | `ScheduledPlaybackContractTests.SetScheduledEndTime_StopsPlaybackAtExplicitDspTimeRegardlessOfClipLength` |
| Mid-play pitch change rescaling the derived end time | partial | `ScheduledPlaybackContractTests.SetPitch_AboveOneMidPlay_ShortensDerivedRemainingDuration`, `SetPitch_BelowOneMidPlay_LengthensDerivedRemainingDuration`, `SetPitch_AfterExplicitScheduledEndTime_DoesNotRescaleEndTime`; on the seam players of a loop, `LoopHandoverTests.Play_WithPlainLoop_PitchBelowOneAndFollowTargetRideAcrossTwoSeams`. Not pinned: pitch at or below 0, a change while paused, a gradual change during a live `PitchControl` fade, and a per-handle change after the next seam player is pre-spawned, which does not reach it (TEST_FINDINGS #72, not pinned: the window is too narrow to land a `SetPitch` in reliably). |
| Plain looping (`LoopType.Loop`) | covered | `LoopHandoverTests.Play_WithPlainLoop_HandleKeepsDrivingTheSoundAcrossTwoSeams` |
| What a handover carries across a loop seam — pitch, follow target, an in-flight volume fade, fixed position, per-type track effect | covered | `LoopHandoverTests.Play_WithPlainLoop_PitchBelowOneAndFollowTargetRideAcrossTwoSeams` (pitch and follow target, with the seam period measured on the DSP clock), `Play_WithPlainLoop_InFlightFadeTrackEffectAndPositionRideAcrossSeams` (a 3 s fade spanning several 0.5 s iterations, the effect send, the position); `OnBGMChanged` not firing at a looping BGM's seam by `BGMEdgeCaseTests.OnBGMChanged_AcrossALoopingBGMsHandoverSeam_DoesNotFire_ButCurrentBGMPlayerFollowsTheHandover` |
| A looping entity with a clip `Delay` | covered | `PlaybackEdgeCaseTests.Loop_WithAClipDelay_DelaysOnlyTheFirstIterationAndNotEachSeam` |
| Seamless looping with a transition time | covered | `LoopHandoverTests.Play_WithSeamlessLoop_CrossfadesTwoPlayersAcrossTheSeam`; a transition longer than the clip by `SeamlessLoop_WithTransitionLongerThanTheClip_LoopsOncePerTransitionWithABoundedPlayerCount` (TEST_FINDINGS #59: the period stretches to the transition) |
| Chained playback (intro → loop → outro) | covered | `LoopHandoverTests.ChainedPlayMode_HandsOverIntroToLoopToOutro_OutroHandoverFiresSynchronouslyOnStop` |
| BGM transitions (`SetTransition`) | covered | `BGMTransitionTests.SetTransition_Default_*`, `SetTransition_OnlyFadeOut_*`, `SetTransition_OnlyFadeIn_*`; `Immediate` through `BGMChangedEventTests.OnBGMChanged_*` and the `StopMode.Pause`/`Mute` transition tests. CrossFade: the overlap by `SetTransition_CrossFade_OutgoingAndIncomingBGMOverlap`, and the outgoing BGM ending once its fade-out completes by `SetTransition_CrossFade_EndsOutgoingBGMOnceItsFadeOutCompletes`. |
| `AlwaysPlayMusicAsBGM` (RuntimeSetting) | covered | `AlwaysPlayMusicAsBGMTests.AlwaysPlayMusicAsBGM_Enabled_*`, `_Disabled_*` |
| `OnBGMChanged` event | covered | `BGMChangedEventTests.OnBGMChanged_WhenANewBGMReplacesTheCurrentOne_ReportsTheNewPlayer`; the double-fire quirk (TEST_FINDINGS #11) is also characterized by this test. That a looping BGM's handover seam does not raise it, while `CurrentBGMPlayer` follows the handover: `BGMEdgeCaseTests.OnBGMChanged_AcrossALoopingBGMsHandoverSeam_DoesNotFire_ButCurrentBGMPlayerFollowsTheHandover` |
| Stop with fade — general | covered | An explicit fade is pinned (see the `Stop(fadeOut)` row). `Stop()` and `Pause()` with no fade argument fall back to the clip's authored `FadeOut` (`FadeData.UseClipSetting`): `FadeAndTrimTests.Stop_WithoutAFade_FadesOutOverTheClipsAuthoredFadeOut`, `Pause_WithoutAFade_FadesOutOverTheClipsAuthoredFadeOutThenFreezes`. `StopControl`'s don't-double-fade branch — a `Stop()` during the clip's own fade-out waits it out rather than restarting it — by `Stop_WhileTheClipsOwnFadeOutRuns_WaitsItOutInsteadOfRestartingIt`. On a loop, TEST_FINDINGS #58. |
| A pause longer than the rest of the clip | covered | `PlaybackEdgeCaseTests.Pause_LongerThanTheRemainingClip_ResumesFromThePausedSampleAndPlaysTheRemainder` |
| Pause across a handover seam | covered | `LoopHandoverTests.Pause_DuringSeamlessLoopHandoverSeam_DoesNotThrowAndResumes` |

### Volume, pitch, mixer, effects

Behaviors: [inventory/volume-mixer.md](inventory/volume-mixer.md).

| Behavior | Status | Pinned by |
|---|---|---|
| Master volume writes to the mixer's Master exposed parameter | covered | `VolumePitchMixerTests.SetVolume_Master_WritesDirectlyToMixerAndNeverEntersLinearProduct` |
| Per-BroAudioType volume affects live and future players | covered | `VolumePitchMixerTests.SetAudioTypeVolume_ToExactlyDefault_PushesLive_AndNonDefaultAppliesToFuturePlayersAndMixer` — covers the exactly-1f edge case on live players and a non-default factor on future ones |
| Per-SoundID volume composes with the per-type factor | covered | `AuthoredVolumeTests.SetVolume_ComposesMultiplicativelyWithTheAuthoredClipAndMasterVolume`. The exclusivity claim — that a per-SoundID call reaches only players with that ID — is pinned for pitch (`VolumePitchMixerTests.SetPitch_BySoundId_AppliesToThatInstanceOnly`) but not for volume. |
| Clip volume and Entity MasterVolume bake into one Fader | partial | The linear product is pinned by the test above; the clip-vs-entity split is unobservable through the public API, so the two factors are not separated. |
| Volume fades run in linear space, re-converted to dB each frame | covered | `VolumeFadeTests.SetVolume_ByTypeWithFade_RampsLivePlayerOverDuration` asserts mid-ramp that the track's exposed parameter equals `20*Log10(GetVolume())` in the same frame — an identity a dB-space ramp, or one that only writes the mixer at completion, cannot satisfy. |
| Fade direction picks ease from FadeInEase/FadeOutEase; re-anchors mid-fade | covered | `VolumeFadeTests.SetVolume_BySoundIdThenByHandleWithFade_ReanchorsOnTheCurrentLevelMidFlight` reverses a ramp in flight, and `VolumeFadeTests.Stop_WithFade_DuringFadeIn_RampsDownFromCurrentLevelNotFromFull` interrupts a clip fade-in — both read the re-anchored level in the frame of the call. |
| Unrouted playback when the mixer track pool is exhausted, and its warning | partial | Dominator pool: `DominatorTrackRoutingTests.AsDominator_BeyondThePoolCapacity_PlaysUnroutedAndWarns`, which also expects the pool's warning. The generic pool is deferred. Playing enough sounds to exhaust it is cheap; the complication is that at that voice count Unity's voice virtualization (Max Real Voices) starts, and the virtual-track release path moves tracks on its own, so the routing a test reads is not deterministic. |
| WebGL volume path (`UpdateWebGLVolume`) | out of scope | Behind `#if UNITY_WEBGL`; unreachable from an Editor Play Mode run. |
| Virtualized-and-released player restores un-mixed loudness | out of scope | Requires real voice virtualization (concurrent voices beyond Max Real Voices) plus a 0.5s grace period — not deterministically forceable in this suite. |
| `AudioSource.volume` is linear; only mixer params are dB | covered | `VolumePitchMixerTests.SetVolume_Master_*` asserts the dB conversion happens on the mixer side; `AudioMathTests.ToDecibel_*` pins the conversion math itself. `PlaybackEdgeCaseTests.SetVolume_LeavesARoutedPlayersAudioSourceVolumeAtFull_ButAnUnroutedPlayerTakesTheLinearValue` reads `AudioSource.volume`: 1 on a routed player, exactly the linear 0.5 on one with no mixer track. |
| `SetPitch` drives `AudioSource.pitch`, clamped to the AudioSource range | covered | `VolumePitchMixerTests.SetPitch_WithOutOfRangeValue_ClampsToAudioSourceRange` |
| Per-type `SetVolume` / `SetPitch` with a flag value that is not a concrete type | covered | `ErrorPathTests.SetVolumeAndSetPitch_WithBroAudioTypeNone_WarnAndChangeNothing`; Unity's "Everything" (-1) is treated as `All` — `SetVolume_WithUnitysEverythingFlag_IsTreatedAsAllAndWritesOnlyTheMasterVolume`, `SetPitch_WithUnitysEverythingFlag_ReachesLivePlayersAndEveryConcreteTypePref` |
| `Utility.VolumeToSlider` / `SliderToVolume` / `BroVolumeToSlider` curves behind `SoundVolume` | covered | `AudioMathTests.VolumeToSlider_MatchesTheHandDerivedValue` and `SliderToVolume_MatchesTheHandDerivedValue` pin `Linear`, `Logarithmic` and `BroVolume` against hand-derived literals, with and without boost; `BroVolumeToSlider_DefaultAllowBoost_IsTrue` pins the default |
| `SetPitch` before playback defers the fade rather than snapping | covered | `VolumePitchMixerTests.SetPitch_BeforePlaybackStarts_DefersFadeRatherThanSnapping` |
| `SetPitch` recalculates the scheduled end time on every change | partial | `ScheduledPlaybackContractTests.SetPitch_AboveOneMidPlay_ShortensDerivedRemainingDuration` and `SetPitch_BelowOneMidPlay_LengthensDerivedRemainingDuration`; a seam player's end derived from a carried pitch by `LoopHandoverTests.Play_WithPlainLoop_PitchBelowOneAndFollowTargetRideAcrossTwoSeams`. Pitch at or below 0, a change while paused, a gradual `PitchControl` change and a change that misses an already pre-spawned seam player (TEST_FINDINGS #72) are not pinned; see the mid-play pitch row in the time-dependent table. |
| Per-SoundID pitch reaches only that ID's live players and is not stored | covered | `VolumePitchMixerTests.SetPitch_BySoundId_AppliesToThatInstanceOnly` |
| Master / per-type pitch use the same persistence + live-push pattern as volume | covered | `AuthoredPitchAndRandomizationTests.SetPitch_Master_StoresIntoEveryConcreteTypePrefAndReachesFuturePlayers` — master pitch is *not* the volume pattern: it writes every concrete type's pref rather than a single master stage (TEST_FINDINGS #56). |
| The entity's authored `Pitch` reaches `AudioSource.pitch`, and a per-type pitch replaces rather than scales it | covered | `AuthoredPitchAndRandomizationTests.Play_WithAuthoredEntityPitch_ReachesAudioSourceAndIsReplacedNotScaledByTypePitch` (TEST_FINDINGS #55) |
| Per-play randomization (`RandomFlags`, `PitchRandomRange`, `VolumeRandomRange`) is drawn at Play, base ± range/2 | covered | `AuthoredPitchAndRandomizationTests.Play_WithRandomPitchAndVolumeFlags_JittersWithinHalfRangePerPlay` — the arithmetic itself stays unit-tested in EditMode; this pins that a Play draws from it at all. |
| Every player acquires a pooled mixer track, Generic by default and Dominator for a dominator | covered | `VolumePitchMixerTests.Play_AcquiresPooledMixerTrackAndReusesOneAfterRecycle` (Generic); `DominatorTrackRoutingTests.Play_AsDominatorInTheSameFrame_*` (Dominator) |
| A returned track is silenced and goes back to the right pool for reuse | covered | `MixerTrackRecycleTests.Recycle_SilencesTheReturnedTrack_AndTheNextPlayTakesThatSameTrack` reads the track's exposed parameter at full before the recycle and muted after it, and checks the next play takes that same group back; `Recycle_WhileRoutedThroughTheEffectSend_ReturnsTheTrackWithBothTrackAndSendMuted` does the same for a track routed through its effect send. Both behind `!UNITY_WEBGL`. |
| A track with an active effect routes its volume to the per-track send parameter, and back when the effect ends | covered | `AudioEffectTests.SetEffect_LowPass_WritesFrequencyToMixerAndRoutesFuturePlayersThroughEffectSend` and `SetEffect_ScopedToMusic_ReroutesLivePlayerOfThatTypeOnly` read the `<Track>` and `<Track>_Effect` parameters: level on the send and the dry track muted while the effect is on, the reverse after the reset, and an SFX player left on its dry track while Music is re-routed. The effect ending at recycle by `MixerTrackRecycleTests.Recycle_WhileRoutedThroughTheEffectSend_*`. |
| A dominator flips the main mix to `Main_Dominated` and reverts once nothing dominant remains | covered | `DominatorTrackRoutingTests.Play_AsDominatorInTheSameFrame_RoutesToADominatorTrackAndDucksTheMainTrack` and `DominatorEffectParameterTests.QuietOthers_WithZeroFadeTime_*` read `Main` while ducked and after it recovers. `LowPassOthers_*` / `HighPassOthers_*` now also stop the dominator and assert `Main_LowPass` / `Main_HighPass` return to their defaults and `Main` to full volume. The PlayMode fixture's teardown checks the same two parameters after every test. |
| `SetEffect`'s waitable resets the effect to default once its condition is met | covered | `AudioEffectTests.SetEffect_LowPass_ForSeconds_AutoResetsToMaxFrequencyAfterDuration` — which also samples the parameter throughout the hold and asserts it stays at the set value until the reset — and `SetEffect_WithDefaultZeroFade_ThenForSeconds_AutoResetsWithoutThrowing`; the reset-all path by `SetEffect_None_ResetsEveryTrackedEffectParameterToItsDefault`. The timed reset restores only the parameter: the type stays routed through the effect send, pinned by `SetEffect_LowPass_ForSeconds_ResetsTheParameterButLeavesTheTypeRoutedThroughTheEffectSend` (TEST_FINDINGS #71). |
| `EffectType.Volume` is rejected outside a dominator | covered | `AudioEffectTests.SetEffect_VolumeOnNonDominator_LogsErrorAndLeavesMixerUntouched` |
| `EffectAutomationHelper` restarts for a more intense effect and queues a less intense one | partial | The ordering it relies on, `Effect.CompareTo` / `IsMoreIntenseThan` with LowPass's inverted sign, is unit-tested in `AudioMathTests` (tier 0.4). No test sends two overlapping `SetEffect` calls and reads which target the mixer ends on. |

---

## Facts settled in the live Editor

Established by probe or grep during ranking; they override anything in the section files that guesses at them.

- **Mixer parameters are exposed as `BroName` expects.** `BroAudioMixer` answers `GetFloat` for `Master` (rests at 0 dB), `Main`, and `Main_Dominated`. Master-volume and dominator assertions are viable at the mixer boundary.
- **Track pools: 36 generic `Track*` groups, 4 `Dominator*` groups.** Forcing generic-pool exhaustion needs 37 concurrent voices — too expensive to be worth it. The dominator pool, at 4, is cheap to exhaust.
- **`EffectParaNameSuffix` is `"_Effect"`**; track names are `Master` / `Track` / `Main` / `Main_Dominated` / `Dominator`.
- **`IAudioStoppable`'s members are all `internal`, but every one is re-exposed** as a public extension method in `BroAudioChainingMethod.cs`. Tests call `Stop`/`Pause`/`UnPause` (including the `Action onFinished` overloads) normally — no reflection, no `InternalsVisibleTo`.
- **`BroAudio.OnBGMChanged` is public**, forwarding to `MusicPlayer.OnBGMChanged`. No reflection needed — but it is a *static* event, so any test that subscribes must unsubscribe.
- **`StopMode.Mute` is reachable** via the public `SetTransition(this IMusicPlayer, Transition, StopMode)` extension. It is a BGM transition mode, not a general stop mode.
- **Follow-target tracking is not observable** through `IAudioSourceProxy` — the proxy exposes no `transform`/`gameObject`.
- **Localization has locales but no table.** `Assets/Localization/` holds three `Locale` assets and the settings asset — no `AssetTable`. `LocalizationClipStrategy` is testable in EditMode via `Inject()`; the full `Play()` → `SoundManager` → resolved clip path needs an `AssetTable` fixture first.

---

## Editor suite

A separate EditMode assembly covering `BroAudioEditor` (`Ami.BroAudio.Editor.Tests`), distinct from the
runtime tiers above — its own coverage ledger, its own tier vocabulary (E0-E4), defined in
[TESTING_PLAN_EDITOR.md](TESTING_PLAN_EDITOR.md).

This assembly also compiles the relocated `ClipSelectionTests.cs`, `AudioMathTests.cs` and
`LocalizationClipStrategyTests.cs` (see the tier 0 note above). The two shipped-data gaps once
deliberately left red — `SoundSource_PositionMode` had no shipped text, and a stale asset key pointed at
a deleted enum member — have both since been fixed; see [FIXED_ISSUES.md](FIXED_ISSUES.md).

Per-file test files, each verified passing in isolation: `IsolationContractTests`,
`EditorUtilityPureTests`, `TransportSetValueTests`, `TransportHasDifferentPositionTests`,
`RectSplitRatioTests`, `RectScopingTests`, `EditorReflectionNamingTests`, `ShippedDataTests`,
`IssueReportMarkdownTests`, `SerializedPropertyResetTests`, `SerializedTransportTests`, `ClipEditingTests`,
`AssetWritingTests`, `CoreDataAndUpdaterTests`, `FindingCoverageTests`, `OptionalPackageEditorTests`,
plus the relocated `ClipSelectionTests`, `AudioMathTests`, `EaseCurveTests`, `LocalizationClipStrategyTests`
(the last behind `PACKAGE_LOCALIZATION`). Two non-fixture files support them: `EditorReflected`, the one
place every non-public editor member the assembly reaches by name is kept and looked up (failing with a
`BroAudioException` that names the member), and `EditorRunIsolationGuard`, the assembly-wide
`[SetUpFixture]` described below.

The isolation contract lives in `BroEditorTestFixture`
(`Assets/Tests/Editor/BroEditorTestFixture.cs`): JSON snapshot/restore of the on-disk `EditorSetting` and
`RuntimeSetting` with each asset's dirty bit put back the way the test found it (not cleared, which would
discard a developer's unsaved edit), restore of the `LastEditAudioAsset` EditorPrefs key — deleted again if
the test created it — and of `EditorGUIUtility.systemCopyBuffer`, and an `Assets/EditorTestsScratch_Temp/`
folder deleted in TearDown. Every TearDown step runs even when an earlier one throws, `OnTearDown`
included, and the failures are rethrown together. `IsolationContractTests` guards the fixture itself
(`E_SettingAssets_DirtyBitIsRestoredAfterAMutatingTest` for the dirty bit). The per-test restore works in
memory, so `EditorRunIsolationGuard` checks the whole run from outside: the settings files' bytes on disk,
BroAudio's EditorPrefs keys, the clipboard and the temp folder, before the first test and after the last.
`git status` is clean after a run; nothing under `Assets/BroAudio/`, `ProjectSettings/` or `Packages/` is
touched.

`AssetWritingTests` needs its own containment mechanism on top of that, because new entities are not
written beside the asset they belong to: `AudioAssetEditor` writes them to `EditorSetting.AssetOutputPath`.
The fixture redirects that setting into the temp folder before creating anything and restores the
developer's real path in TearDown.

### Coverage ledger (Editor tiers)

| Tier | Status | Test files |
|---|---|---|
| E0 — pure functions | **covered** | `EditorUtilityPureTests.cs`, `TransportSetValueTests.cs` (including the exact-midpoint rounding, `SetValue_Start_ExactMidpoint_RoundsAwayFromZero`), `TransportHasDifferentPositionTests.cs`, `RectSplitRatioTests.cs`, `RectScopingTests.cs` (off-origin scopes, TEST_FINDINGS #62), `EditorReflectionNamingTests.cs`, `IssueReportMarkdownTests.cs`, `CoreDataAndUpdaterTests.cs` (`GetMaxAcceptableClipCount`, `TryParseCoreData` — which throws on malformed JSON, TEST_FINDINGS #69 —, and the `BroUpdater` version gates, driven against in-memory settings). One E0 target, the `GetSerializedEnumIndex` / `GetAudioTypeByIndex` round-trip, is **out of scope**: both helpers were dead code with a broken round-trip and were deleted (see [FIXED_ISSUES.md](FIXED_ISSUES.md)), so there is nothing left to test. |
| E1 — shipped-data integrity | **covered** | `ShippedDataTests.cs`, which reads the **committed** `BroInstruction` asset under `Resources~/Editor` rather than the gitignored local copy, so a stale copy cannot hide or fake a gap |
| E2 — SerializedProperty operations | **covered** | `SerializedPropertyResetTests.cs`, `SerializedTransportTests.cs` |
| E3 — clip editing | **covered** | `ClipEditingTests.cs`, including a failed `Trim` on a streaming clip (TEST_FINDINGS #61) |
| E4 — asset-writing paths | **covered** | `AssetWritingTests.cs` |

### Out of scope (Editor suite)

| Behavior | Why |
|---|---|
| IMGUI drawing | `Event.current` is null outside `OnGUI`, so logic that exists only inside a draw call is not testable without a host `EditorWindow` and pumped repaints |
| `ScriptingDefinesUtility` | Changes `PlayerSettings` scripting defines, which triggers a recompile and a domain reload that kills the run |
| `AudioProxyModifierCodeGenerator` / `ProxyModifierCodeGenerator` | Write generated `.cs` into `Runtime/Player/AutoGeneratedCode/`, triggering the same recompile-and-reload outcome, plus a dirty repo |
| `SoundIDUpgrader` / `FileStructureUpgrader` | Rewrite scenes, prefabs and folders project-wide |
| `PackageExporter` | Gated behind `#if BroAudio_DevOnly` |
| Log text *as the behavior under test* | Carried over from the runtime suite's anti-goals. `LogAssert.Expect` is still used where a test provokes an expected `LogType.Error` — otherwise the error alone fails the run — but it checks the `LogType` and BroAudio's tag, never the sentence, and no test asserts a log message *is* the feature. |

### Deferred (Editor suite)

| Behavior | Why deferred |
|---|---|
| `BroVersion.SetVersion` | Writes a `.txt` into the shipped package's `Editor/Resources` and re-imports it |
| `LibraryManagerWindow` and every other `EditorWindow` | Opens-without-throwing is near-zero signal and leaks window state |
| `FieldUsageFinder` | Scans every asset in the project — slow, and its result depends on project contents |
| `BroUserDataGenerator.CheckAndGenerateUserData` | Writes into the shipped package's own Resources folders and completes on an async `ResourceRequest` callback, so it cannot be exercised without breaking the isolation contract. Dropped from E4. |

### Assembly constraint

`Tests.asmdef` is all-platform (`includePlatforms: []`) and therefore cannot reference the Editor-only
`BroAudioEditor` — which is why `Assets/Tests/Editor/EditorTests.asmdef` exists as its own assembly,
referencing `BroAudio`, `BroAudioEditor`, and `Tests` (reusing `TestAudioLibrary` from the runtime suite
rather than writing a second clip builder).

---

## Tier 0 — EditMode units (phase 2, do first)

Pure functions, no `SoundManager`, no Play Mode, no clocks. This is the cheapest confidence in the whole plan
and it runs in milliseconds — literally: the three files below live in
`Assets/Tests/Editor/` and compile into `EditorTests.asmdef`, so they run in the EditMode lane instead
of riding along with the PlayMode suite.

| # | Behavior | Where | Risk |
|---|---|---|---|
| 0.1 | Clip-selection strategies against a hand-built `BroAudioClip[]` — Single, Sequence (incl. per-`sequenceId` reset), Shuffle (`_lastUsed` sentinel, 1- and 2-clip cases), Random weighting, Velocity boundary (`i == 0 ? 0 : i - 1`), Chained stage dispatch | `Runtime/Utility/ClipSelection/` | High — Shuffle is the most stateful class in the codebase |
| 0.2 | Linear↔dB conversion: `ToDecibel` / `ToNormalizeVolume` round-trip, `allowBoost` branches, `vol == 0` clamp to `MinVolume` | `Ami.Extension.AudioExtension` | High — silently wrong volume everywhere |
| 0.3 | `ClampNormalize` / `ClampDecibel` — pin that their `allowBoost` default is `false` while the conversion helpers' default is `true` | `Ami.Extension.AudioExtension` | Medium — a "harmonize the defaults" refactor is a behavior change |
| 0.4 | `Effect.CompareTo` / `IsMoreIntenseThan` — especially LowPass's deliberate sign inversion — plus `Effect.IsDefault()` and `IsValidFrequency` bounds | `Ami.BroAudio.Effect` | Medium — least obvious code path in the effects system |
| 0.5 | `AudioEntity.GetRandomValue(baseValue, RandomFlag)` and the static range overload; `HasLoop`'s 4-arg overload (the 2-arg one needs a live `SoundManager`) | `Ami.BroAudio.Data.AudioEntity` | Low-medium |
| 0.6 | `LocalizationClipStrategy.SelectClip` after `Inject()` with a lambda-supplied clip — sidesteps the missing AssetTable entirely | `Runtime/Utility/ClipSelection/` | Medium |

`Utility.SliderToVolume` / `VolumeToSlider` / `BroVolumeToSlider` — runtime code behind `SoundVolume`, whose
default slider is `SliderType.BroVolume` — are pinned against hand-derived literals for `Linear`,
`Logarithmic` and `BroVolume`, with and without boost, by `AudioMathTests.VolumeToSlider_MatchesTheHandDerivedValue`
and `SliderToVolume_MatchesTheHandDerivedValue`.

## Tier 1 — Core playback (phase 2)

High risk, low timing complexity. Everything here is frame-clock or immediate.

| # | Behavior | Observable | Risk |
|---|---|---|---|
| 1.1 | **Stale handle after recycle is inert, not fatal.** Hold the `IAudioPlayer` past playback end, then call `Stop`/`SetVolume`/`AsBGM`/`.AudioSource` on it | `IsActive` false, `ID == SoundID.Invalid`, no exception, no effect on the pooled player now serving someone else | **Highest** — a stored handle in a MonoBehaviour is the most common real-world pattern, and a regression here is a live NRE in a shipped game |
| 1.2 | **`Stop(All, 0f)` clears every concrete type.** One player per `BroAudioType`, one call, all deactivate | each handle's `IsActive` → false | **Highest** — the fixture's own teardown depends on this; a bug cascades into false results across the whole suite |
| 1.3 | **Pause freezes in place.** Pause → UnPause preserves the playhead, does not re-fire `OnStart`, does not rewind | `IsActive` stays true across the pause; `IsPlaying` toggles; `AudioSource.timeSamples` unchanged | High — a naive Stop/Play reimplementation violates this silently |
| 1.4 | **`IsActive` vs `IsPlaying` windows.** Queued-but-not-drained (active, not playing) and paused (active, not playing) | both properties, one frame apart | High — explicitly load-bearing per the source doc comments |
| 1.5 | **Rejected `Play` returns an inert `Empty.AudioPlayer`.** Force it with a stub `IPlayableValidator` returning false | returned handle inert; a full fluent chain off it never NREs and never returns null | Medium — silent-failure path |
| 1.6 | **Volume composition.** master (mixer dB, separate stage) vs per-`BroAudioType` vs per-`SoundID` vs `clip.Volume × entity.MasterVolume` (baked into one fader) | `AudioMixer.GetFloat("Master")` for master; `IAudioPlayer.GetVolume()` for the linear product | High — gates all audible output, and the four inputs compose at two different levels |
| 1.7 | **Per-type volume reaches live and future players alike**, including exactly `1f` — the `Mathf.Approximately` skip in `PlayControl` was removed (FIXED_ISSUES #7) | live player's volume vs. a subsequently-played one | Medium — a regression reintroducing a default-value skip would only show on a fresh player |
| 1.8 | **Mixer track acquisition and return.** Every player gets a pooled `AudioMixerGroup`; recycling returns it silenced | `player.AudioSource.outputAudioMixerGroup` non-null and named `Track*` | High — routing is the backbone of every other mixer behavior |
| 1.9 | **Pitch via `AudioSource`**, clamped to `[-3, 3]`, plus the deferred-fade path when `SetPitch` precedes playback | `player.AudioSource.pitch` | High — audible and gameplay-relevant |
| 1.11 | **Lifecycle callbacks.** `OnStart` once (not re-fired on resume), `OnUpdate` per frame, `OnPause` per transition, `OnEnd` once with a still-valid `SoundID` | counters incremented from the callbacks | Medium-high — primary integration point for game code |

## Tier 2 — Time-dependent (phase 3)

Everything here needs a clock decision per test. Hand writers `WaitDspSeconds` and `WaitUntilOrTimeout` by name.

| # | Behavior | Clock | Risk |
|---|---|---|---|
| 2.1 | **Pause across a handover seam** — pause a looping player at the DSP instant the handover fires | DSP | **Highest** — named in project memory as a live NRE source |
| 2.2 | **Plain loop via player handover** — a *new* `AudioPlayer` continues the sound; `AudioSource.loop` is never set | DSP | High |
| 2.3 | **Seamless loop crossfade seam** — two instances fading in opposite directions across the boundary | DSP seam, frame ramps | High — hardest behavior in the suite to test stably |
| 2.4 | **`Stop` re-entrancy / don't-double-fade** (`IsStopping`, with `FadeData.Immediate` special-cased through) | frame | High — comment-heavy logic citing a specific prior commit |
| 2.5 | **`clip.Delay` vs explicit schedule priority** — explicit wins | DSP | High — already regressed once |
| 2.6 | **Fade in / fade out durations**, plus the explicit-override argument's one-shot consume semantics | frame ramp, DSP gate on natural end | High — hot path, silent failure |
| 2.7 | **Scheduled start / end time** via `ISchedulable` | DSP | Medium |
| 2.8 | **BGM transitions** (sequential vs overlapping per `Transition` mode), `OnBGMChanged`, `AlwaysPlayMusicAsBGM` | frame | High — headline feature |
| 2.9 | **Mid-play pitch change rescales the derived end time** | DSP recompute, synchronous trigger | High — dense arithmetic with a "no API to undo" admission in-comment |
| 2.10 | **Clip `StartPosition` / `EndPosition` trims** | frame / DSP | Medium |
| 2.11 | **Chained playback** — the outro handover is *not* DSP-gated like every other handover | DSP + immediate | Medium |

## Tier 3 — Selection and policy (phase 4)

| # | Behavior | Setup | Risk |
|---|---|---|---|
| 3.1 | **Voice limiting** rejects past the cap, and the count is decremented on end | Requires an explicitly wired `PlaybackGroup` on `_group` — a code-built entity has none | High — headline feature, single mutable `int`, no bounds check |
| 3.2 | **Voice limiting counts at enqueue, not at audible start** | same | Medium — surprising, undocumented, and a plausible "fix" would change it |
| 3.3 | **Comb-filtering rule** — same-frame and queued-frame detection, global vs positioned asymmetry | wired group | High — most branch-dense rule in the codebase |
| 3.4 | **A custom `IPlayableValidator` overrides the group** | none (validator passed to `Play`) | Low, but it is the doorway for the whole extension point |
| 3.5 | **Selection state is per-`AudioEntity`**, shared across concurrent plays of the same `SoundID`, never auto-reset | none | Medium — cross-cutting, affects nearly every other selection test |
| 3.6 | **`AsBGM()` twice returns the same decorator** (idempotent, not stacked); `AsBGM` + `AsDominator` coexist | none | Medium |
| 3.7 | **`RuntimeSetting` toggles that change `Play` behavior** — assert the *off* state as well as the default-on state | fixture already snapshots/restores the asset | Medium |

## Deferred

Real behaviors, deliberately not covered — cost far exceeds the confidence gained. Revisit only if one regresses in the wild.

| Behavior | Why deferred |
|---|---|
| Generic track-pool exhaustion → unrouted playback fallback | Needs 37 concurrent voices. Playing them is cheap; the complication is that at that count Unity's voice virtualization starts, and BroAudio's virtual-track release path moves tracks on its own, so which players are routed is not deterministic. *Partial substitute:* the 4-slot **dominator** pool is exhausted instead, which exercises the same `AudioTrackObjectPool` null-return path. |
| Virtual-track release / reacquire | Needs actual voice virtualization past Max Real Voices plus a 0.5s grace period — not deterministically forceable in a small scene |
| WebGL volume path | Needs the `UNITY_WEBGL` define; genuinely separate code, untestable in the Editor |
| `RuntimeSetting.DefaultAudioPlayerPoolSize` | Needs a fresh bootstrap (destroy the manager, `SoundManager.Init()`), as `TeardownTests` does |
| Full `Play()` → `SoundManager` → localized clip resolution, and `LocalizedAudioChanged` handlers firing | Needs an `AssetTable` with audio entries, committed as a fixture the way the addressable tones are. The strategy itself is covered at 0.6; the subscription guards by `LocalizedAudioChangedSubscriptionTests`; load, release and play of an entity with no table by `LocalizationRuntimeGuardTests` |
| Mid-playback `outputAudioMixerGroup` swap glitch behavior | Engine-level, flagged as unverified even in the engine notes |

### A seamless loop whose transition outlasts its clip

This case was once left untested for fear that `ScheduleNextPlayback`'s negative wait window would recurse
without bound into an uncatchable `StackOverflowException`. It does not, and it is now pinned: the loop
period stretches from the clip length to the `TransitionTime` while the player count stays bounded. The
mechanism and the pinning test are recorded as [TEST_FINDINGS #59](TEST_FINDINGS.md).

## Out of scope

| Behavior | Why |
|---|---|
| Editor windows, inspectors, Library Manager | Explicit anti-goal |

---

## Open questions — answered

All five were settled by close source reading plus the shipped `BroRuntimeSetting.asset`. Tests should still
*assert* these rather than assume them; that is what characterization means.

1. **Does resume re-trigger the fade-in?** Yes, structurally — `PlayControl`'s fade-in block has no `isResuming`
   guard, and `TryGetFadeIn` returns true for any resolved fade > 0, including the clip's own `FadeIn`
   (not just an explicit override). Whether it is *audible* depends on where `SetupClipVolume` leaves
   `_clipVolume` on resume, since `Fade` ramps from current to target. Assert the observed behavior.
2. **Is `Stop(fadeOut)` mid-fade interruptible by `UnPause()`?** No. `UnPause` requires
   `_stopMode == StopMode.Pause`; during a `Stop` fade `_stopMode` is `StopMode.Stop`, so it logs
   *"Cannot UnPause: The player is not paused"* and no-ops. It is `_stopMode` that blocks it, not `IsStopping`.
   Separately, a second `Stop` during a fade is blocked by `IsStopping` unless the override is exactly
   `FadeData.Immediate`.
3. **Does `clip.Delay` apply after the first loop iteration?** It can. `SetClipDelayIfNotScheduled` runs at the
   top of *every* `PlayControl`, including handover players, and applies whenever
   `_pref.ScheduledStartTime <= 0`. The `isFirstLoopIteration` gate in `ResolveScheduledTiming` is a separate
   concern and does not suppress it. So a newly-picked clip's `Delay` on a later iteration depends entirely on
   whether the handover carried a non-zero `ScheduledStartTime`.
4. **Does `SetScheduledStartTime`'s pause quirk surface in `IAudioPlayer` state?** No — only in raw `AudioSource`
   timing. The code never touches `_stopMode` or `_onPaused` on that path, and an in-source comment confirms the
   behavior is deliberate ("Intentionally kept as a feature.", in `ISchedulable.SetScheduledStartTime`).
5. **Shipped `BroRuntimeSetting.asset` values:** `AudioFilterSlope: 1` (FourPole), `DefaultAudioPlayerPoolSize: 5`,
   `AlwaysPlayMusicAsBGM: 1`, `UpdateMode: 0`, `GlobalPlaybackGroup` **assigned**. (This asset lives under
   the gitignored `Assets/BroAudio/Resources/`, so it is local to each checkout. It also carried
   `PitchSetting: 1`; that field no longer exists on `RuntimeSetting`, so the stored value simply stops
   deserializing into anything and Unity drops it the next time it writes the asset.)

One consequence worth carrying forward:

- **`GlobalPlaybackGroup` being assigned does not affect code-built entities.** It is consulted only through
  `AudioAsset.LinkPlaybackGroup` and `PlaybackGroup`'s parent fallback, and a code-built entity has no
  `AudioAsset`. The plan's premise holds: voice-limit and comb-filtering tests on code-built entities must
  wire a group explicitly. Every authored entity *does* play under the global group, so the fixture now sets
  a factory global group for every test and `DefaultPlaybackGroupTests` plays `AudioAsset`-backed entities
  under it — the configuration users ship.
