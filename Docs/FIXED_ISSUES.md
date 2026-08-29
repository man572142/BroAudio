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
