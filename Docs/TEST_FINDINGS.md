# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.

Fixed findings move to [FIXED_ISSUES.md](FIXED_ISSUES.md), and leave it once the fix ships on `main`. A
number in neither file is retired, not free: a new finding takes a number above every one ever used.
Tests and code comments cite findings by number, so sections are never renumbered or merged.

Number 68 is withdrawn: it described Shuffle not playing each clip once per cycle, which the maintainer
confirmed is not part of its contract.

The tests that pin finding N carry `[Category("Finding_N")]`, so `-testCategory Finding_14` selects
everything that pins #14, in either suite. `FindingCoverageTests` holds this file to the coverage rules in
[ADDING_A_TEST.md §6](ADDING_A_TEST.md#6-findings).

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
| 34 | Editor / Logging | Fifteen `Debug.Log*` calls under `Assets/BroAudio/Editor/` still carry no `[BroAudio]` prefix | Open, not pinned |
| 35 | MonoComponent / SoundSource | `Stop On Disable` silently does nothing when the object is disabled in the frame it was enabled | Open, characterized |
| 36 | MonoComponent / SoundVolume | `Only Apply Once` applies only the *first* settings entry, on the very first enable | Open, characterized |
| 37 | MonoComponent / SoundVolume | A setting typed `BroAudioType.All` writes the master volume, which `Reset On Disable` cannot restore | Open, characterized |
| 38 | MonoComponent / SpectrumAnalyzer | Whether the serialized `SoundSource` is polled is decided once, in `Start` | Open, characterized |
| 39 | MonoComponent / SpectrumAnalyzer | A band narrower than one FFT bin makes RMS/Average divide by zero, and the band runs away to one end of the scale | Open, characterized |
| 40 | MonoComponent / SpectrumAnalyzer | Every band draws a `Weighted` field in the inspector that the runtime never reads | Open, characterized |
| 41 | Playback / Stop | `Stop(onFinished)` on a recycled handle drops the callback silently | Open, characterized |
| 42 | Decorators / Dominator | `AsDominator()` after playback started cannot re-route the player, so it filters itself | Open, characterized |
| 43 | Decorators / Dominator | `QuietOthers` with a zero fade time is overwritten by `SwitchMainTrackMode`, so nothing ducks | Open, characterized |
| 44 | Decorators / Dominator | A looping dominator's incoming player takes a generic track at the first seam, so it ducks itself | Open, characterized |
| 45 | Playback / Handover | `TransferAddedEffectComponents` runs once per decorator plus once, so the added-effect list and Unity's refusal logs multiply at every seam | Open, characterized |
| 46 | Spatial / Recycling | `ResetSpatial` resets `rolloffMode` but never clears the `CustomRolloff` curve data underneath it | Open, characterized |
| 48 | Teardown | `BroAudio.SetEffect` is not `Manager?.`-gated like the other release verbs, so it throws once the manager is gone | Open, characterized |
| 49 | Teardown | Release verbs on an `IAudioPlayer` handle that outlived the manager throw instead of no-op'ing | Open, characterized |
| 50 | Teardown | `Fader.StopCoroutine`'s defensive no-op reaches the throwing `SoundManager.Instance` | Open, characterized |
| 51 | Volume / Master | A zero-fade `SetVolume` cannot cancel an in-flight master fade, so the old ramp keeps writing | Open, not pinned |
| 53 | Easing | `SetEase` discards `Mathf.Clamp01`'s return value, so out-of-range input reaches the curve and a ramp's last frame can land short of its target, or on NaN | Open, characterized |
| 54 | Easing | An `Ease` outside the enum returns 0 for the whole fade instead of falling back to a curve | Open, characterized |
| 55 | Pitch | A per-type pitch **replaces** the entity's authored pitch instead of scaling it | Open, characterized |
| 56 | Pitch | Master `SetPitch` writes every concrete type's pref, unlike master `SetVolume` | Open, characterized |
| 57 | Volume / Fade | A timed per-type `SetVolume` ramps live players but stores the target instantly, so a sound started mid-fade begins at the end value | Open, characterized |
| 58 | Looping / Stop | `Stop` with a fade on a looping sound goes silent at the current iteration's end, while the handle stays active for the rest of the fade | Open, characterized |
| 59 | Looping / Seamless | A `SeamlessLoop` whose `TransitionTime` outlasts the clip loops once per `TransitionTime`, not once per clip | Open, characterized |
| 60 | Playback / Validation | `Play(id, (Transform)null)` throws a raw `NullReferenceException` before any validation runs | Open, characterized |
| 61 | Editor / Clip editing | A failed `Trim` leaves a zeroed sample buffer behind that later edits apply to | Open, characterized |
| 62 | Editor / Rect math | `Scoping(Rect, Rect)` clamps a scope-local rect against the scope's global edge | Open, characterized |
| 65 | Playback / Pause | `UnPause` during a Pause fade-out leaves `IsStopping` set, so every later faded `Stop` is discarded | Open, characterized |
| 66 | Addressables | A key that fails to load throws out of `PlayControl` and strands the player active and silent | Open, characterized |
| 67 | Music / StopMode | `StopMode.Mute` has no path that unmutes, and a muted player is never recycled when its clip ends | Open, characterized |
| 69 | Editor / Core data | `TryParseCoreData` throws on malformed JSON instead of returning false | Open, characterized |
| 70 | Easing | `SetFadeInEase`/`SetFadeOutEase` have no effect on the clip's own authored FadeIn/FadeOut | Open, characterized |
| 71 | Effects | A timed non-dominator `SetEffect` resets the mixer parameter but leaves the type routed through the effect send | Open, characterized |
| 72 | Pitch / Handover | A `SetPitch` after the next loop player is pre-spawned does not reach it, so the loop reverts at the seam | Open, not pinned |

---

## 8. A freshly constructed LowPass/HighPass `Effect` reports as not default

**Where:** `Effect(EffectType)` constructor and `Effect.IsDefault`

The constructor seeds a filter's `Value` from `BroAdvice` (LowPass 300 Hz, HighPass 2000 Hz), but
`IsDefault()` compares against `Effect.Defaults`, the no-filtering ends of the range
(`AudioConstant.MaxFrequency` / `MinFrequency`). So `new Effect(EffectType.LowPass)` and `HighPass` report
not default. `Volume` lines up only because the `Value` setter converts linear `1f` to `0` dB.

`SoundManager.SetEffect` reads `IsDefault()` as "remove this effect". The factories take an explicit
frequency and `ResetLowPass`/`ResetHighPass` pass the neutral ones, so they resolve correctly; the trap is the
public constructor, where a default-constructed effect is not default.

Status: Open, characterized. Pinned by `AudioMathTests.IsDefault_LowPass_ParameterlessConstructor_IsNotDefault`
and `IsDefault_HighPass_ParameterlessConstructor_IsNotDefault`.

## 9. `ShuffleClipStrategy` can repeat the previous clip

**Where:** `ShuffleClipStrategy`

`MulticlipsPlayMode.Shuffle` is documented as "not repeating with the previous one", but `_lastUsed` is
refreshed only at pool exhaustion and in the fallback scan, never after an ordinary in-cycle hit, so two
consecutive `SelectClip` calls can return the same clip.

Status: Open, characterized. Pinned by
`ClipSelectionTests.SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent`, which
proves the gap exists rather than asserting the documented invariant.

## 10. `out index` disagrees with the returned clip in two strategies

**Where:** `VelocityClipStrategy.SelectClip`, `ShuffleClipStrategy.SelectClip`

- **Velocity:** the "above every threshold" fallthrough returns the last clip without assigning `index`,
  which stays `0`.
- **Shuffle:** the fallback scan keeps advancing `index` while probing, then returns the clip found at the
  earlier index.

Editor-only blast radius: the `PickNewClip(context, out index)` overload is consumed only by
`EntityReplayRequest` and `AudioEntityEditor`, so the inspector can highlight one clip row while
previewing another. Runtime playback uses the overloads that discard the index.

Status: Open, characterized. Pinned by
`ClipSelectionTests.SelectClip_WithValueAboveEveryThreshold_ReturnsLastClipButLeavesIndexStaleAtZero` and
`SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip`.

## 11. `OnBGMChanged` fires twice per BGM swap, once with `null`

**Where:** `MusicPlayer.Recycle`, `MusicPlayer.CurrentBGMPlayer`

Replacing one BGM with another raises `BroAudio.OnBGMChanged` twice in the same frame: first `null`, when
the outgoing player's `Recycle()` clears `CurrentBGMPlayer` through the raising setter, then the incoming
player. The signature (`Action<IAudioPlayer>`) and its doc give no hint of a null, so a subscriber that
reads `.ID` throws. Subscribers must null-check, and cannot count invocations to detect a swap.
`UpdateInstance` writes the backing field directly so loop/chain handovers do not raise the event; keep that.

Status: Open, characterized. Pinned by
`BGMChangedEventTests.OnBGMChanged_WhenANewBGMReplacesTheCurrentOne_ReportsTheNewPlayer`, which polls for the
incoming player's `SoundID` (an exact count is unobservable within one frame) and asserts a null was raised.

## 12. Comb-filtering prevention is bypassed whenever one play is global and the other positioned

**Where:** `DefaultPlaybackGroup.HasPassedCombFilteringRule`

Distance is compared only when both plays are positioned. When exactly one is global (position
`Utility.GloballyPlayedPosition`, i.e. `Vector3.negativeInfinity`), the pair is exempted whenever
`_ignoreIfDistanceIsGreaterThan > 0`, with no distance check. That threshold defaults to `0.1f`, so by
default a global and a positioned play of the same `SoundID` inside the comb-filtering window are never
rejected, however close. An in-source TODO suggests using the AudioListener's position.

Status: Open, characterized. Pinned by
`PlaybackGroupTests.Play_GlobalThenPositioned_WithinCombFilteringWindow_ExemptedRegardlessOfActualDistance`,
with a 10 s window so the window expiring cannot explain the acceptance. Controls:
`Play_GlobalThenPositioned_WithDistanceExemptionOff_RejectsSecond` and
`Play_PositionedCloseTogether_WithinCombFilteringWindow_RejectsSecond`.

## 13. `HasLoop` writes its out parameter even when it returns false

**Where:** `AudioEntity.HasLoop` (Chained branch)

For a Chained entity, `transitionTime` is set to the chained default before the return value is decided, so
with `DefaultChainedPlayModeLoop` at `LoopType.None` it returns `false` yet hands back the configured
transition time instead of `0`. Every current caller checks the return or discards the value; it is a trap
for the next one.

Status: Open, characterized. Pinned by
`ChainedLoopDefaultSettingTests.HasLoop_TwoArgOverload_TracksDefaultChainedPlayModeLoopSetting`.

## 14. The addressable unload setting does not control the unload delay

**Where:** `SoundManager.AddressableCleanupRoutine`, `SoundManager.UpdateLoadedEntityLastPlayedTime`

`AutomaticallyUnloadUnusedAddressableAudioClipsAfter` is used only as the routine's polling interval,
clamped to 1–5 s and cached on the first iteration (runtime changes are ignored). The staleness threshold
is a hardcoded `60.0` seconds.

Worse, nothing in production registers an entity with the routine: `UpdateLoadedEntityLastPlayedTime` only
updates keys already in `_loadedEntityLastPlayedTime`, and its call sites, all meant to register on load,
never add one. The dictionary stays empty and auto-unload never fires.

Status: Open, characterized. Both halves pinned by
`AddressablesTests.CleanupRoutine_WithTheUnloadDelaySetToFiveSeconds_StillMeasuresStalenessAgainstSixtySeconds`:
with a 5 s setting it asserts two preloaded entities are not registered, then back-dates one by 61 s (which
registers it) and one by 30 s, and asserts the first is released and the second kept. Each tick walks every
key without yielding, so the "kept" half cannot pass vacuously.

## 21. The `params float[] ratios` rect splits do not land on the far edge

**Where:** `EditorScriptingExtension.SplitRectHorizontal`/`SplitRectVertical` (`params float[] ratios`
overloads, via the shared `SplitHorizontal` helper)

Each segment loses a full `gap` at the first and last index and half a `gap` in between, which balances only
at 4 segments: 2 fall a full gap short of `xMax`/`yMax`, 3 fall half a gap short, 5+ overshoot. The two-way
`out Rect, out Rect` overload lands on the edge exactly, so the two forms disagree for the same inputs.

Status: Open, characterized. Pinned by `RectSplitRatioTests.SplitRectHorizontal_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule`,
`SplitRectHorizontal_RatiosArrayForm_TwoWay_FallsShortOfOriginXMax_UnlikeTheDedicatedOverload` and
`SplitRectVertical_RatiosArrayForm_ThreeWay_MatchesPerSegmentOffsetRule`.

## 22. `SplitRectVertical` silently no-ops on a null array

**Where:** `EditorScriptingExtension.SplitRectVertical(Rect, float, Rect[], params float[])`

`resultRects ??= new Rect[ratios.Length]` fills a local array the caller never sees: no exception, no log.
The horizontal form logs "Rects array is null!" and returns.

Status: Open, characterized. Pinned by
`RectSplitRatioTests.SplitRectVertical_RatiosArrayForm_NullArray_SilentlyNoOps_UnlikeHorizontal`.

## 23. `GetFieldName` lowercases every occurrence of the leading letter

**Where:** `EditorScriptingExtension.GetFieldName`

It lowercases the first letter with `string.Replace(char, char)`, which replaces every occurrence, so
`"FooF"` becomes `"_foof"`, not `"_fooF"`.

Status: Open, characterized. Pinned by
`EditorReflectionNamingTests.GetFieldName_ReplacesEveryOccurrenceOfTheLeadingChar_NotJustTheFirst`.

## 24. `BroEditorUtility.Combine` is naked concatenation

**Where:** `BroEditorUtility.Combine` (three-argument and `params string[]` overloads)

Both join segments with `"/"` and never normalize, so a segment ending in a slash yields `//`.

Status: Open, characterized. Pinned by
`EditorUtilityPureTests.Combine_ThreeArgForm_TrailingSlashOnInput_YieldsDoubleSlash` and
`Combine_ParamsForm_TrailingSlashOnInput_YieldsDoubleSlash`.

## 25. `ConvertToMono` Downmixing drops the final group

**Where:** `AudioClipEditingHelper.ConvertToMono` (Downmixing)

The running sum is flushed on entering a new group, so the last group is never emitted: output is
`totalSamples / channels - 1` frames. The `SelectOneChannel` mode keeps the full count.

Status: Open, characterized. Pinned by
`ClipEditingTests.ConvertToMono_Downmixing_AveragesEachFrameButDropsTheFinalFrame`, which also asserts each
emitted frame averages its own L/R pair.

## 26. `Reverse` transposes stereo channels

**Where:** `AudioClipEditingHelper.Reverse`

`Array.Reverse` on the interleaved sample array swaps L and R as well as reversing time.

Status: Open, characterized. Pinned by `ClipEditingTests.Reverse_Stereo_ReversesRawArraySoChannelsAreTransposed`.

## 27. `AddSlient` prepends, and its sample count truncates

**Where:** `AudioClipEditingHelper.AddSlient`

Silence goes at the start, which the name does not say. The pad length is a plain `(int)` cast of
`time * frequency * channels`, not the `Math.Round(..., AwayFromZero)` that `FadeIn`/`FadeOut` and
`GetDataSample` use, so a time just under an integer sample count loses one sample.

Status: Open, characterized. Pinned by `ClipEditingTests.AddSlient_PrependsSilenceAndShiftsOriginalDataToTail`
and `AddSlient_PadLengthTruncatesInsteadOfRounding`.

## 29. `GetResultClip` returns the original instance when nothing was edited

**Where:** `AudioClipEditingHelper.GetResultClip`

It returns the source clip itself, not a copy, so a caller that mutates the result mutates the user's clip.

Status: Open, characterized. Pinned by `ClipEditingTests.GetResultClip_NoEdit_ReturnsOriginalInstance`.

## 31. `CreateScriptableObjectIfNotExist` checks existence with `Resources.Load`, not the AssetDatabase

**Where:** `BroEditorUtility.CreateScriptableObjectIfNotExist` (via `TryLoadResources`)

Outside a Resources folder the existence check never finds the asset, so the call creates a fresh instance
and overwrites whatever is at the path. Latent: every production caller passes a Resources path.

Status: Open, characterized. Pinned by
`AssetWritingTests.CreateScriptableObjectIfNotExist_OutsideAResourcesFolder_CreatesANewInstanceEveryTime`.

## 32. A positive `Delay` alone makes `HasDifferentPosition` true

**Where:** `Transport.HasDifferentPosition`

It ORs in `Delay > StartPosition`, so an entity with untouched Start/End positions reports a different
position as soon as it has any delay. A delay shifts when playback begins, not where in the clip; nothing
downstream is known to misbehave, so whether this is intended is open.

Status: Open, characterized. Pinned by
`TransportHasDifferentPositionTests.HasDifferentPosition_DelayGreaterThanStart_IsTrue_EvenWithStartAndEndAtZero`.

## 34. The Editor assembly was never swept for the `[BroAudio]` log prefix

**Where:** `Debug.Log*` calls under `Assets/BroAudio/Editor/`

Fifteen calls in ten shipped files emit without `Utility.LogTitle`, so a consumer sees a console message with
nothing identifying BroAudio: `FieldUsageFinder` (5), `SoundIDUpgrader` (2; a third has a plain-text
`[BroAudio]`), and one each in `AudioSourcePreviewStrategy`, `EditorVolumeTransporter`,
`SpatialSettingsEditorWindow`, `AudioEntityEditor`, `ReorderableClips`, `ReadOnlyTextAreaAttributeDrawer`,
`EditorScriptingExtension` (the multi-float-field guard) and `ReflectionExtension`
(`CreateNewObjectWithReflection`). Excluded because they never ship: `Editor/DevTools/` and the two calls
inside `#if BroAudio_DevOnly` in `BroUserDataGenerator`. Commented-out calls are not counted.

Status: Open, characterized. Not pinned: a mechanical sweep with no behavior for a test to assert.

## 35. `SoundSource`'s Stop On Disable is skipped for a sound that is still queued

**Where:** `SoundSource.OnDisable`

The guard requires `CurrentPlayer.IsPlaying` (`AudioSource.isPlaying`), but `BroAudio.Play` only enqueues;
`SoundManager.LateUpdate` starts the voice. Disabling the object in the same frame it was enabled (a pooled
object spawned and despawned at once) skips the stop, and the queued sound then plays to completion with
nothing left to stop it. With a non-zero `Delay`, `OnEnable` goes through `SetDelay` →
`AudioSource.PlayScheduled`, which sets `isPlaying` immediately, so the delayed case stops correctly and the
undelayed one does not. Checking `IsActive` would cover both; `AudioPlayer.Stop` already handles a
not-yet-playing player.

Status: Open, characterized. Pinned by
`SoundSourceTests.OnDisable_InTheSameFrameAsOnEnable_LeavesTheQueuedVoicePlaying`.

## 36. `SoundVolume`'s Only Apply Once applies only the first settings entry

**Where:** `SoundVolume.OnEnable`

`_hasApplyOnce` is a single component-wide flag raised inside the loop it gates, so the first `Setting`
consumes the one permitted apply on the first enable and every later entry is skipped, including moving its
slider. Raising the flag after the loop would match the name.

Status: Open, characterized. Pinned by
`SoundVolumeTests.OnEnable_WithOnlyApplyOnceAndSeveralSettings_AppliesOnlyTheFirstEntry`, with
`OnEnable_WithSeveralSettings_AppliesEveryOneOfThem` as the control.

## 37. A `SoundVolume` setting typed `BroAudioType.All` writes a volume Reset On Disable cannot restore

**Where:** `SoundVolume.Setting.ApplyVolumeToSystem`, `RecordOrigin`, `ResetToOrigin`; `SoundManager.SetVolume`

The audio type is a flags field, so `All` is selectable. Apply goes through `SoundManager.SetVolume`, which
routes `All` to `SetMasterVolume` (the mixer's Master parameter). Record/reset goes through
`OriginVolumeRecorder`, which snapshots and restores the concrete types' pref volumes. So with Apply On
Enable and Reset On Disable both on, the master moves on enable and stays moved after disable. There is no
master read-back a recorder could use (the level lives only in the mixer parameter, or `WebGLMasterVolume`).

Status: Open, characterized. Pinned by
`SoundVolumeTests.OnEnable_WithAllAudioType_WritesTheMasterVolumeThatResetOnDisableCannotRestore`.

## 38. `SpectrumAnalyzer` decides whether it has a SoundSource once, in `Start`

**Where:** `SpectrumAnalyzer.Start`, `SpectrumAnalyzer.Update`

`_isUsingSoundSource` is snapshotted in `Start`, and `Update` polls `_soundSource` only when it is set. A
`_soundSource` assigned after `Start` (spawner, re-targeted pooled prefab, Play Mode inspector edit) is never
polled, and the meter stays flat with no error. `SetSource` still works.

Status: Open, characterized. Pinned by
`SpectrumAnalyzerTests.Update_TakesThePlayerFromItsSoundSource_ButOnlyIfItWasAssignedBeforeStart`.

## 39. A `SpectrumAnalyzer` band narrower than one FFT bin runs away off the decibel scale

**Where:** `SpectrumAnalyzer.GetFrequencyRangeIndex`, `SpectrumAnalyzer.UpdateSpectrum` (local `GetRMS`/`GetAverage`)

Two band frequencies that round to the same bin give `range.length == 0` (easy with many bands at a low
resolution scale), and RMS/Average divide by it. Which way the band breaks depends on the one bin it reads:

- **Zero bin:** `0/0` is NaN, which survives `ToDecibel` and fails every ballistics comparison towards
  falling (decay chosen, `Mathf.Sign(NaN)` is `-1`, the snap-to-target branch never runs), so the band sinks
  forever. `Amplitube` clamps at `MinVolume` and hides it; `DecibelVolume` sinks past -80 without bound.
- **Any energy:** `x/0` is `+Infinity`, clamped to `MaxDecibelVolume`, so the band attacks to +20 dB and stays
  pinned there, even on mixer residual noise. CI takes this branch.

`Metering.Peak` is unaffected. Two smaller bugs in the same code, neither with a pinning test: the metering
loops run `i <= range.end` and so read `length + 1` bins while dividing by `length` (non-Peak means inflated
by `(n + 1) / n`), and a band above Nyquist indexes `_spectrum` out of range.

Status: Open, characterized. Pinned by
`SpectrumAnalyzerTests.Update_WithABandNarrowerThanOneFftBin_LeavesTheFloorUnderRmsButHoldsUnderPeak`, which
asserts the runaway, not its direction.

## 40. `SpectrumAnalyzer`'s per-band Weighted field is inspector-only

**Where:** `SpectrumAnalyzer.Band._weighted`; `SpectrumAnalyzerEditor.OnAddElement`, `OnDrawBandElement`

The editor draws a Weighted field per band and seeds it to `1`, presenting it as a per-band gain, but nothing
reads `_weighted`: `UpdateSpectrum` uses only the metered amplitude and the ballistics.

Status: Open, characterized. Pinned by `SpectrumAnalyzerTests.Update_BandWeighting_HasNoEffectOnTheBandOutput`,
which drives a real 440 Hz tone, since on silence a multiplier would leave both bands equal anyway.

## 41. `Stop(onFinished)` on a recycled handle drops the callback, silently

**Where:** `AudioPlayerInstanceWrapper`'s `IAudioStoppable.Stop` overloads; `InstanceWrapper<T>.Instance`,
`Recycle`; `Empty.EmptyAudioPlayer`'s `IAudioStoppable.Stop` overloads

The overloads forward via `Instance?.Stop(...)`, and `Instance` is null once the player is recycled, so the
whole call is swallowed. Right for the parameterless overloads, but the `Action onFinished` overloads drop
their continuation: never stored, never invoked, and the `void` return cannot report it. The empty player a
rejected `Play` returns does the same. So `bgm.Stop(2f, () => SceneManager.LoadScene(...))` on a BGM that
already ended or was replaced never loads the scene; only the opt-in
`RuntimeSetting.LogAccessRecycledPlayerWarning` gives any sign.

On the live path, `StopControl` invokes `onFinished` after `EndPlaying()`, and the no-fade early-out before
it; a handler must not touch the handle either way. A fix could invoke `onFinished` immediately on an
already-recycled handle.

Status: Open, characterized. Pinned by
`PlaybackLifecycleTests.Stop_WithOnFinishedCallback_FiresAfterTheFadeButIsDroppedByARecycledHandle`.

## 42. `AsDominator()` after playback has started cannot re-route the player

**Where:** `AudioPlayer.SetupAudioTrack`, `AudioPlayer.IsDominator`

`SetupAudioTrack` is the only place `TrackType` becomes `Dominator`, and it runs once, when
`SoundManager.LateUpdate` drains the play queue. `AsDominator()` called later attaches the decorator but the
player stays on a generic track under `Main`, inside the group its own `QuietOthers`/`LowPassOthers`
affects, so it ducks and filters itself. Nothing warns. The `LowPassOthers_MovesDominatorLowPassParameter_*`
tests and their HighPass twin decorate late too, but assert only that the parameter moved, which holds on
either track.

Status: Open, characterized. Pinned by
`DominatorTrackRoutingTests.Play_ThenAsDominatorAfterPlaybackStarted_StaysOnAGenericTrack`, with
`Play_AsDominatorInTheSameFrame_RoutesToADominatorTrackAndDucksTheMainTrack` as the correct-routing control.

## 43. `QuietOthers` with a zero fade time is overwritten before it takes effect

**Where:** `EffectAutomationHelper.SetEffectTrackParameter`, `SwitchMainTrackMode`, `TweakTrackParameter`, `Tweak`

`SetEffectTrackParameter` starts `TweakTrackParameter` (writes the ducked level to `Main_Dominated`) and then
calls `SwitchMainTrackMode(true)`, which sets `Main` to `MinDecibelVolume` and `Main_Dominated` to 0 dB.
With a non-zero fade the tween yields first, so it lands last and ducks correctly. With fade 0 the tween
writes synchronously inside `StartCoroutine`, and `SwitchMainTrackMode` then overwrites it: two frames after
`QuietOthers(0.2f, 0f)`, `Main` is -80 dB and `Main_Dominated` 0 dB instead of the requested -13.98 dB, so
nothing is quieted. `DominatorPlayer`'s `.While(PlayerIsPlaying)` does not rewrite it.

Status: Open, characterized. Pinned by
`DominatorEffectParameterTests.QuietOthers_WithZeroFadeTime_MutesMainAndLeavesMainDominatedAtFullVolume`,
which also checks `Main` returns to full volume once the dominator stops.

## 44. A looping dominator loses its Dominator track at the first handover seam

**Where:** `AudioPlayer.PlayControl` (its `SetupAudioTrack` call), `ScheduleNextPlayback`, `BeginHandover`,
`ReceiveHandover`; `AudioPlayerInstanceWrapper.UpdateInstance` (decorator transfer)

The incoming loop player is requested and started one `ScheduledPlaybackWarmUpTime` before the seam, and
reaches `SetupAudioTrack` synchronously, but decorators move only at `BeginHandover`, at the seam. So it
reads `IsDominator == false` and takes a generic track. The decorator then transfers and the duck persists
(`DominatorPlayer.PlayerIsPlaying()` stays true), so from the first seam the dominator plays under `Main`
at the level it imposed on everything else, the same self-ducking as #42 with no caller error. The generic
track also picks up non-dominator `SetEffect` filters, which a real dominator skips.

A fix: carry the outgoing `TrackType` through `ReceiveHandover`/`PlaybackHandoverData`, or transfer
decorators at request time.

Status: Open, characterized. Pinned by
`DominatorTrackRoutingTests.Play_LoopingDominator_KeepsDuckingAcrossASeamButTheIncomingPlayerTakesAGenericTrack`,
which asserts the decorator and the duck survive and the track does not.

## 45. `TransferAddedEffectComponents` runs once per decorator, plus once, and the effect list multiplies at every seam

**Where:** `AudioPlayerInstanceWrapper.UpdateInstance`

`AudioPlayerDecorator` is an `AudioPlayerInstanceWrapper`, so each `decorator.UpdateInstance(newInstance)`
re-enters this override and calls `Instance.TransferAddedEffectComponents(newInstance)`, then the outer call
does it once more. The other transfers null their source; `_addedEffects` does not, so with N decorators the
list is copied N+1 times. Unity allows one filter of each type per GameObject, so only the first
`AddComponent` succeeds; each later one logs Unity's untagged "Can't add component" as `LogType.Log`, but
`SetAddedEffectComponents` still appends an entry. With two decorators and one added filter the list is 3
long after the first seam and 9 after the second, with 2 then 8 refusals logged, tripling per seam.

Reachable by any looping entity with an added filter and a decorator, e.g. a looping Music entity
(`AlwaysPlayMusicAsBGM` attaches `MusicPlayer`) whose spatial setting `HasLowPassFilter`.

Status: Open, characterized. Pinned by
`AudioEffectTests.Loop_WithAnAddedEffectAndTwoDecorators_MultipliesTheEffectListAtEachSeamWhileUnityKeepsOneFilter`:
one filter per incoming player, list lengths 3 and 9 (read by reflection), and 2 and 8 refusals, counted as
the largest group of identical untagged logs in each seam's window without reading their text.

## 46. `ResetSpatial` resets `rolloffMode` but leaves the `CustomRolloff` curve data behind

**Where:** `AudioPlayer.ResetSpatial` (called from `AudioPlayer.EndPlaying` on every playback end)

Every scalar spatial property is reset, and `rolloffMode` leaves `Custom`, but the `CustomRolloff` keyframes
are never cleared (`Utility.SetCustomCurveOrResetDefault` refuses that curve type). A pooled `AudioSource`
keeps a previous entity's rolloff curve for the rest of the run. Inert today, since `SetSpatial` selects
`Custom` only together with a fresh `SetCustomCurve`; any path that sets `Custom` without a curve would
inherit an unrelated sound's attenuation.

Status: Open, characterized. Pinned by
`SpatialAndPriorityTests.Recycle_AfterA3DSound_ResetsScalarSpatialStateButLeavesTheCustomRolloffCurveBehind`,
which asserts both the reset scalars and the leftover curve.

## 48. `BroAudio.SetEffect` is not `Manager?.`-gated like the other release verbs

**Where:** both `BroAudio.SetEffect` overloads

The release verbs on the facade (`Stop`, `Pause`, `UnPause`, `SetVolume`, `SetPitch`, the `Release*`
methods) go through the null-safe `BroAudio.Manager` and no-op during teardown. `SetEffect` uses the throwing
`SoundManager.Instance`, so it throws `BroAudioException` once the manager is gone. The code does not say
which group it belongs to, but it reads as release-side (the natural `OnDisable` call to clear a filter) and
returns a waitable, not a player.

Status: Open, characterized. Pinned by
`TeardownTests.SetEffect_OnBroAudioFacade_WithManagerDestroyed_ThrowsBroAudioException`; moving it onto
`Manager?.` turns that test red.

## 49. Release verbs on a player handle that outlived the manager throw instead of no-op'ing

**Where:** `AudioPlayerInstanceWrapper.LogInstanceIsNull`, reached from `InstanceWrapper<T>.Instance` /
`IsAvailable`

`Instance` calls `IsAvailable()` with `logWarning: true`, so accessing a wrapper whose player is gone calls
`LogInstanceIsNull()`, which reads `SoundManager.Instance.Setting` through the throwing accessor. Pooled
players are children of the manager's transform (`AudioPlayerObjectPool`), so destroying the manager
destroys them too, and a cached `IAudioPlayer` then throws `BroAudioException` from `Stop`, `Pause`,
`UnPause`, `SetVolume` and `SetPitch`, against the teardown contract. `IsActive`/`IsPlaying` use
`IsAvailable(false)` and stay safe. A fix: check `SoundManager.HasInstance` (or `BroAudio.Manager`) first.

Status: Open, characterized. Pinned by
`TeardownTests.StaleHandle_HeldAcrossManagerDestruction_ReleaseVerbsThrowInsteadOfSilentlyNoOp`, which also
asserts the safe `IsActive`/`IsPlaying` contrast.

## 50. `Fader.StopCoroutine`'s defensive no-op reaches the throwing accessor

**Where:** `Fader._coroutineExecutor`

The executor is `SoundManager.Instance`, so the teardown guard dereferences the throwing accessor (same root
cause as #49): with the manager destroyed, `Fader.Complete` throws `BroAudioException`. No production path
is known to reach it, because every `Fader` lives in an `AudioPlayer` destroyed with the manager.

Status: Open, characterized. Pinned by
`TeardownTests.Fader_CompleteWithManagerDestroyed_ThrowsBroAudioExceptionInsteadOfTheDefensiveNoOp`, which
builds a `Fader` directly with a recording `IAudioBus`.

## 51. A zero-fade `SetVolume` cannot cancel an in-flight master fade

**Where:** `SoundManager.SetMasterVolume`

Only the timed branch calls `RestartCoroutine`, which is what stops the previous ramp. A zero-fade
`SetVolume(vol, 0f)` writes Master once and leaves a running fade alive to overwrite it next frame. The
early return when the target equals the current value also skips cancellation. Both bite when paused: a
master fade started at `Time.timeScale == 0` under `AudioMixerUpdateMode.Normal` rewrites its start value
every frame, so a `SetVolume(thatValue, 0f)` early-returns and the fade resumes on unpause. The WebGL branch
has the same shape. A fix would stop `_masterVolumeCoroutine` on both the zero-fade branch and the early
return.

Status: Open, characterized. Not pinned: no test asserts the failed cancellation;
`BroAudioTestFixture.DrainMasterVolumeFade` drains any master fade in teardown so the suite does not depend
on it.

## 53. `SetEase` discards `Mathf.Clamp01`'s return value

**Where:** `EaseExtension.SetEase`

`Mathf.Clamp01(value);` is a bare statement, so `value` reaches the curve unclamped. Out-of-range input is
corrected, inverted, amplified or turned into NaN depending on the curve: `1.5f` under InQuad gives `2.25`,
`1.2f` under OutSine turns back down to `0.951`, InCirc past 1 is NaN, and `-1f` under InQuad is `1`.

The master-volume ramp, `AudioPlayer.PitchControl` and `EffectAutomationHelper.Tweak` evaluate the ease
after adding the frame's delta, so their last pass uses `t > 1` (`FaderModule` checks first and stays in
range). `Mathf.Lerp` clamps, so a rising curve is harmless, but a curve that turns down past 1 (OutSine, the
factory fade-out ease; OutQuad; InOutSine) ends short of the target, and InCirc writes NaN to the mixer
parameter. A later timed master fade lerps from that NaN and stays NaN; only a zero-fade `SetVolume` clears it. Pitch
always uses Linear and is unaffected. Absorbs the former #47.

Status: Open, characterized. Pinned by
`EaseCurveTests.SetEase_OutOfRangeInput_IsNotClamped_CharacterizesDiscardedClamp01`,
`EaseCurveTests.SetEase_InCircPastOne_IsNaN`, and
`VolumePitchMixerTests.SetVolume_MasterFadeWithInCircEase_LastFrameWritesNaNToTheMixer` (NaN reaches Master,
and a zero-fade `SetVolume` clears it). The short landing depends on frame length, so it is pinned only at
the function. Fix: `value = Mathf.Clamp01(value);`.

## 54. An undefined `Ease` silences the whole fade

**Where:** `EaseExtension.SetEase` (the `_ => 0` arm)

Any undefined `Ease` value returns progress `0` for every `t`, so a fade-in stays silent for its whole
duration then snaps to full. Reachable without a bad cast: `Ease` is serialized by ordinal into
`RuntimeSetting.DefaultFadeInEase` and friends, so a reordered or shrunk enum deserializes to an undefined
value, with no console error.

Status: Open, characterized. Pinned by `EaseCurveTests.SetEase_UndefinedEaseValue_FallsBackToZero`, with
`EaseCurveTests.EaseMember_KeepsItsSerializedOrdinal` failing if any member's ordinal moves.

## 55. A per-type pitch replaces the entity's authored pitch instead of scaling it

**Where:** `AudioPlayer.GetBasePitch`

When the per-type pitch is not the default, the entity's authored `Pitch` is ignored: after
`SetPitch(SFX, 0.5f)` an entity authored at 1.5 plays at 0.5, not 0.75, and its random range applies around
the per-type value. Volume layers multiply instead. Invisible at the default pitch, which new entities use.

Status: Open, characterized. Pinned by
`AuthoredPitchAndRandomizationTests.Play_WithAuthoredEntityPitch_ReachesAudioSourceAndIsReplacedNotScaledByTypePitch`,
which asserts 0.5 and explicitly not 0.75.

## 56. Master `SetPitch` and master `SetVolume` are asymmetric on `BroAudioType.All`

**Where:** `SoundManager.SetVolume` vs `SoundManager.SetPitch`

`SetVolume` with `All` writes the mixer's Master parameter. `SetPitch` with `All` has no such branch and
writes every concrete type's pref through `SetPlaybackPrefByType`, so a "master" pitch reaches every future
player through `GetBasePitch` (see #55) and can only be undone type by type.

Status: Open, characterized. Pinned by
`AuthoredPitchAndRandomizationTests.SetPitch_Master_StoresIntoEveryConcreteTypePrefAndReachesFuturePlayers`.

## 57. A timed per-type `SetVolume` snaps future players while live ones ramp

**Where:** `SoundManager.SetVolume(float, BroAudioType, float)`

`SetPlaybackPrefByType` stores the target volume immediately (no `fadeTime`), while live players of the
type get a `fadeTime` ramp. `PlayControl` applies the stored pref with `Complete`, so a sound started
mid-fade begins at the end value beside siblings still ramping, audible in the usual "duck a category while
sounds keep firing" case.

Status: Open, characterized. Pinned by `VolumeFadeTests.SetVolume_ByTypeWithFade_RampsLivePlayerOverDuration`,
which asserts in the frame of the call that the pref is already at target and the live player still at origin.

## 58. `Stop` with a fade on a looping sound goes silent at the current iteration's end

**Where:** `AudioPlayer.StopControl`

`StopControl` replaces `PlayControl`, cancels `ScheduleNextPlayback` and discards the pre-spawned
`_nextPlayer`, so nothing hands the loop over and the voice ends at the current iteration's clip end. The
volume ramp continues over silence and the handle stays `IsActive` until the fade completes; for a loop
shorter than the fade, the fade-out is heard as a cut. One-shots are unaffected.

Status: Open, characterized. Pinned by
`LoopHandoverTests.Stop_ByTypeWithFade_FadesOneShotsButALoopFallsSilentAtItsCurrentIterationEnd`.

## 59. A `SeamlessLoop` whose `TransitionTime` outlasts the clip loops once per `TransitionTime`

**Where:** `AudioPlayer.PlayControl`, `AudioPlayer.ScheduleNextPlayback`

`PlayControl` waits out its fade-in (now `TransitionTime` long via `ApplySeamlessFade`) before starting
`ScheduleNextPlayback`, which schedules the successor at the clip end minus the fade-out, clamped to now.
With `TransitionTime` longer than the clip, each player spawns its successor one `TransitionTime` after it
started, so the loop period becomes `TransitionTime`: a 0.5 s clip with a 1.5 s transition sounds for 0.5 s
then fades over silence for the rest of each period. Live players stay bounded, so this is timing, not a leak.

Status: Open, characterized. Pinned by
`LoopHandoverTests.SeamlessLoop_WithTransitionLongerThanTheClip_LoopsOncePerTransitionWithABoundedPlayerCount`:
over three transitions it asserts 3–6 player starts (a per-clip period would give about 9), at most 4 live
at once, and that the handle still drives the loop and `Stop` recycles every player.

## 60. `Play(id, (Transform)null)` throws a raw `NullReferenceException` before any validation

**Where:** `SoundManager.Play(SoundID, Transform, float, IPlayableValidator)`

`followTarget.position` is evaluated as an argument to `IsPlayable`, so every `Transform` overload throws a
raw `NullReferenceException` before the `SoundID`, entity or playback group is checked; even
`SoundID.Invalid` logs nothing. Every other invalid-input path logs and returns `Empty.AudioPlayer`, per the
project's log-and-return rule. Nothing is extracted from the pool first, so nothing leaks.

Status: Open, characterized. Pinned by
`ErrorPathTests.Play_WithANullFollowTarget_ThrowsNullReferenceExceptionBeforeAnyValidation`: the throw for
both overloads and for `SoundID.Invalid` with no log, no voice, and the contrast that `Play(SoundID.Invalid)`
without a target logs and returns an inactive player.

## 61. A failed `Trim` leaves a zeroed sample buffer that later edits apply to

**Where:** `AudioExtension.TryGetSampleData`, `AudioClipEditingHelper.Trim`

`TryGetSampleData` allocates its `out` array before `AudioClip.GetData` and returns it even when `GetData`
fails. `Trim` assigns it straight into `_sampleDatas`, so after a failed Trim (e.g. a streaming clip) the
helper holds zeros, `CanEdit` is true, and later edits run on silence and set `HasEdited`. `GetResultClip`
would then build silence; that last step is suspected, not observed, because the result clip cannot be read
back. Without the failed Trim the helper is not editable (`GetSampleData` returns null on failure).

Status: Open, characterized. Pinned by
`ClipEditingTests.Trim_OnStreamingClip_LeavesAZeroedBufferThatLaterEditsApplyTo` (`CanEdit` true,
`AdjustVolume` reports an edit, the buffer is ten zeros). Contrast:
`ClipEditingTests.StreamingClip_WithoutAFailedTrim_IsNotEditable`.

## 62. `Scoping(Rect, Rect)` clamps a scope-local rect against the scope's global edge

**Where:** `EditorScriptingExtension.Scoping(Rect, Rect, Vector2)`

It converts the rect to scope-local coordinates, then clamps against the global `scope.xMax`/`yMax`. Correct
only with the scope at the origin; real callers pass an `EditorWindow.position`. With scope (100, 100, 50, 50),
local (10, 10, 80, 90) is not clamped at all, and local (10, 10, 200, 200) becomes 140 × 140 instead of
40 × 40. `DeScope` uses the same clamp on global coordinates and is correct.

Status: Open, characterized. Pinned by `RectScopingTests.Scoping_OffOriginScope_ClampsLocalRectAgainstGlobalEdge`
and `Scoping_OffOriginScope_LocalRectPastTheGlobalEdge_IsClampedToTheGlobalEdge`. Contrast:
`RectScopingTests.DeScope_OffOriginScope_ClampsGlobalRectAgainstGlobalEdge`.

## 65. `UnPause` during a Pause fade-out leaves `IsStopping` set, so every later faded `Stop` is discarded

**Where:** `AudioPlayer.Stop`, `StopControl`, `IAudioStoppable.UnPause(float)`, `PlayInternal`

A faded `Pause` runs `StopControl`, which sets `IsStopping` and clears it only as its last step. `UnPause`
checks only `_stopMode == StopMode.Pause`, so mid-fade it calls `PlayInternal`, whose `RestartCoroutine`
kills `StopControl` before that step. The resume works, but `IsStopping` stays true until `EndPlaying`, and
`Stop` returns early for any non-immediate fade while `IsStopping`, so every faded `Stop` (including the
clip-setting default of `BroAudio.Stop(id)` and bare `Stop()`) is ignored; only a zero-fade `Stop` gets
through. A faded `Stop` interrupted by `UnPause` is unaffected: `UnPause` warns and returns
(`ErrorPathTests.UnPause_WhileAFadedStopIsInProgress_WarnsAndTheStopStillCompletes`). A fix would clear
`IsStopping` when `PlayInternal` replaces a running `StopControl`.

Status: Open, characterized. Pinned by
`ErrorPathTests.UnPause_DuringAPauseFadeOut_ResumesButLeavesIsStoppingSet_SoALaterFadedStopIsIgnored`
(`IsStopping` still set while playing; a later `Stop(0.5f)` leaves it at full volume).

## 66. An Addressables key that fails to load throws out of `PlayControl` and strands the player

**Where:** `BroAudioClip.GetAudioClip` (`BroAudioClip.Addressables.cs`); `AudioPlayer.PlayControl`,
`WaitForAddressablesToLoad`

For an `AssetReference` whose GUID no catalog resolves, `PlayControl` waits for the failed load and calls
`GetAudioClip()`, which throws rather than returning null: a `NullReferenceException` in the Editor
(`editorAsset` is null), a `BroAudioException` in a player (the synchronous retry fails). The throw is in a
coroutine step outside `PlayInternal`'s `try`/`catch`, so the coroutine dies, `EndPlaying` never runs, and
the player stays active, silent and out of the pool until stopped. `WaitForAddressablesToLoad`'s own
"Failed to load" error is unreachable. Conflicts with the log-and-return rule.

Status: Open, characterized. Pinned by
`AddressablesTests.Play_WithAKeyThatCannotLoad_ThrowsOutOfPlayControlAndStrandsThePlayerActiveAndSilent`
(behind `PACKAGE_ADDRESSABLES`): the "not preloaded" error (the only BroAudio-tagged log with the factory
`AutomaticallyLoadAddressableAudioClips` off), an `Exception` log, and the player active and never playing.

## 67. `StopMode.Mute` has no path that unmutes, and a muted player is never recycled

**Where:** `StopMode.Mute`; `AudioPlayer.Stop`, `StopControl`, `StartPlaying`, `IAudioStoppable.UnPause(float)`;
`MusicPlayer.StopCurrentPlayer`

`StopMode.Mute` is documented to keep playing in the background until played again, and `StartPlaying` has
a case for that, but it is reachable only as a BGM transition stop mode and:

- **Nothing re-plays the muted instance.** `BroAudio.Play(id)` always extracts a new player, and `UnPause`
  accepts only `StopMode.Pause`, so it warns. Playing again starts a second, full-volume player beside the
  silent one.
- **A muted player leaks until stopped.** `StopControl` replaces the `PlayControl` that would have ended the
  player at its clip end, so when the clip runs out `EndPlaying` never runs and the player keeps its pool
  slot (`IsActive`, not playing). A BGM routine that mutes at every change accumulates them.

Status: Open, characterized. Pinned by
`BGMEdgeCaseTests.StopModeMute_PlayingTheSameSoundAgainStartsANewPlayerAndLeavesTheMutedOneRunningSilently`
and `BGMEdgeCaseTests.StopModeMute_TheMutedPlayerIsNeverRecycledWhenItsClipEnds_OnlyAnExplicitStopFreesIt`.

## 69. `TryParseCoreData` throws on malformed JSON instead of returning false

**Where:** `BroEditorUtility.TryParseCoreData`; caller `BroUserDataGenerator.GetInitialData`

Only a null or empty text asset is guarded; malformed JSON makes `JsonUtility.FromJson` throw
`ArgumentException` out of the `Try*` method. `GetInitialData` migrates a legacy core-data file during
user-data generation, so a corrupted legacy file aborts generation instead of falling back to the default
output path.

Status: Open, characterized. Pinned by
`CoreDataAndUpdaterTests.TryParseCoreData_WithMalformedText_ThrowsInsteadOfReturningFalse`.

## 70. `SetFadeInEase`/`SetFadeOutEase` have no effect on the clip's own authored fades

**Where:** `PlaybackPreference.TryGetFadeIn`, `TryGetFadeOut`, `TryGetOrConsumeOverride`;
`FadeData.SetEase`, `FadeData.TryGetOrConsumeOverride`

The setters write the ease into the player's `FadeData`, but `TryGetOrConsumeOverride` starts from
`RuntimeSetting`'s default ease and uses the `FadeData` ease only when there is a pending one-shot override
(`Play(id, fadeIn)`, `Stop(fadeOut)`) or a base fade (set only by `SeamlessLoop`). A clip's authored
FadeIn/FadeOut, including the fade-out before a natural end, therefore always uses the global default curve.

Status: Open, characterized. Pinned by
`FadeAndTrimTests.SetFadeInEase_AndSetFadeOutEase_DoNotShapeTheClipsOwnAuthoredFades`, which samples authored
3 s fades a third of the way in and finds the factory curves (InCubic in, OutSine out), not the requested
OutCubic/InCubic. Contrast: `FadeAndTrimTests.SetFadeInEase_AndSetFadeOutEase_ShapeExplicitFades`.

## 71. A timed non-dominator `SetEffect` leaves the type routed through the effect send after it resets

**Where:** `SoundManager.SetEffect(BroAudioType, Effect)`, `SoundManager.SetPlayerEffect`;
`EffectAutomationHelper.TweakTrackParameter`

For a non-default, non-dominator effect, `SetEffect` calls `SetPlayerEffect(..., Add)` inline, which sets the
type's pref effect bit and routes live players through `<Track>_Effect`, then passes a null `onReset`; only
the `Remove` path wires one. When a `ForSeconds`/`Until`/`While` waitable ends, the mixer parameter is reset
but the routing is not: the type's bit stays set, and current and future players of the type stay on the
effect send until a default-valued `SetEffect`. The test fixture clears this (`ResetTrackEffects`) and
`VerifyGlobalStateRestored` checks it. A fix would pass the `Remove` callback for a timed `Add` too.

Status: Open, characterized. Pinned by
`AudioEffectTests.SetEffect_LowPass_ForSeconds_ResetsTheParameterButLeavesTheTypeRoutedThroughTheEffectSend`
(behind `!UNITY_WEBGL`): after `Effect_LowPass` resets, the SFX LowPass bit is set, the live player is on the
send, and a new player is routed through `<Track>_Effect` with its dry track muted; `Effect.ResetLowPass()`
then clears all three.

## 72. A `SetPitch` after the next loop player is pre-spawned does not reach it

**Where:** `AudioPlayer.ScheduleNextPlayback`; `AudioPlayer.RecalculateScheduledEndTime`,
`ShiftScheduledTimes`; `IAudioPlayer.SetPitch` (`AudioPlayer.Pitch.cs`)

A looping player requests its successor `ScheduledPlaybackWarmUpTime` before the seam with its `TargetPitch`
baked into `PlaybackHandoverData.Pitch`. A handle `SetPitch` in that window re-pitches the playing instance
and shifts the successor's schedule (`ShiftScheduledTimes`) but not its pitch, so after the seam the loop
continues, permanently, at the old pitch. The window is `AudioConstant.MixerWarmUpTime` (0.1 s) or the output
latency if longer. Per-type `SoundManager.SetPitch` walks every active player and is unaffected. A fix would
forward the pitch to `_nextPlayer` as the schedule shift already is.

Status: Open, characterized from the code. Not pinned: the window is too narrow to land a `SetPitch` in
reliably, and widening it would mean overwriting `ScheduledPlaybackWarmUpTime` on the live `SoundManager` by
reflection, which the maintainer chose not to do.
