# Selection, policy and decorators

Source walked: `Runtime/Utility/ClipSelection/*`, `Runtime/DataStruct/Core/AudioEntity.cs` (+`.Localization.cs`),
`Runtime/Player/PlaybackGroup/*`, `Runtime/Interface/IPlayableValidator.cs`, `Runtime/Player/MusicPlayer.cs`,
`DominatorPlayer.cs`, `AudioPlayerDecorator.cs`, `Runtime/RuntimeSetting.cs`, `Runtime/BroAudioChainingMethod.cs`,
`Runtime/SoundManager/SoundManager.Playback.cs`, `SoundManager.Localization.cs`.

Two setup facts apply to nearly every behavior below and are not repeated in every row:

- **Groups**: a code-built `AudioEntity` (`TestAudioLibrary.CreateEntity`) has `_group == null` and `AudioAsset == null`,
  so `AudioEntity.PlaybackGroup` (`_group ? _group : _upperGroup`) resolves to `null`. In
  `SoundManager.IsPlayable` (`SoundManager.Playback.cs:44-61`), `validator = customValidator ?? entity.PlaybackGroup`,
  and `if (validator != null && !validator.IsPlayable(...))` — a null validator means the check is skipped
  entirely and Play always succeeds. Any group/voice-limit/comb-filtering test must either set `_group` via
  `TestAudioLibrary.SetPrivateField(entity, "_group", group)` or pass an `IPlayableValidator` to the `Play(id, validator)` overload.
- **Clip-selection state lives on the `AudioEntity`, not on the player or the play call.** `AudioEntity._clipSelectionStrategy`
  is a single field reused across every `PickNewClip()` call for that entity (`AudioEntity.cs:41,74-80`). Two
  concurrent `Play()` calls on the same `SoundID` advance the *same* Sequence/Shuffle cursor. State only resets
  via the explicit `BroAudio.ResetMultiClipStrategy(id[, sequenceId])` API — never automatically between plays, stops, or entity recycles.

## Clip selection strategies

### Single mode always plays clips[0]

- **Observable**: `player.AudioSource.clip` after one frame, or `SingleClipStrategy.SelectClip(clips, ctx, out index)` directly.
- **Setup**: entity with `MulticlipsPlayMode = MulticlipsPlayMode.Single` (default); `Clips[0]` set.
- **Timing class**: EditMode-pure for the strategy; frame for the AudioSource proxy.
- **Edge cases**: `Clips` null/empty → `Utility.ClipListIsNullOrEmpty` logs and returns null; `Clips[0]` reference-null → logs `"...first clip is null."` and returns null; `Clips[0]` non-null but unset (`IsSet == false`, i.e. no `AudioClip`/addressable) → logs `"No valid clip is set in [...]"` and returns null. Reference-null and unset are separate checks with distinct messages. Single used to accept the unset case and return it, deferring the failure to playback; that was fixed (TEST_FINDINGS #6) so it now matches `Sequence`/`Shuffle`.
- **Regression risk**: low — the strategy is trivial; the null-vs-unset distinction is the only subtlety.

### Sequence mode (default) cycles clips 0..N-1 and wraps

- **Observable**: repeated `PickNewClip()` / `SelectClip` calls return clips[0],[1],...,[N-1],[0],... via `out index`.
- **Setup**: `MulticlipsPlayMode = Sequence`; `Clips` array with 2+ set entries.
- **Timing class**: EditMode-pure (strategy) or DSP/frame if driven through repeated `BroAudio.Play(id)` calls.
- **Edge cases**: a single-clip array stays on index 0 every call (`nextIndex >= clips.Length ? 0 : nextIndex` with Length 1 → always 0). An unset clip at the computed `nextIndex` logs `GetNoValidClipMessage` and resets `_sequenceIndex = -1` (so the *next* call restarts the scan from `clips[0]` if it's set, not necessarily from where it left off) — worth a dedicated edge-case test with a hole in the middle of the array.
- **Regression risk**: medium — the wrap arithmetic and the "clips[0].IsSet" bootstrap branch (`_sequenceIndex == -1` and first call) are easy to get subtly wrong (off-by-one on wrap, or reordering after a Reset).

### Sequence mode: named SequenceIds run independent cursors

- **Observable**: `ClipSelectionContext.SequenceId` set to different strings on the same entity yields independent index progressions per id, tracked in `SequenceClipStrategy._namedSequenceIndices` (a `Dictionary<string,int>`).
- **Setup**: same entity; vary `context.SequenceId` (EditMode) or chain `.SetSequenceId("foo")` on the returned `IAudioPlayer` before the frame's `LateUpdate` drains the play queue (`AudioPlayer.cs` forwards to `_pref.SetSequenceId(...)`, which no-ops outside Sequence mode; the clip is picked lazily in the play coroutine at `AudioPlayer.Playback.cs:76`, so the call must land before that coroutine runs — i.e. chained in the same statement as `BroAudio.Play`).
- **Edge cases**: `SequenceId == null` explicitly routes to the *default* (non-named) cursor, not a named entry called `"null"` — `SequenceClipStrategy.cs:28` checks `context.SequenceId != null`. `Reset(sequenceId: null)` resets the default cursor; `Reset(sequenceId: "x")` only removes `"x"`'s dictionary entry.
- **Regression risk**: medium — two independent pieces of mutable state (`_sequenceIndex` and the dictionary) must stay behaviorally identical; a fix to one path alone is a classic regression source.

### Random mode: uniform when all Weights are 0, weighted otherwise

- **Observable**: `RandomClipStrategy.SelectClip` return distribution over many calls (EditMode, statistical test) or `out index` counts.
- **Setup**: `MulticlipsPlayMode = Random`; vary each `Clips[i].Weight` (public field on `BroAudioClip`, no reflection needed — `entity.Clips[i].Weight = n`).
- **Timing class**: EditMode-pure.
- **Edge cases**: all weights 0 → `Random.Range(0, clips.Length)`, uniform (`RandomClipStrategy.cs:22-27`). Any nonzero weight anywhere → switches to weighted mode for *all* clips, including the ones left at 0 (they become unreachable, `targetWeight < sum` never true for a 0-width bucket). Single-clip array always returns that clip regardless of Weight.
- **Regression risk**: medium — statistical, so a broken weighting is easy to ship without a flaky-enough test catching it; a test should assert bucket boundaries deterministically (e.g. seed `UnityEngine.Random` or assert index 0 never appears when its Weight is forced to 0 among nonzero peers) rather than trust raw frequency counts.

### Shuffle mode never repeats the immediately-previous clip, and cycles the full pool before allowing any repeat

- **Observable**: `ShuffleClipStrategy.SelectClip` sequence over many calls — assert no two consecutive picks are equal, and every clip appears once before any clip repeats within a full-pool cycle.
- **Setup**: `MulticlipsPlayMode = Shuffle`; 3+ set clips (2-clip case is a dedicated edge case, see below).
- **Timing class**: EditMode-pure.
- **Edge cases**: **single-clip entity** — `Use(clips, index, out result, true)` immediately fails the `checkLastUsed` branch (`result == _lastUsed`) after the first pick, forcing the fallback scan loop; with only one clip the loop's `increment` search still lands back on the same clip (there is no other index to land on), so Shuffle degrades to "always the same clip" without erroring — worth asserting explicitly, since it's a real behavior not an error path. **Two-clip entity** forces strict alternation. The "ran out of unused clips" branch (`Reset()` + remembering `_lastUsed`) is the other cheap-to-miss edge case: assert that after every clip has been used once, the *next* pick still avoids repeating the clip that was just used (mediated by `_lastUsed`, set even through the reset path).
- **Regression risk**: high — this is the most stateful, most branch-heavy strategy in the group (two loops, a `HashSet`, a `_lastUsed` sentinel with a documented "avoid overusing Random when few clips remain" trick at `ShuffleClipStrategy.cs:44-45`); it is also the strategy most likely to be touched by a future "make it truly random" refactor that would quietly break the no-immediate-repeat guarantee.

### Shuffle vs Random: the guarantee Random explicitly does not make

- **Behavior**: Random can return the same clip on consecutive calls (no history tracking at all — `RandomClipStrategy` holds no state, `Reset()` is a no-op); Shuffle cannot except via the `_lastUsed` sentinel across a full-pool reset.
- **Observable**: run N calls on identically configured entities in Random vs Shuffle mode; Random should show consecutive repeats over enough trials, Shuffle should not (barring the single-clip edge case above).
- **Setup / Timing**: same as the two rows above.
- **Regression risk**: medium — this is the contract most likely to be described informally in docs/comments and worth pinning down as an explicit comparison test, not just two separate strategy tests.

### Velocity mode selects by the highest Weight threshold not exceeding the given Velocity

- **Observable**: `VelocityClipStrategy.SelectClip(clips, new ClipSelectionContext(value), out index)` — clips are treated as ascending thresholds; the strategy returns the *last* clip whose `Velocity` (== `Weight`) is `<= context.Value`, or the very last clip if `Value` exceeds every threshold (`VelocityClipStrategy.cs:15-24`).
- **Setup**: `MulticlipsPlayMode = Velocity`; set `Clips[i].Weight` as ascending thresholds (e.g. 0, 40, 80, 127 for a MIDI-style velocity curve). Via a live player: chain `.SetVelocity(n)` on the `IAudioPlayer` before the queue drains — `AudioPlayer.cs:187-191` calls `_pref.SetVelocity(velocity)`, which itself guards `Entity.PlayMode != MulticlipsPlayMode.Velocity` and logs+no-ops otherwise (`PlaybackPreference.cs:128-136`) — a good dedicated edge case.
- **Edge cases**: `Value` below every threshold (including 0 when `Clips[0].Weight > 0`) → the loop's `i == 0 ? 0 : i-1` guard means index 0 is still returned, not an out-of-range/negative index (`VelocityClipStrategy.cs:20`). Non-monotonic Weight ordering (author error) — the linear scan doesn't validate ordering, so behavior degenerates silently; worth one test documenting "no ordering validation exists" rather than assuming it does. `SetVelocity` called on a non-Velocity entity: logs an error, `_contextValue` is left untouched (stays at 0, i.e. whatever `GetContextValue` set for that PlayMode).
- **Regression risk**: medium — the `i == 0 ? 0 : i - 1` boundary is a classic off-by-one and the "no validation of ascending order" behavior is the kind of thing a well-meaning fix could silently "improve" into a breaking change.

### Chained mode maps PlaybackStage directly to a fixed clip index

- **Observable**: `ChainedClipStrategy.SelectClip(clips, new ClipSelectionContext((int)PlaybackStage.Start/Loop/End), out index)` → `index = context.Value - 1`, i.e. Start(1)→clips[0] (intro), Loop(2)→clips[1] (loop body), End(3)→clips[2] (outro). `Math.Max(context.Value - 1, 0)` floors `PlaybackStage.None` (0) to index 0 too (`ChainedClipStrategy.cs:11`).
- **Setup**: `MulticlipsPlayMode = Chained`; 3 clips. `PlaybackPreference`'s private `GetContextValue` seeds a fresh play at `PlaybackStage.Start` automatically (`PlaybackPreference.cs:197-201`) — a first `BroAudio.Play(id)` on a Chained entity always starts at the intro clip without any extra setup.
- **Edge cases**: `clips.Length < 3` (missing the End clip) → `index >= clips.Length` logs `"There's no clip for Chained Play Mode Stage..."` and returns null; this is exactly the situation `PlaybackPreference.CanHandoverToEnd()` guards against separately (`Entity.Clips.Length < (int)PlaybackStage.End`) — the two checks are redundant/overlapping and worth a comment in Conflicts if they ever disagree.
- **Regression risk**: low for the strategy itself (pure arithmetic); the stage *progression* (Start→Loop/End via handover, `AudioPlayer.Playback.cs:315,326-328`) is high-risk but belongs to the looping/handover territory (see project memory `looping-via-handover.md`), not this section — noted here only because the strategy is the cheap, pure half of that story and is fully EditMode-testable in isolation from the handover machinery.

### Localization mode selects the BroAudioClip row whose Locale matches the active locale, then resolves the actual AudioClip out-of-band

- **Observable**: `LocalizationClipStrategy.SelectClip` return value's `.GetAudioClip()`, and `out index` for row identification.
- **Setup**: this strategy is **not** reachable through `AudioEntity.PickNewClip()` without a live `SoundManager` (it constructs `new SoundID(this)` and calls `SoundManager.Instance.TryGetCachedLocalizedClip`, `AudioEntity.cs:56-63`) — but the strategy class itself takes no such dependency. Call `LocalizationClipStrategy.Inject(localizedAudio, entityName, tryGetCachedClip)` directly:
  - `localizedAudio`: a `UnityEngine.Localization.LocalizedAudioClip` with non-empty `TableReference`/`TableEntryReference` — these only need `ReferenceType != Empty` (i.e. a `TableReference` constructed from any non-null name/GUID string), **they do not need to resolve to a real, existing table** (`LocalizationClipStrategy.cs:31-44`, `HasValidLocalizationReferences` in `SoundManager.Localization.cs:260-271` performs the identical shape check).
  - `tryGetCachedClip`: a plain `Func<AudioClip>` — inject a lambda returning a `NewClip()` test double. This is checked *before* the real `LoadAssetAsync()`/`WaitForCompletion()` path (`LocalizationClipStrategy.cs:46-57`), so a non-null return here fully bypasses Addressables/Localization asset resolution.
  - Per-row `Locale` field: `entity.Clips[i].Locale` (public field, `BroAudioClip.Locale`, only compiled under `PACKAGE_LOCALIZATION` — present in this project). Set to a `LocaleIdentifier` matching one of `en` / `ja-JP` / `zh-TW` from `Assets/Localization/`.
  - Swap `UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale` in the test and restore it in teardown.
- **Timing class**: EditMode-pure for the strategy in isolation (per the injection trick above); DSP/frame if exercised through `BroAudio.Play()` on a real `SoundManager`-backed Localization entity.
- **Edge cases**: `_localizedAudio == null` or empty references → logs error, `index = -1`, returns null. `tryGetCachedClip()` returns null *and* the real load also fails/returns null → logs warning, `index = -1`. `SelectedLocale` doesn't match any row's `Locale` → falls through to the `LocalizedBroAudioClipWrapper(resolvedClip)` overload (no `BroAudioClip` backing, `index = 0`, and per-clip playback properties — Volume/Delay/StartPosition/etc. — silently default rather than erroring, per `LocalizedBroAudioClipWrapper.cs:34-39`).
- **Regression risk**: medium — the "no row matches locale, degrade to defaults" fallback is easy to lose sight of in a refactor since it doesn't throw or clearly log at error level (only a warning).

### ChangeClipPerLoop re-picks a clip on every loop iteration

- **Observable**: `IAudioPlayer.CurrentPlayingClip` (or `.AudioSource.clip`) differs across successive loop iterations of the same play, for a Random/Shuffle/Sequence entity with the flag set.
- **Setup**: `TestAudioLibrary.SetPrivateField(entity, "Flags", AudioEntityFlag.ChangeClipPerLoop)` (auto-property `Flags` → backing field `<Flags>k__BackingField`), combined with `Loop = true` or `SeamlessLoop = true`.
- **Timing class**: DSP (driven by the handover mechanism, `AudioPlayer.Playback.cs:326-328`) — this is squarely the "looping via handover" territory documented separately in project memory; flagged here only because the *entity flag itself* and its Random/Shuffle/Sequence interaction is this section's concern.
- **Edge cases**: without the flag, a looping entity keeps playing the *same* clip instance across iterations (no re-pick) — worth a contrasting test.
- **Regression risk**: medium — cross-cutting with handover; a change to either side (flag check or handover scheduling) can break this without either area's own tests noticing.

## Randomization ranges (RandomFlag / pitch & volume jitter)

### RandomFlag.Volume / RandomFlag.Pitch apply ± half-range jitter around the base value; absent flags return the base value exactly

- **Observable**: `entity.GetMasterVolume()` / `entity.GetPitch()` (public API on `IAudioEntity`) — or the underlying `AudioEntity.GetRandomValue(baseValue, flag)` — called many times, asserting results fall within `[base - range/2, base + range/2]` and (statistically) are not always equal to `base` when the flag is set, and are *always* exactly `base` when it is not.
- **Setup**: `MasterVolume`/`Pitch` (auto-properties, backing fields `<MasterVolume>k__BackingField` / `<Pitch>k__BackingField`), `VolumeRandomRange`/`PitchRandomRange` (same pattern), and `RandomFlags` (`<RandomFlags>k__BackingField`) — a `[Flags]` enum, set via `RandomFlag.Volume | RandomFlag.Pitch` to test both, or either alone.
- **Timing class**: EditMode-pure (`GetRandomValue` only touches `UnityEngine.Random`, no SoundManager).
- **Edge cases**: `range == 0` with the flag set → jitter collapses to exactly `base` (half = 0, `Random.Range(-0,0)` always 0) — a legitimate "flag on but no-op" state worth asserting doesn't throw. `RandomFlags == RandomFlag.None` → `GetRandomValue` short-circuits before ever calling `Random.Range` (`AudioEntity.cs:90-93`), so `Pitch`/`Volume` random range values are provably irrelevant when the flag is off — a good "doesn't matter" assertion.
- **Regression risk**: low — small, pure, well-isolated function; the main risk is the `[Flags]` `Contains` extension (project convention forbids `Enum.HasFlag`) silently regressing to boxing/wrong semantics if someone "simplifies" it.

## Playback-group voice limiting and rejection

### MaxPlayableCountRule rejects Play once the concurrent count reaches the configured limit

- **Observable**: `BroAudio.Play(id)` returns `Empty.AudioPlayer` (an inactive, `IsActive == false` player) once the limit is reached, versus a real active player below it. Best proxy: `IAudioPlayer.IsActive` immediately after the call (no frame wait needed — rejection happens synchronously in `SoundManager.IsPlayable`, before the play queue).
- **Setup**: build a `DefaultPlaybackGroup` via `ScriptableObject.CreateInstance<DefaultPlaybackGroup>()`, set its private field `_maxPlayableCount` to a `MaxPlayableCountRule` (implicit `int`→`MaxPlayableCountRule` conversion, so `TestAudioLibrary.SetPrivateField(group, "_maxPlayableCount", (MaxPlayableCountRule)1)` or construct the rule directly), then wire it onto the entity: `TestAudioLibrary.SetPrivateField(entity, "_group", group)`. Track the group with `Track()` for teardown.
- **Timing class**: immediate (rejection) / frame (to confirm the accepted ones actually start).
- **Edge cases**: limit of exactly 1 — second concurrent Play on the same or a *different* `SoundID` sharing the group is rejected (the rule is per-group, not per-entity: `DefaultPlaybackGroup._currentPlayingCount` is one field on the group instance). Limit `<= 0` (including the factory default `-1`, exposed in the inspector as "Infinity") means no limit at all — `_maxPlayableCount <= 0 || _currentPlayingCount < _maxPlayableCount` (`DefaultPlaybackGroup.cs:74-77`). Count decrements via `player.OnEnd(...)`, so a rejected slot frees up only once the *accepted* player actually finishes/stops and its `OnEnd` fires — not merely when playback audibly ends but when `AudioPlayer` recycling runs its `OnEnd` callbacks.
- **Regression risk**: high — voice limiting is a headline feature; the count is a single mutable `int` on a ScriptableObject with no defensive bounds-checking, and the increment/decrement pairing (`OnGetPlayer` vs `OnEnd`) is exactly the kind of thing that leaks under an exception mid-callback or a player recycled without ever completing.

### The voice-limit count increments at enqueue time (OnGetPlayer), not at audible playback start

- **Observable**: two `BroAudio.Play(id)` calls issued in the *same frame*, before `LateUpdate` drains the queue, are both counted immediately — the second is rejected (given limit 1) even though neither has started audible playback yet.
- **Setup**: same as above; issue both `Play()` calls before yielding a frame.
- **Timing class**: immediate — `validator?.OnGetPlayer(player)` runs synchronously inside `SoundManager.IsPlayable` (`SoundManager.Playback.cs:59-61`), before the player is even enqueued.
- **Regression risk**: medium — a plausible "fix" would move the count increment to actual playback start, which would change this contract; worth pinning down explicitly since it's surprising and not obviously documented anywhere else.

### CombFilteringRule rejects a same-ID replay within the configured time window (and needs a live SoundManager)

- **Observable**: two `BroAudio.Play(id)` calls on the same `SoundID` within `_combFilteringTime` seconds — the second one's `IsActive` is false immediately, and (if `_logCombFilteringWarning`) a `Debug.LogWarning` fires.
- **Setup**: `DefaultPlaybackGroup` with `_combFilteringTime` set (`TestAudioLibrary.SetPrivateField(group, "_combFilteringTime", (CombFilteringRule)0.5f)`), wired onto `entity._group`. Requires a live `SoundManager` regardless — the rule reads `SoundManager.Instance.TryGetPreviousPlayerFromCombFilteringPreventer` (`DefaultPlaybackGroup.cs:81`), and `SoundManager._combFilteringPreventer` is populated for **every** play regardless of whether any group uses the rule (`SoundManager.Playback.cs:79-80`, "Whether there's any group implementing this or not, we're tracking it anyway").
- **Edge cases**: **Ignore If Same Frame** (`_ignoreCombFilteringIfSameFrame`) — two same-ID plays enqueued in the same frame are exempted from the rule when true (`isSameFrame` is also true when the previous play is still queued but hasn't executed — `Mathf.Approximately(previousPlayTime, 0f)`, `DefaultPlaybackGroup.cs:99-104`). **Ignore If Distance Is Greater Than** (`_ignoreIfDistanceIsGreaterThan`) — for two *positioned* (non-global) plays farther apart than this distance, the rule is skipped even within the time window; for one global + one positioned play, distance is meaningless and the rule instead ignores based on `_ignoreIfDistanceIsGreaterThan > 0f` alone (`DefaultPlaybackGroup.cs:122-127`) — a genuinely odd asymmetric case worth a dedicated test.
- **Regression risk**: high — most branch-dense rule in the codebase (frame-boundary detection via `TimeExtension.UnscaledCurrentFrameBeganTime`, global-vs-positioned distance logic, an explicit TODO at `DefaultPlaybackGroup.cs:123` about using the AudioListener's position). This is a good candidate for the orchestrator to prioritize.

### A custom IPlayableValidator passed to Play() overrides the entity's own PlaybackGroup entirely

- **Observable**: `BroAudio.Play(id, customValidator)` — `customValidator ?? entity.PlaybackGroup` (`SoundManager.Playback.cs:53`) means a non-null explicit validator is used exclusively; the entity's own `_group` (if any) is never consulted for that call.
- **Setup**: a hand-rolled `IPlayableValidator` test double (trivial to implement — two methods) is the cheapest way to test `SoundManager.IsPlayable`'s branching without touching `PlaybackGroup`/`Rule`/ScriptableObjects at all.
- **Timing class**: immediate.
- **Regression risk**: low — a straightforward `??` — but worth one test since it's the doorway for the whole `IPlayableValidator` extension point mentioned in `Runtime/Interface/IPlayableValidator.cs`'s XML doc ("often used with overriding the PlaybackGroup").

## Decorators: AsBGM / AsDominator

### AsBGM() attaches a MusicPlayer decorator to the player — it is composition, not a subtype swap

- **Observable**: `player.AsBGM()` returns an `IMusicPlayer`; the *same* underlying `AudioPlayer`/`SoundID` continues to satisfy `IAudioPlayer` (they're the same `AudioPlayerInstanceWrapper`, `Wrap(...)` at `AudioPlayerInstanceWrapper.cs:161-164,167-170` always returns `this`). Confirm via `((IAudioPlayer)musicPlayerResult).ID == originalPlayer.ID`.
- **Setup**: any active player from `BroAudio.Play(id)`.
- **Timing class**: immediate (decorator attach is synchronous, no frame wait) — though the entity should be `BroAudioType.Music` or `AlwaysPlayMusicAsBGM` disabled if you don't want the *implicit* auto-attach (see RuntimeSetting section) to interfere with an explicit-attach test.
- **Regression risk**: low for the attach mechanism itself; see the next row for the actually-risky part.

### Calling AsBGM() a second time on the same player returns the same MusicPlayer decorator instance — it does not stack or replace

- **Observable**: reference-equality of two `AsBGM()` calls' *decorator* is not directly exposed through the public `IMusicPlayer` interface (both return the same wrapper), but is observable behaviorally: call `.SetTransition(Transition.Immediate)` then `.AsBGM().SetTransition(Transition.CrossFade)` again — the second call's settings win for the *same* decorator (there's only ever one `MusicPlayer` per `AudioPlayer`, tracked in `_decorators` via `Utility.GetOrCreateDecorator` at `Utility.cs:172-183`, which does a `TryGetDecorator<T>` lookup before creating).
- **Setup**: same player, two `AsBGM()` calls in sequence with different `SetTransition` args between them.
- **Timing class**: immediate.
- **Edge cases**: this is the "distinct case" called out in the task brief — a naive test might expect a *new* `IMusicPlayer` each call, or expect the second call to be a no-op; neither is correct. The correct assertion is "settings from the second call are the ones honored at the next `DoTransition`".
- **Regression risk**: medium — `GetOrCreateDecorator`'s idempotency is easy to break if someone "simplifies" it to always `Add` a new decorator, silently reintroducing duplicate-decorator bugs (duplicate `OnEnd`/transition wiring).

### AsDominator() attaches independently of AsBGM() — a player can carry both decorators simultaneously

- **Observable**: `player.AsBGM()` then separately `player.AsDominator()` (or vice versa) both succeed and both remain queryable via `AudioPlayer.TryGetDecorator<T>` (used internally by `AudioPlayerInstanceWrapper.GetDecorator<T>`, `AudioPlayerInstanceWrapper.cs:178-185`).
- **Setup**: same player, both decorator methods called.
- **Timing class**: immediate. Gated behind `#if !UNITY_WEBGL` in source — irrelevant for Editor Play Mode tests, worth a one-line note only.
- **Regression risk**: low — both live in the same `List<AudioPlayerDecorator>` keyed by type, no interaction between them at attach time.

### AlwaysPlayMusicAsBGM (RuntimeSetting) auto-attaches the BGM decorator for BroAudioType.Music, using the configured default transition

- **Observable**: `BroAudio.Play(musicId)` — without ever calling `.AsBGM()` explicitly — still produces a player for which `MusicPlayer.CurrentBGMPlayer` becomes non-null after a frame, and a second `BroAudio.Play(otherMusicId)` triggers a transition per `Setting.DefaultBGMTransition`/`DefaultBGMTransitionTime` (`SoundManager.Playback.cs:73-76`).
- **Setup**: entity `AudioType = BroAudioType.Music`; `SoundManager.Instance.Setting.AlwaysPlayMusicAsBGM = true` (public field, direct assignment — restored automatically by the base fixture's `JsonUtility.FromJsonOverwrite` snapshot/restore in `BroAudioTestFixture`, no manual cleanup needed).
- **Timing class**: frame (need `LateUpdate` to drain the queue) for `IsPlaying`/`AudioSource` observations; immediate for the decorator attach itself.
- **Edge cases**: setting `AlwaysPlayMusicAsBGM = false` and playing a `BroAudioType.Music` entity — no implicit `AsBGM()`, `MusicPlayer.CurrentBGMPlayer` is untouched by that play. Non-Music `AudioType` entities are never auto-decorated regardless of the setting.
- **Regression risk**: medium — this is a global default that silently changes behavior for an entire `AudioType`; a test asserting the *off* state is as valuable as one asserting the *on* state, since it's easy to only test the (more visible) default-on path.

## Chaining-method fluent API (BroAudioChainingMethod.cs)

### Every chaining method is null-safe and no-ops on a recycled/invalid player

- **Observable**: call any `BroAudioChainingMethod` extension (`.SetVolume`, `.SetPitch`, `.AsBGM`, `.Stop`, etc.) on `Empty.AudioPlayer` or a player after it has fully recycled (post-Stop, past `IsActive == false`) — none throw; most return a non-null but inert wrapper (`?.` operator throughout, e.g. `BroAudioChainingMethod.cs:58-59`).
- **Setup**: `BroAudio.Stop(id, 0f)`, wait for `!player.IsActive`, then invoke chaining methods on the stale reference.
- **Timing class**: immediate.
- **Regression risk**: low — mechanical `?.` forwarding — but a good defensive smoke test given how much of the public API surface funnels through this one file.

### SetVelocity and SetSequenceId are both guarded no-ops outside their own mode

- **Observable**: `.SetVelocity(n)` on a non-Velocity entity logs `Debug.LogError` and leaves `_contextValue` unchanged; `.SetSequenceId("x")` on a non-Sequence entity does the same and leaves `SequenceId` unchanged (both in `PlaybackPreference.cs`). `SequenceId`'s setter is private, so the guard cannot be bypassed.
- **Setup**: entity in `Random`/`Single`/etc. mode; call `.SetVelocity()` and separately `.SetSequenceId()`; expect a logged error from each.
- **Timing class**: immediate.
- **Regression risk**: low — the guards are symmetric now (TEST_FINDINGS #5); the risk is a refactor that drops one of them.

## RuntimeSetting toggles that change Play behavior

RuntimeSetting fields are **public plain fields** (not auto-properties), e.g. `public bool AlwaysPlayMusicAsBGM = true;` — set them directly on `SoundManager.Instance.Setting` in a test, no `TestAudioLibrary.SetPrivateField`/reflection needed. The base fixture already snapshots/restores the whole `RuntimeSetting` via `JsonUtility` per test, so mutating it is safe and requires no manual cleanup.

| Field | Behavior it changes | Notes |
|---|---|---|
| `AlwaysPlayMusicAsBGM` | Auto-attaches `MusicPlayer` decorator to every `BroAudioType.Music` play | see decorator section above |
| `DefaultBGMTransition` / `DefaultBGMTransitionTime` | Transition type/time used by the auto-BGM path above | only applies when `AlwaysPlayMusicAsBGM` is true and a prior BGM is active |
| `DefaultChainedPlayModeLoop` / `DefaultChainedPlayModeTransitionTime` | `AudioEntity.HasLoop()` (parameterless overload) falls back to these when a Chained-mode entity has neither `Loop` nor `SeamlessLoop` set (`AudioEntity.cs:111-116,131-135`) | the 4-arg `HasLoop(out,out,loop,time)` overload takes explicit defaults instead and needs no `SoundManager` — prefer it for EditMode coverage of the fallback logic itself |
| `LogAccessRecycledPlayerWarning` | Whether accessing a recycled `IAudioPlayer` logs a warning (`AudioPlayerInstanceWrapper.LogInstanceIsNull`) | orthogonal to selection but lives in the same settings object; cheap to cover alongside a decorator/group test that already recycles a player |
| `GlobalPlaybackGroup` | Parent fallback for any `PlaybackGroup` whose rule has `_isOverride == false` (`PlaybackGroup.cs:21-37`, `Rule<T>.Initialize`) | edge case only — most rules default `_isOverride = true` and never consult this; a test would need `TestAudioLibrary.SetPrivateField(rule, "_isOverride", false)` on top of setting this field |
| `AutomaticallyLoadAddressableAudioClips` | Gates automatic Addressables preloading on Play (`SoundManager.Playback.cs:94-117`) | **out of scope** — this project's Addressables setup has no audio entries (see Could-not-determine below); the toggle itself is trivially assertable (does/doesn't call `clip.LoadAssetAsync()`) but the "did it actually load" half is not testable here |

## EditMode unit-test candidates

No `SoundManager`, no `AudioEntity`, no ScriptableObject needed — construct the strategy, call `SelectClip`/`Reset` directly against a hand-built `BroAudioClip[]` and a `ClipSelectionContext`:

- `Ami.BroAudio.Runtime.SingleClipStrategy.SelectClip`
- `Ami.BroAudio.Runtime.SequenceClipStrategy.SelectClip` / `.Reset()` / `.Reset(string sequenceId)`
- `Ami.BroAudio.Runtime.RandomClipStrategy.SelectClip`
- `Ami.BroAudio.Runtime.ShuffleClipStrategy.SelectClip` / `.Reset()`
- `Ami.BroAudio.Runtime.VelocityClipStrategy.SelectClip`
- `Ami.BroAudio.Runtime.ChainedClipStrategy.SelectClip` (feed `ClipSelectionContext((int)PlaybackStage.Start/Loop/End)` directly)
- `Ami.BroAudio.Runtime.LocalizationClipStrategy.SelectClip`, after `.Inject(localizedAudio, name, tryGetCachedClip)` with a synthetic non-empty `LocalizedAudioClip` and a lambda-supplied `AudioClip` — see the Localization row above for exactly how to avoid needing a real Addressables/Localization table
- `Ami.BroAudio.Data.AudioEntity.GetRandomValue(float baseValue, float range)` (the static overload) and `AudioEntity.GetRandomValue(baseValue, RandomFlag)` on a `ScriptableObject.CreateInstance<AudioEntity>()` instance with fields set via `TestAudioLibrary.SetPrivateField` — no `SoundManager` needed since this path never touches it
- `Ami.BroAudio.Data.AudioEntity.HasLoop(out LoopType, out float, LoopType chainedDefaultLoop, float chainedDefaultTransitionTime)` — the 4-arg overload, explicit defaults, no `SoundManager.Instance` dependency (the 2-arg overload *does* need one)
- `Ami.BroAudio.Utility.GetRandomValue`, `Utility.ClipListIsNullOrEmpty` — small pure helpers exercised incidentally by the above

## Conflicts observed

- `ChainedClipStrategy.cs:16` (`index < 0 || index >= clips.Length` → error) and `PlaybackPreference.CanHandoverToEnd()`'s `Entity.Clips.Length < (int)PlaybackStage.End` check (`PlaybackPreference.cs:173-174`) are two independent guards against the same "not enough clips for Chained mode" condition, computed differently (one via the strategy at selection time, one via the handover pre-check). They happen to agree today; nothing enforces that they always will if either is edited in isolation.

## Could not determine statically

- Whether `Assets/Localization/` actually has a registered `AssetTable`/`String Table Collection` with an `AudioClip` entry. Reading `Assets/Localization/Localization Settings.asset` directly shows `m_TableCollectionName:` empty in the sampled fields, and no table asset files exist under `Assets/Localization/` (only the three `Locale` assets and the settings asset) — suggesting the *locales* are configured but no actual localized *table* exists yet. This doesn't block testing `LocalizationClipStrategy` itself (the `Inject()` bypass documented above sidesteps it entirely), but it does mean the full `BroAudio.Play()` → `SoundManager` → real resolved `AudioClip` path for `MulticlipsPlayMode.Localization` is untestable as authored today. The orchestrator should confirm this in the live Editor (Window > Asset Management > Localization Tables) rather than trust this file-system read.
- Exact behavior of `PlaybackGroup.Parent`/`GlobalPlaybackGroup` fallback when a rule's `_isOverride` is false and `GlobalPlaybackGroup` is itself null vs. pointing at another `DefaultPlaybackGroup` with its own rules — the code path exists (`PlaybackGroup.cs:21-37`, `Rule<T>.Initialize`) but is a low-traffic edge case not exercised by anything read here; worth a quick live-Editor sanity check before writing tests, since chained-parent resolution (a group's parent's parent) is not obviously covered by the single `_parent` field's logic.
- Whether `AudioEntity.Flags`/`ChangeClipPerLoop` combined with `Chained` mode (both drive `needNewClip` in `AudioPlayer.Playback.cs:326-328` via an `||`) produces any surprising interaction — not traced end-to-end here since it crosses into the handover/looping territory owned by a different section.
---

## Coverage ledger

Status per behavior above, per the runtime plan's Definition of Done. **covered** = the core contract is
pinned by a test; **partial** = pinned for some inputs, with the gap named; **deferred** = no test yet, and
testable; **out of scope** = deliberately not tested, with the reason.

| Behavior | Status | Pinned by |
|---|---|---|
| Single mode always plays clips[0] | covered | `ClipSelectionTests.SelectClip_WithSetClips_AlwaysReturnsFirstClip` plus the null-array, null-reference and unset-clip cases |
| Sequence mode cycles 0..N-1 and wraps | covered | `ClipSelectionTests.SelectClip_Repeatedly_CyclesThroughClipsAndWrapsToStart`, `_WithSingleClip_AlwaysReturnsIndexZero`, `_WithUnsetClipMidSequence_LogsErrorThenRestartsFromZero`, `Reset_RestartsDefaultSequenceFromZero` |
| Sequence mode: named SequenceIds run independent cursors | covered | `ClipSelectionTests.SelectClip_WithTwoSequenceIds_AdvancesIndependently`, `_WithNullSequenceId_SharesDefaultCursor`, `Reset_WithSequenceId_OnlyResetsThatNamedCursor` |
| Random mode: uniform when all Weights are 0, weighted otherwise | covered | `ClipSelectionTests.SelectClip_WithAllWeightsZero_ReturnsIndexWithinRange`, `_WithAnyNonzeroWeight_NeverSelectsZeroWeightClips` |
| Shuffle never repeats the previous clip, and cycles the pool | covered (as a finding) | `ClipSelectionTests.SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent` — the test pins the actual behavior, which contradicts the documented intent; see TEST_FINDINGS #9 |
| Shuffle vs Random: the guarantee Random does not make | covered | Same pair, plus `SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip` |
| Velocity mode selects by highest Weight threshold not exceeded | covered | `ClipSelectionTests.SelectClip_WithValueBelowEveryThreshold_*`, `_WithValueBetweenThresholds_*`, `_WithValueAboveEveryThreshold_*`, `_WithNonMonotonicWeights_*` |
| Chained mode maps PlaybackStage to a fixed clip index | covered | `ClipSelectionTests.SelectClip_AtStartStage/AtLoopStage/AtEndStage/AtNoneStage_*`, `_WithTooFewClipsForStage_*` |
| Localization mode selects the row matching the active locale | covered | `LocalizationClipStrategyTests` (4 tests, behind `PACKAGE_LOCALIZATION`) |
| ChangeClipPerLoop re-picks a clip on every loop iteration | deferred | No test sets `ChangeClipPerLoop`. `LoopHandoverTests` covers the seam itself but always with one clip. |
| RandomFlag.Volume / RandomFlag.Pitch apply ± half-range jitter | covered | `ClipSelectionTests.GetRandomValueStatic_*` and `GetRandomValue_*` (5 tests) |
| MaxPlayableCountRule rejects Play at the limit | covered | `PlaybackGroupTests.Play_BeyondMaxPlayableCount_RejectsThenAcceptsAfterASlotFrees` |
| The voice-limit count increments at enqueue, not at audible start | covered | `PlaybackGroupTests.Play_TwoPlaysInSameFrame_BothCountAgainstLimitBeforeEitherStartsPlaying` |
| CombFilteringRule rejects a same-ID replay in the window | covered | `PlaybackGroupTests.Play_SameID_WithinCombFilteringWindow_RejectsSecond` and the three flag/position variants |
| A custom IPlayableValidator overrides the entity's PlaybackGroup | covered | `PlaybackGroupTests.Play_WithCustomValidator_OverridesGroupEntirely` |
| AsBGM() attaches a MusicPlayer decorator — composition, not a subtype swap | covered | `SelectionStateAndDecoratorTests.AsBGM_CalledTwice_ReturnsTheSameMusicPlayerDecoratorInstance` (asserts via the private `_decorators` list) |
| Calling AsBGM() twice returns the same decorator instance | covered | Same test |
| AsDominator() attaches independently of AsBGM() | covered | `SelectionStateAndDecoratorTests.AsBGM_AndAsDominator_CoexistOnTheSamePlayer` |
| AlwaysPlayMusicAsBGM auto-attaches the BGM decorator | covered | `SchedulingAndMusicTests.AlwaysPlayMusicAsBGM_Enabled_*` and `_Disabled_*` |
| Every chaining method is null-safe on a recycled/invalid player | partial | `SelectionStateAndDecoratorTests.AudioSource_AccessedAfterRecycle_*` and `PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal` cover the stale-handle path; the full fluent surface is not swept method by method. |
| SetVelocity and SetSequenceId are guarded no-ops outside their own mode | covered | `SelectionStateAndDecoratorTests.SetVelocity_CalledBeforeQueueDrains_*`, `SetSequenceId_WithDifferentIds_*`; the wrong-mode guard itself was the subject of commit `42fd0644` |
| RuntimeSetting toggles that change Play behavior | partial | `AlwaysPlayMusicAsBGM` is covered above. `DefaultAudioPlayerPoolSize` is **out of scope** — it is read once at `SoundManager` bootstrap, which the persistent singleton passes before any test runs. |

"EditMode unit-test candidates", "Conflicts observed" and "Could not determine statically" elsewhere in this file
are research notes, not behaviors, and carry no status.
