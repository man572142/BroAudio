# BroAudio Editor Testing Plan

Handoff doc for the session that builds BroAudio's **Editor-assembly** regression suite.

Companion to [TESTING_PLAN.md](TESTING_PLAN.md), which covers the runtime and is **done**, ledger in
`Docs/TEST_INVENTORY.md`. Read that doc's *Principles* and
*Anti-goals* sections; they apply verbatim here and are not restated. Delegation is **not** inherited —
this suite has its own agent and model allocation, below. Everything else in this doc covers what is
different about testing `BroAudioEditor`.

**Goal:** protect the authoring tools users actually touch — name validation, clip trimming, the
instruction strings, the settings assets — with EditMode tests that run in milliseconds and leave the
project byte-identical afterwards. Coverage % is not a goal.

---

## What is different from the runtime suite

| | Runtime suite | Editor suite |
|---|---|---|
| Mode | PlayMode | **EditMode** (`[Test]`, not `[UnityTest]`) |
| The isolation problem | a `DontDestroyOnLoad` singleton leaking state | **the project on disk** leaking state |
| The hard wall | timing (frame vs DSP clock) | **IMGUI** — no `Event.current` outside `OnGUI` |
| The fatal accident | a leaked playing voice | **a domain reload mid-run** |
| Runtime cost | ~18s | should be well under a second |

The timing facts that dominate the runtime plan are irrelevant here. The three that dominate this one
are the IMGUI wall, the domain reload, and the disk footprint. All three are covered below.

---

## Ground truth

### The assembly: a new one is required

`Tests.asmdef` is all-platform (`includePlatforms: []`) and therefore **cannot reference
`BroAudioEditor`**, which is `includePlatforms: ["Editor"]`. Widening it is not an option — it would
drag Editor types into the player build of the runtime suite.

Create `Assets/Tests/Editor/EditorTests.asmdef`:

- `includePlatforms: ["Editor"]`
- references: `UnityEngine.TestRunner`, `UnityEditor.TestRunner`, `BroAudio`, `BroAudioEditor`, `Tests`
- `precompiledReferences: ["nunit.framework.dll"]`, `overrideReferences: true`
- `autoReferenced: false`, `defineConstraints: ["UNITY_INCLUDE_TESTS"]`
- copy the two `versionDefines` (`PACKAGE_ADDRESSABLES`, `PACKAGE_LOCALIZATION`) from `Tests.asmdef`
- the committed file also carries four GUID references to the optional-package assemblies — three of
  them shared with `Tests.asmdef` — added when the optional-package suites moved into this assembly

Referencing `Tests` is the point: **`TestAudioLibrary` is reusable as-is** — `CreateClip(seconds, name)`
generates a procedural clip of an exactly known sample count, which is what the clip-editing tests
need. Do not write a second clip builder.

Namespace: `Ami.BroAudio.Editor.Tests` for tests of the editor tooling itself. That filter no longer
selects the whole EditMode lane, though: the pure-logic suites that later moved out of PlayMode into this
assembly — `AudioMathTests`, `ClipSelectionTests`, `EaseCurveTests`, `LocalizationClipStrategyTests`,
`OptionalPackageEditorTests` — kept the runtime suite's `Ami.BroAudio.Tests` namespace. Filter on
`Ami.BroAudio` for the whole EditMode lane, `Ami.BroAudio.Editor.Tests` for the editor-tooling half.

### Two shipped-data bugs the first test found — both since fixed

Verified by reading the shipped `BroInstruction` asset against the `Instruction` enum. The copy under
version control is `Assets/BroAudio/Resources~/Editor/BroInstruction.asset`;
`Editor/Resources/BroInstruction.asset` is the copy `BroUserDataGenerator` generates into a user's
project, and both fixes below landed in both copies.

1. **`Instruction.SoundSource_PositionMode` (450) had no entry in the asset.** The enum and the asset
   held the same number of entries; the counts cancelled and hid the gap. `BroInstructionHelper.GetText`
   returned `MissingText` — the literal string `??????????` — and the Sound Source position-mode tooltip
   only looked intact because `SoundSourceEditor` drew a hardcoded literal instead of going through the
   instruction system at all. **Fixed — [FIXED_ISSUES.md](FIXED_ISSUES.md) #18.**
2. **Asset key `15` was stale** — a pitch-shifting tooltip whose enum member was deleted (the enum still
   carries a comment pinning the values around the hole). It deserialized to an undefined
   `(Instruction)15` and was never read. **Fixed — [FIXED_ISSUES.md](FIXED_ISSUES.md) #19.**

The two sets line up exactly today: every enum member has an entry, every entry maps to a defined member,
no duplicates. **Do not carry the totals around as facts** — they grow with every new tooltip. Re-derive
them: the enum members are the entries in `Assets/BroAudio/Editor/EditorSettings/Instruction.cs`, the
asset entries are its `Key:` lines (74 and 74 when this was last checked, counting `None = 0`). And note
that equal totals are *not* the check — that coincidence is exactly what hid the 450 gap. The tests
compare the two sets member by member, in both directions.

Both were **findings before they were fixes**, which is the rule this plan runs on: the tests in
`Assets/Tests/Editor/ShippedDataTests.cs` were written as "every enum value resolves to real text" and
"every asset key is a defined enum member", landed red on 450 and 15, were logged as findings, and only
then was the data repaired. They are green now, and they stay green **by fixing the asset, never by
adding an exclusion list** — their failure messages say so.

### `BroInstruction` fails loudly on duplicate keys

`BroInstruction.OnEnable` builds its dictionary with `_actualDict.Add(...)`, which **throws** on a
duplicate key, leaving the dictionary half-built and every later `GetText` returning `??????????`.
Worth one test; it is the failure mode nobody would diagnose from the symptom.

### State that leaks between EditMode tests

The mirror of the runtime plan's singleton problem. Solve it once in a base fixture:

- **`EditorSetting` and `RuntimeSetting` are real assets on disk** (`Editor/Resources/`,
  `Runtime/Resources/`). Snapshot with `JsonUtility.ToJson` and restore with `FromJsonOverwrite` —
  exactly the trick `BroAudioTestFixture` already uses for `RuntimeSetting`. Then clear the dirty flag;
  a test must not leave the user with modified assets staged.
- **`BroEditorUtility._editorSetting` / `_runtimeSetting` are static caches.** A test that deletes or
  re-creates either asset leaves a stale cached reference for every later test. Prefer
  mutate-and-restore over delete-and-recreate.
- **`EditorPrefs`**, keyed by `PlayerSettings.productGUID` — `EditorSetting.LastEditAudioAsset`.
- **`EditorGUIUtility.systemCopyBuffer`** — `PropertyClipboard` writes the user's actual system
  clipboard. Any test touching it restores the previous contents.
- **Temp assets.** Write them to one folder, `Assets/EditorTestsScratch_Temp/`, created in `SetUp` and
  `AssetDatabase.DeleteAsset`-ed in `TearDown`. **Never write into `Assets/BroAudio/`** — that subtree
  *is* the shipped package. The name deliberately contains no `BroAudio`: `AssetPostprocessorEditor`
  matches every imported path against `BroAudio` / `Bro_Audio` / `com.ami.broaudio` to decide whether to
  run `BroUserDataGenerator`, so a temp folder carrying the package name fires user-data generation into
  the shipped package on the first import of a run.

### Three things that will kill a run

- **`ScriptingDefinesUtility`** changes `PlayerSettings` scripting defines → recompile → domain reload →
  the run dies mid-suite. Out of scope, permanently.
- **`AudioProxyModifierCodeGenerator` / `ProxyModifierCodeGenerator`** write `.cs` into
  `Runtime/Player/AutoGeneratedCode/` → same outcome, plus a dirty repo.
- **`SoundIDUpgrader.StartUpgrade`** and `FileStructureUpgrader` rewrite scenes, prefabs and folders
  project-wide. Do not invoke them from a test.

### The IMGUI wall

`Event.current` is null outside an `OnGUI` callback, and `EditorGUI.*` / `GUILayout.*` throw or no-op
without a live event context. Anything whose logic exists only *inside* a draw call is not testable
without building a host `EditorWindow` and pumping repaints — a large, flaky harness for very little
signal.

**The boundary to hold:** test the logic the drawing calls, not the drawing. `DrawClipPropertiesHelper`
is the model — `SetPlaybackPositions` and `SetFadingValues` are `public static`, take an `ITransport`,
and contain the whole behavior; `DrawPlaybackPositionField` around them is just IMGUI. Where a piece of
logic is trapped inside a draw method, **note it in the inventory as untestable-as-authored and stop.**
Do not refactor production code to expose it — propose the seam, stop, ask.

---

## Running the suite

Same single-Editor lane rule as the runtime plan: **only the orchestrator runs `unity cmd`.**

```bash
unity cmd run_tests --mode EditMode --filter Ami.BroAudio
```

```bash
unity cmd test_status
```

EditMode tests run inside the Editor's own domain, so an unhandled exception that triggers a reload
takes the run with it. If `test_status` returns nothing coherent, check `unity cmd console` for a
recompile before assuming a test bug.

Prefer `[Test]` throughout. Reach for `[UnityTest]` only where editor frames genuinely matter (the
`EditorAudioPreviewer` / `PlaybackIndicatorUpdater` path, if it is ever covered) — EditMode yields
advance on `EditorApplication.update`, and the semantics are not PlayMode's.

---

## Delegation

Orchestration stays in the main session on **Opus 5**. Everything mechanical fans out to **Sonnet 5**.
The orchestrator's context is the scarce resource — not the token count. Fan out the reading and the
typing; keep the decisions and the Editor in one place.

### The hard constraint: one Editor, one lane

`unity cmd recompile`, `run_tests`, `test_status` and `eval_file` all drive **the single connected
Editor**. Two agents compiling or running at once interleave and hand each other's results back.

- **Only the orchestrator runs `unity cmd`.** Subagents read source and write files, then report.
  Nothing else.
- Exception: one agent explicitly handed the lane for a focused probe — and then nothing else runs
  until it returns.
- So: **fan out on writing, serialize on verifying.** Three test files written in parallel and compiled
  once beats three write→compile→run round-trips.

### Who does what

| Work | Agent | Model | Why |
|---|---|---|---|
| Orchestration, phase E0 harness, isolation contract, ranking, findings judgement | main session | **Opus 5** | every writer inherits these decisions |
| Writing a test file against a proven harness | `general-purpose` | **Sonnet 5** | mechanical once the conventions exist |
| Updating `Docs/TEST_INVENTORY.md` and `Docs/TEST_FINDINGS.md` | `general-purpose` | **Sonnet 5** | transcription against a fixed template |
| Source sweeps — "what does X actually do", "find every caller of Y" | `Explore` | Sonnet 5 | read-heavy, returns a conclusion instead of a file dump |
| Triaging a compile error or a failed run's console output | `Explore` | Haiku 4.5 | grep and report, no judgement |
| Final review of the finished suite | `general-purpose` | **Opus 5** | judging whether a test is worth keeping is not mechanical |
| Final HTML report | `general-purpose` | **Opus 5** | writing for a human who was not in the session |

Do not spend Opus on reading source. Do not spend Sonnet on the harness or the isolation contract. If a
question is answerable by one grep, grep it — a subagent spawn costs a cold context.

Spawn shape — all writers in **one message** so they run concurrently, backgrounded, and the
orchestrator compiles once when they have all returned:

```
Agent({ subagent_type: "general-purpose", model: "sonnet",
        description: "Write E0 pure-function tests", prompt: "<context contract below>" })
```

### Context contract for every spawn

A subagent starts cold and re-derives nothing for free. Each prompt carries, verbatim:

1. absolute paths to the harness it builds on — `Assets/Tests/Editor/EditorTests.asmdef`,
   `BroEditorTestFixture.cs`, `Assets/Tests/Runtime/TestAudioLibrary.cs` — and an instruction to
   **read them before writing anything**;
2. the exact targets to cover — a named row of the tier tables in this doc, never "test the utilities";
3. the isolation contract (settings snapshot/restore, temp folder, clipboard) and the **IMGUI wall**;
4. the **characterize, do not fix** rule, and the open findings from `Docs/TEST_FINDINGS.md` that the
   slice touches;
5. the prohibitions, verbatim (below);
6. what to return: files written, ≤10 lines on what each test asserts, and anything it could not observe.

### Writer prohibitions — paste into every writer prompt

- **No `unity cmd`.** You cannot compile or run. The orchestrator does that.
- **No production-code changes** anywhere under `Assets/BroAudio/`.
- **No harness edits** — `EditorTests.asmdef`, `BroEditorTestFixture`, `TestAudioLibrary`. If you need
  one, **stop and say so**; every agent in flight depends on them.
- **No new files outside `Assets/Tests/Editor/`.**
- **Characterize, do not fix.** Surprising behavior is a finding to report, not a bug to correct.

---

## Ranked tiers

Ordered by confidence-per-test. Stop and show the user the picture after E1.

### E0 — Pure functions, no Unity state

Milliseconds, zero setup, permanent value. Take all of it.

| Target | What to protect |
|---|---|
| `BroEditorUtility.IsInvalidName` | The **error-code precedence**: empty → `StartWithNumber` → `ContainsInvalidWord` → `ContainsWhiteSpace`. Note the quirk: `IsValidWord` returns *true* for whitespace, so `"a b"` reports `ContainsWhiteSpace`, and a leading digit outranks everything. |
| ~~`GetSerializedEnumIndex` ↔ `GetAudioTypeByIndex`~~ | **Gone — nothing left to protect.** The round-trip was already broken (the composite `All` produced `VoiceOver`'s index, and converting back gave `VoiceOver`) and nothing in the package called either helper, so both were deleted along with the three tests that pinned them. See [FIXED_ISSUES.md](FIXED_ISSUES.md) #20. |
| `Transport.SetValue` | The budget rule in `GetLengthLimit`: each value clamps to `FullLength` minus the *other four*. Rounds to 3 digits, away-from-zero. `Delay` is only `Max(0)` — never length-clamped. Also `HasDifferentPosition`'s odd `Delay > StartPosition` term. |
| `EditorScriptingExtension` rect math | `SplitRectHorizontal`/`Vertical` — both the ratio form and the `params float[] ratios` form: gap accounting, and what happens when ratios don't sum to 1. `Scoping`/`DeScope` round-trip. `GetBackingFieldName`/`GetFieldName`. |
| `BroEditorUtility.Combine` | Naked `+ "/" +` concatenation — a trailing slash yields `//`. Characterize it. |
| `ForeachConcreteDrawedProperty`, `Contains` | Flag iteration stops at `DrawedProperty.All`; every concrete flag is visited exactly once. |
| `IssueReportMarkdown` | `ComposeTitle` label mapping per `IssueType`, and `BuildGitHubIssueURL` escaping (a title with spaces, `#`, `&`). Pure string, no network. |

### E1 — Shipped-data integrity

Cheapest real-bug detection in the plan. This is where the two shipped-data findings were caught; both
are fixed now, and these tests are what keeps them fixed.

- Every `Instruction` value resolves through `BroInstructionHelper.GetText` to a non-empty string that
  is not `MissingText`. **Was red on 450; green since #18.**
- No duplicate keys in the asset (see the `Add`-throws note above).
- Every asset key maps to a defined enum member. **Was red on 15; green since #19.**
- `EditorSetting.ResetToFactorySettings` yields an `AudioTypeSetting` for every concrete
  `BroAudioType`, and `GetAudioTypeColor` / `TryGetAudioTypeSetting` agree with it.
- `GetSpectrumColor(index)` at 0, at `SpectrumBandColors.Count - 1`, and out of range.

### E2 — SerializedProperty operations

Needs a temp `ScriptableObject` + `SerializedObject`. Still EditMode-fast.

- `ResetBroAudioClipSerializedProperties` / `ResetBroClipPlaybackSetting` zero exactly the intended
  fields and leave the others alone.
- `SerializedTransport` writes each `TransportType` back to the right property, after `Transport`'s
  clamping, and applies.
- `FindBackingFieldProperty` / `TryFindPropertyRelative` against a real `AudioEntity`. **This is the
  guard for `TestAudioLibrary`'s reflection** — the runtime suite depends on backing-field names like
  `<Loop>k__BackingField` and has no test that notices when one is renamed.
- `SafeSetCurve` with a null curve and with a zero-key curve.

### E3 — Clip editing (`AudioClipEditingHelper`)

Deterministic sample math — the Clip Editor's actual product, and the only place in the Editor assembly
where a bug corrupts a user's audio file. Build inputs with `TestAudioLibrary.CreateClip`, or a ramp
clip (`data[i] = i / n`) so index↔value is directly assertable.

Cover `Trim`, `AddSlient`, `AdjustVolume`, `Reverse`, `FadeIn`, `FadeOut`, `ConvertToMono` and
`GetResultClip`. Characterize these edges rather than fixing them:

- `GetResultClip` returns the **original instance** when `HasEdited` is false — reference equality, not
  a copy. *(Open: TEST_FINDINGS #29.)*
- `ConvertToMono` Downmix accumulates a running sum whose grouping is offset by one and **drops the
  final group** — output length is `n/channels - 1`, not `n/channels`. *(Open: TEST_FINDINGS #25.)*
- `Reverse` reverses the raw interleaved array, which **swaps L/R** on a stereo clip. *(Open:
  TEST_FINDINGS #26.)*
- `AddSlient` prepends silence (the name says nothing about which end). *(Open: TEST_FINDINGS #27.)*

Two edges on this list were characterized first and repaired later, so they now read the other way round:

- `FadeIn(0f)` used to compute `1f / 0` = ∞ with a loop body that never ran — no audio was harmed, but
  the call still flagged the clip as edited and forced a pointless copy. **Fixed —
  [FIXED_ISSUES.md](FIXED_ISSUES.md) #28:** a fade window that rounds to zero samples returns
  immediately and reports no edit (`ClipEditingTests.FadeIn_ZeroTime_IsANoOpAndDoesNotReportAnEdit`).
- `Trim` past the end of the clip used to wrap around and splice the clip's own opening onto its end —
  `AudioClip.GetData` wraps rather than failing. **Fixed — [FIXED_ISSUES.md](FIXED_ISSUES.md) #30:** the
  read clamps to the samples that actually remain
  (`ClipEditingTests.Trim_RangeLongerThanTheClip_ClampsToTheEndInsteadOfWrappingAround`).

### E4 — Asset-writing paths — ask first

Real disk footprint. Do these last, only with the user's go-ahead, and only inside the temp folder.

- `CreateScriptableObjectIfNotExist<T>` — creates with factory settings; returns the existing asset the
  second time.
- `AudioAssetEditor.CreateNewEntity` / `SetAssetName` / `Verify`.
- `BroUserDataGenerator.CheckAndGenerateUserData`.

**Deferred, with reasons:** `BroVersion.SetVersion` (writes a `.txt` into the shipped package's
`Editor/Resources` and re-imports it); `LibraryManagerWindow` and every other `EditorWindow`
(opens-without-throwing is near-zero signal and leaks window state); `FieldUsageFinder` (scans every
asset in the project — slow, and its result depends on project contents).

### Out of scope

IMGUI drawing. `ScriptingDefinesUtility`. The code generators and `PackageExporter` (`#if
BroAudio_DevOnly`). `SoundIDUpgrader` / `FileStructureUpgrader`. Anything asserting on log text.

---

## Phases

Work in order. Do not fan out a phase until the previous one is green.

**Phase E0 — Harness.** `EditorTests.asmdef` + `BroEditorTestFixture` carrying the isolation contract:
settings snapshot/restore, temp folder create/delete, clipboard restore.
**Exit criterion:** one E1 test compiles and runs green twice in a row via `unity cmd run_tests --mode
EditMode`, and `git status` is clean afterwards.

*Delegation: none. Main session, Opus 5. Every writer downstream copies what you write here, so a wrong
convention costs a rewrite of the whole suite.*

**Phase E1 — Pure functions and shipped data** (tiers E0 + E1). The bulk of the value. This is the phase
that landed the two red shipped-data tests; they were logged as findings and shown to the user before
anything continued, and were repaired later (#18, #19), so the same two tests run green today.

*Delegation: 3 `general-purpose` (Sonnet 5) writers spawned in one message — (a) name validation +
`Combine` + flag helpers (the enum-index round-trip that used to sit in this slice is gone with the
helpers themselves, #20), (b) `Transport` clamping + rect math, (c) shipped-data integrity +
`IssueReportMarkdown`. Orchestrator compiles and runs once when all three return.*

**Phase E2 — SerializedProperty and Transport writeback** (tier E2).

*Delegation: 2 `general-purpose` (Sonnet 5) writers — one for the reset/curve helpers, one for
`SerializedTransport` + the backing-field-name guard. Hand both the temp-`ScriptableObject` pattern from
the fixture by name; a cold agent otherwise invents its own and leaks assets.*

**Phase E3 — Clip editing** (tier E3). The one tier worth a careful read of the production code first —
several of the edges above are load-bearing for what the assertions should say.

*Delegation: 1 `general-purpose` (Sonnet 5) writer, with every characterization edge quoted into the
prompt. This is where an agent "fixes" the Downmix off-by-one if you let it — repeat the characterize
rule in the same paragraph as the edge list.*

**Phase E4 — Asset writing** (tier E4). Confirm with the user before the first test that writes a file.

*Delegation: main session, Opus 5. Disk footprint plus Editor commands — not a subagent's lane.*

Commit green at every phase boundary before fanning out again; a phase that ends red gets thrown away
when context runs out. After each phase, spawn one `general-purpose` (Sonnet 5) to add that phase's row
to `Docs/TEST_INVENTORY.md` and append any new findings to `Docs/TEST_FINDINGS.md` — it is transcription
against a fixed template, and it keeps the orchestrator's context for decisions.

---

## Wrap-up: review, then report

Only after the last phase is green, the docs are updated, and the work is committed. Sequential — the
report needs the review's output.

### 1. Review — one `general-purpose` on **Opus 5**

```
Agent({ subagent_type: "general-purpose", model: "opus",
        description: "Review the editor test suite", prompt: "..." })
```

Hand it: this plan, the file list and diff of `Assets/Tests/Editor/`, `Docs/TEST_FINDINGS.md`, and the
final `test_status` output. Ask it to look for, in this order:

- tests that pass no matter what the production code does — tautologies, assertions on constants,
  `Assert.NotNull` on something the test itself just constructed;
- a characterization that quietly encodes a **bug** as correct behavior without a matching entry in
  `Docs/TEST_FINDINGS.md`;
- missing restores in the isolation contract — a mutated setting, a temp asset, a clipboard write that
  survives the run;
- any file touched under `Assets/BroAudio/`, `ProjectSettings/` or `Packages/`;
- tests that depend on another test having run.

It returns a ranked list with `file:line` and **does not edit anything**. The orchestrator decides what
to act on; fixes go to a Sonnet 5 writer, re-verified in the one Editor lane.

### 2. Report — one `general-purpose` on **Opus 5**, spawned after the review returns

```
Agent({ subagent_type: "general-purpose", model: "opus",
        description: "Write the HTML summary report", prompt: "..." })
```

Hand it: the phase log, per-file test counts and total runtime, both findings docs, and the reviewer's
list with the orchestrator's disposition of each item.

Requirements, verbatim into its prompt:

- **Load the `artifact-design` skill before writing anything**, then write the page to a file and
  publish it with the `Artifact` tool.
- The audience is a Unity developer who was **not** in this session and does not read C#. Lead with
  what is protected now and what turned out to be broken — not with a list of test method names.
- State the two shipped-data findings in plain language: what a user of the package saw before they were
  repaired (#18, #19), and that the tests now hold that data correct.
- Show the numbers that matter (tests per area, runtime, findings open vs. resolved). No walls of code;
  short snippets only where a snippet is the clearest explanation.
- Say plainly what is **not** covered and why — the IMGUI wall, the deferred tiers.

Relay the artifact URL to the user; a subagent's final report is not shown to them.

---

## Definition of done

- Suite runs green twice consecutively via `unity cmd run_tests --mode EditMode`, and each fixture also
  passes in isolation.
- **`git status` is clean after a run.** No file under `Assets/BroAudio/`, `ProjectSettings/` or
  `Packages/` is modified, and no temp asset survives.
- No domain reload is triggered by any test.
- Total EditMode runtime stays under a second or two — flag it if not.
- `Docs/TEST_INVENTORY.md` gains an Editor section marking each target covered / deferred / out of scope.
- Every finding is written down exactly once: still-open ones in `Docs/TEST_FINDINGS.md`, repaired ones
  moved to `Docs/FIXED_ISSUES.md` with their commit. The shipped-data pair is the worked example — both
  sit in the fixed list (#18, #19) and neither appears in the open one.
- `ShippedDataTests` is green because the shipped asset is correct, not because a test grew an exclusion
  list: every `Instruction` member resolves to real text, and every asset key is a defined member.
- Every fixture named in `.github/required-test-suites.json` appears in the results of its leg. A suite
  that compiled to nothing is absent rather than red, so without that check a green run can cover less
  than it claims.
- No production code changed. If a test is impossible without a seam, **propose the seam, stop, ask.**

> **Note.** The "do not fix" rule above is the standard the suite is held to, not a literal
> description of the log — a maintainer-approved fix can land on production code the rule otherwise
> forbids, and approval does not require waiting for both suites to be green first. Fixed findings
> move to [FIXED_ISSUES.md](FIXED_ISSUES.md) with their commit; open findings stay in
> [TEST_FINDINGS.md](TEST_FINDINGS.md). Characterization work still follows the original rule — find
> it, pin it, log it, and ask before fixing.

- The Opus 5 review has run and every item is either fixed or explicitly dismissed in writing.
- The Opus 5 HTML report is published and its URL handed to the user.
