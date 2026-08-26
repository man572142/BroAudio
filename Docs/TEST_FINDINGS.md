# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.

Findings 1-7 have since been **fixed** (or the dead code removed) — see each section for what
changed. The rest are still open, and the suite characterizes their current behavior as-is.

| # | Area | Finding | Status |
|---|---|---|---|
| 1 | `BroAudioClip` + Addressables | `IsAddressablesAvailable()` NREs on a code-constructed clip | **Fixed** — field initialized inline + null-guarded |
| 2 | Pitch | `PitchShiftingSetting.AudioMixer` is a silent no-op — the mixer write is commented out | **Fixed** — enum removed, pitch is always `AudioSource.pitch` |
| 3 | Clip selection | `LayeredClipStrategy` is unreachable dead code | **Fixed** — file removed |
| 4 | Seamless loop | `SeamlessLoopHelper.cs` is entirely commented out | **Fixed** — file removed |
| 5 | Clip selection | `SetSequenceId` lacks the wrong-mode guard that `SetVelocity` has | **Fixed** — guard added |
| 6 | Clip selection | `SingleClipStrategy` accepts an unset clip; `Sequence`/`Shuffle` check `IsSet` | **Fixed** — `IsSet` check added |
| 7 | Volume | Per-type volume of exactly `1f` is skipped for future plays but pushed to live players | **Fixed** — the skip is gone |
| 8 | Effects | A freshly constructed LowPass/HighPass `Effect` reports as *not* default | Open, characterized |
| 9 | Clip selection | `ShuffleClipStrategy` **can** repeat the previous clip, contradicting its documented contract | Open, characterized |
| 10 | Clip selection | `out index` disagrees with the returned clip in `Velocity` and `Shuffle` | Open, characterized |
| 11 | Music | `OnBGMChanged` fires twice per swap, once with a `null` argument | Open, characterized |
| 12 | Playback group | Comb-filtering is bypassed for any global+positioned pair, without comparing distance | Open, characterized |
| 13 | Looping | `HasLoop` populates its `transitionTime` out parameter even when it returns false | Open, characterized |
| 14 | Addressables | `AutomaticallyUnloadUnusedAddressableAudioClipsAfter` does not control the unload delay | Open, characterized |

---

## 1. `BroAudioClip.IsAddressablesAvailable()` threw on a code-constructed clip — **fixed**

**Where:** `Assets/BroAudio/Runtime/DataStruct/BroAudioClip.Addressables.cs`

With `com.unity.addressables` installed, `new BroAudioClip()` left the `AudioClipAssetReference`
field `null` (C# `new` does not run Unity's serializer, which would materialize the
`[SerializeField]` reference). The first `IsValid()` / `IsSet` / `IsAddressablesAvailable()` call
then threw `NullReferenceException` — inside `AudioPlayer.PlayControl`, where it surfaced as a
swallowed coroutine exception rather than a clear error. Every other member of that partial
(`IsLoaded`, `IsLoading`, `LoadAssetAsync`, `ReleaseAsset`, `GetCurrentOperationHandle`)
dereferenced the same field unguarded.

Assets authored in the Editor were unaffected, so it never showed up in normal use — which is
exactly what made it easy to ship.

**Fix:** the field is initialized inline, so a code-constructed clip matches what the serializer
produces and every member below it is null-safe; `IsAddressablesAvailable()` is null-guarded as well.

```csharp
[SerializeField] private AssetReferenceT<AudioClip> AudioClipAssetReference = new AssetReferenceT<AudioClip>(string.Empty);
...
public bool IsAddressablesAvailable() => AudioClipAssetReference != null && !string.IsNullOrEmpty(AudioClipAssetReference.AssetGUID);
```

The test workaround (`TestAudioLibrary.FillNullAssetReference`, which filled the field by
reflection) has been removed along with it.

## 2. `PitchShiftingSetting.AudioMixer` silently did nothing — **fixed by removing the enum**

**Where:** `Assets/BroAudio/Runtime/Player/AudioPlayer.Pitch.cs`

The `AudioMixer` case of the pitch-shifting switch had its only effective statement commented out,
so the branch fell through to a bare `break`. Selecting `PitchShiftingSetting.AudioMixer` made
`SetPitch` a no-op: `TargetPitch` was recorded but never applied anywhere, and `AudioSource.pitch`
was written only in the `AudioSource` case. The Preferences toggle that exposed the choice was
already commented out too.

**Fix:** pitch is now always applied through `AudioSource.pitch`. `PitchShiftingSetting`,
`RuntimeSetting.PitchSetting`, `FactorySettings.PitchShifting` and `SoundManager.PitchSetting` are
gone; the switches in `AudioPlayer.SetPitch` and the entity inspector's pitch drawer are flattened
to the AudioSource path.

`AudioConstant.MaxMixerPitch` (10f) is left in place — it is public API and harmless as a constant,
but nothing references it any more.

## 3. `LayeredClipStrategy` was unreachable — **fixed by removing the file**

The class compiled and implemented `IClipSelectionStrategy`, but had zero references anywhere under
`Assets/` and `MulticlipsPlayMode` has no `Layered` member, so `AudioEntity.PickNewClip`'s switch
could never construct it. Never tested — testing unreachable code buys nothing.

## 4. `SeamlessLoopHelper.cs` was entirely commented out — **fixed by removing the file**

Dead file. The real seamless-loop mechanism lives in `AudioPlayer.Playback.cs` /
`AudioPlayer.Scheduling.cs`.

## 5. `SetSequenceId` lacked the wrong-mode guard that `SetVelocity` has — **fixed**

**Where:** `Assets/BroAudio/Runtime/Player/PlaybackPreference.cs`

`SetVelocity` logs and no-ops when called on an entity whose play mode is not `Velocity`.
`SetSequenceId` had the identical "irrelevant outside my mode" shape but no guard and no log.

**Fix:** `PlaybackPreference.SetSequenceId` now mirrors `SetVelocity`, and `SequenceId`'s setter is
private so the guard cannot be bypassed:

```csharp
public void SetSequenceId(string sequenceId)
{
    if (Entity.PlayMode != MulticlipsPlayMode.Sequence)
    {
        Debug.LogError($"Cannot set sequence id on [{Entity}] because it's not using SequencePlayMode. (current : {Entity.PlayMode})");
        return;
    }
    SequenceId = sequenceId;
}
```

## 6. `SingleClipStrategy` accepted an unset clip — **fixed**

`SingleClipStrategy` treated a reference-null `clips[0]` as an error but returned a
non-null-but-unset `BroAudioClip` (`IsSet == false`) as if it were valid, deferring the failure to
whatever tried to play it. `SequenceClipStrategy` and `ShuffleClipStrategy` check `.IsSet` directly;
Single was the odd one out.

**Fix:** Single now rejects an unset clip too, logging in the same shape as `Sequence`.

## 7. Per-type volume: live players and future plays disagreed at exactly `1f` — **fixed**

`AudioPlayer.PlayControl` skipped applying the stored `_audioTypePref[type].Volume` to a
freshly-started player when that pref was `Mathf.Approximately(pref.Volume, DefaultTrackVolume)` —
i.e. exactly `1f`. `SoundManager.SetVolume(BroAudioType, ...)` had no such guard and pushed the value
to every currently active matching player regardless. Harmless in effect (a fresh fader already sits
at `1f`), but it meant "read the type pref and expect it to mirror what a live player has" was not a
safe assumption.

**Fix:** the `Mathf.Approximately` guard is gone; only the `IsFading` guard remains, so the pref and
live players agree at every value.

```csharp
if (!_audioTypeVolume.IsFading)
{
    _audioTypeVolume.Complete(audioTypePref.Volume, false);
}
```

## 8. A freshly constructed LowPass/HighPass `Effect` reports as not default

**Where:** `Assets/BroAudio/Runtime/DataStruct/Effect.cs`

`Effect` has two different ideas of what a filter's "default" frequency is, and they disagree.

**Side one — the constructor** seeds `Value` from `BroAdvice`, whose frequencies are *recommended
starting points for an audible filter*:

```csharp
public Effect(EffectType type) : this()
{
    Type = type;

    Value = type switch
    {
        EffectType.Volume => AudioConstant.FullVolume,
        EffectType.LowPass => BroAdvice.LowPassFrequency,   // 300f
        EffectType.HighPass => BroAdvice.HighPassFrequency, // 2000f
        _ => default,
    };
}
```

**Side two — `IsDefault()`** compares against `Effect.Defaults`, which is wired to `AudioConstant`'s
*neutral, no-filtering* ends of the frequency range:

```csharp
public static class Defaults
{
    public const float Volume = AudioConstant.FullVolume;
    public const float LowPass = AudioConstant.MaxFrequency;  // 22000f — a low-pass that filters nothing
    public const float HighPass = AudioConstant.MinFrequency; //    10f — a high-pass that filters nothing
}

public bool IsDefault() => Type switch
{
    EffectType.Volume => Value == AudioConstant.FullDecibelVolume,
    EffectType.LowPass => Value == Defaults.LowPass,
    EffectType.HighPass => Value == Defaults.HighPass,
    _ => false,
};
```

So a just-constructed filter effect is not "default" by its own test:

| expression | `Value` after construction | `IsDefault()` compares to | result |
|---|---|---|---|
| `new Effect(EffectType.LowPass)` | `300f` | `22000f` | `false` |
| `new Effect(EffectType.HighPass)` | `2000f` | `10f` | `false` |
| `new Effect(EffectType.Volume)` | `0f` (dB) | `0f` (`FullDecibelVolume`) | `true` |

`EffectType.Volume` is the one that lines up, and only because the `Value` setter converts it:
`AudioConstant.FullVolume` (linear `1f`) goes through `value.ToDecibel()` and lands on `0f`, which is
exactly `AudioConstant.FullDecibelVolume`. Note that `Defaults.Volume` is the *linear* `1f` and is
never consulted by `IsDefault()` — only `Defaults.LowPass` / `Defaults.HighPass` are.

**Why it matters:** `SoundManager.SetEffect` uses `IsDefault()` to decide that a call means "remove
this effect" rather than "add it":

```csharp
SetEffectMode mode = SetEffectMode.Add;
if (effect.Type == EffectType.None)
{
    mode = SetEffectMode.Override;
}
else if (effect.IsDefault())
{
    mode = SetEffectMode.Remove;
}
```

The reachable path is fine: the public factories are explicit, and `Effect.ResetLowPass()` /
`Effect.ResetHighPass()` pass `AudioConstant.MaxFrequency` / `MinFrequency`, so they do read as
default and correctly resolve to `Remove`. The trap is the `Effect(EffectType)` constructor, which is
`public`: `SetEffect(new Effect(EffectType.LowPass))` reads as "add a 300 Hz low-pass", which is
defensible, but "a default-constructed effect is default" is not true here and anyone reasoning from
the type name will get it wrong.

Covered by `AudioMathTests`, which asserts the current behavior.

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

## 11. `OnBGMChanged` fires twice per BGM swap, once with `null`

**Where:** `Assets/BroAudio/Runtime/Player/MusicPlayer.cs`

Replacing one BGM with another raises `BroAudio.OnBGMChanged` **twice, both within the same frame**:

1. `null` — the outgoing player's `Recycle()` runs `if (CurrentBGMPlayer == Instance) CurrentBGMPlayer = null;`,
   and the property setter raises the event for that transition too.
2. the incoming player — `DoTransition` then assigns `CurrentBGMPlayer = Instance`.

The event's signature is `Action<IAudioPlayer>` with no nullability hint, and the XML doc describes it simply as
firing when the BGM changes. A subscriber that does the obvious thing — read `.ID`, or store the argument and
use it — throws on the null pass. Nothing in the API surface signals that a null is coming.

Two consequences for anyone writing against this:

- Always null-check the argument.
- Do not count invocations to detect a swap. Both raises land in one frame, so a per-frame poll for
  "exactly N events" can never observe the intermediate value. `SchedulingAndMusicTests` polls for the arrival
  of a player with the expected `SoundID` instead, and that is the pattern to copy.

`UpdateInstance` deliberately writes the backing field rather than the property when a loop or chain hands over
to a new player, precisely so this event does *not* fire for handovers — the logical BGM has not changed there.
That guard is worth preserving; the null raise above is the case it does not cover.

## 12. Comb-filtering prevention is bypassed whenever one play is global and the other positioned

**Where:** `Assets/BroAudio/Runtime/Player/PlaybackGroup/DefaultPlaybackGroup.cs`, `HasPassedCombFilteringRule`

The rule compares distance only when *both* plays are positioned:

```csharp
if (!currentIsGlobal && !previousIsGlobal)
{
    var sqrDistance = (currentPlayPos - previousPlayer.PlayingPosition).sqrMagnitude;
    if (sqrDistance > Mathf.Pow(_ignoreIfDistanceIsGreaterThan, 2)) return true;
}

// Only one is played globally
// TODO: use the AudioListener's position as the global position?
if ((currentIsGlobal != previousIsGlobal) && _ignoreIfDistanceIsGreaterThan > 0f)
{
    return true;
}
```

The second branch computes no distance at all. It exempts the pair whenever the distance threshold is merely
*enabled*. A globally-played sound has position `Utility.GloballyPlayedPosition`, which is
`Vector3.negativeInfinity` — there is no meaningful distance to compare, so the code opts out rather than
choosing a position.

`_ignoreIfDistanceIsGreaterThan` defaults to `0.1f`, i.e. enabled. So **by default, playing the same `SoundID`
globally and positionally within the comb-filtering window never triggers prevention**, no matter how close
together the two plays are — which is precisely the situation the rule exists to catch.

The in-source `TODO` shows this is known and unresolved rather than intended; the AudioListener's position is
the obvious candidate for the missing global position. Characterized by `PlaybackGroupTests`.

## 13. `HasLoop` writes its out parameter even when it returns false

**Where:** `Assets/BroAudio/Runtime/DataStruct/Core/AudioEntity.cs`, `HasLoop`

```csharp
else if (MulticlipsPlayMode == MulticlipsPlayMode.Chained)
{
    loopType = chainedDefaultLoop;
    transitionTime = chainedDefaultTransitionTime;
}
return loopType != LoopType.None;
```

For a Chained entity, `transitionTime` is assigned *before* the return value is decided. If
`DefaultChainedPlayModeLoop` is `LoopType.None`, the method returns `false` — correctly reporting no loop —
but still hands back the configured transition time rather than `0`.

The usual C# contract for a `bool TryX(out ...)` shape is that the out parameter is meaningless on `false`.
Callers here happen to respect that (`SoundManager.Playback.cs` discards both with `out _, out _`), so nothing
is broken today. It is a trap for the next caller that reads the out value without checking the return first.

Characterized by `SelectionStateAndDecoratorTests`.

## 14. The addressable unload setting does not control the unload delay

**Where:** `Assets/BroAudio/Runtime/SoundManager/SoundManager.cs`, `AddressableCleanupRoutine`

```csharp
_addressableCleanupInterval = new WaitForSecondsRealtime(
    Mathf.Clamp(Setting.AutomaticallyUnloadUnusedAddressableAudioClipsAfter, 1f, 5f));
...
if (currentTime - lastPlayedTime > 60.0)
{
    UnloadAddressableEntity(id);
}
```

The setting named *"Automatically Unload Unused Addressable Audio Clips After"* is used only as the routine's
**polling interval**, clamped to 1-5 seconds. The actual staleness threshold — how long a clip must go unused
before it is released — is the hardcoded literal `60.0`.

So the setting does not do what its name says. Raising it from 60 to 300 does not keep clips loaded longer; it
does nothing at all beyond 5, because of the clamp. Lowering it to 10 does not unload sooner; it just polls
more often. The unload delay is always 60 seconds and is not configurable.

Two smaller consequences:

- `_addressableCleanupInterval` is built once on the first routine iteration and cached, so changing the setting
  at runtime has no effect even on the polling interval.
- The routine is testable only by back-dating `_loadedEntityLastPlayedTime`, which is what
  `AddressablesTests` does — waiting out a hardcoded 60 seconds is not viable in a suite.

---

## Environmental note, not a product defect

The PlayMode test scene contains no `AudioListener`, so Unity logs *"There are no audio listeners in the
scene"* during runs. It is harmless, but it means `LogAssert.NoUnexpectedReceived()` cannot be used in this
suite — it catches that message and fails the test. Assert specific expected logs instead, or assert behavior.