# BroAudio Test Inventory

Ranked plan for the regression suite. Goal: **maximum behavioral confidence per test, minimum test count.**
Coverage percentage is not a goal.

Detail lives in four section files; this file is the index, the ranking, and the coverage ledger.

| Section | File |
|---|---|
| Play / Stop / Pause lifecycle | [inventory/lifecycle.md](inventory/lifecycle.md) |
| Volume, pitch, mixer, effects | [inventory/volume-mixer.md](inventory/volume-mixer.md) |
| Time-dependent behavior | [inventory/time-dependent.md](inventory/time-dependent.md) |
| Selection, policy, decorators | [inventory/selection-policy.md](inventory/selection-policy.md) |

Status values: **covered** · **planned (phase N)** · **deferred** · **out of scope**

## Coverage ledger

| Tier | Status | Test files |
|---|---|---|
| 0 — EditMode units | **covered** (0.1–0.6) | `ClipSelectionTests.cs`, `AudioMathTests.cs`, `LocalizationClipStrategyTests.cs` |
| 1 — Core playback | **covered** (1.1–1.11) | `PlaybackLifecycleTests.cs`, `VolumePitchMixerTests.cs`, `PlaybackSmokeTests.cs` |
| 2 — Time-dependent | **covered** (2.1-2.11) | `FadeAndTrimTests.cs`, `LoopHandoverTests.cs`, `SchedulingAndMusicTests.cs` |
| 3 — Selection and policy | **covered** (3.1-3.7) | `PlaybackGroupTests.cs`, `SelectionStateAndDecoratorTests.cs` |
| 5 — Addressables | **covered** | `AddressablesTests.cs` |
| 6 — MonoComponents | **partial** — `SoundSource` only | `SoundSourceTests.cs` |

Tier status is the summary; the **per-behavior** ledger required by the plan's Definition of Done lives at
the bottom of each inventory file — [lifecycle](inventory/lifecycle.md#coverage-ledger),
[selection-policy](inventory/selection-policy.md#coverage-ledger),
[time-dependent](inventory/time-dependent.md#coverage-ledger) and
[volume-mixer](inventory/volume-mixer.md#coverage-ledger) — where every inventoried behavior is marked
covered / partial / deferred / out of scope with the test that pins it.

179 tests, ~24s per PlayMode run. `AudioEffectTests.cs` (added 2026-08-29, 22 tests, `157 -> 179`)
covers the two largest remaining gaps outside the tiers above: the per-player Unity filter surface
(`AddChorusEffect`/`AddLowPassEffect`/etc. — attach, duplicate, remove, recycle cleanup, the
`OnAudioFilterRead` callback, exposed parameter writes) and the mixer-routed `BroAudio.SetEffect`
automation, plus remaining untested public statics.
Every fixture also passes in isolation, so no test depends on another having run. (`run_tests` has no shuffle
or seed option, so per-fixture isolation is the closest available substitute for the Definition of Done's
shuffled-order requirement.) Re-run and confirmed all-green 2026-08-30 via `unity cmd run_tests --mode
PlayMode --async_tests` (179/179). That run predates commit `48516f57` and the follow-up tidy-up,
both of which touched production code the suite exercises — **re-run before trusting the green.**

`SoundSourceTests.cs` (added 2026-08-30, 15 tests, `179 -> 194`) opens tier 6 by covering the
`SoundSource` no-code component: the three `PositionMode`s (global stays 2D; StayHere snapshots the
transform; FollowGameObject tracks it — which is also this suite's only coverage of
`BroAudio.Play(SoundID, Transform)`), the Play On Enable / Only Play Once / Stop On Disable / Override
Fade Out / Delay / Override Playback Group inspector toggles, Play's replace-don't-layer semantics, and
the guard clauses that keep Stop/Pause/UnPause/SetVolume/SetPitch inert without a live player. It found
TEST_FINDINGS #35. **These 15 tests have not been executed** — they were written in an environment with
no Unity Editor, so the `194` above is arithmetic, not a run. Compile and run them before trusting
either number.

The other two components under `Runtime/MonoComponent/` — `SoundVolume` and `SpectrumAnalyzer` — remain
uncovered, which is why tier 6 is *partial*.

`RuntimeSetting.DefaultAudioPlayerPoolSize` is **not** covered: it is read once at `SoundManager` bootstrap,
which the persistent singleton passes before any test runs, so mutating it live has no observable effect.

`Tests.asmdef` gained a `Unity.Localization` reference for 0.6 — asmdef references are not transitive, so
referencing `BroAudio` does not bring Localization types into scope. Phase 4 needs it regardless.

---

## Facts settled in the live Editor

Established by probe or grep during ranking; they override anything in the section files that guesses at them.

- **Mixer parameters are exposed as `BroName` expects.** `BroAudioMixer` answers `GetFloat` for `Master` (rests at 0 dB), `Main`, and `Main_Dominated`. Master-volume and dominator assertions are viable at the mixer boundary.
- **Track pools: 36 generic `Track*` groups, 4 `Dominator*` groups.** Forcing generic-pool exhaustion needs 37 concurrent voices — too expensive to be worth it. The dominator pool, at 4, is cheap to exhaust.
- **`EffectParaNameSuffix` is `"_Effect"`**; track names are `Master` / `Track` / `Main` / `Main_Dominated` / `Dominator`.
- **`IAudioStoppable`'s members are all `internal`, but every one is re-exposed** as a public extension method in `BroAudioChainingMethod.cs`. Tests call `Stop`/`Pause`/`UnPause` (including the `Action onFinished` overloads) normally — no reflection, no `InternalsVisibleTo`.
- **`BroAudio.OnBGMChanged` is public** (`Runtime/BroAudio.cs:22`), forwarding to `MusicPlayer.OnBGMChanged`. No reflection needed — but it is a *static* event, so any test that subscribes must unsubscribe.
- **`StopMode.Mute` is reachable** via the public `SetTransition(this IMusicPlayer, Transition, StopMode)` extension. It is a BGM transition mode, not a general stop mode — the lifecycle section's "unreachable" note is wrong on this point.
- **Follow-target tracking is not observable** through `IAudioSourceProxy` — the proxy exposes no `transform`/`gameObject`.
- **Localization has locales but no table.** `Assets/Localization/` holds three `Locale` assets and the settings asset — no `AssetTable`. `LocalizationClipStrategy` is testable in EditMode via `Inject()`; the full `Play()` → `SoundManager` → resolved clip path is not testable as authored.

---

## Editor suite

A separate EditMode assembly covering `BroAudioEditor` (`Ami.BroAudio.Editor.Tests`), distinct from the
runtime tiers above — its own coverage ledger, its own tier vocabulary (E0-E4), defined in
[TESTING_PLAN_EDITOR.md](TESTING_PLAN_EDITOR.md).

**97 EditMode tests, all pass, ~5s wall.** The two shipped-data gaps once deliberately left red —
TEST_FINDINGS #18 (`SoundSource_PositionMode` had no shipped text) and #19 (stale asset key `15`) —
have both since been fixed; see [FIXED_ISSUES.md](FIXED_ISSUES.md). Confirmed all-green 2026-08-30
via `unity cmd run_tests --mode EditMode` (98/98, the +1 being an unrelated Addressables package
stub test). That run predates commit `48516f57` and the follow-up tidy-up, both of which touched
production code the suite exercises — **re-run before trusting the green.**

Per-file counts, each also verified passing in isolation: `IsolationContractTests` 5,
`EditorUtilityPureTests` 16, `TransportAndRectMathTests` 28, `ShippedDataTests` 6,
`IssueReportMarkdownTests` 5, `SerializedPropertyResetTests` 5, `SerializedTransportTests` 8,
`ClipEditingTests` 16, `AssetWritingTests` 8.

The isolation contract lives in `BroEditorTestFixture`
(`Assets/Tests/Editor/BroEditorTestFixture.cs`): JSON snapshot/restore of the on-disk `EditorSetting` and
`RuntimeSetting` plus `EditorUtility.ClearDirty`, restore of the `LastEditAudioAsset` EditorPref and
`EditorGUIUtility.systemCopyBuffer`, and an `Assets/BroAudioEditorTests_Temp/` folder deleted in
TearDown. `IsolationContractTests` guards the fixture itself. `git status` is clean after a run; nothing
under `Assets/BroAudio/`, `ProjectSettings/` or `Packages/` is touched.

`AssetWritingTests` needs its own containment mechanism on top of that, because new entities are not
written beside the asset they belong to: `AudioAssetEditor` writes them to `EditorSetting.AssetOutputPath`.
The fixture redirects that setting into the temp folder before creating anything and restores the
developer's real path in TearDown.

### Coverage ledger (Editor tiers)

| Tier | Status | Test files |
|---|---|---|
| E0 — pure functions | **covered** | `EditorUtilityPureTests.cs`, `TransportAndRectMathTests.cs`, `IssueReportMarkdownTests.cs`. One E0 target, the `GetSerializedEnumIndex` / `GetAudioTypeByIndex` round-trip, is **out of scope**: both helpers were dead code with a broken round-trip and were deleted in `48516f57` (FIXED_ISSUES #20), so there is nothing left to test. |
| E1 — shipped-data integrity | **covered** | `ShippedDataTests.cs` |
| E2 — SerializedProperty operations | **covered** | `SerializedPropertyResetTests.cs`, `SerializedTransportTests.cs` |
| E3 — clip editing | **covered** | `ClipEditingTests.cs` |
| E4 — asset-writing paths | **covered** | `AssetWritingTests.cs` |

### Out of scope (Editor suite)

| Behavior | Why |
|---|---|
| IMGUI drawing | `Event.current` is null outside `OnGUI`, so logic that exists only inside a draw call is not testable without a host `EditorWindow` and pumped repaints |
| `ScriptingDefinesUtility` | Changes `PlayerSettings` scripting defines, which triggers a recompile and a domain reload that kills the run |
| `AudioProxyModifierCodeGenerator` / `ProxyModifierCodeGenerator` | Write generated `.cs` into `Runtime/Player/AutoGeneratedCode/`, triggering the same recompile-and-reload outcome, plus a dirty repo |
| `SoundIDUpgrader` / `FileStructureUpgrader` | Rewrite scenes, prefabs and folders project-wide |
| `PackageExporter` | Gated behind `#if BroAudio_DevOnly` |
| Log text *as the behavior under test* | Carried over from the runtime suite's anti-goals. `LogAssert.Expect` is still used where a test provokes an expected `LogType.Error` — otherwise the error alone fails the run — but no test asserts a log message *is* the feature. |

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
and it runs in milliseconds.

| # | Behavior | Where | Risk |
|---|---|---|---|
| 0.1 | Clip-selection strategies against a hand-built `BroAudioClip[]` — Single, Sequence (incl. per-`sequenceId` reset), Shuffle (`_lastUsed` sentinel, 1- and 2-clip cases), Random weighting, Velocity boundary (`i == 0 ? 0 : i - 1`), Chained stage dispatch | `Runtime/Utility/ClipSelection/` | High — Shuffle is the most stateful class in the codebase |
| 0.2 | Linear↔dB conversion: `ToDecibel` / `ToNormalizeVolume` round-trip, `allowBoost` branches, `vol == 0` clamp to `MinVolume` | `Ami.Extension.AudioExtension` | High — silently wrong volume everywhere |
| 0.3 | `ClampNormalize` / `ClampDecibel` — pin that their `allowBoost` default is `false` while the conversion helpers' default is `true` | `Ami.Extension.AudioExtension` | Medium — a "harmonize the defaults" refactor is a behavior change |
| 0.4 | `Effect.CompareTo` / `IsMoreIntenseThan` — especially LowPass's deliberate sign inversion — plus `Effect.IsDefault()` and `IsValidFrequency` bounds | `Ami.BroAudio.Effect` | Medium — least obvious code path in the effects system |
| 0.5 | `AudioEntity.GetRandomValue(baseValue, RandomFlag)` and the static range overload; `HasLoop`'s 4-arg overload (the 2-arg one needs a live `SoundManager`) | `Ami.BroAudio.Data.AudioEntity` | Low-medium |
| 0.6 | `LocalizationClipStrategy.SelectClip` after `Inject()` with a lambda-supplied clip — sidesteps the missing AssetTable entirely | `Runtime/Utility/ClipSelection/` | Medium |

`Utility.SliderToVolume` / `BroVolumeToSlider` piecewise mapping is **deferred** — it is editor-slider presentation math, not runtime behavior.

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
| 1.7 | **Per-type volume reaches live and future players alike**, including exactly `1f` — the `Mathf.Approximately` skip in `PlayControl` was removed (TEST_FINDINGS #7) | live player's volume vs. a subsequently-played one | Medium — a regression reintroducing a default-value skip would only show on a fresh player |
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
| Generic track-pool exhaustion → unrouted playback fallback | Needs 37 concurrent voices. The behavioral fork is real and high-risk, but the setup cost is out of proportion. *Partial substitute:* exhaust the 4-slot **dominator** pool instead, which exercises the same `AudioTrackObjectPool` null-return path cheaply. |
| Virtual-track release / reacquire | Needs actual voice virtualization past Max Real Voices plus a 0.5s grace period — not deterministically forceable in a small scene |
| WebGL volume path | Needs the `UNITY_WEBGL` define; genuinely separate code, untestable in the Editor |
| `Utility.SliderToVolume` / `BroVolumeToSlider` piecewise math | Editor-slider presentation, not runtime behavior |
| `PlaybackGroup.Parent` / `GlobalPlaybackGroup` multi-level fallback | Low-traffic path; not traced end to end |
| Mid-playback `outputAudioMixerGroup` swap glitch behavior | Engine-level, flagged as unverified even in the engine notes |
| Seamless loop with `TransitionTime` longer than the clip | **Not tested on purpose — see below.** |

### The one case deliberately left untested

A seamless loop whose `TransitionTime` exceeds its clip length was inventoried as an edge case worth
characterizing (the DSP wait window goes negative and handover "collapses to immediate"). It is not covered.

The writer who took that slice traced `ScheduleNextPlayback` together with `RestartCoroutine`'s
run-synchronously-until-first-yield semantics and concluded the collapse is not a one-time quirk but unbounded
recursion — each handover synchronously spawning the next — ending in a `StackOverflowException`. That
exception cannot be caught by .NET and would terminate the Editor process rather than fail a test, so they
declined to write it. That was the right call under uncertainty.

A follow-up static read does **not** support the conclusion: the handover player's `PlayControl` reaches
`while (_clipVolume.IsFading) yield return null;` for the seamless fade-in *before* it starts the next
`ScheduleNextPlayback`, which breaks the synchronous chain. So the recursion probably terminates.

Neither reading is certain, and the two outcomes are wildly asymmetric — a passing test on one side, a crashed
Editor on the other. **Ask the user before running this one**, and if it is run, do it in a throwaway Editor
instance rather than the connected one.

## Out of scope

| Behavior | Why |
|---|---|
| Full `Play()` → `SoundManager` → localized clip resolution | No `AssetTable` exists in the project. The strategy itself is covered at 0.6 |
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
   behavior is deliberate ("Some might consider this behavior a feature, so it has been left as is").
5. **Shipped `BroRuntimeSetting.asset` values:** `AudioFilterSlope: 1` (FourPole), `DefaultAudioPlayerPoolSize: 5`,
   `AlwaysPlayMusicAsBGM: 1`, `UpdateMode: 0`, `GlobalPlaybackGroup` **assigned**. (This asset lives under
   the gitignored `Assets/BroAudio/Resources/`, so it is local to each checkout. It also carried
   `PitchSetting: 1`; that field no longer exists on `RuntimeSetting`, so the stored value simply stops
   deserializing into anything and Unity drops it the next time it writes the asset.)

One consequence worth carrying forward:

- **`GlobalPlaybackGroup` being assigned does not affect code-built entities.** It is consulted only through
  `AudioAsset.LinkPlaybackGroup` and `PlaybackGroup`'s parent fallback, and a code-built entity has no
  `AudioAsset`. The plan's premise holds: voice-limit and comb-filtering tests must wire a group explicitly.
