# BroAudio Test Inventory

Ranked plan for the regression suite. Goal: **maximum behavioral confidence per test.**
Coverage is not the highest priority, and coverage percentage is not a target.

Detail lives in four section files; this file is the index, the ranking, and the coverage ledger.

| Section | File |
|---|---|
| Play / Stop / Pause lifecycle | [inventory/lifecycle.md](inventory/lifecycle.md) |
| Volume, pitch, mixer, effects | [inventory/volume-mixer.md](inventory/volume-mixer.md) |
| Time-dependent behavior | [inventory/time-dependent.md](inventory/time-dependent.md) |
| Selection, policy, decorators | [inventory/selection-policy.md](inventory/selection-policy.md) |

Status values, here and in the section ledgers: **covered** · **partial** · **deferred** · **out of scope**.
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

Tier status is the summary, and a tier is **partial** when any of its rows below is; the **per-behavior** ledger required by the plan's Definition of Done lives at
the bottom of each inventory file — [lifecycle](inventory/lifecycle.md#coverage-ledger),
[selection-policy](inventory/selection-policy.md#coverage-ledger),
[time-dependent](inventory/time-dependent.md#coverage-ledger) and
[volume-mixer](inventory/volume-mixer.md#coverage-ledger) — where every inventoried behavior is marked
covered / partial / deferred / out of scope with the test that pins it.

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

## Facts settled in the live Editor

Established by probe or grep during ranking; they override anything in the section files that guesses at them.

- **Mixer parameters are exposed as `BroName` expects.** `BroAudioMixer` answers `GetFloat` for `Master` (rests at 0 dB), `Main`, and `Main_Dominated`. Master-volume and dominator assertions are viable at the mixer boundary.
- **Track pools: 36 generic `Track*` groups, 4 `Dominator*` groups.** Forcing generic-pool exhaustion needs 37 concurrent voices — too expensive to be worth it. The dominator pool, at 4, is cheap to exhaust.
- **`EffectParaNameSuffix` is `"_Effect"`**; track names are `Master` / `Track` / `Main` / `Main_Dominated` / `Dominator`.
- **`IAudioStoppable`'s members are all `internal`, but every one is re-exposed** as a public extension method in `BroAudioChainingMethod.cs`. Tests call `Stop`/`Pause`/`UnPause` (including the `Action onFinished` overloads) normally — no reflection, no `InternalsVisibleTo`.
- **`BroAudio.OnBGMChanged` is public**, forwarding to `MusicPlayer.OnBGMChanged`. No reflection needed — but it is a *static* event, so any test that subscribes must unsubscribe.
- **`StopMode.Mute` is reachable** via the public `SetTransition(this IMusicPlayer, Transition, StopMode)` extension. It is a BGM transition mode, not a general stop mode — the lifecycle section's "unreachable" note is wrong on this point.
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
