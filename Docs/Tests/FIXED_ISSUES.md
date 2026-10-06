# Fixed Issues

Issues found while building the regression suite that have been fixed but not yet shipped on `main`.
Written in plain language for release notes and future review. Open findings stay in
[TEST_FINDINGS.md](TEST_FINDINGS.md).

Once a fix ships on `main`, its row and section are deleted. Its number stays retired, so a new finding
never reuses it; numbers up to 73 are retired.

| # | Area | Issue | Commit |
|---|---|---|---|
| 45 | Playback / Handover | A looping sound with an added filter and a decorator multiplied its effect list, and Unity's refusal logs, at every loop seam | pending |
| 49 | Teardown | Stop/Pause/SetVolume/SetPitch on a player handle threw once the SoundManager was destroyed | pending |
| 53 | Easing | A fade's last frame could stop short of its target, or write NaN to the mixer | pending |
| 60 | Playback / Validation | Playing on a null or destroyed follow target threw a raw NullReferenceException | pending |
| 69 | Editor / Core data | A corrupted legacy core-data file threw out of user-data generation | pending |

---

## 45. Looping sounds with an added filter multiplied their effect list at every seam

**What was wrong:** A looping sound hands its state to a fresh player at each seam, including filters added
through `AddLowPassEffect` and friends. That transfer ran once per attached
decorator (BGM, Dominator) plus once more. Unity allows only one filter of each type per GameObject, so every
extra copy was refused with an untagged console message, yet each still added an entry to the player's list.
The list doubled or tripled at every seam, so a long-running loop did exponentially more work and logging
at each one.

**How it's fixed:** The receiving player skips any filter type it already has, so repeated transfers are
harmless and one filter with one entry carries across every seam.

## 49. Player handles threw during teardown

**What was wrong:** Holding an `IAudioPlayer` and calling `Stop`, `Pause`, `UnPause`, `SetVolume` or
`SetPitch` on it after the SoundManager was destroyed, typically from `OnDestroy` or on application quit,
threw a `BroAudioException`. The handle tried to check a setting on the destroyed manager before deciding to
do nothing.

**How it's fixed:** The handle checks that the manager still exists first, so these calls are silent no-ops
during teardown, like the matching `BroAudio.*` calls.

## 53. Fades could end short of their target, or on NaN

**What was wrong:** The easing function meant to clamp its input to 0–1 but discarded the clamped value.
Several ramps (master volume, effect parameters) evaluate one step past the end on their last frame, so
curves that turn back down past the end, including the default fade-out curve, stopped slightly short of
the target, and `InCirc` wrote NaN to the mixer, leaving the master stage broken until a zero-fade
`SetVolume`.

**How it's fixed:** The clamp is applied, so every ramp ends exactly on its target.

## 60. A null or destroyed follow target threw

**What was wrong:** `BroAudio.Play(id, transform)` with a null or destroyed `Transform` threw a
`NullReferenceException` before any validation, where every other invalid input logs an error and returns
an inactive player.

**How it's fixed:** The follow target is checked first: the call logs an error and returns the inactive
player.

## 69. A corrupted legacy core-data file aborted user-data generation

**What was wrong:** When upgrading from an old BroAudio version, the user-data generator reads the legacy
core-data JSON. If that file was malformed, `TryParseCoreData` threw instead of returning false, and
generation stopped.

**How it's fixed:** A parse error logs a warning and returns false, so generation falls back to the default
output path.
