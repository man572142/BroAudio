# Adding a Test

The working rules for a new test, in one place. [GOAL.md](GOAL.md) is the standard these come from; the
base fixtures' XML comments hold the detail behind each one.

## 1. Characterize, don't fix

Pin what the code does today, even where it looks wrong. If it contradicts the docs or the obvious intent,
the test still asserts the actual behavior, and the conflict goes into
[TEST_FINDINGS.md](TEST_FINDINGS.md) (see §6). Don't change production code to make a test pass or to make
something observable: propose the seam, stop, and ask the maintainer. A production change the maintainer
does ask for goes in a commit of its own, never folded into the diff that adds or edits tests, and gets an
entry in [FIXED_ISSUES.md](FIXED_ISSUES.md).

## 2. Pick the suite

| The code under test | Suite | Folder / assembly | Base class | Namespace |
|---|---|---|---|---|
| Needs `SoundManager`, a playing voice, the mixer, or frames/DSP time | PlayMode | `Assets/Tests/Runtime/` · `Tests.asmdef` | `BroAudioTestFixture` | `Ami.BroAudio.Tests` |
| Pure runtime logic (math, clip-selection strategies, flag helpers) with no `SoundManager` | EditMode | `Assets/Tests/Editor/` · `EditorTests.asmdef` | none | `Ami.BroAudio.Tests` |
| `BroAudioEditor` tooling | EditMode | `Assets/Tests/Editor/` · `EditorTests.asmdef` | `BroEditorTestFixture` | `Ami.BroAudio.Editor.Tests` |

Choose the cheapest suite that can observe the behavior. Assert at the highest reliable boundary: the
public `BroAudio` / `IAudioPlayer` API first, then Unity state (`AudioSource`, mixer parameters), and
internal state only when nothing external shows the result. Code behind `PACKAGE_ADDRESSABLES` or
`PACKAGE_LOCALIZATION` goes inside the matching `#if`. Drawing code (IMGUI) is out of scope; test the logic
a draw call uses, not the draw call.

## 3. Build fixtures in code, through the base class

PlayMode (`BroAudioTestFixture`):

- `NewSound(name, type, clips)` builds a tracked entity and returns its `SoundID`. Use `NewEntity` plus
  `IdOf` when the entity needs configuring first, `NewClip(seconds)` for a generated tone of exact length,
  and `NewGroup(...)` for a `DefaultPlaybackGroup` with only the rules you need. A code-built entity has no
  `AudioAsset`, so it plays outside any playback group unless you give it one.
- `NewAssetBackedSound` / `NewAssetBackedEntity` build the shape a Library Manager entity has — owned by an
  `AudioAsset` — and so play under the shipped global group (`FactoryGlobalPlaybackGroup`, a factory
  `DefaultPlaybackGroup`): two plays of one ID in the same frame, or within 0.04 s, are rejected. Use it when
  the behavior is what a user's authored sound does; use `NewSound` when a test needs to replay an ID freely.
- Anything else you create goes through `Track(obj)`, which destroys it in TearDown. Subscribe to
  `OnBGMChanged` only through `SubscribeBgmChanged`, since the event is static.
- Don't re-solve isolation. Setup puts `RuntimeSetting` at factory values (including a fresh factory global
  group) and, under `BroAudio_InitManually`, calls `BroAudio.Init()` once, as a project on that define must.
  Teardown stops and drains every player, destroys the tracked objects *before* resetting any global state
  (a component's `OnDisable` can write it), drains the master fade, resets effect parameters and volumes,
  then verifies that the per-type volume and pitch prefs, the per-type LowPass/HighPass bits and the
  dominator's `Main_LowPass` / `Main_HighPass` read their defaults, reporting anything that leaked under the
  test's name. A test that needs another setting value sets it in its own body.
- Set private fields through `TestAudioLibrary`'s reflection helpers and its `Reflected` names, never a
  string literal. A new nested class under `TestAudioLibrary.Reflected` needs a mapping to its production
  type in `ReflectionCanaryTests`, which fails until it has one and then resolves every name in it.

EditMode (`BroEditorTestFixture`):

- The fixture snapshots and restores the settings assets (putting each one's dirty bit back as it found
  it), the `LastEditAudioAsset` EditorPrefs key (deleting it if the test created it) and the system
  clipboard. Every TearDown step runs even if an earlier one throws. Per-fixture setup and teardown go in
  `OnSetUp` / `OnTearDown` overrides. `EditorRunIsolationGuard` then checks the whole run from outside —
  the settings files' bytes on disk, BroAudio's EditorPrefs keys, the clipboard, the temp folder — so save
  any open edit to a settings asset before running.
- Reach non-public editor members through `EditorReflected`, which keeps every such name in one place and
  fails with a `BroAudioException` naming the member, rather than calling `GetField` / `GetMethod` inline.
- Build in memory with `NewScriptableObject<T>` / `NewSerializedObject<T>`. The only place a test may write
  to disk is `EnsureTempFolder()`, which is deleted afterwards. Never write under `Assets/BroAudio/`, and
  never name a temp path with the package name (the import hook would fire).
- Nothing may trigger a domain reload: no script defines, no code generators, no upgraders.
- **The `Resources~` precondition.** Editor tests load `Editor/Resources/` assets (such as
  `BroInstruction.asset`), which are gitignored and copied from the committed `Resources~` only when
  absent. After changing a file under `Resources~`, delete its local copy and let it regenerate, or the
  tests read stale data.

A committed fixture asset needs a reason Unity forces on it, as with the addressable tones in
`Assets/Tests/Fixtures/`, and is regenerated by hand, never by a run.

## 4. Wait on the right clock, with a named budget

- `BroAudio.Play` only enqueues; the voice starts in that frame's `LateUpdate`. Yield before asserting.
- **DSP clock** for DSP-scheduled state (`timeSamples`, scheduled start/end, loop and handover seams):
  `WaitDspSeconds`. **Frame clock** for fades, pitch ramps and anything driven by `Utility.GetDeltaTime()`:
  `WaitForSeconds` or frame waits. The two run at different rates on a machine with no audio device.
- For a state flip, poll with `WaitUntilOrTimeout` / `WaitForPlaybackStart` / `WaitForRecycle`, never a bare
  `WaitForSeconds`. Every timeout is a named budget (`DefaultPlaybackWaitSeconds`,
  `RampConvergenceWaitSeconds`, `HandoverWaitSeconds`, `SlowAddressableWaitSeconds`) or arithmetic on the
  test's own data (`fadeTime + 1f`). Don't invent a new number. Tests that wait on Addressables go under
  `[Category("Slow")]`.
- Keep every decisive window at least 1 s wide: one slow frame can move a sample point by a third of a
  second.
- A wait is not an assertion. Check that the test can fail: if the clip would end by itself inside the
  wait, a no-op `Stop` still passes. Assert the state change directly, or use a clip longer than the wait.
- If the voice itself must advance in real time, start with `yield return RequireRealtimeAudioClock();`.
  It ignores the test on a machine without an audio device, and `AudioClockProbeTests` turns that into a
  failure in CI.

## 5. Oracles and logs

- Write expected values as literals derived by hand, not from the production function under test. Pick
  values where a plausible mutation gives a different answer (0.4 rather than 0.5 or 1).
- Seed and restore `Random` for anything random, and state the false-pass odds.
- Expect a log only by its kind: `LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix)`. A
  log Unity emits without BroAudio's tag uses `TestAudioLibrary.AnyLogMessage`. Never match the sentence.
  CI enforces this: `.github/scripts/check_log_expectations.py` fails on any `LogAssert.Expect` whose
  pattern is not the shared prefix, and each untagged Unity log a test expects has to be added to its
  `ALLOWED_UNTAGGED` list, by file and count — a decision for review.
- Reference serialized fields through each type's `NameOf` class.

## 6. Findings

- A test that pins an open finding carries `[Category("Finding_N")]`: an underscore, because NUnit rejects
  a category containing a hyphen and fails the test before it runs.
- A new defect gets the next free number, shared by TEST_FINDINGS.md and FIXED_ISSUES.md: a row in the
  summary table and a section of its own, citing code by type and member, never by line number, ending in
  a `Status:` line that names the pinning test. If you leave it unpinned on purpose, write **Not pinned**
  and the reason on that `Status:` line, and "not pinned" in its table row.
- `FindingCoverageTests` fails if an open finding has neither a tag on a runnable test (not a helper, not
  `[Ignore]`d or `[Explicit]`) nor that note, if the note sits anywhere but the `Status:` line, if a tag
  names a finding the document doesn't record, if a finding noted as unpinned also has a tagged test, if
  the summary table and the sections disagree (a missing or extra row, or a Status cell that says "not
  pinned" when the section doesn't, or vice versa), or if a number is both open and in FIXED_ISSUES.md. A
  pin inside an `#if` (Addressables, `!UNITY_WEBGL`) counts as gated out when that condition is false; the
  gate is read from the test source, and a symbol the fixture cannot evaluate fails it.
- When a finding is fixed, its entry moves to FIXED_ISSUES.md and its tags come off, and the pinning test
  is updated to assert the new behavior.

## 7. Update the records

- Mark the behavior in the per-behavior ledger of [TEST_INVENTORY.md](TEST_INVENTORY.md): **covered**,
  **partial** (with the gap named), **deferred** or **out of scope** (with the reason), citing the test as
  `Class.Method`. TEST_INVENTORY is the only place coverage status is recorded.
- If the test pins a behavior the section files under `Docs/inventory/` do not describe, add it there as
  behavior only — what it does and how it can be observed, with no status, test verdict or finding state.
- List a new test file in [TEST_INVENTORY.md](TEST_INVENTORY.md). There is no CI list to add it to: the
  fixtures each leg must run are derived from the sources by `.github/scripts/derive_test_suites.py` (the
  owning asmdef picks the leg, the `#if`s around the class are evaluated for that leg), and a derived
  fixture missing from a leg's results fails it. Run the script to see what your fixture is held to.
- Commit each new `.cs` file with the `.meta` Unity generates for it.
- Run the test before handing it back, in its own suite and in isolation. A test that has never run is not
  finished.

## 8. What CI holds a change to

- **Every test reaches a verdict.** A leg fails on any Ignored, Skipped, Explicit or Inconclusive result
  that `.github/test-results-policy.json` does not allow; each allowance is narrow and carries its reason.
  The main one: a `RequireRealtimeAudioClock` skip, only on a leg without an audio device.
- **Three legs.** EditMode, PlayMode (on an image with a realtime audio device), and
  **EditMode-NoOptionalPackages**, which removes Addressables and Localization before Unity starts — so
  every assembly must compile without them — and runs the EditMode suite there, with the optional-package
  probes inverted to assert the packages are gone.
- **Random order, nightly.** A scheduled run executes every leg with `-randomOrderSeed`, and prints the seed
  so a failure can be reproduced by re-running the workflow with it. A test that only passes after another
  fails there.
- **Static checks**, with no Editor: `check_log_expectations.py` (§5), `derive_test_suites.py` (§7), and
  `check_fixed_issues_record.py`, which fails a commit — and a pull request or push as a whole — that
  changes production `.cs` under `Assets/BroAudio/` and tests together without touching
  `Docs/FIXED_ISSUES.md` (§1). A commit that genuinely needs no entry (a comment-only edit, a rename a test
  follows) says so with a `No-Fixed-Issue: <reason>` trailer, which the check prints for review.
