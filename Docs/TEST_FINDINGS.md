# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.

Findings 1-7, 15-20, 28, 30 and 33 have since been fixed and moved to
[FIXED_ISSUES.md](FIXED_ISSUES.md).
| # | Area | Finding | Status |
|---|---|---|---|
| 8 | Effects | A freshly constructed LowPass/HighPass `Effect` reports as *not* default | Open, characterized |
| 9 | Clip selection | `ShuffleClipStrategy` **can** repeat the previous clip, contradicting its documented contract | Open, characterized |
| 10 | Clip selection | `out index` disagrees with the returned clip in `Velocity` and `Shuffle` | Open, characterized |
| 11 | Music | `OnBGMChanged` fires twice per swap, once with a `null` argument | Open, characterized |
| 12 | Playback group | Comb-filtering is bypassed for any global+positioned pair, without comparing distance | Open, characterized |
| 13 | Looping | `HasLoop` populates its `transitionTime` out parameter even when it returns false | Open, characterized |
| 14 | Addressables | `AutomaticallyUnloadUnusedAddressableAudioClipsAfter` does not control the unload delay | Open, characterized |
| 21 | Editor / Rect math | The `params float[] ratios` rect splits do not land on the far edge | Open, characterized |
| 22 | Editor / Rect math | `SplitRectVertical` silently no-ops on a null array | Open, characterized |
| 23 | Editor / Rect math | `GetFieldName` lowercases every occurrence of the leading letter | Open, characterized |
| 24 | Editor / Path utility | `BroEditorUtility.Combine` is naked concatenation | Open, characterized |
| 25 | Editor / Clip editing | `ConvertToMono` Downmixing drops the final group | Open, characterized |
| 26 | Editor / Clip editing | `Reverse` transposes stereo channels | Open, characterized |
| 27 | Editor / Clip editing | `AddSlient` prepends, and its sample count truncates | Open, characterized |
| 29 | Editor / Clip editing | `GetResultClip` returns the original instance when nothing was edited | Open, characterized |
| 31 | Editor / Asset writing | `CreateScriptableObjectIfNotExist` checks existence with `Resources.Load`, not the AssetDatabase | Open, characterized |
| 32 | Editor / Transport | A positive `Delay` alone makes `HasDifferentPosition` true, with Start and End both at 0 | Open, characterized |
| 34 | Editor / Logging | Fifteen `Debug.Log*` calls under `Assets/BroAudio/Editor/` still carry no `[BroAudio]` prefix | Open, characterized |
| 35 | MonoComponent / SoundSource | `Stop On Disable` silently does nothing when the object is disabled in the frame it was enabled | Open, characterized |

---

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

## 21. The `params float[] ratios` rect splits do not land on the far edge

**Where:** `Assets/BroAudio/Editor/Extension/EditorScriptingExtension.cs`,
`SplitRectHorizontal`/`SplitRectVertical` (the `params float[] ratios` overloads, backed by the shared
`SplitHorizontal` helper)

```csharp
float offsetWidth = i == 0 || i == resultRects.Length - 1 ? gap : gap * 0.5f;
```

Each segment loses a full `gap` at the array's first and last index and only half a `gap` in between,
which only balances exactly at 4 segments — 2 segments fall a full gap short of `xMax`/`yMax`, 3 fall
half a gap short, and 5 or more overshoot. The dedicated two-way `out Rect, out Rect` overload above it
in the same file applies a clean `halfGap` to both sides and lands on the edge exactly, for any gap — so
the two forms contradict each other for the same inputs.

Characterized in `TransportAndRectMathTests` (`SplitRectHorizontal_RatiosArrayForm_ThreeWay_...`,
`SplitRectHorizontal_RatiosArrayForm_TwoWay_FallsShortOfOriginXMax_UnlikeTheDedicatedOverload`,
`SplitRectVertical_RatiosArrayForm_ThreeWay_...`).

## 22. `SplitRectVertical` silently no-ops on a null array

**Where:** `Assets/BroAudio/Editor/Extension/EditorScriptingExtension.cs`, `SplitRectVertical(Rect,
float, Rect[], params float[])`

```csharp
resultRects ??= new Rect[ratios.Length];
```

It reassigns `resultRects` into a throwaway local array the caller never sees — no exception, no log.
`SplitRectHorizontal`'s equivalent overload guards the same case in its shared `SplitHorizontal` helper
by logging `"Rects array is null!"` and returning. Characterized in
`TransportAndRectMathTests.SplitRectVertical_RatiosArrayForm_NullArray_SilentlyNoOps_UnlikeHorizontal`.

## 23. `GetFieldName` lowercases every occurrence of the leading letter

**Where:** `Assets/BroAudio/Editor/Extension/EditorScriptingExtension.cs`, `GetFieldName`

```csharp
if (char.IsUpper(propertyName[0]))
{
    propertyName = propertyName.Replace(propertyName[0], propertyName[0].ToLower());
}
return $"_{propertyName}";
```

It calls `propertyName.Replace(firstChar, lowerFirstChar)` — the global `string.Replace(char, char)`
overload, not a single-position substitution — so `"FooF"` becomes `"_foof"` rather than `"_fooF"`.
Characterized in `TransportAndRectMathTests.GetFieldName_ReplacesEveryOccurrenceOfTheLeadingChar_NotJustTheFirst`.

## 24. `BroEditorUtility.Combine` is naked concatenation

**Where:** `Assets/BroAudio/Editor/Utility/BroEditorUtility/BroEditorUtility.Path.cs`

```csharp
public static string Combine(string path1, string path2, string path3)
{
    return path1 + "/" + path2 + "/" + path3;
}
```

It joins with `+ "/" +` and never normalizes, so a segment that already ends in a slash produces a
doubled `//`. The `params string[]` overload builds the same way and has the identical quirk.
Characterized in `EditorUtilityPureTests.Combine_ThreeArgForm_TrailingSlashOnInput_YieldsDoubleSlash`
and `Combine_ParamsForm_TrailingSlashOnInput_YieldsDoubleSlash`.

---

## 25. `ConvertToMono` Downmixing drops the final group

**Where:** `Assets/BroAudio/Editor/Extension/AudioClipEditingHelper.cs`

The running sum is flushed when the loop *enters* a new group, so the last group is accumulated and
never added — output length is `totalSamples / channels - 1`, not `totalSamples / channels`. A user
downmixing a stereo clip in the Clip Editor loses the final sample frame.

Status: Open, characterized. Covered by
`ClipEditingTests.ConvertToMono_Downmixing_OffsetsGroupingAndDropsFinalGroup`. The `SelectOneChannel`
path does **not** have this bug — it keeps the full frame count.

## 26. `Reverse` transposes stereo channels

**Where:** `Assets/BroAudio/Editor/Extension/AudioClipEditingHelper.cs`

It calls `Array.Reverse` on the raw interleaved sample array, so L and R swap as well as the clip
playing backwards.

Status: Open, characterized.

## 27. `AddSlient` prepends, and its sample count truncates

**Where:** `Assets/BroAudio/Editor/Extension/AudioClipEditingHelper.cs`

The name says nothing about which end — silence goes at the *start*. It also sizes the pad with a
plain `(int)` cast of `time * frequency * channels` rather than the `Math.Round(..., AwayFromZero)`
that `FadeIn`/`FadeOut` and `GetDataSample` use, so a time value that lands just under an integer
silently loses one sample.

Status: Open, characterized.

## 29. `GetResultClip` returns the original instance when nothing was edited

**Where:** `Assets/BroAudio/Editor/Extension/AudioClipEditingHelper.cs`

Not a copy — reference equality. A caller that mutates the "result" is mutating the user's source
clip.

Status: Open, characterized.

## 31. `CreateScriptableObjectIfNotExist` checks existence with `Resources.Load`, not the AssetDatabase

**Where:** `Assets/BroAudio/Editor/Utility/BroEditorUtility/BroEditorUtility.DataHandler.cs` (via
`TryLoadResources`)

Outside a Resources folder the guard never fires, so the call creates a fresh instance and overwrites
whatever was at the path. Every production caller passes a Resources path, so it is latent.

Status: Open, characterized. Covered by
`AssetWritingTests.CreateScriptableObjectIfNotExist_OutsideAResourcesFolder_CreatesANewInstanceEveryTime`.


## 32. A positive `Delay` alone makes `HasDifferentPosition` true

**Where:** `Assets/BroAudio/Editor/Transport/Transport.cs`

`HasDifferentPosition` ORs in a `Delay > StartPosition` term alongside the start/end comparisons, so an
entity whose playback positions are untouched still reports a "different position" as soon as it carries
any delay at all — `0 > 0` is false, but any positive `Delay` clears that bar.

Whether that is intended is the open question: a delay shifts *when* playback begins, not *where* in the
clip it starts, so counting it as a position difference is at least surprising. Nothing downstream appears
to misbehave because of it today, which is why it is characterized rather than fixed.

Status: Open, characterized. Pinned by
`TransportAndRectMathTests.HasDifferentPosition_DelayGreaterThanStart_IsTrue_EvenWithStartAndEndAtZero`.

---

## Environmental note, not a product defect

The PlayMode test scene contains no `AudioListener`, so Unity logs *"There are no audio listeners in the
scene"* during runs. It is harmless, but it means `LogAssert.NoUnexpectedReceived()` cannot be used in this
suite — it catches that message and fails the test. Assert specific expected logs instead, or assert behavior.

---

## 34. The Editor assembly was never swept for the `[BroAudio]` log prefix

Findings #15 and #33 each fixed a handful of unprefixed logs, but neither was a sweep of the Editor
assembly as a whole. Fifteen `Debug.Log*` calls under `Assets/BroAudio/Editor/` still emit without
`Utility.LogTitle`, so a package consumer who trips one sees a bare console message with nothing
identifying BroAudio as the source:

| File | Count |
|---|---|
| `Utility/FieldUsageFinder.cs` | 5 |
| `Utility/SoundIDUpgrader.cs` | 2 (a third already carries a plain-text `[BroAudio]`) |
| `Utility/BroUserDataGenerator.cs` | 2 |
| `AudioPreview/AudioSourcePreviewStrategy.cs` | 1 |
| `AudioPreview/EditorVolumeTransporter.cs` | 1 |
| `EditorWindow/SpatialSettingsEditorWindow.cs` | 1 |
| `EntityPropertyDrawer/AudioEntityEditor.cs` | 1 |
| `EntityPropertyDrawer/ReorderableClips.cs` | 1 |
| `Extension/AttributeDrawer/ReadOnlyTextAreaAttributeDrawer.cs` | 1 |
| `Extension/EditorScriptingExtension.cs` | 1 (the multi-float-field guard, not covered by #33) |

`Editor/DevTools/` is excluded — it is gated behind `BroAudio_DevOnly` and never ships.

This is recorded rather than fixed because it is a mechanical sweep across ten files with no test
pinning any of them, which is a different-shaped change from the three logs #33 fixed (those had to
move, because tests asserted their exact text).

Status: Open, characterized. Not pinned by a test.

---

## 35. `SoundSource`'s Stop On Disable is skipped for a sound that is still queued

**Where:** `Assets/BroAudio/Runtime/MonoComponent/SoundSource.cs:55-61`

```csharp
protected virtual void OnDisable()
{
    if(_stopOnDisable && CurrentPlayer != null && CurrentPlayer.IsPlaying)
    {
        CurrentPlayer.Stop(_overrideFadeOut);
    }
}
```

The guard is `IsPlaying`, which bottoms out at `AudioSource.isPlaying`. But `BroAudio.Play` does not start a
voice - it enqueues into `SoundManager._playbackQueue`, and `SoundManager.LateUpdate` drains it. So in the
window between `OnEnable`'s `Play()` and that frame's `LateUpdate`, a `SoundSource` holds a player that is
`IsActive` but not yet `IsPlaying`, and the `OnDisable` guard rejects it.

Disable the GameObject inside that window - `SetActive(true)` then `SetActive(false)` in the same frame, the
normal shape of a pooled object that is spawned and immediately despawned - and Stop On Disable does nothing
at all. The queued play still starts on the next `LateUpdate` and runs to completion, now with no
`SoundSource` in a position to stop it: the component's `OnDisable` has already been and gone.

The asymmetry gets sharper with `Delay` set. A non-zero Delay makes `OnEnable` call
`CurrentPlayer.SetDelay(...)`, which routes into `SetScheduledStartTime` -> `PlayInternal()` -> the
`PlayControl` coroutine, whose body runs synchronously as far as `AudioSource.PlayScheduled`. `isPlaying`
reads `true` from the moment of a `PlayScheduled` call, so *the delayed configuration stops correctly in the
same frame and the undelayed one does not* - the opposite of what a user would predict.

`IsActive` (true from the moment `Play` enqueues) is the state that actually means "this SoundSource owns a
player", and `AudioPlayer.Stop` already handles a not-yet-playing player: it falls into
`if (!ID.IsValid() || !isPlaying) { onFinished?.Invoke(); EndPlaying(); return; }`. Not changed here, per
the plan's characterize-don't-fix rule.

Status: Open, characterized. Pinned by
`SoundSourceTests.OnDisable_InTheSameFrameAsOnEnable_LeavesTheQueuedVoicePlaying`.