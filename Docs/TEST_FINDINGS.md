# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.
**Nothing here has been fixed** — the suite characterizes current behavior as-is.

| # | Area | Finding | Status |
|---|---|---|---|
| 1 | `BroAudioClip` + Addressables | `IsAddressablesAvailable()` NREs on a code-constructed clip | Open, worked around in tests |
| 2 | Pitch | `PitchShiftingSetting.AudioMixer` is a silent no-op — the mixer write is commented out | Open, to be characterized as-is |
| 3 | Clip selection | `LayeredClipStrategy` is unreachable dead code | Open, out of scope |
| 4 | Seamless loop | `SeamlessLoopHelper.cs` is entirely commented out | Open, out of scope |
| 5 | Clip selection | `SetSequenceId` lacks the wrong-mode guard that `SetVelocity` has | Open |
| 6 | Clip selection | `SingleClipStrategy` accepts an unset clip; `Sequence`/`Shuffle` check `IsSet` | Open |
| 7 | Volume | Per-type volume of exactly `1f` is skipped for future plays but pushed to live players | Open |
| 8 | Effects | A freshly constructed LowPass/HighPass `Effect` reports as *not* default | Open, characterized |
| 9 | Clip selection | `ShuffleClipStrategy` **can** repeat the previous clip, contradicting its documented contract | Open, characterized |
| 10 | Clip selection | `out index` disagrees with the returned clip in `Velocity` and `Shuffle` | Open, characterized |

---

## 1. `BroAudioClip.IsAddressablesAvailable()` throws on a code-constructed clip

**Where:** `Assets/BroAudio/Runtime/DataStruct/BroAudioClip.Addressables.cs:92`

```csharp
public bool IsAddressablesAvailable() => !string.IsNullOrEmpty(AudioClipAssetReference.AssetGUID);
```

**What happens:** with `com.unity.addressables` installed, `new BroAudioClip()` leaves the
`AudioClipAssetReference` field `null` (C# `new` does not run Unity's serializer, which would
materialize the `[SerializeField]` reference). The first `IsValid()` / `IsSet` / `IsAddressablesAvailable()`
call then throws `NullReferenceException` — inside `AudioPlayer.PlayControl`, where it surfaces as a
swallowed coroutine exception rather than a clear error.

Every other member of that partial (`IsLoaded`, `IsLoading`, `LoadAssetAsync`, `ReleaseAsset`,
`GetCurrentOperationHandle`) dereferences the same field unguarded.

**Why it matters beyond tests:** any runtime path that builds a `BroAudioClip` in code hits this.
Assets authored in the Editor are unaffected, so it will not show up in normal use — which is
exactly what makes it easy to ship.

**Suggested (not applied):** null-guard the field, e.g. `AudioClipAssetReference != null && !string.IsNullOrEmpty(...)`,
or initialize it inline at the field declaration.

**Test workaround:** `TestAudioLibrary.FillNullAssetReference` assigns an empty
`AssetReferenceT<AudioClip>` via reflection, reproducing what Unity's serializer produces.
Remove the workaround if the production code is ever guarded.

## 2. `PitchShiftingSetting.AudioMixer` silently does nothing

**Where:** `Assets/BroAudio/Runtime/Player/AudioPlayer.Pitch.cs`

The `AudioMixer` case of the pitch-shifting switch has its only effective statement commented out, so the
branch falls through to a bare `break`. Selecting `PitchShiftingSetting.AudioMixer` in Preferences makes
`SetPitch` a no-op: `TargetPitch` is recorded but never applied anywhere, and `AudioSource.pitch` is written
only in the `AudioSource` case. This contradicts the enum's apparent contract (a real choice between two
pitch-shifting mechanisms) and the XML doc on `BroAudio.SetPitch`.

**Suggested (not applied):** either restore the mixer write or remove the enum member.

## 3. `LayeredClipStrategy` is unreachable

**Where:** `Assets/BroAudio/Runtime/Utility/ClipSelection/LayeredClipStrategy.cs`

The class compiles and implements `IClipSelectionStrategy`, but has zero references anywhere under `Assets/`
and `MulticlipsPlayMode` has no `Layered` member, so `AudioEntity.PickNewClip`'s switch can never construct it.
Either work-in-progress or forgotten. Not tested — testing unreachable code buys nothing.

## 4. `SeamlessLoopHelper.cs` is entirely commented out

**Where:** `Assets/BroAudio/Runtime/SoundManager/SeamlessLoopHelper.cs`

Dead file. The real seamless-loop mechanism lives in `AudioPlayer.Playback.cs` / `AudioPlayer.Scheduling.cs`.
Recorded so nobody goes looking for the logic there.

## 5. `SetSequenceId` lacks the wrong-mode guard that `SetVelocity` has

`SetVelocity` logs and no-ops when called on an entity whose play mode is not `Velocity`. `SetSequenceId` has
the identical "irrelevant outside my mode" shape but no guard and no log. Looks like an oversight rather than
a deliberate asymmetry.

## 6. `SingleClipStrategy` accepts an unset clip

`SingleClipStrategy` treats a reference-null `clips[0]` as an error but returns a non-null-but-unset
`BroAudioClip` (`IsSet == false`) as if it were valid, deferring the failure to whatever tries to play it.
`SequenceClipStrategy` and `ShuffleClipStrategy` check `.IsSet` directly. Single mode is the odd one out.

## 7. Per-type volume: live players and future plays disagree at exactly `1f`

`AudioPlayer.PlayControl` skips applying the stored `_audioTypePref[type].Volume` to a freshly-started player
when that pref is `Mathf.Approximately(pref.Volume, DefaultTrackVolume)` — i.e. exactly `1f`.
`SoundManager.SetVolume(BroAudioType, ...)` has no such guard and pushes the value to every currently active
matching player regardless. Harmless in effect today (a fresh fader already sits at `1f`), but it means
"read the type pref and expect it to mirror what a live player has" is not a safe assumption.

## 8. A freshly constructed LowPass/HighPass `Effect` reports as not default

**Where:** `Assets/BroAudio/Runtime/DataStruct/Effect.cs`

The parameterless `Effect(EffectType.LowPass)` / `Effect(EffectType.HighPass)` constructors seed `Value` from
`BroAdvice.LowPassFrequency` / `BroAdvice.HighPassFrequency` (300 Hz / 2000 Hz — the *recommended* values),
but `IsDefault()` compares against `AudioConstant.MaxFrequency` / `MinFrequency` (22000 Hz / 10 Hz — the
*neutral, no-filtering* values). So a just-constructed filter effect is not "default" by its own test.
`EffectType.Volume` has no such mismatch.

This matters because `SoundManager.SetEffect` uses `effect.IsDefault()` to decide whether the call means
"remove this effect" (`SetEffectMode.Remove`). Covered by `AudioMathTests`, asserting the current behavior.

## 9. `ShuffleClipStrategy` can repeat the previous clip

**Where:** `Assets/BroAudio/Runtime/Utility/ClipSelection/ShuffleClipStrategy.cs`

`MulticlipsPlayMode.Shuffle` is documented as "Same as random but not repeating with the previous one".
In practice `_lastUsed` is only refreshed at pool exhaustion and during the fallback scan — never after an
ordinary in-cycle hit — so two consecutive `SelectClip` calls can return the same clip.

`ClipSelectionTests` proves the gap exists rather than asserting the documented (and false) invariant.

## 10. `out index` disagrees with the returned clip in two strategies

**Where:** `VelocityClipStrategy.cs` and `ShuffleClipStrategy.cs`

- **Velocity:** the "above every threshold" fallthrough does `return clips[clips.Length - 1]` without ever
  reassigning `index`, which stays at its initial `0`.
- **Shuffle:** the fallback scan keeps advancing `index` while probing for an unused clip, then returns
  `result` — the clip found at the *earlier* index. `clips[index]` is then not the returned clip.

Both are the same defect class: the returned clip and its reported index describe different array slots.

**Blast radius is Editor-only.** The `PickNewClip(context, out index)` overload is consumed only by
`Editor/AudioPreview/EntityReplayRequest.cs` and `Editor/EntityPropertyDrawer/AudioEntityEditor.cs`; runtime
playback goes through the overloads that discard the index. The visible symptom is the inspector highlighting
one clip row while previewing another.

Both are characterized by tests that fail loudly if the mismatch is ever fixed.
