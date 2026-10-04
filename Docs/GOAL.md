# BroAudio Test Suite: Intent

What the regression suite is for. The working rules for writing a test: [ADDING_A_TEST.md](ADDING_A_TEST.md).

## The goal

**Maximum behavioral confidence per test.** The suite exists so BroAudio can be refactored and upgraded across Unity versions without silently changing what the user hears. A test earns its place by the confidence it adds; coverage percentage is not a target.

## Characterize, don't fix

Every test pins current behavior as-is, even where it contradicts the docs or the apparent intent; the conflict is logged in [TEST_FINDINGS.md](TEST_FINDINGS.md) and stays open until the maintainer asks for a fix. Some tests fail loudly if a still-open defect gets fixed, so a repair is a deliberate test update, never a surprise.

This separates two questions that usually get tangled: *what does it do* (the tests) and *what should it do* (the findings). The findings list is the actionable output; the tests are the safety net that makes acting on it safe.

## What a good test looks like

- **Highest reliable boundary**: observed through the public API or Unity's audio state, not internals.
- **Scenarios, not methods**: one user-meaningful behavior, even across several systems; no test exists because a class or branch does.
- **Unit tests only where they pay**: conversion math, clip selection, flag helpers.
- **Self-contained**: fixtures built in code, and the project byte-identical after a run.

| Suite | Isolation problem it solves |
|---|---|
| Runtime (PlayMode) | One `SoundManager` singleton persists across the whole run. |
| Editor (EditMode) | The project on disk — settings assets, prefs, clipboard — leaks state. |

## Anti-goals

Coverage targets. A test per method. `WaitForSeconds` sprinkled until it passes. Refactoring production code for testability without asking. Testing how Editor windows or inspectors draw. Asserting on log text. Handing back tests that were never executed.
