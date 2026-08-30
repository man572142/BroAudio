# Play / Stop / Pause lifecycle

Source walked: `Runtime/BroAudio.cs`, `Runtime/SoundManager/SoundManager.Playback.cs`,
`Runtime/Player/AudioPlayer.cs` + `.Playback.cs` + `.Recycling.cs`,
`Runtime/Player/AudioPlayerInstanceWrapper.cs`, `Runtime/Player/EmptyInstance.cs`,
`Runtime/Interface/AudioPlayer/*`, `Runtime/Extension/Tools/{InstanceWrapper,ObjectPool}.cs`,
`Runtime/Player/ObjectPool/AudioPlayerObjectPool.cs`, `Runtime/Player/PlaybackGroup/*`.

All timing claims below defer to the three facts in the task brief: Play only enqueues
(`SoundManager.LateUpdate` drains `_playbackQueue` and calls `IPlayable.Play()`), the first
Play Mode frame can't touch the mixer, and fades/handovers/loops run on `AudioSettings.dspTime`
while everything else runs on frames.

## Play — global / positioned / follow-target

| | |
|---|---|
| **Behavior** | `BroAudio.Play(id)`, `Play(id, position)`, and `Play(id, transform)` each hand back a live player handle that starts audible playback on the entity's clip, at 2D / a fixed 3D point / a continuously-tracked transform respectively. |
| **Observable** | `IAudioPlayer.IsActive` true synchronously on return; after `WaitFrames(1)`+ poll, `IsPlaying` true and `player.AudioSource.clip` equals the entity's clip. For position/follow overloads, `AudioSource` `spatialBlend`/`transform.position` (via the underlying `AudioPlayer.transform`, not exposed on the interface — see "Could not determine statically") reflect 3D placement. |
| **Edge cases** | Invalid/unregistered `SoundID`; entity with no clips (`_pref.PickNewClip()` returns null); a custom `IPlayableValidator` that rejects the play; `followTarget` destroyed mid-playback. |
| **Timing class** | Frame (enqueue is immediate; audible start is next `LateUpdate`, i.e. next frame minimum). |
| **Regression risk** | High — this is the primary API surface; the queue/`LateUpdate` split is exactly the kind of thing an "optimization" could silently break (e.g. inlining Play). |

## Play returns Empty.AudioPlayer when the sound is not playable

| | |
|---|---|
| **Behavior** | When `SoundManager.IsPlayable` fails (unknown `SoundID`, or a validator/rule rejects it — e.g. `DefaultPlaybackGroup`'s max-instance or comb-filtering rule, or a caller-supplied `IPlayableValidator`), `Play` returns the shared `Empty.AudioPlayer` singleton instead of a real handle, and no sound plays. |
| **Observable** | Public API only: returned handle's `IsActive`/`IsPlaying` are both `false`; all `Stop`/`Pause`/`SetVolume`/etc. calls on it are silent no-ops (verified by absence of exceptions and by no audible/mixer side effect). Passing a stub `IPlayableValidator` whose `IsPlayable` returns `false` is the reliable way to force this path without depending on a real `DefaultPlaybackGroup` asset. |
| **Edge cases** | Invalid `SoundID` (entity lookup fails) vs. a valid entity rejected by a rule — both return the same `Empty.AudioPlayer`, so a test can't distinguish "not found" from "rejected" through the return value alone. |
| **Timing class** | Immediate (rejection happens synchronously inside `Play`, before anything is queued). |
| **Regression risk** | Medium — silent-failure path; a regression here manifests as "nothing plays" with no error, easy to miss in manual testing. |

## Stop by SoundID

| | |
|---|---|
| **Behavior** | `BroAudio.Stop(id)` / `Stop(id, fadeOut)` stops every currently active player whose `ID` matches, fading out over the entity's authored fade (or the override) first. |
| **Observable** | Poll `player.IsActive` (from the original `Play` handle) until `false`; `player.IsPlaying` becomes `false` at the same or an earlier point. With `fadeOut = 0`, `AudioSource.isPlaying` goes false essentially next frame; with `fadeOut > 0`, `AudioSource` keeps playing (audible) while `AudioMixer`/clip volume ramps toward silence — see the Stop fade sub-entry below. |
| **Edge cases** | Stopping an ID with zero active players (silent no-op — `StopPlayer` just finds nothing to iterate); stopping the same ID twice back-to-back (`IsStopping` guard makes the second call within the fade window a no-op unless `fadeOut` is exactly `0`, i.e. `FadeData.Immediate`, which special-cases through even mid-stop); stopping an ID that was already fully stopped/recycled. |
| **Timing class** | Frame for the deactivation signal; DSP-driven fade coroutine in between if `fadeOut > 0`. |
| **Regression risk** | High — `StopPlayer` iterates the live pool backwards and mutates via `player.Stop`; an off-by-one or wrong-direction iteration silently skips players. |

## Stop by BroAudioType, including the All flag

| | |
|---|---|
| **Behavior** | `BroAudio.Stop(BroAudioType audioType[, fadeOut])` stops every active player whose type is contained in `audioType` (a `[Flags]` enum). `BroAudioType.All` is normalized via `ConvertEverythingFlag()` before the `Contains` check, so it must catch every concrete type (`Music`, `UI`, `Ambience`, `SFX`, `VoiceOver` per the fixture's `ConcreteAudioTypes`). |
| **Observable** | Play one player per concrete `BroAudioType`, call `Stop(BroAudioType.All, 0f)` (exactly what `BroAudioTestFixture.BroAudioTearDown` already relies on for isolation), then poll every player's `IsActive` to `false`. For a single-flag call (e.g. `Stop(BroAudioType.SFX)`), assert only SFX players stop and others remain playing. |
| **Edge cases** | A combined flag value covering a subset of types; `BroAudioType.All` when zero players are active (no-op); a type with no players currently active mixed with types that do have active players in the same call. |
| **Timing class** | Frame (deactivation) + DSP fade if `fadeOut > 0`. |
| **Regression risk** | High — the test harness's own teardown isolation depends on `Stop(All, 0f)` reliably clearing every player; a bug here would cascade into false failures/passes across the whole suite, not just this feature. |

## Stop with a completion callback

| | |
|---|---|
| **Behavior** | There is no `BroAudio.Stop(id, callback)` at the static-facade level — `SoundManager.Stop(id, fadeTime)` always calls the interface's fade-only overload, so `onFinished` stays `null` for type/ID-wide stops. A callback is only reachable through the `IAudioPlayer` handle returned by `Play`, via `IAudioStoppable.Stop(Action onFinished)` / `Stop(float fadeOut, Action onFinished)`, both `internal` (only reachable from within the `Ami.BroAudio` assembly / via the public method actually named on the interface — confirm accessibility before relying on it from `Assets/Tests`). |
| **Observable** | The callback fires (`Assert.IsTrue(invoked)`) at the same moment `IsActive` flips to `false` — `onFinished?.Invoke()` is the very last statement of `StopControl`/the immediate-return branch in `AudioPlayer.Stop(float, StopMode, Action)`. |
| **Edge cases** | Callback supplied but player already inactive (`Stop` immediate-return branch still invokes it, so it always fires exactly once, even for a no-op stop); callback supplied through a since-recycled `IAudioPlayerInstanceWrapper` handle (wrapper's `Instance` is null → `Instance?.Stop(onFinished)` silently drops the call and the callback never fires). |
| **Timing class** | Immediate for the double-no-op case; coroutine-end (still same-frame-resolvable via fade path) otherwise. |
| **Regression risk** | Medium — the `internal` visibility of the callback overloads on `IAudioStoppable` means this may not even be reachable from the Tests assembly without `InternalsVisibleTo`; needs a live-Editor check (see "Could not determine statically"). |

## Pause / UnPause by SoundID and by BroAudioType

| | |
|---|---|
| **Behavior** | `BroAudio.Pause(id[, fadeOut])` freezes playback in place (`AudioSource.Pause()`), preserving playhead position; `UnPause(id[, fadeIn])` resumes from exactly that position without re-triggering `OnStart`. Same pair exists for `BroAudioType`. |
| **Observable** | `player.IsActive` stays `true` throughout (pause does not deactivate); `player.IsPlaying` (== `AudioSource.isPlaying`) goes `false` on Pause and `true` again on UnPause. `AudioSource.timeSamples` should be unchanged (or nearly so, modulo fade) across the Pause→UnPause round trip — the strongest external proof that this is a real pause, not a stop/restart. |
| **Edge cases** | Pausing a player that hasn't started playing yet (still queued/pre-`LateUpdate`, or itself mid-fade-in) — `IsPausedBeforeStart` short-circuits `Play()` and `Stop(..., StopMode.Pause, ...)` takes the early "not yet playing" branch that flips internal `_stopMode` to `Pause` and fires `OnPause` without ever calling `AudioSource.Pause()` (nothing to pause yet); `UnPause` called on a player that isn't paused (`_stopMode != StopMode.Pause`) — logs a warning and no-ops rather than doing anything destructive; Pause→UnPause across a loop/handover boundary (resume path explicitly skips full re-setup — see `PlayInternal`'s `isResuming` branch — to avoid rewinding the playhead or leaking the mixer track). |
| **Timing class** | Frame for the `IsPlaying` flip; DSP-based end-time rebasing (`RebaseScheduleAfterPause`, `RecalculateScheduledEndTime`) on resume so the seamless-loop/fade-out schedule doesn't drift by the paused duration. |
| **Regression risk** | High — the "freeze in place" contract (no playhead loss, no re-fade-in, no double `OnStart`) is exactly the kind of behavior a naive Stop/Play reimplementation would silently violate. |

## StopMode.Mute — unreachable from the public API

| | |
|---|---|
| **Behavior** | `AudioPlayer.Stop(float, StopMode, Action)` and its `PlayInternal`/`StartPlaying` companions implement a third stop mode, `Mute`, that is documented (on the enum) as "keeps playing in the background... unmuted on next play." |
| **Observable** | N/A as a public-API test — no traced caller in `Runtime/` passes `StopMode.Mute` into `Stop(...)`; it is reachable only by calling the internal `AudioPlayer.Stop(float, StopMode, Action)` overload directly via reflection. |
| **Edge cases** | n/a — flagged as a gap, not a scenario to enumerate. |
| **Timing class** | n/a |
| **Regression risk** | Low as a *user-facing* regression (nothing public exercises it today) but worth a reflection-based characterization test purely to pin current behavior before anyone wires up a caller — see "Conflicts observed". |

## Player recycling and the stale-handle contract

| | |
|---|---|
| **Behavior** | When a player finishes (`EndPlaying` → `Recycle()`), it clears `ID` to `SoundID.Invalid`, returns its `AudioMixerGroup` track and itself to their pools, and nulls out the `InstanceWrapper` it handed to the caller. The `IAudioPlayer` the caller is holding is actually an `AudioPlayerInstanceWrapper` (confirmed by `PlayerToPlay` always wrapping before returning), so the original reference stays valid as an object but becomes inert. |
| **Observable** | Public API only: after the tracked `player.IsActive`/`IsPlaying` go `false` post-recycle, calling `player.Stop()`/`SetVolume()`/`AsBGM()`/etc. on the *same* stale reference is a safe no-op (`InstanceWrapper.IsAvailable()` guards every member); `player.ID` reads back as `SoundID.Invalid`; the underlying pooled `AudioPlayer` component may already have been handed to a *different* `Play` call and be playing unrelated audio — the stale wrapper must never accidentally observe or affect it. `Setting.LogAccessRecycledPlayerWarning` (an `EditorSetting`/`RuntimeSetting` toggle) additionally logs a warning on stale access via `LogInstanceIsNull()`. |
| **Edge cases** | Touching the handle exactly on the recycle frame (race between `EndPlaying` and a queued caller action — order depends on whether the caller's code runs before or after `SoundManager.LateUpdate` recycles); calling `AsBGM()`/`AsDominator()` on a stale handle (`Empty.MusicPlayer`/`Empty.DominatorPlayer` returned per `AudioPlayerInstanceWrapper`'s `IsAvailable()` guard, not a crash); reading `.AudioSource` on a stale handle (`Empty.AudioSource`, since `Instance` is null). |
| **Timing class** | Frame (recycle happens synchronously inside the coroutine that ends playback, itself frame/DSP driven). |
| **Regression risk** | High — this is the single most dangerous seam in the lifecycle: a caller holding a completed `IAudioPlayer` reference and calling into it is an extremely common real-world pattern (e.g. a stored reference in a MonoBehaviour), and any null-safety regression here becomes a live NRE in a shipped game. |

## Same pooled AudioPlayer instance is reused across independent Play calls

| | |
|---|---|
| **Behavior** | `AudioPlayerObjectPool` (an `ObjectPool<AudioPlayer>`) has an unbounded active side — `Extract()` creates a new instance only when the free-list is empty, and never refuses/caps extraction — but a bounded free-list on return (`Recycle` destroys the excess once `Pool.Count == MaxPoolSize`, from `Setting.DefaultAudioPlayerPoolSize`). So a `GameObject`/`AudioPlayer` component that finished sound A can be immediately re-extracted and start sound B; there is no "pool exhausted, playback dropped" failure mode. |
| **Observable** | Public API: `HasAnyPlayingInstances` / a second `Play` call returning a *working* handle proves extraction never fails; internal (no reliable external proxy) to prove the same `AudioPlayer` component was reused would require comparing `((AudioPlayerInstanceWrapper)handle)`'s underlying instance via reflection or the explicit `AudioPlayer` cast operator exposed on `AudioPlayerInstanceWrapper`. |
| **Edge cases** | Rapid Play/Stop/Play cycling beyond `DefaultAudioPlayerPoolSize` to force pool churn (exercises `DestroyObject`/`CreateObject` boundary); `RemoveFromPreventer`'s own guard — it logs a warning if asked to remove a target whose `IsActive` is already `false` (defensive, indicates a double-recycle would be caught, not silently swallowed). |
| **Timing class** | Immediate (pool operations are synchronous, no coroutine involved). |
| **Regression risk** | Medium — pool bugs (double-extract, double-recycle) are classic and this codebase has an explicit warning guard for exactly one such case (`RemoveFromPreventer`), suggesting it was hit before. |

## OnStart / OnUpdate / OnPause / OnEnd callbacks

| | |
|---|---|
| **Behavior** | `IAudioPlayer.OnStart/OnUpdate/OnPause/OnEnd` register `Action` delegates (`-=` then `+=`, so re-registering the same delegate instance is idempotent rather than double-firing) that fire respectively: once, when the source actually starts playing (`HasStartedPlaying` flips); every frame while active, via `_onUpdate?.Invoke` calls scattered through the fade/wait loops in `PlayControl`; on every transition into `Pause` (both the "already stopped, mark paused" branch and the coroutine `StopControl` branch call `_onPaused?.Invoke`); and once, from `EndPlaying`, right before `Recycle()` — meaning the handle is still nominally "active" (`ID` not yet cleared) for the duration of the `OnEnd` callback itself. |
| **Observable** | Public API: register a callback that flips a bool/increments a counter, then assert against wall/DSP-clock-independent counts (e.g. `OnStart` fired exactly once even across a Pause/UnPause round trip — resume explicitly does NOT re-invoke it, see `if (!HasStartedPlaying)` guard in `PlayControl`). `OnEnd`'s `SoundID` parameter should equal the original `ID` even though recycle (which invalidates `ID`) happens immediately after. |
| **Edge cases** | Registering the same `Action` reference twice (no double-fire, by design of `-=`/`+=`); callbacks registered on a since-recycled/stale handle (silently dropped, handled by the wrapper's `IsAvailable()` guard returning `Empty.AudioPlayer`, so registration itself becomes a no-op that returns a permanently-empty handle); `OnUpdate` firing rate during a fade vs. steady playback (it's invoked inside every `yield return null` wait loop, so once per frame, not tied to DSP granularity); exceptions thrown from a user callback (not guarded anywhere visible — an exception inside `_onStart?.Invoke` would propagate up through the coroutine and potentially abort `PlayControl` mid-setup — flagged below). |
| **Timing class** | Frame (all four are invoked from frame-stepped coroutine code, never from the audio thread). |
| **Regression risk** | Medium-high — callbacks are a primary integration point for game code (UI sync, VFX triggers); silent double-fire or drop is subtle and easy to miss without a dedicated regression test. |

## IsActive vs IsPlaying

| | |
|---|---|
| **Behavior** | `IsActive` (`ID.IsValid()`) is `true` from the instant `Play` enqueues through Stop's full teardown/recycle; it does not distinguish queued vs. playing vs. paused vs. mid-fade-out. `IsPlaying` (`AudioSource.isPlaying`) is `false` while queued (pre-`LateUpdate`), flips `true` once `AudioSource.Play()`/`UnPause()` actually runs, and flips `false` again on Pause or full Stop. |
| **Observable** | Public API directly — this pairing is already the backbone of `PlaybackSmokeTests`: `IsActive` true immediately after `Play`, `IsPlaying` only true after a frame + poll. |
| **Edge cases** | The queued-but-not-yet-drained window (`IsActive == true && IsPlaying == false`, exists for exactly one frame minimum); the paused window (same combination, but reachable at any time, not just start); Addressables-not-yet-loaded window if applicable (`PlayControl` yields on `WaitForAddressablesToLoad` before `AudioSource.clip` is even assigned — `IsActive` true, `IsPlaying` false, for an unbounded time if the load hangs). |
| **Timing class** | Frame. |
| **Regression risk** | High — this exact distinction is called out in the class doc comments and is clearly load-bearing for consumers who poll it to decide when it's safe to, e.g., read `AudioSource`. |

## Empty.AudioPlayer — the null-object path

| | |
|---|---|
| **Behavior** | `Empty.AudioPlayer` is a single shared `EmptyAudioPlayer` instance (not a `new` one per failure) implementing the full `IAudioPlayer` surface as inert no-ops: `ID` is always `SoundID.Invalid`, `IsActive`/`IsPlaying` always `false`, every mutator (`Stop`, `Pause`, `SetVolume`, `AddAudioEffect`, `OnStart`, etc.) returns `this` (or void) without side effects, `AsBGM()`/`AsDominator()` return the matching shared empty decorator singletons, not something wrapping this instance. |
| **Observable** | Public API entirely — chain arbitrary calls off a `Play` that returned `Empty.AudioPlayer` and assert nothing throws and no `AudioSource`/mixer state changes anywhere in the scene. |
| **Edge cases** | Fluent chaining through multiple no-op calls (`player.SetVolume(1f).SetPitch(1f).AsBGM()...`) must keep returning usable empty objects at every step, never `null`, so a chain never NREs; `Empty.AudioPlayer` is a static field, not a property, so nothing here re-creates it — a test must not assume identity resets between calls (it's the same object every time, which is itself an observable/testable invariant). |
| **Timing class** | Immediate. |
| **Regression risk** | Low-medium — it's a simple null-object, but its entire purpose is "never let a rejected Play crash calling code," so a single missed interface member (compile-checked, but a *behavioral* slip — e.g. returning `null` instead of `this` from one method) would defeat that purpose for just that one call in the chain. |

## Comb-filtering preventer bookkeeping

| | |
|---|---|
| **Behavior** | Every successful `Play` records `_combFilteringPreventer[id] = player` in `SoundManager`, independent of whether any `PlaybackGroup` rule actually uses it; a player normally removes itself via `RemoveFromPreventer` when recycled, but only if it's still the entry on record for that ID (a later `Play` of the same ID overwrites the dictionary entry, so an earlier player's recycle is a safe no-op against a newer one's entry). |
| **Observable** | Internal only — `TryGetPreviousPlayerFromCombFilteringPreventer(id, out previousPlayer)` is the sole accessor and it's not exposed publicly; a test would need reflection or an `InternalsVisibleTo` seam to assert on it directly. The externally-observable proxy is `DefaultPlaybackGroup`'s comb-filtering *rejection* (a rapid same-ID re-Play returning `Empty.AudioPlayer`), but that requires a real `PlaybackGroup` asset, which is out of scope for a pure-lifecycle test (belongs with playback-group/rules testing instead). |
| **Edge cases** | Rapid re-Play of the same ID before the first player starts (`PlaybackStartingTime` still 0, `previousIsInQueue` branch in `HasPassedCombFilteringRule`); the same ID played twice in the same frame with `_ignoreCombFilteringIfSameFrame` on vs. off. |
| **Timing class** | Immediate (dictionary write) / frame (comparison uses `TimeExtension.UnscaledCurrentFrameBeganTime`). |
| **Regression risk** | Low for this lifecycle section specifically — the interesting behavior lives in `DefaultPlaybackGroup`, which is a separate concern; here it's just bookkeeping plumbing. |

## Conflicts observed

- **`StopMode.Mute` is unreachable from any traced public-API path.** The enum is documented ("keeps playing in the background... unmuted on next play") and `AudioPlayer.Playback.cs` fully implements the `Mute` branch in both `StartPlaying()` and `StopControl`'s ending `switch`, but no caller in `Runtime/` ever passes `StopMode.Mute` into `Stop(float, StopMode, Action)`. Either there's a caller elsewhere (decorator, editor tooling) not found by this search, or it's dead/future functionality. Not fixed — noted for the orchestrator to verify live (e.g. `grep -r "StopMode.Mute"` across the full repo including Editor/).
- **`IAudioStoppable`'s callback-bearing `Stop`/`Pause`/`UnPause` overloads are declared `internal`.** `Assets/Tests` is presumably a separate assembly without `InternalsVisibleTo` into the runtime assembly (per project memory: "Tests access internals via System.Reflection (no InternalsVisibleTo)"). If so, `player.Stop(onFinished)` is not directly callable from test code and must go through reflection or an `internal` call site inside the runtime assembly itself — this constrains how the "Stop with a callback" behavior above can actually be exercised.

## Could not determine statically

- Whether `IAudioStoppable.Stop(Action onFinished)` / `Stop(float, Action)` are actually invocable from `Assets/Tests/**` given their `internal` modifier and the "no InternalsVisibleTo" note in project memory — needs a live compile check in the Editor.
- Whether `StopMode.Mute` has any live caller outside `Runtime/` (Editor tooling, a decorator not covered by this file list, or sample code) — only `Runtime/` was searched per the task's file list.
- The exact `AudioSource` field observable for follow-target tracking (`AudioPlayer.Update()` writes `transform.position`, but `transform` isn't exposed through `IAudioSourceProxy`/`IAudioPlayer` — a test would need `player.AudioSource` plus reading the source's `GameObject.transform`, or reflection into the underlying `AudioPlayer`) — confirm `IAudioSourceProxy`'s actual member set before relying on it.
- Whether pool growth under sustained pressure (many concurrent `Play` calls beyond `DefaultAudioPlayerPoolSize`) has any perceptible cost/behavior difference worth a regression test, or whether it's purely a memory/GC concern out of scope for behavioral characterization.
- Precise interaction between `Pause` called on a player that is itself mid-handover (seamless loop about to hand off to `_nextPlayer`) — `StopControl`'s `CanHandoverToEnd`/`BeginHandover` logic branches only reference the `Stop` path explicitly; whether `Pause` (which goes through the same `Stop(overrideFade, StopMode.Pause, onFinished)` entry point) correctly suppresses or preserves a pending handover was not traced in depth and would benefit from a live-Editor trace or a dedicated loop/handover test file (likely out of this section's scope — see project memory's "Looping via handover" note).
---

## Coverage ledger

Status per behavior above, per the runtime plan's Definition of Done. **covered** = the core contract is
pinned by a test; **partial** = pinned for some overloads/inputs, with the gap named; **deferred** = no test
yet, and testable; **out of scope** = deliberately not tested, with the reason.

| Behavior | Status | Pinned by |
|---|---|---|
| Play — global / positioned / follow-target | partial | `PlaybackSmokeTests.Play_AfterQueueIsDrained_PlaysTheEntitysClip` (global); `PlaybackGroupTests.Play_PositionedFarApart_*` (positioned). The `Play(id, transform)` follow-target overload has no test. |
| Play returns Empty.AudioPlayer when the sound is not playable | covered | `PlaybackLifecycleTests.Play_RejectedByValidator_ReturnsInertEmptyPlayer` |
| Stop by SoundID | covered | `PlaybackSmokeTests.Stop_AfterPlaying_DeactivatesThePlayer`; `FadeAndTrimTests.Stop_SecondNonImmediateCall_*`, `Stop_WithImmediateFade_*` |
| Stop by BroAudioType, including the All flag | covered | `PlaybackLifecycleTests.Stop_WithAllFlag_DeactivatesEveryConcreteType`, `Stop_WithSingleFlag_LeavesOtherTypesPlaying` |
| Stop with a completion callback | deferred | No test passes an `onFinished` callback to `Stop`. |
| Pause / UnPause by SoundID and by BroAudioType | partial | `PlaybackLifecycleTests.Pause_ThenUnPause_FreezesAndResumesFromSamePosition` covers the by-SoundID path; the `BroAudioType` overloads are untested. |
| StopMode.Mute — unreachable from the public API | out of scope | No public API reaches it, so there is nothing to call from a test. Removal is a product decision, not a coverage gap. |
| Player recycling and the stale-handle contract | covered | `PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal`; `SelectionStateAndDecoratorTests.AudioSource_AccessedAfterRecycle_*` |
| Same pooled AudioPlayer instance is reused across independent Play calls | covered | `VolumePitchMixerTests.Play_AcquiresPooledMixerTrackAndReusesOneAfterRecycle` |
| OnStart / OnUpdate / OnPause / OnEnd callbacks | covered | `PlaybackLifecycleTests.Callbacks_OnStartOnUpdateOnPause_FireWithExpectedCounts`, `OnEnd_WhenPlaybackFinishes_FiresOnceWithOriginalID` |
| IsActive vs IsPlaying | covered | `PlaybackLifecycleTests.IsActiveAndIsPlaying_AroundQueueDrain_TrackDifferentWindows` |
| Empty.AudioPlayer — the null-object path | covered | `PlaybackLifecycleTests.Play_RejectedByValidator_ReturnsInertEmptyPlayer` |
| Comb-filtering preventer bookkeeping | covered | `PlaybackGroupTests.Play_SameID_WithinCombFilteringWindow_RejectsSecond` and the four sibling cases |

"Conflicts observed" and "Could not determine statically" elsewhere in this file are research notes, not behaviors, and carry no status.
