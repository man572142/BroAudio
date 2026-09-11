# BroAudio Test Suite: Intent

One-page summary of what the regression suite is for. Full plans: [TESTING_PLAN.md](TESTING_PLAN.md) (runtime) and [TESTING_PLAN_EDITOR.md](TESTING_PLAN_EDITOR.md) (editor).

## The goal

**Maximum behavioral confidence per test, minimum test count.** The suite exists so BroAudio can be refactored and upgraded across Unity versions without silently changing what the user hears. Coverage percentage is explicitly not a goal.

## The core idea: characterize, don't fix

Before this work there were zero tests and no independent record of how the library actually behaves. So the suite was built as a **characterization pass**: every test pins current behavior as-is, even where it contradicts the docs or the apparent intent.

- Where behavior looked wrong, the test asserts the *actual* behavior and the conflict is logged in [TEST_FINDINGS.md](TEST_FINDINGS.md).
- No test was made to pass by changing production code. That is narrower than "zero production changes during the build", which the repository does not support: three commits changed production code over the course of this work. `0817926` fixed `EffectAutomationHelper` in the same diff as the test that hit it ([FIXED_ISSUES.md](FIXED_ISSUES.md) #17); `b9a9069` prefixed three Editor rect-split logs (#33) and added a `BroInstruction.NameOf` seam so the Editor tests could drop their string literals; `35d5616` reworked `AssetPostprocessorEditor` so a cold CI clone generates its user data at all. The seam and the postprocessor change answer to no finding and have no FIXED_ISSUES entry.
- Fixing was the maintainer's call every time, never taken unilaterally — but only half the fixes waited for both suites. Eight of the sixteen in [FIXED_ISSUES.md](FIXED_ISSUES.md) (#1–#7 and #17) were approved once the *runtime* suite was green, before an Editor suite existed at all: the EditMode harness (`7717347`) arrived four days after #1–#7 and a day after #17. The other eight came after it. Fixed findings move to FIXED_ISSUES.md with their commit. Several tests are written to fail loudly if a still-open defect gets fixed, so a repair is a deliberate test update, never a surprise.

This separates two questions that usually get tangled: *what does it do* (the tests) and *what should it do* (the findings). The findings list is the actionable output; the tests are the safety net that makes acting on it safe.

## What a good test looks like here

1. **Highest reliable boundary.** Assert through the public `BroAudio` / `IAudioPlayer` API first, then Unity runtime state (`AudioSource`, mixer parameters) as the proxy for what is heard, and internal state only when nothing external observes the result.
2. **Scenarios, not methods.** One test may cross several internal systems if they implement one user-meaningful behavior. No test exists because a class or branch exists.
3. **Unit tests only where they genuinely pay.** Conversion math, clip-selection strategies, flag helpers: fast, deterministic EditMode tests.
4. **Built in code, bar two committed fixtures.** Tests construct their own `AudioEntity` library through `TestAudioLibrary` and generate their clips at runtime. The exceptions are `Assets/Tests/Fixtures/AddressableTone{A,B}.wav` — two 44 KB sine tones, committed and addressed by hardcoded GUID from `TestAudioLibrary` — because an `AssetReference` resolves through the AssetDatabase, so an addressable clip has to be a real asset on disk. Nothing regenerates them during a run (only the manual `Tools > BroAudio > Tests > Regenerate Addressable Fixtures`), so the project is still byte-identical after one.

## Shape

| Suite | Where | Isolation problem it solves |
|---|---|---|
| Runtime (PlayMode) | `Assets/Tests/Runtime/` | One `SoundManager` singleton persists across the whole run; a base fixture stops, drains, and restores state per test. |
| Editor (EditMode) | `Assets/Tests/Editor/` | The project on disk leaks state; the fixture snapshots and restores settings assets, prefs, clipboard, and a temp output folder. |

Ranked inventory of covered / deferred / out-of-scope behaviors: [TEST_INVENTORY.md](TEST_INVENTORY.md).

## Anti-goals

Coverage targets. A test per method. `WaitForSeconds` sprinkled until it passes. Refactoring production code for testability without asking. Testing Editor windows or inspectors. Asserting on log text. Handing back tests that were never executed.
