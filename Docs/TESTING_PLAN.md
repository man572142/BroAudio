# BroAudio Testing Plan

Handoff doc for a `/goal` session that builds BroAudio's regression suite.

**Goal:** maximum behavioral confidence per test, minimum test count. Coverage % is not a goal.

---

## Ground truth (verified 2026-08-23, branch `DEV_Unity6`)

Read this before planning. It is the part a fresh session cannot guess, and several of these facts decide the test architecture.

### What already exists

- `Assets/Tests/Tests.asmdef` — references `UnityEngine.TestRunner`, `UnityEditor.TestRunner`, `BroAudio`; `nunit.framework.dll`; `autoReferenced: false`; constrained to `UNITY_INCLUDE_TESTS`. Standard, correct, all-platform.
- No test files yet. No prior test commits in any branch — the suite starts empty.
- `com.unity.test-framework@1.6.0`, `com.unity.addressables@2.8.1`, `com.unity.localization@1.5.12` installed.

Note: `Tests.asmdef` does not reference `BroAudioEditor`, which is correct — Editor windows and inspectors are out of scope. If an EditMode test ever needs `UnityEditor` APIs, split it into an Editor-only asmdef rather than widening this one.

### Tests can build their own audio library in code — no Editor authoring needed

This is the fact that makes black-box testing feasible, and it is not obvious:

- `SoundID` wraps an `AudioEntity` reference directly. `public SoundID(AudioEntity entity)` is public.
- `SoundManager.TryGetEntity` just reads `id.Entity` — **no registry, no bank, no `BroAudioData` lookup**.
- `AudioEntity` is a `ScriptableObject`, so `ScriptableObject.CreateInstance<AudioEntity>()` works.
- `id.ToAudioType()` returns `entity.AudioType`, so audio type is per-entity, set on the instance.

**Build entities with the existing factory, not raw `CreateInstance`:**

```csharp
AudioEntity.CreateNewInstance(audioAsset: null, name: "TestSfx", BroAudioType.SFX)
```

**Raw `ScriptableObject.CreateInstance<AudioEntity>()` bypasses that and leaves `MasterVolume` and `Pitch` at `0`** — silent, and playback that never advances, with no error either way. Use the factory and the problem does not exist.

**Verified by execution** via `unity cmd eval_file` against the live Editor: an entity built this way gives `Validate() == True`, `id.IsValid() == True`, `id.ToAudioType() == SFX`, and a non-null `PickNewClip()`.

What the factory does *not* do, and a test helper still must:

- **Clips.** `AudioEntity.Clips` is a public field, but `BroAudioClip.AudioClip` is `[SerializeField] private` — assigning it needs reflection (`BindingFlags.Instance | BindingFlags.NonPublic`, field name `"AudioClip"`) or `SerializedObject`. `BroAudioClip`'s `Volume/Delay/StartPosition/EndPosition/FadeIn/FadeOut/Weight` are public.
- **Anything beyond the three defaults.** Loop, seamless loop, play mode, spatial settings, random flags are all `private set` / private fields. Backing-field names confirmed by the probe: `<Loop>k__BackingField`, `<SeamlessLoop>k__BackingField`, `<TransitionTime>k__BackingField`, `<SpatialSetting>k__BackingField`, `<PitchRandomRange>k__BackingField`, `<VolumeRandomRange>k__BackingField`, `<RandomFlags>k__BackingField`, `<Flags>k__BackingField`; plain private fields `MulticlipsPlayMode`, `_group`, `SeamlessTransitionType`, `TransitionTempo`, `SnapToFullVolume`, `_localizedAudio`.
- `IsValid()` / `ToAudioType()` are extension methods on `SoundIDExtension`, not members of `SoundID`.
- **No `PlaybackGroup`** — confirmed null in the probe (`_group` null and no `AudioAsset`). `IsPlayable` skips validation when the validator is null, and `InitBank`'s global-group assignment does not apply. **Voice-limit / group tests must wire a group explicitly.**

**Deliverable: one thin `TestAudioLibrary` helper** — factory call, plus reflection for clips and whichever fields a given test varies. Sample `.wav` files exist under `Assets/BroAudio/Samples/Demo/AudioClip/` (footsteps ~0.36s, BGM, ambience), plus authored `AudioEntity` assets in `Samples/Demo/AudioAssets/Demo/` if you would rather test against shipped data.

### Timing: three facts that decide your synchronization strategy

Do not invent a timing convention before reading these.

1. **`Play()` does not start playback.** `SoundManager.Play` enqueues into `_playbackQueue`; `SoundManager.LateUpdate` drains it and calls `IPlayable.Play()` (`Runtime/SoundManager/SoundManager.Playback.cs:141`). A test **must yield at least one frame** after calling `BroAudio.Play(...)` before asserting on `AudioSource` state. Asserting immediately is the most likely first bug in a new test.
2. **`AudioMixer.SetFloat` silently fails on the first Play Mode frame** (`Awake`/`OnEnable`). `SoundManager` works around this with `WaitForEndOfFrame`. Any test asserting mixer parameters must clear that frame first.
3. **Frame time is not DSP time.** Scheduling (`PlayScheduled`), seamless loops and handovers run on `AudioSettings.dspTime`; fades run on coroutines/frames. Pick per behavior, and prefer polling-with-timeout helpers (`yield return WaitUntil(cond)` plus a deadline) over `WaitForSeconds`.

Full engine notes: `.claude/rules/unity-audio-engine.md` (auto-loads when editing runtime/test code). Trust that file over Unity's docs where they conflict.

### Test isolation is a structural problem, not a convention to pick

`SoundManager` is a `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` + `DontDestroyOnLoad` singleton bootstrapped from `Resources/SoundManager.prefab`. In a PlayMode run it spawns **once and persists across every test in the run.** State that leaks between tests:

- master / per-`BroAudioType` volume, and AudioMixer parameter values
- the `AudioPlayer` pool and mixer-group pools (a leaked playing voice pollutes the next test)
- `_combFilteringPreventer` (keyed by `SoundID`)
- `MusicPlayer`'s current BGM and the static `OnBGMChanged` event subscribers
- `RuntimeSetting` (`Resources/BroRuntimeSetting.asset`) — **a real asset on disk**; toggles like `AlwaysPlayMusicAsBGM` change `Play` behavior. Mutating it in a test dirties the user's project unless saved and restored.

Decide the isolation strategy **first** and apply it uniformly: a `[SetUp]`/`[TearDown]` that stops everything, drains a frame, and restores settings — or accept one domain reload per fixture. Whatever you choose, write it once in a base fixture; do not re-solve it per test file.

### Optional-package state

- **Localization is genuinely ready.** Three locales exist (`Assets/Localization/`): `en`, `ja-JP`, `zh-TW`, with the locale table registered in Addressables. `MulticlipsPlayMode.Localization` is testable with only a fixture that swaps `LocalizationSettings.SelectedLocale` and restores it.
- **Addressables is installed but has no audio entries.** `Default Local Group` is empty; the only addressable entries are the locale assets. Testing `AudioEntity.UseAddressables` / `SoundManager`'s load-and-cleanup paths first requires marking a couple of demo `.wav` files addressable and assigning their `AssetReference`s. That is real setup work with a project-file footprint — **do it as its own phase, last, and confirm with the user before adding addressable entries.**

---

## Running tests: use the Unity CLI, not the Test Runner window

`unity` CLI `1.0.0-beta.6` is installed and an Editor is already connected:

```bash
unity status
```

That prints the live Editor (port 7800, project `D:\UnityProjects\BroAudio6000`, 6000.3.9f1). Use the connected Editor — it avoids a cold batch-mode boot per iteration.

Editor commands take the form `unity cmd <name> --param value`. Inspect any command's parameters with `unity cmd --query <name> --detail full`. The working loop:

```bash
unity cmd recompile && unity cmd recompile_status
```

```bash
unity cmd run_tests --mode PlayMode --filter Ami.BroAudio --async_tests true
```

```bash
unity cmd test_status
```

Also useful: `list_tests` (enumerate without running), `console` / `get_console_logs` (compile errors and `[BroAudio]` output), `cancel_tests`, and `eval_file` — run C# in the live Editor from a `.cs` file. **`eval_file` is the fastest way to check what the runtime actually does before writing a test around it**; the entity-builder facts above were established that way in one call. Full-run form with a report file:

```bash
unity test --mode PlayMode --output test-results.xml
```

`--retries` reports tests that pass on re-run as flaky — use it once the suite is sizeable to catch timing flakiness rather than guessing at it.

**Consequence for the executing session: never hand a test back unrun.** Compile it, run it, read the result. A test that has not executed is not a deliverable.

---

## Delegation and model budget

The orchestrating session's context is the scarce resource — not the token count. Fan out the reading and the typing; keep the decisions and the Editor in one place.

### The hard constraint: one Editor, one lane

`unity cmd recompile`, `run_tests`, `test_status` and `eval_file` all drive **the single connected Editor**. Two agents compiling or running at once interleave and hand each other's results back.

- **Only the orchestrator runs `unity cmd`.** Subagents read source and write files, then report. Nothing else.
- Exception: one agent explicitly handed the lane (a focused debug or probe task) — and then nothing else runs until it returns.
- So: **fan out on writing, serialize on verifying.** Three test files written in parallel and compiled once beats three write→compile→run round-trips.

### Who does what

| Work | Agent | Model | Why |
|---|---|---|---|
| Orchestration, phase 0 harness, isolation strategy, ranking, findings judgement | main session | Opus | every other agent inherits these decisions |
| Source sweeps — "what does X actually do", "find every caller of Y" | `Explore` | Sonnet | read-heavy, returns a conclusion instead of a file dump |
| Phase 1 inventory, one API area each | `general-purpose` | Sonnet | reads broadly, writes one doc section |
| Writing a test file against a *proven* harness | `general-purpose` | Sonnet | mechanical once the conventions exist |
| Triaging a compile error or a failed run's console output | `Explore` | Haiku | grep and report, no judgement |

Do not spend Opus on reading source. Do not spend Sonnet on deciding the isolation strategy. If a question is answerable by one grep, grep it — a subagent spawn costs a cold context.

### Context contract for every spawn

A subagent starts cold and re-derives nothing for free. Each prompt carries, verbatim:

1. absolute paths to the harness files it builds on (base fixture, `TestAudioLibrary`) and an instruction to **read them before writing anything**;
2. the exact behaviors to cover — a named slice of `Docs/TEST_INVENTORY.md`, never "test fades";
3. the three timing facts and the **characterize, do not fix** rule from this doc;
4. the prohibitions: **no `unity cmd`, no production-code changes, no harness edits**;
5. what to return: files written, ≤10 lines on what each test asserts, and anything it could not observe.

If a writer agent finds it needs a harness change, it **stops and says so**. The orchestrator makes harness edits, because every agent in flight depends on them.

Commit the green suite at every phase boundary before fanning out again. A phase that ends red is a phase that gets thrown away when context runs out.

---

## Principles

**Characterize, do not fix.** Tests capture current behavior as-is. If actual behavior conflicts with the docs, apparent intent, or a cleaner design, characterize the actual behavior and **log the conflict in `Docs/TEST_FINDINGS.md`**. Never change production code to make a test pass. This effort is not a refactor.

**Highest reliable boundary.** Prefer, in order: (1) public `BroAudio` / `IAudioPlayer` API, (2) Unity runtime state (`AudioSource`, `AudioMixer` params) as the proxy for what the user hears, (3) internal state — only when no reliable external observation exists, or when the internal value *is* the authoritative result. Never reach inward just because it is easier to assert.

**Scenarios, not methods.** One test may span several internal systems if they implement one user-meaningful behavior. Never write a test because a class, method or branch exists.

**Unit tests where they genuinely pay.** Conversion math (linear↔dB), clip-selection strategies (`Runtime/Utility/ClipSelection/`), flags/enum helpers, state machines. These are EditMode-fast and deterministic — take them. Not for padding coverage.

---

## Phases

Work in order. Do not start phase 2 until phase 1's harness is proven by a green test.

**Phase 0 — Harness.** `versionDefines` already added to `Tests.asmdef`. Base PlayMode fixture carrying the isolation strategy. `TestAudioLibrary` builder. Frame/DSP wait helpers.
**Exit criterion:** one test plays a real clip through the public API and asserts an `AudioSource` is playing the expected clip — run twice in a row via the CLI, green both times.

*Delegation: none. Main session, Opus. Everything downstream is copied from what you write here.*

**Phase 1 — Behavioral inventory.** Walk the public API (`Runtime/BroAudio.cs`, `BroAudioChainingMethod.cs`, `Interface/`) and the Preferences toggles. Produce `Docs/TEST_INVENTORY.md`: per behavior — what to protect, how it is observable, edge cases, timing class (frame / DSP / coroutine), regression risk. Then rank. **Stop and show the user the ranked list before mass-writing tests.**

*Delegation: 3–4 parallel `general-purpose` (Sonnet), one API area each — play/stop lifecycle, volume+mixer, time-dependent, selection+policy — each appending its own section to `Docs/TEST_INVENTORY.md`. Ranking is the orchestrator's, not theirs.*

**Phase 2 — Core playback.** Play/Stop/Pause/Resume lifecycle, `IAudioPlayer` handle validity after recycle, volume, pitch, mixer routing. Highest risk, lowest timing complexity — do first.

*Delegation: 2–3 `general-purpose` (Sonnet) writers, one test file each, spawned together. Orchestrator compiles and runs once when all have returned.*

**Phase 3 — Time-dependent behavior.** Fades, cross-fades, music transitions, delay/scheduling, looping (note: loops work by **player handover**, not `AudioSource.loop` — the pause/resume seam is a known NRE source), seamless loops, chained playback.

*Delegation: same fan-out, but hand each writer the DSP-vs-frame fact and the polling-with-timeout helper by name. This is where a cold agent invents `WaitForSeconds` if you let it. Triage failures with an `Explore` (Haiku) reading the console.*

**Phase 4 — Selection and policy.** Multi-clip modes (Random/Shuffle/Sequence/Velocity/Chained), randomization ranges, playback groups, voice limits, dominator/BGM decorators, Localization (locale swap + restore in the fixture).

*Delegation: same fan-out. Voice-limit and group tests need an explicitly wired `PlaybackGroup` — say so in the prompt, it is not discoverable from the harness.*

**Phase 5 — Addressables.** Only after the above are green and only with the user's go-ahead, since it adds addressable entries to the project. Cover: load-on-play, preload, the unused-entity cleanup routine, and playing an entity whose clip has not finished loading.

*Delegation: main session. Project-file footprint plus Editor commands — not a subagent's lane.*

---

## Definition of done

- Suite runs green twice consecutively from a cold Editor, and in a shuffled order.
- Suite runs green through `unity test --mode PlayMode` (not just the Test Runner window).
- No test depends on another test having run.
- Total PlayMode runtime stays sane (target: minutes, not tens of minutes) — flag it if not.
- `Docs/TEST_INVENTORY.md` marks each inventoried behavior covered / deferred / out-of-scope.
- `Docs/TEST_FINDINGS.md` lists every behavior/doc conflict found, unresolved and un-"fixed".
- No production code changed. If a test is impossible without a seam, **propose the seam, stop, ask.**

> **Amendment (2026-08-30).** The "do not fix" rule above governed the suite while it was being
> built, and it held: every finding was characterized first and logged before anything changed. Once
> both suites were green the maintainer reviewed the findings and approved fixing a subset of them, so
> the repository now contains production changes this plan originally forbade. Fixed findings move to
> [FIXED_ISSUES.md](FIXED_ISSUES.md) with their commit; the rest stay open in
> [TEST_FINDINGS.md](TEST_FINDINGS.md). New characterization work still follows the original rule —
> find it, pin it, log it, and ask before fixing.


## Anti-goals

Coverage targets. A test per method. `WaitForSeconds` sprinkled until it passes. Refactoring production code to be more testable without asking. Testing Editor windows or inspectors. Asserting on log text. Handing back tests that were never executed. Two agents driving the Editor at once. Spawning an agent to answer what one grep answers.
