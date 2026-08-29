# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.

Findings 1-7 have since been fixed (or the dead code removed) and their write-ups removed from
this file — they are recorded in plain language in [FIXED_ISSUES.md](FIXED_ISSUES.md), which keeps
the same numbering, so citations elsewhere (e.g. `TEST_FINDINGS #3`) still resolve. The rest are
still open, and numbering is left as-is.

| # | Area | Finding | Status |
|---|---|---|---|
| 8 | Effects | A freshly constructed LowPass/HighPass `Effect` reports as *not* default | Open, characterized |
| 9 | Clip selection | `ShuffleClipStrategy` **can** repeat the previous clip, contradicting its documented contract | Open, characterized |
| 10 | Clip selection | `out index` disagrees with the returned clip in `Velocity` and `Shuffle` | Open, characterized |
| 11 | Music | `OnBGMChanged` fires twice per swap, once with a `null` argument | Open, characterized |
| 12 | Playback group | Comb-filtering is bypassed for any global+positioned pair, without comparing distance | Open, characterized |
| 13 | Looping | `HasLoop` populates its `transitionTime` out parameter even when it returns false | Open, characterized |
| 14 | Addressables | `AutomaticallyUnloadUnusedAddressableAudioClipsAfter` does not control the unload delay | Open, characterized |
| 15 | Logging | Five runtime logs in the `Ami.Extension` namespace carry no `Utility.LogTitle` prefix | Open, characterized |
| 16 | Effects | `ResetAllEffect` can report completion once per tracked effect instead of once | Open, latent |
| 17 | Editor / Instructions | `Instruction.SoundSource_PositionMode` (450) has no shipped text | Open, characterized |
| 18 | Editor / Instructions | `BroInstruction.asset` key `15` is stale, belongs to no enum value | Open, characterized |
| 19 | Editor / Audio type | `GetSerializedEnumIndex` and `GetAudioTypeByIndex` disagree for the composite `All` | Open, latent |
| 20 | Editor / Rect math | The `params float[] ratios` rect splits do not land on the far edge | Open, characterized |
| 21 | Editor / Rect math | `SplitRectVertical` silently no-ops on a null array | Open, characterized |
| 22 | Editor / Rect math | `GetFieldName` lowercases every occurrence of the leading letter | Open, characterized |
| 23 | Editor / Path utility | `BroEditorUtility.Combine` is naked concatenation | Open, characterized |

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

## 15. Runtime logs in `Ami.Extension` carry no `[BroAudio]` prefix

**Where:** `Assets/BroAudio/Runtime/Extension/` — `AudioExtension.cs:72`, `AudioExtension.cs:122`,
`FlagsExtension.cs:40`, `LoopExtension.cs:35`, `LoopExtension.cs:43`

The project rule is that every runtime log is prefixed with `Utility.LogTitle` (the `[BroAudio]`
rich-text tag) so console output is attributable to the package. Commit `78ffd841` did a pass over
the runtime for exactly this. What it left untouched is not scattered — it is precisely the five
`Debug.LogError` calls that live in the generic `Ami.Extension` namespace:

```csharp
// AudioExtension.cs:122
public static bool IsValidFrequency(float freq)
{
    if (freq < MinFrequency || freq > MaxFrequency)
    {
        Debug.LogError($"The given frequency should be in {MinFrequency}Hz ~ {MaxFrequency}Hz.");
        return false;
    }
    return true;
}
```

The grouping is almost certainly deliberate: `Ami.Extension` is the package's general-purpose
extension namespace, and prefixing from there would make it depend on `Ami.BroAudio.Utility`. Every
log in the `Ami.BroAudio.*` namespaces is prefixed, including ones that build their message in a
helper (`SequenceClipStrategy.GetNoValidClipMessage`) or a `const` (`SoundManager`'s `nullRefLog`).
`Debug.LogException` calls are excluded throughout, since an exception carries no message string to
prefix.

**Why it matters:** the boundary is undocumented, and at least one of these logs is reachable from
public API, so it surfaces to package consumers as an unattributed console error:

```csharp
player.AsDominator().LowPassOthers(0f);   // logs a bare "The given frequency should be in 10Hz ~ 22000Hz."
```

`IsValidFrequency` is the guard behind `LowPassOthers` / `HighPassOthers`, and it logs *before*
returning false, so the caller's own range check never gets a chance to report it. A user sees an
error with no indication of which package raised it.

This also makes the two guards on the same decorator inconsistent in both level and format:

| call | level | prefixed |
|---|---|---|
| `LowPassOthers(freq)` — invalid frequency | `Error` | no |
| `QuietOthers(vol)` — out-of-range volume | `Warning` | yes |

Either outcome is defensible — prefix them and accept the namespace dependency, or route the message
back through the caller — but the current state means a package consumer cannot tell where half of
these errors come from.

Characterized by `SelectionStateAndDecoratorTests`, which asserts the current `Error`-without-prefix
behavior for the frequency guard and the `Warning`-with-prefix behavior for the volume guard.

## 16. `ResetAllEffect` can report completion once per tracked effect instead of once

**Where:** `Assets/BroAudio/Runtime/SoundManager/EffectAutomationHelper.cs` — `ResetAllEffect`

The reset loop counts its outstanding tweens, but increments the counter *after* starting each one:

```csharp
tweaker.Coroutine = StartCoroutine(Tweak(current, GetEffectDefaultValue(effectType), effect.Fading.FadeOut, ...,  onTweakFinished));
tweaker.WaitableList.Clear();
tweakingCount++;
```

`StartCoroutine` runs the body up to its first `yield`, and `Tweak` has two paths that reach the end
without yielding at all: `from == to` hits an early `yield break`, and a zero `fadeTime` — the default
for `new Effect(EffectType.None)` — makes `while (currentTime < fadeTime)` skip its body. On either
path `onTweakFinished` fires *before* the matching `tweakingCount++`, so the counter goes to `-1`,
satisfies `if (tweakingCount <= 0)`, and fires `onResetFinished`. The `++` then brings it back to `0`
and the next tracked effect repeats the whole thing — N tracked effects means N completion callbacks
instead of one.

**Why it is latent, not a live bug:** `onResetFinished` is currently always `null` on this path.
`ResetAllEffect` runs only when `effect.Type == EffectType.None`, and `SoundManager.SetEffect` maps
that to `SetEffectMode.Override`, which leaves `onResetEffect` unassigned — it only builds a callback
for `SetEffectMode.Remove`. So the extra invocations hit a `?.` and vanish. Anything that later gives
the `None` path a callback inherits the bug.

Same root cause as #17's crash: this class assumes `StartCoroutine` defers the body, and Unity does
not. Moving `tweakingCount++` above the `StartCoroutine` call fixes it.

## 17. Instruction 450 has no shipped text

**Where:** `Assets/BroAudio/Editor/Resources/BroInstruction.asset`, keyed against
`Ami.BroAudio.Editor.Instruction`

`Instruction.SoundSource_PositionMode` (value 450) has no entry in the shipped asset, so
`BroInstructionHelper.GetText` returns its `MissingText` sentinel — the literal `??????????` — and the
Sound Source position-mode tooltip ships broken. The enum has 68 members and the asset has 68 entries,
so the counts cancel and hide the gap.

**This is a user-visible defect**, not a quirk. Covered by
`ShippedDataTests.EveryInstructionEnumValue_ResolvesToNonMissingText`, which is deliberately red.

## 18. Asset key 15 is stale

**Where:** `Assets/BroAudio/Editor/Resources/BroInstruction.asset`, key `15`

A pitch-shifting tooltip whose `Instruction` member was deleted; `Instruction.cs` pins the surrounding
values in a comment because of the hole. It deserializes to an undefined `(Instruction)15` and is never
read — harmless, but it is dead shipped data.

Covered by `ShippedDataTests.BroInstructionAsset_EveryKeyIsADefinedEnumValue`, deliberately red.

## 19. `GetSerializedEnumIndex` and `GetAudioTypeByIndex` disagree for the composite `All`

**Where:** `Assets/BroAudio/Editor/Utility/BroEditorUtility/BroEditorUtility.SerializedProperty.cs`

```csharp
public static int GetSerializedEnumIndex(this BroAudioType audioType)
{
    int index = 0;
    int intAudioType = (int)audioType;
    while (intAudioType > 0)
    {
        index++;
        intAudioType = intAudioType >> 1;
    }
    return index;
}

public static BroAudioType GetAudioTypeByIndex(int enumIndex)
{
    BroAudioType audioType = BroAudioType.None;
    while (enumIndex > 0)
    {
        audioType = audioType.ToNext();
        enumIndex--;
    }
    return audioType;
}
```

`GetSerializedEnumIndex` counts the bit-length of the underlying int, so `All` (31) yields index 5 — the
same index `VoiceOver` (16) yields — while reaching `All` from the other direction takes 6 `ToNext()`
steps. The round-trip therefore collapses `All` into `VoiceOver`. Concrete types are unaffected, and
**the pair has no caller left anywhere in the package** (verified by grep), so it is latent rather than
user-visible.

Characterized green by `EditorUtilityPureTests.EnumIndexRoundTrip_All_CollapsesIntoVoiceOver`.

## 20. The `params float[] ratios` rect splits do not land on the far edge

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

## 21. `SplitRectVertical` silently no-ops on a null array

**Where:** `Assets/BroAudio/Editor/Extension/EditorScriptingExtension.cs`, `SplitRectVertical(Rect,
float, Rect[], params float[])`

```csharp
resultRects ??= new Rect[ratios.Length];
```

It reassigns `resultRects` into a throwaway local array the caller never sees — no exception, no log.
`SplitRectHorizontal`'s equivalent overload guards the same case in its shared `SplitHorizontal` helper
by logging `"Rects array is null!"` and returning. Characterized in
`TransportAndRectMathTests.SplitRectVertical_RatiosArrayForm_NullArray_SilentlyNoOps_UnlikeHorizontal`.

## 22. `GetFieldName` lowercases every occurrence of the leading letter

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

## 23. `BroEditorUtility.Combine` is naked concatenation

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

## Environmental note, not a product defect

The PlayMode test scene contains no `AudioListener`, so Unity logs *"There are no audio listeners in the
scene"* during runs. It is harmless, but it means `LogAssert.NoUnexpectedReceived()` cannot be used in this
suite — it catches that message and fails the test. Assert specific expected logs instead, or assert behavior.
