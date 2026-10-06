# Play / Stop / Pause lifecycle

## Play — global / positioned / follow-target

- **Behavior**: `BroAudio.Play(id)`, `Play(id, position)` and `Play(id, transform)` return a live handle that plays the entity's clip in 2D, at a fixed 3D point, or tracking a transform.
- **Observable**: `IsActive` true on return; after playback starts, `IsPlaying` true and `AudioSource.clip` is the entity's clip. Position and tracking live on the pooled player's `transform`, which the interface does not expose: reach it with the fixture's `InstanceOf(player)`.
- **Edge cases**: unregistered `SoundID`; entity with no clips; a rejecting `IPlayableValidator`; follow target destroyed mid-playback.

## Play returns Empty.AudioPlayer when the sound is not playable

- **Behavior**: when `SoundManager.IsPlayable` fails (unknown `SoundID`, or the validator rejects it), `Play` returns the shared `Empty.AudioPlayer` and nothing plays. Rejection is synchronous, before anything is queued.
- **Observable**: the handle's `IsActive`/`IsPlaying` are false and every call on it is a silent no-op. A stub `IPlayableValidator` returning false forces this path without a `PlaybackGroup` asset.
- **Edge cases**: "not found" and "rejected" return the same object, so the return value cannot tell them apart.

## Stop by SoundID

- **Behavior**: `BroAudio.Stop(id[, fadeOut])` stops every active player with that `ID`, fading over the entity's authored fade or the override.
- **Observable**: poll the original handle's `IsActive` to false. With `fadeOut > 0` the voice stays audible while its volume ramps down.
- **Edge cases**: an ID with no active players (no-op); a second `Stop` during the fade is ignored (`IsStopping`) unless its fade is exactly `FadeData.Immediate`; an ID already recycled.

## Stop by BroAudioType, including the All flag

- **Behavior**: `BroAudio.Stop(BroAudioType[, fadeOut])` stops every active player whose type the `[Flags]` value contains. `All` goes through `ConvertEverythingFlag()` first and must catch every concrete type. The fixture's teardown relies on `Stop(All, 0f)`.
- **Observable**: play one player per concrete type, stop, poll each `IsActive` to false. For a single flag, only that type's players stop.
- **Edge cases**: a combined flag covering a subset; `All` with nothing active; a type with no players mixed with types that have some; a fade across one-shots and a loop — the one-shots fade, the loop falls silent at the end of its current iteration.

## Stop with a completion callback

- **Behavior**: the static facade has no callback overload. `IAudioStoppable`'s callback members are `internal`; callers reach them through the public `BroAudioChainingMethod` extensions (`player.Stop(onFinished)`, `Stop(fadeOut, onFinished)`).
- **Observable**: with a fade, the callback fires after the fade and after `EndPlaying()` has recycled the player, so `IsActive` is already false when it runs. On a player that is not playing, `Stop` takes an early-out that fires it synchronously, before `EndPlaying()`.
- **Edge cases**: a recycled handle drops the callback silently (`Instance?.Stop(onFinished)` on a null instance), as does `Empty.AudioPlayer`.

## Pause / UnPause by SoundID and by BroAudioType

- **Behavior**: `Pause(id[, fadeOut])` freezes playback in place; `UnPause(id[, fadeIn])` resumes from the same sample without re-firing `OnStart`. The same pair exists per `BroAudioType`.
- **Observable**: `IsActive` stays true; `IsPlaying` goes false on pause and true on resume; `AudioSource.timeSamples` is unchanged across the round trip.
- **Edge cases**:
  - Pause before the player starts: `Stop(..., StopMode.Pause, ...)` takes the not-playing branch, marks the player paused and fires `OnPause` without calling `AudioSource.Pause()`; `IsPausedBeforeStart` then short-circuits `Play()`.
  - `UnPause` on a player that is not paused warns and no-ops.
  - Resume skips full re-setup (`PlayInternal`'s `isResuming` branch); end-time schedules are rebased by the paused DSP duration (`RebaseScheduleAfterPause`).
  - A clip with its own `FadeIn` restarts that fade from silence on resume unless `UnPause(fadeIn)` overrides it.
  - `Pause()` with no argument fades out over the clip's authored `FadeOut` before freezing.
  - A pause longer than the rest of the clip resumes from the paused sample and plays the remainder.
  - `Pause(0f)` inside a seamless-loop crossfade pauses only the incoming player (the handle already points at it); the outgoing player plays its tail out. `UnPause` resumes it without throwing.

## StopMode.Mute — a BGM transition stop mode

- **Behavior**: `Mute` keeps the voice playing silently. Its only public entry is `SetTransition(this IMusicPlayer, Transition, StopMode)`, which mutes the outgoing BGM instead of stopping or pausing it. The enum doc says it is unmuted on next play, but no path unmutes it.
- **Observable**: the outgoing BGM's `GetVolume()` drops to near zero while its handle stays `IsActive` and `IsPlaying`.
- **Edge cases**: playing the same sound again starts a new player and leaves the muted one running; `UnPause` warns (it accepts only a paused player); when the muted clip runs out the source stops but the player stays `IsActive` and keeps its pool slot until an explicit `Stop`.

## Player recycling and the stale-handle contract

- **Behavior**: on finish (`EndPlaying` → `Recycle()`) a player clears `ID` to `SoundID.Invalid`, returns its mixer track and itself to their pools, and detaches the `AudioPlayerInstanceWrapper` it handed out. The caller's handle is always that wrapper, so it stays valid as an object but goes inert.
- **Observable**: after recycle, every call on the same handle is a no-op (`InstanceWrapper.IsAvailable()` guards each member) and `ID` reads `SoundID.Invalid`. The pooled `AudioPlayer` may already be serving another `Play`; the stale handle must not observe or affect it. `Setting.LogAccessRecycledPlayerWarning` adds a warning on stale access (`LogInstanceIsNull()`).
- **Edge cases**: touching the handle on the recycle frame (order depends on whether caller code runs before `SoundManager.LateUpdate`); `AsBGM()`/`AsDominator()` on a stale handle return `Empty.MusicPlayer`/`Empty.DominatorPlayer`; `.AudioSource` on a stale handle returns `Empty.AudioSource`.

## Same pooled AudioPlayer instance is reused across independent Play calls

- **Behavior**: `AudioPlayerObjectPool` never refuses extraction — it creates a player when the free list is empty — but bounds the free list on return, destroying the excess once it holds `MaxPoolSize` (from `Setting.DefaultAudioPlayerPoolSize`). A finished player can be re-extracted at once; there is no "pool exhausted" failure.
- **Observable**: a further `Play` always returns a working handle. Reuse of the same component shows by comparing `InstanceOf(handle)` across plays.
- **Edge cases**: Play/Stop cycling past `DefaultAudioPlayerPoolSize` exercises the create/destroy boundary; `RemoveFromPreventer` warns if asked to remove an inactive player, so a double recycle is logged rather than swallowed; after a 3D sound, the recycled player keeps its scalar spatial settings reset but still carries the previous sound's custom rolloff curve.

## OnStart / OnUpdate / OnPause / OnEnd callbacks

- **Behavior**: registration does `-=` then `+=`, so registering the same delegate twice fires it once. `OnStart` fires once, when the source starts playing; `OnUpdate` once per frame inside `PlayControl`'s and the fade's wait loops; `OnPause` on every transition into pause (both the not-yet-playing branch and `StopControl`); `OnEnd` once, from `EndPlaying` just before `Recycle()`, so `ID` is still valid during the callback.
- **Observable**: count invocations. `OnStart` fires once across a Pause/UnPause round trip (`if (!HasStartedPlaying)` in `PlayControl`). `OnEnd`'s `SoundID` equals the original `ID`.
- **Edge cases**: registering on a stale handle is a no-op returning `Empty.AudioPlayer`; `OnUpdate` is frame-rate, not DSP-rate; user callbacks are not try-guarded, so a throwing `OnStart` aborts `PlayControl`.

## IsActive vs IsPlaying

- **Behavior**: `IsActive` (`ID.IsValid()`) is true from `Play` until recycle and does not distinguish queued, playing, paused or fading out. `IsPlaying` (`AudioSource.isPlaying`) is false while queued, true once the source plays or resumes, false again on pause or stop.
- **Observable**: `IsActive` true immediately after `Play`; `IsPlaying` only after playback starts.
- **Edge cases**: the queued window (`IsActive && !IsPlaying`, at least one frame); the paused window (same pair, at any time); an unloaded Addressables clip — `PlayControl` waits on the load before assigning `AudioSource.clip`, so the pair holds for as long as the load takes.

## Empty.AudioPlayer — the null-object path

- **Behavior**: `Empty.AudioPlayer` is one shared static `EmptyAudioPlayer`: `ID` is `SoundID.Invalid`, `IsActive`/`IsPlaying` false, every mutator returns `this` (or void) with no side effect, and `AsBGM()`/`AsDominator()` return the shared empty decorators.
- **Observable**: chain calls off a rejected `Play` and assert nothing throws and no `AudioSource` or mixer state changes.
- **Edge cases**: a fluent chain (`SetVolume(1f).SetPitch(1f).AsBGM()...`) must never yield `null`; the instance is the same object on every rejection.

## Comb-filtering preventer bookkeeping

- **Behavior**: every accepted `Play` records `_combFilteringPreventer[id] = player`, whether or not any group uses the rule. A recycled player removes the entry only if it is still the one on record, so an earlier player's recycle leaves a newer one's entry alone.
- **Observable**: `SoundManager.Instance.TryGetPreviousPlayerFromCombFilteringPreventer(id, out player)` (public). The rejection it drives belongs to `DefaultPlaybackGroup` (see selection-policy.md).
- **Edge cases**: a same-ID replay before the first player starts (its `PlaybackStartingTime` is still 0, the `previousIsInQueue` branch of `HasPassedCombFilteringRule`); two same-ID plays in one frame with `_ignoreCombFilteringIfSameFrame` on vs. off.

## Further behaviors

- **Play with a fade-in at a position or on a follow target.** `Play(id, position, fadeIn)` and
  `Play(id, followTarget, fadeIn)` place a 3D voice at (or tracking) the target and ramp it from silence
  over the given fade.
- **Play with a null follow target.** `Play(id, (Transform)null)`, or with a destroyed `Transform`, logs an
  error and returns an inactive player, even for `SoundID.Invalid`.
- **Play of an entity whose clip slot holds no `AudioClip`.** The play is accepted, then logs one error and
  recycles without sounding; a Random-mode entity whose slots are all empty selects one of them and does
  the same.
- **UnPause misuse.** `UnPause` on a player that is not paused warns and leaves playback untouched; during a
  faded `Stop` it warns and the stop still completes; during a Pause fade-out it resumes playback but leaves
  `IsStopping` set, so a later faded `Stop` is discarded.
- **Release, load and query verbs once the manager is gone.** The facade's release verbs, including the
  Addressables/Localization ones, are silent no-ops with the manager destroyed; the optional packages'
  load and `IsLoaded` verbs throw `BroAudioException`.
