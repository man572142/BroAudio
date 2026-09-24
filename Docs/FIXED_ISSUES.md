# Fixed Issues

Issues found while building the regression suite that have since been fixed. Written in plain
language for release notes and future review. Open findings stay in [TEST_FINDINGS.md](TEST_FINDINGS.md).

Unreleased (after 3.2.3).

| # | Area | Issue | Commit |
|---|---|---|---|
| 1 | Addressables | Playing a clip created in code crashed with a NullReferenceException | `ddec2e03` |
| 2 | Pitch | The "AudioMixer" pitch option did nothing at all | `4357a8eb` |
| 3 | Clip selection | Dead code: `LayeredClipStrategy` could never run | `97c93cee` |
| 4 | Looping | Dead code: `SeamlessLoopHelper.cs` was fully commented out | `3701e52c` |
| 5 | Clip selection | `SetSequenceId` silently did the wrong thing in the wrong play mode | `42fd0644` |
| 6 | Clip selection | Single play mode accepted an empty clip and failed later | `bac5ed45` |
| 7 | Volume | Setting a type volume to exactly 1 didn't reach newly started players | `42e1a940` |
| 17 | Effects | `SetEffect(...).ForSeconds(...)` threw on the default fade time | `0817926a` |
| 18 | Editor / Instructions | `Instruction.SoundSource_PositionMode` had no shipped text, and the other Sound Source tooltips were hardcoded | `a9165aa5` |
| 15 | Logging | Five runtime logs in the `Ami.Extension` namespace carried no `Utility.LogTitle` prefix | `2b0552f1` |
| 19 | Editor / Instructions | `BroInstruction.asset` key `15` was stale, belonging to no enum value | `a474a8a7` |
| 16 | Effects | Resetting all effects could report completion once per effect instead of once | `48516f57` |
| 20 | Editor / Audio type | Dead code: two audio-type index helpers with no callers, and a broken round-trip | `48516f57` |
| 28 | Editor / Clip editing | A zero-length fade divided by zero and reported an edit it never made | `48516f57` |
| 30 | Editor / Sample data | Trimming past the end of a clip spliced it with its own beginning | `48516f57` |
| 33 | Editor / Logging | Three rect-splitting logs in the Editor assembly carried no `[BroAudio]` prefix | `b9a9069f` |
| 52 | Editor / Instructions | `BroInstruction` had no `NameOf` class, so its serialized fields were reachable only by string literal | `b9a9069f` |
| 63 | Logging | Nine runtime logs in the `Ami.BroAudio` namespaces carried no `Utility.LogTitle` prefix | `78ffd841` |
| 64 | Editor / User data | A fresh clone never generated the user-data assets, because the import hook looked only at the first imported path | `35d5616e` |

---

## 1. Playing a code-created clip crashed when Addressables is installed

**What was wrong:** If you built a `BroAudioClip` in code (`new BroAudioClip()`) instead of authoring
it in the Library Manager, its internal asset reference stayed empty — Unity only fills that in when
it loads the asset from disk. The first time anything asked the clip whether it was valid, it threw a
NullReferenceException. Because that happened inside a coroutine, you got a silent failure instead of
a useful error. Editor-authored assets were never affected, which is why it went unnoticed.

**How it's fixed:** The asset reference is now created up front, so a code-made clip looks exactly
like a serialized one, and the "is this an addressable clip?" check tolerates a missing reference.

## 2. The "AudioMixer" pitch option did nothing

**What was wrong:** Pitch could be set to shift through either the AudioSource or the AudioMixer. The
AudioMixer path had its actual work commented out, so picking it made `SetPitch` a no-op: the value
was stored but never applied to anything. The Preferences toggle exposing the choice was already
disabled too.

**How it's fixed:** The option was removed rather than repaired. Pitch is now always applied through
`AudioSource.pitch`, which is what everyone was already getting. `PitchShiftingSetting` and its
related settings fields are gone.

## 3. Dead code: `LayeredClipStrategy`

**What was wrong:** A clip-selection strategy class that nothing could ever create — there is no
"Layered" play mode to select it. Leftover or unfinished work.

**How it's fixed:** File deleted.

## 4. Dead code: `SeamlessLoopHelper.cs`

**What was wrong:** The whole file was commented out. Anyone looking for the seamless-loop logic would
find it here and find nothing.

**How it's fixed:** File deleted. The real seamless-loop handling lives in `AudioPlayer.Playback.cs`
and `AudioPlayer.Scheduling.cs`.

## 5. `SetSequenceId` in the wrong play mode

**What was wrong:** `SetVelocity` refuses (and logs) when the sound isn't using Velocity play mode.
`SetSequenceId` had the same "only meaningful in one mode" nature but accepted the call quietly and
stored a value that would never be used.

**How it's fixed:** `SetSequenceId` now logs and ignores the call when the sound isn't in Sequence
play mode, matching `SetVelocity`. The property can no longer be set around the guard.

## 6. Single play mode accepted an empty clip

**What was wrong:** In Single play mode, a clip slot with no audio file assigned was treated as valid
and passed along, so the failure appeared later somewhere less obvious. Sequence and Shuffle modes
already rejected it up front.

**How it's fixed:** Single mode now rejects an unassigned clip and logs the same message Sequence
does, so the problem is reported where it actually is.

## 7. Type volume of exactly 1 skipped newly started players

**What was wrong:** `SetVolume` on a `BroAudioType` applied to all currently playing sounds of that
type, but sounds started *afterwards* skipped the stored value when it was exactly 1. The audible
result was the same (a new player already starts at full volume), but it made the stored setting and
live players inconsistent, so reading it back couldn't be trusted.

**How it's fixed:** The special case for 1 was removed; the stored volume is always applied to a
starting player.

## 17. `SetEffect(...).ForSeconds(...)` threw on the default fade time

**What was wrong:** The documented way to apply a temporary effect is to chain a wait onto
`SetEffect`:

```csharp
BroAudio.SetEffect(Effect.LowPass(800f)).ForSeconds(2f);
```

`Effect.LowPass` and its siblings take a fade time that defaults to 0, and with no fade there was
nothing for the internal tween to do — it finished the instant it was started, before `SetEffect`
had even returned. The `.ForSeconds(2f)` that followed then went looking for the tween it was meant
to attach to, found it already gone, and threw. `.Until(...)` and `.While(...)` failed the same way,
which also put dominator effects at risk, since ducking everything but the dominator waits with
`.While(...)` internally. The only way to use the chaining API at all was to pass an explicit
non-zero fade time. In the PlayMode test suite the exception aborted the whole run rather than
failing one test.

**How it's fixed:** When the tween has already finished, the wait now re-arms it instead of failing —
the effect is queued again with the wait already attached, so it holds for the requested duration and
then resets itself, exactly as it does with a non-zero fade. Silently doing nothing was the other
option, but that would have left the effect applied forever with no error to explain why.

The fix is `0817926a` on the `test` branch. An earlier copy of the same change, `4eced071`, was cited
here before; it was rewritten before landing and is not an ancestor of `test`.

## 18. Editor / Instructions: `SoundSource_PositionMode` had no shipped text

**What was wrong:** The Sound Source inspector's Position Mode tooltip was a hardcoded string
literal on the `GUIContent` in `SoundSourceEditor.cs`, instead of going through the
`BroInstructionHelper`/`BroInstruction.asset` instruction system every other editor tooltip uses.
Because of that, `Instruction.SoundSource_PositionMode` (450) had no corresponding entry in the
shipped asset, so anything that *did* resolve it through the normal path got back the
`??????????` missing-text sentinel.

**How it's fixed:** Added the tooltip text as key `450` in the committed source copy of the asset,
`Resources~/Editor/BroInstruction.asset` (the `Editor/Resources/` copy is gitignored and generated from
it), and `SoundSourceEditor` now builds `_positionModeContent` via
`_instruction.GetText(Instruction.SoundSource_PositionMode)` like the rest of the editor code.

The same commit went further than the missing entry. The other six Sound Source tooltips (Play On Enable,
Only Play Once, Stop On Disable, Override Fade Out, Override Playback Group, Delay) were also hardcoded
literals; each gained an `Instruction` member (`SoundSource_PlayOnEnable` through `SoundSource_Delay`,
values 451-456) and a matching asset key, and `SoundSourceEditor` now resolves all seven through the
instruction system. The tooltip text itself is unchanged. The commit also carried unrelated test edits —
see *Departures from the own-commit rule* below.

## 15. Five runtime logs in `Ami.Extension` carried no `[BroAudio]` prefix

**What was wrong:** Every runtime log is supposed to be prefixed with `Utility.LogTitle` (the
`[BroAudio]` tag) so console output is attributable to the package. Five `Debug.LogError` calls in
the generic `Ami.Extension` namespace — `AudioExtension.TryGetSampleData`,
`AudioExtension.IsValidFrequency`, `FlagsExtension.GetFlagsOnCount`, and two in
`LoopExtension.MainLoopLogic` — were missed by an earlier pass over the rest of the codebase.
`IsValidFrequency` is reachable from public API (`LowPassOthers`/`HighPassOthers` on a dominator
player), so package consumers could see an unattributed console error.

**How it's fixed:** All five now prefix with `Ami.BroAudio.Utility.LogTitle`, matching the existing
pattern already used elsewhere in `Ami.Extension` (e.g. `InstanceWrapper<T>.LogInstanceIsNull`).
`Ami.Extension` picks up the type via the namespace's `Ami` parent rather than a new `using`, so no
assembly dependency changes.

## 19. `BroInstruction.asset` key `15` was stale

**What was wrong:** Key `15` in the shipped instruction asset was a pitch-shifting tooltip whose
`Instruction` enum member had been deleted. It deserialized to the undefined `(Instruction)15` and was
never read by anything — harmless, but dead shipped data.

**How it's fixed:** Removed the `Key: 15` entry from `BroInstruction.asset` (both the shipped copy and
the `Resources~` source copy).

## 20. Dead code: `GetSerializedEnumIndex` / `GetAudioTypeByIndex`

**What was wrong:** A pair of `BroEditorUtility` helpers that converted a `BroAudioType` to a
dropdown index and back. Nothing in the package called either one any more, and they no longer
agreed with each other: the "to index" side counted bits of the underlying number, so the composite
`All` produced the same index as `VoiceOver`, and converting that index back gave you `VoiceOver`.

**How it's fixed:** Both methods deleted, along with the three tests that pinned their behavior.
Fixing dead code is worse value than removing it; whoever needs the conversion next can write the
version their caller actually requires.

## 28. A zero-length fade divided by zero and reported an edit

**What was wrong:** In the Clip Editor's `AudioClipEditingHelper`, `FadeIn(0f)` (and `FadeOut(0f)`)
computed a per-sample volume step of `1 / 0` = infinity. The infinity was never applied — the loop
that would have used it runs zero times — so no audio was harmed. But the call still marked the clip
as edited, which made the helper build a full copy of a clip nothing had changed.

**How it's fixed:** Both methods now return immediately when the fade window rounds to zero samples.
No division, and no phantom edit, so an untouched clip is handed back as the original instance.

## 30. Trimming past the end of a clip spliced it with its own beginning

**What was wrong:** `TryGetSampleData` asked Unity for however many samples the requested range
implied, without checking that many were actually left in the clip. Unity's `AudioClip.GetData`
does not fail in that situation — it wraps around and keeps reading from the *start* of the clip.
A stale or too-large end position therefore produced a clip with a chunk of its own opening spliced
onto the end, silently, with no error and the edit reported as successful.

**How it's fixed:** The read now clamps to the samples that actually remain after the start
offset, so it stops at the end of the clip instead of wrapping. A range that leaves nothing to read
logs an error and returns false, like every other failure on this path. Normal trims are unaffected —
the clamp only ever removes samples that were not in the clip to begin with. Covered by
`ClipEditingTests.Trim_RangeLongerThanTheClip_ClampsToTheEndInsteadOfWrappingAround`, which is the
test this finding previously lacked.

## 16. Resetting all effects could report completion once per effect

**What was wrong:** `SetEffect` with `EffectType.None` resets every active effect at once, and counts
the fades it started so it can report back when the last one lands. The count was raised *after* each
fade was launched, but a fade with nothing to do (already at its target, or a zero fade time) finishes
immediately rather than on a later frame — so it decremented a count that had not been raised yet. The
"everything is done" callback then fired once per tracked effect instead of once for the whole reset.
It stayed invisible only because that callback is never supplied on this path today.

**How it's fixed:** The count is raised before each fade starts, and the loop holds one count of its
own that it releases when it has finished launching everything. A fade that completes instantly can no
longer end the reset early, and the callback fires exactly once — including once, immediately, when
there was nothing to reset at all.

## 33. Three rect-splitting logs in the Editor assembly carried no `[BroAudio]` prefix

**What was wrong:** The same problem as #15, one assembly over. `EditorScriptingExtension`'s two
`SplitRect*` ratio-sum guards and the inner `SplitHorizontal` null-array guard logged errors with no
`Utility.LogTitle` prefix, so a developer whose rect ratios did not sum to 1 saw an unattributed error
in the console. The earlier sweep covered the runtime assembly only.

**How it's fixed:** All three now prefix with `BroAudio.Utility.LogTitle`, exactly as #15 did. The
tests that assert these logs previously matched the message string *exactly*, which would have turned
them red the moment the prefix was added; they now match on a substring `Regex`, the same
prefix-agnostic shape the runtime tests already use. The rest of the Editor assembly is still unswept —
see TEST_FINDINGS #34.

## 52. `BroInstruction` had no `NameOf` class

**What was wrong:** Editor code that reaches a serialized field through `SerializedObject` is meant to
go through the type's nested `NameOf` class instead of a string literal, and `SoundSource`,
`BroAudioClip`, `AudioAsset`, `IssueReportDraft` and the rest all carry one. `BroInstruction` was the
exception, so the only way to walk its instruction table was to spell `"_dictionary"`, `"Key"` and
`"Value"` out by hand — `ShippedDataTests`, which reads the shipped asset that way, did exactly that.
Renaming any of the three would have compiled cleanly and then failed at runtime, with nothing
pointing at the member that moved.

**How it's fixed:** `BroInstruction` gains the nested `NameOf` class the convention asks for —
`Dictionary`, `Key` and `Value`, each a `nameof` of the real member, so a rename is a compile error at
the call site. `ShippedDataTests` now reads the asset through it, literals gone, and its "this field
was renamed" assertion message quotes the constant rather than restating the old name.

Unlike every other entry here, this one was never an open finding: it came out of a review of the test
code itself rather than out of characterizing the library, so it has no number in
[TEST_FINDINGS.md](TEST_FINDINGS.md). It takes the next free number in the sequence the two
documents share.

## 63. Nine runtime logs in the `Ami.BroAudio` namespaces carried no `[BroAudio]` prefix

**What was wrong:** Every runtime log is supposed to start with `Utility.LogTitle`, the `[BroAudio]` tag,
so a console message can be traced to the package. Nine `Debug.LogError`/`LogWarning` calls in six
runtime files did not:

- `BroAudioClip.GetAudioClip` (Addressables partial): the "still loading" warning;
- `SoundID`: the entity-lookup failure, once in the legacy-ID upgrade and once in
  `SoundIDExtension.TryConvertIdToEntity`;
- `Rule.RuleMethod` and the `EmptyRule` constructor: the "rule not initialized" errors;
- `PlaybackPreference.SetVelocity` and `SetSequenceId`: the wrong-play-mode refusals;
- `SoundManager.TryGetAddressableEntity`: the "entity isn't marked as addressable" error;
- `SoundManager.TryGetEntity`: the "SoundID hasn't been assigned" error.

Several of these fire on ordinary misuse of public API — a `SetVelocity` on a non-Velocity sound, a
`Play` with an unassigned `SoundID` — so users saw unattributed errors.

**How it's fixed:** Each call now prefixes its message with `Utility.LogTitle`, exactly as the rest of the
runtime does. The five equivalent logs in the generic `Ami.Extension` namespace were deliberately left
out of this change and fixed separately (#15).

This was never an open finding: it was a production change made while the suite was being built, and it
had no entry here until a later review of the records. It takes a number from the sequence the two
documents share.

## 64. A fresh clone never generated the user-data assets

**What was wrong:** BroAudio's per-project assets — `BroEditorSetting`, `BroRuntimeSetting`,
`SoundManager.prefab` and the rest of `Assets/BroAudio/{Editor/,}Resources/` — are not under version
control; `BroUserDataGenerator` builds them from `Resources~` the first time the package is imported.
`AssetPostprocessorEditor` decided whether to run it by checking only the *first* path of each imported
batch for the package name. On a project with no `Library/` folder, the first path of the big initial
import is whatever sorts first — in this repository, an Addressables data asset — so the generator never
ran and the project had no settings assets and no `SoundManager` prefab. CI starts from exactly that
state, and nearly every test in both suites failed on it.

**How it's fixed:** The postprocessor now scans every imported path for the package name, and a static
flag makes the generator run at most once per domain reload instead of on every batch that happened to
lead with a BroAudio asset. The first-run setup wizard is also skipped in batch mode, where nobody can
answer it. The same commit made the CI workflow upload its test-result files; that part is not
production code.

This was never an open finding: the change was made to get CI running, and it had no entry here until a
later review of the records. It takes a number from the sequence the two documents share.

---

## Departures from the own-commit rule

[GOAL.md](GOAL.md) asks for every production change to land in its own commit, never folded into a diff
that adds or edits tests. These commits did not, and are recorded here so the history reads correctly.
The first two predate that wording in GOAL.md.

| Commit | Production change | Folded in with |
|---|---|---|
| `48516f57` | Fixes #16, #20, #28 and #30 | A new test, `ClipEditingTests.Trim_RangeLongerThanTheClip_ClampsToTheEndInsteadOfWrappingAround`, the #28 pins in `ClipEditingTests` rewritten, and the three #20 tests deleted from `EditorUtilityPureTests` |
| `b9a9069f` | Fixes #33 and #52 | A review pass that edits test files across both suites (fixture, helper and comment changes) and the test docs |
| `a9165aa5` | Fix #18, plus the six extra Sound Source `Instruction` entries | The removal of `ShippedDataTests`' "expected red" note for #18, and finding-number corrections in `ClipEditingTests` and `TransportAndRectMathTests` comments |

Each fix is still correct as recorded above; only the commit boundary departs from the rule. Both
testing plans point here from their *Amendments* sections.
