# Selection, policy and decorators

Two setup facts apply to most entries:

- **Groups.** A code-built entity (`NewSound`/`NewEntity`) has no `_group` and no `AudioAsset`, so `AudioEntity.PlaybackGroup` is null and `SoundManager.IsPlayable` skips validation (`validator = customValidator ?? entity.PlaybackGroup`). Group tests wire `_group` (`TestAudioLibrary.Reflected.AudioEntity.Group`) to a fixture `NewGroup(...)`, or pass an `IPlayableValidator` to `Play(id, validator)`.
- **Clip-selection state lives on the `AudioEntity`.** `_clipSelectionStrategy` is one instance per entity, so concurrent plays of one `SoundID` advance the same Sequence/Shuffle cursor. It resets only through `BroAudio.ResetMultiClipStrategy(id[, sequenceId])`, never between plays, stops or recycles.

## Clip selection strategies

### Single mode always plays clips[0]

- **Observable**: `AudioSource.clip` after start, or `SingleClipStrategy.SelectClip(clips, ctx, out index)`.
- **Edge cases**: null/empty `Clips` → `Utility.ClipListIsNullOrEmpty` logs and returns null; `Clips[0]` null → logs "first clip is null"; `Clips[0]` unset (`IsSet == false`) → logs "No valid clip is set" and returns null, like Sequence and Shuffle.

### Sequence mode cycles clips 0..N-1 and wraps

- **Observable**: repeated `SelectClip` calls yield indices 0, 1, …, N-1, 0 via `out index`.
- **Edge cases**: a one-clip array stays at 0; an unset clip at the next index logs, returns null with index −1, and resets the cursor, so the next call restarts from `clips[0]` rather than after the hole.

### Sequence mode: named SequenceIds run independent cursors

- **Observable**: different `ClipSelectionContext.SequenceId` values on one entity advance independently (`_namedSequenceIndices`). Through a live player, chain `.SetSequenceId("x")` in the same statement as `BroAudio.Play`, before `PlayControl` picks the clip.
- **Edge cases**: `SequenceId == null` uses the default cursor; `Reset(null)` resets only the default cursor; `Reset("x")` removes only `"x"`.

### Random mode: uniform when all Weights are 0, weighted otherwise

- **Observable**: `RandomClipStrategy.SelectClip` distribution over many seeded calls; set weights directly (`entity.Clips[i].Weight`, a public field).
- **Edge cases**: all weights 0 → `Random.Range(0, clips.Length)`; any nonzero weight switches to weighted mode for all clips, making zero-weight clips unreachable; a one-clip array always returns that clip. Assert bucket boundaries deterministically (e.g. a zero-weight clip never appears) rather than trusting raw frequencies. Random keeps no state (`Reset()` is a no-op) and can repeat consecutively.

### Shuffle mode avoids a repeat only across a cycle reset

- **Behavior**: an in-cycle pick is not checked against the clip just returned or the clips already used, so Shuffle can repeat the previous clip and does not visit every clip once per cycle — contrary to the `MulticlipsPlayMode.Shuffle` doc comment. When `_used` covers every clip, it resets and remembers `_lastUsed`, so the first pick after a reset is steered off the previous clip. Over many draws each clip's share is uniform.
- **Observable**: `ShuffleClipStrategy.SelectClip` over many calls; locate the returned clip with `Array.IndexOf`, because after the fallback scan the `out index` can disagree with the clip returned.
- **Edge cases**: a one-clip entity always returns that clip without error; a two-clip entity can repeat.

### Velocity mode selects by Weight thresholds

- **Behavior**: clips are ascending thresholds (`Velocity == Weight`). The strategy returns the clip before the first one whose `Velocity > context.Value`, or the last clip if none exceeds it.
- **Observable**: `VelocityClipStrategy.SelectClip(clips, new ClipSelectionContext(value), out index)`; through a live player, chain `.SetVelocity(n)`.
- **Edge cases**: a value below every threshold returns index 0 (`i == 0 ? 0 : i - 1`); a value above every threshold returns the last clip but leaves `out index` at 0; ordering is not validated, so non-ascending weights degrade silently; `SetVelocity` on a non-Velocity entity logs an error and leaves the context value unchanged.

### Chained mode maps PlaybackStage to a fixed clip index

- **Observable**: `ChainedClipStrategy.SelectClip(clips, new ClipSelectionContext((int)stage), out index)` → `index = Math.Max(stage - 1, 0)`: Start → clips[0], Loop → clips[1], End → clips[2]; `None` floors to 0. A fresh play is seeded at `PlaybackStage.Start` (`PlaybackPreference.GetContextValue`).
- **Edge cases**: fewer than 3 clips → the End stage logs "There's no clip for Chained Play Mode Stage" and returns null. `PlaybackPreference.CanHandoverToEnd()` checks the same condition independently (`Clips.Length < (int)PlaybackStage.End`); the two must agree.

### Localization mode selects the row matching the active locale

- **Observable**: `LocalizationClipStrategy.SelectClip`'s returned `.GetAudioClip()` and `out index`.
- **Setup**: through `PickNewClip()` the strategy needs a live `SoundManager` (`TryGetCachedLocalizedClip`); in isolation it does not. Call `Inject(localizedAudio, entityName, tryGetCachedClip)`:
  - `localizedAudio`: a `LocalizedAudioClip` whose table and entry references are non-empty; they need not resolve to a real table (the same shape check as `HasValidLocalizationReferences`).
  - `tryGetCachedClip`: a lambda returning a test clip; a non-null return bypasses `LoadAssetAsync`.
  - Per-row `entity.Clips[i].Locale` (exists only under `PACKAGE_LOCALIZATION`) set to a `LocaleIdentifier` of one of the project's `Locale` assets.
  - Swap `LocalizationSettings.SelectedLocale` and restore it in teardown.
  End-to-end `Play` resolving a real clip needs an authored `AssetTable` with an `AudioClip` entry.
- **Edge cases**: null or empty references → error, index −1, null; cached getter and load both null → warning, index −1; no row matches the locale → `LocalizedBroAudioClipWrapper(resolvedClip)` with index 0, and per-clip properties (Volume, Delay, StartPosition…) silently take defaults.

### ChangeClipPerLoop re-picks a clip on every loop iteration

- **Observable**: `AudioSource.clip` differs across iterations of one play. Setup: set `AudioEntity.Flags` to `AudioEntityFlag.ChangeClipPerLoop` with `Loop` or `SeamlessLoop`.
- **Edge cases**: without the flag a loop keeps the same clip.

## Randomization ranges (RandomFlag / pitch & volume jitter)

### RandomFlag.Volume / Pitch jitter by ± half the range; absent flags return the base exactly

- **Observable**: `entity.GetMasterVolume()` / `entity.GetPitch()` (or `AudioEntity.GetRandomValue(base, flag)`) over many calls fall within `[base - range/2, base + range/2]`; without the flag they always equal `base`.
- **Setup**: `MasterVolume`, `Pitch`, `VolumeRandomRange`, `PitchRandomRange` and `RandomFlags` are auto-properties (backing fields `<Name>k__BackingField`).
- **Edge cases**: range 0 with the flag set returns `base`; `RandomFlag.None` short-circuits before `Random.Range`, so the ranges are irrelevant.

## Playback-group voice limiting and rejection

### MaxPlayableCountRule rejects Play once the group's count reaches the limit

- **Observable**: `Play` returns `Empty.AudioPlayer` (`IsActive == false`) synchronously once the limit is reached.
- **Setup**: `NewGroup(maxPlayableCount: n)`, wired onto the entity's `_group`.
- **Edge cases**: the count is per group (`_currentPlayingCount`), so a limit of 1 also rejects a different `SoundID` in the same group; `<= 0` (factory `-1`, "Infinity") means no limit; the count decrements through `OnEnd`, i.e. when the accepted player recycles, not when its audio ends.

### The voice-limit count increments at enqueue time

- **Behavior**: `validator.OnGetPlayer(player)` runs inside `SoundManager.IsPlayable`, before the queue drains.
- **Observable**: two `Play` calls in the same frame with limit 1 — the second is rejected though neither has started.

### CombFilteringRule rejects a same-ID replay within its window

- **Observable**: a second play of one `SoundID` within `_combFilteringTime` returns an inactive handle, and logs a warning when `_logCombFilteringWarning` is on (`NewGroup` turns it off).
- **Setup**: `NewGroup(combFilteringTime: t, ...)`. Needs a live `SoundManager` (the rule reads `TryGetPreviousPlayerFromCombFilteringPreventer`).
- **Edge cases**: `_ignoreCombFilteringIfSameFrame` exempts same-frame plays, and a previous play still in the queue counts as same-frame; two positioned plays farther apart than `_ignoreIfDistanceIsGreaterThan` are exempt; one global plus one positioned play is exempt whenever `_ignoreIfDistanceIsGreaterThan > 0`.

### A custom IPlayableValidator replaces the entity's PlaybackGroup

- **Observable**: `Play(id, customValidator)` consults only the custom validator (`customValidator ?? entity.PlaybackGroup`). A hand-rolled `IPlayableValidator` tests `IsPlayable`'s branching without any ScriptableObject.

## Decorators: AsBGM / AsDominator

### AsBGM() composes, it does not swap the player

- **Observable**: `player.AsBGM()` returns the same `AudioPlayerInstanceWrapper` (`Wrap(...)` returns `this`), so `((IAudioPlayer)result).ID == player.ID`. Attach is synchronous.
- **Setup**: for an explicit-attach test, use a non-Music entity or turn `AlwaysPlayMusicAsBGM` off.

### A second AsBGM() reuses the same MusicPlayer

- **Behavior**: `Utility.GetOrCreateDecorator` looks up an existing decorator before creating one, so a player has at most one `MusicPlayer`.
- **Observable**: `.AsBGM().SetTransition(Immediate)` then `.AsBGM().SetTransition(CrossFade)` — the second call's settings are the ones the next `DoTransition` uses; there is no second decorator.

### AsDominator() and AsBGM() coexist

- **Observable**: both attach to one player and both resolve via `AudioPlayer.TryGetDecorator<T>` (inspect `TestAudioLibrary.Reflected.AudioPlayer.Decorators`). `AsDominator` is compiled out under `UNITY_WEBGL`.

### AlwaysPlayMusicAsBGM auto-attaches the BGM decorator to Music plays

- **Observable**: `Play(musicId)` without `.AsBGM()` sets `MusicPlayer.CurrentBGMPlayer`, and a second Music play transitions per `Setting.DefaultBGMTransition`/`DefaultBGMTransitionTime`.
- **Edge cases**: with the setting off, a Music play leaves `CurrentBGMPlayer` untouched; non-Music types are never decorated.

## Chaining-method fluent API (BroAudioChainingMethod.cs)

### Every chaining method no-ops on a recycled or empty player

- **Observable**: any `BroAudioChainingMethod` extension on `Empty.AudioPlayer` or a recycled handle does not throw; most return a non-null, inert handle.

### SetVelocity and SetSequenceId are guarded outside their own mode

- **Observable**: `.SetVelocity(n)` on a non-Velocity entity and `.SetSequenceId("x")` on a non-Sequence entity each log an error and change nothing (`PlaybackPreference`). `SequenceId`'s setter is private, so the guard cannot be bypassed.

## RuntimeSetting toggles that change Play behavior

`RuntimeSetting` fields are public; set them on `SoundManager.Instance.Setting`. The fixture snapshots and restores the whole `RuntimeSetting` per test.

| Field | Behavior it changes | Notes |
|---|---|---|
| `AlwaysPlayMusicAsBGM` | Auto-attaches `MusicPlayer` to every `BroAudioType.Music` play | see decorators above |
| `DefaultBGMTransition` / `DefaultBGMTransitionTime` | Transition used by the auto-BGM path | applies only with `AlwaysPlayMusicAsBGM` on and a prior BGM active |
| `DefaultChainedPlayModeLoop` / `DefaultChainedPlayModeTransitionTime` | `AudioEntity.HasLoop(out, out)` falls back to these for a Chained entity with neither `Loop` nor `SeamlessLoop` | the 4-argument `HasLoop` takes the defaults explicitly and needs no `SoundManager` |
| `LogAccessRecycledPlayerWarning` | Whether a recycled handle logs a warning (`LogInstanceIsNull`) | |
| `GlobalPlaybackGroup` | The group an `AudioAsset`-backed entity plays under when its asset names none, and the parent a rule with `_isOverride == false` borrows its rule method from | `BroUserDataGenerator` always assigns a `DefaultPlaybackGroup` here, so every authored entity passes its rules. Code-built entities (`NewSound`) have no `AudioAsset` and stay outside it (`NewAssetBackedSound` plays under it). A non-override rule with no parent always passes |
| `AutomaticallyLoadAddressableAudioClips` | Automatic Addressables loading on Play | on: the clip is loaded and played. Off (factory default): an error is logged, then the clip is loaded and played anyway; setting the non-preloaded log level to Warning only turns that error into a warning. A key that cannot load: `LoadAssetAsync` completes failed with no BroAudio log of its own, and `Play` throws out of `PlayControl`, leaving the player active and silent |

## EditMode unit-test candidates

No `SoundManager` or ScriptableObject: call `SelectClip`/`Reset` on a hand-built `BroAudioClip[]` and `ClipSelectionContext`.

- `SingleClipStrategy`, `SequenceClipStrategy` (incl. `Reset(string)`), `RandomClipStrategy`, `ShuffleClipStrategy`, `VelocityClipStrategy`, `ChainedClipStrategy` (feed `(int)PlaybackStage` directly).
- `LocalizationClipStrategy` after `Inject(...)` — see the Localization entry.
- `AudioEntity.GetRandomValue(base, range)` (static) and `GetRandomValue(base, RandomFlag)` on `ScriptableObject.CreateInstance<AudioEntity>()`.
- The 4-argument `AudioEntity.HasLoop(out, out, chainedDefaultLoop, chainedDefaultTransitionTime)`; the 2-argument overload needs `SoundManager`.
- `Utility.GetRandomValue`, `Utility.ClipListIsNullOrEmpty`.

## Further behaviors

- **`IAudioPlayer.CurrentPlayingClip`** is the entity row picked for that play, and reads null on a
  recycled handle.
- **`SubscribeLocalizedAudioChanged` / `SoundID.LocalizedAudioChanged`** guard against an entity that is not
  in Localization mode and one with no table or entry set; a handler fires only once a real `AssetTable`
  resolves the entry.
- **A Localization-mode entity with no table.** `LoadAssetAsync` warns and returns an invalid handle; the
  release verbs are silent no-ops on an entity that was never loaded; `Play` logs one error and recycles
  without sounding.
- **The shipped global playback group.** An `AudioAsset`-backed entity resolves to the global group, whose
  factory comb-filtering window is 0.04 s with same-frame plays not exempt: distinct IDs in one frame are
  all accepted, the same ID twice in one frame rejects the second with a tagged warning, positioned plays
  of one ID in one frame are exempt only beyond the factory distance, and a replay after the window is
  accepted. A replay one frame later but still inside 0.04 s depends on the frame rate. A custom group
  that does not override the rule falls back to the global group's window.
- **Comb-filtering exemptions have a rejecting side.** Positioned plays close together, and a global play
  followed by a positioned one with the distance exemption off, are both rejected inside the window.
