# BroAudio Test Inventory

Ranked plan for the regression suite. Goal: **maximum behavioral confidence per test, minimum test count.**
Coverage percentage is not a goal.

Detail lives in four section files; this file is the index, the ranking, and the coverage ledger.

| Section | File |
|---|---|
| Play / Stop / Pause lifecycle | [inventory/lifecycle.md](inventory/lifecycle.md) |
| Volume, pitch, mixer, effects | [inventory/volume-mixer.md](inventory/volume-mixer.md) |
| Time-dependent behavior | [inventory/time-dependent.md](inventory/time-dependent.md) |
| Selection, policy, decorators | [inventory/selection-policy.md](inventory/selection-policy.md) |

Status values: **covered** · **planned (phase N)** · **deferred** · **out of scope**

---

## Facts settled in the live Editor

Established by probe or grep during ranking; they override anything in the section files that guesses at them.

- **Mixer parameters are exposed as `BroName` expects.** `BroAudioMixer` answers `GetFloat` for `Master` (rests at 0 dB), `Main`, and `Main_Dominated`. Master-volume and dominator assertions are viable at the mixer boundary.
- **Track pools: 36 generic `Track*` groups, 4 `Dominator*` groups.** Forcing generic-pool exhaustion needs 37 concurrent voices — too expensive to be worth it. The dominator pool, at 4, is cheap to exhaust.
- **`EffectParaNameSuffix` is `"_Effect"`**; track names are `Master` / `Track` / `Main` / `Main_Dominated` / `Dominator`.
- **`IAudioStoppable`'s members are all `internal`, but every one is re-exposed** as a public extension method in `BroAudioChainingMethod.cs`. Tests call `Stop`/`Pause`/`UnPause` (including the `Action onFinished` overloads) normally — no reflection, no `InternalsVisibleTo`.
- **`BroAudio.OnBGMChanged` is public** (`Runtime/BroAudio.cs:22`), forwarding to `MusicPlayer.OnBGMChanged`. No reflection needed — but it is a *static* event, so any test that subscribes must unsubscribe.
- **`StopMode.Mute` is reachable** via the public `SetTransition(this IMusicPlayer, Transition, StopMode)` extension. It is a BGM transition mode, not a general stop mode — the lifecycle section's "unreachable" note is wrong on this point.
- **Follow-target tracking is not observable** through `IAudioSourceProxy` — the proxy exposes no `transform`/`gameObject`.
- **`LayeredClipStrategy` is dead code.** Zero references anywhere under `Assets/`, and `MulticlipsPlayMode` has no `Layered` member. Out of scope.
- **Localization has locales but no table.** `Assets/Localization/` holds three `Locale` assets and the settings asset — no `AssetTable`. `LocalizationClipStrategy` is testable in EditMode via `Inject()`; the full `Play()` → `SoundManager` → resolved clip path is not testable as authored.

---

## Tier 0 — EditMode units (phase 2, do first)

Pure functions, no `SoundManager`, no Play Mode, no clocks. This is the cheapest confidence in the whole plan
and it runs in milliseconds.

| # | Behavior | Where | Risk |
|---|---|---|---|
| 0.1 | Clip-selection strategies against a hand-built `BroAudioClip[]` — Single, Sequence (incl. per-`sequenceId` reset), Shuffle (`_lastUsed` sentinel, 1- and 2-clip cases), Random weighting, Velocity boundary (`i == 0 ? 0 : i - 1`), Chained stage dispatch | `Runtime/Utility/ClipSelection/` | High — Shuffle is the most stateful class in the codebase |
| 0.2 | Linear↔dB conversion: `ToDecibel` / `ToNormalizeVolume` round-trip, `allowBoost` branches, `vol == 0` clamp to `MinVolume` | `Ami.Extension.AudioExtension` | High — silently wrong volume everywhere |
| 0.3 | `ClampNormalize` / `ClampDecibel` — pin that their `allowBoost` default is `false` while the conversion helpers' default is `true` | `Ami.Extension.AudioExtension` | Medium — a "harmonize the defaults" refactor is a behavior change |
| 0.4 | `Effect.CompareTo` / `IsMoreIntenseThan` — especially LowPass's deliberate sign inversion — plus `Effect.IsDefault()` and `IsValidFrequency` bounds | `Ami.BroAudio.Effect` | Medium — least obvious code path in the effects system |
| 0.5 | `AudioEntity.GetRandomValue(baseValue, RandomFlag)` and the static range overload; `HasLoop`'s 4-arg overload (the 2-arg one needs a live `SoundManager`) | `Ami.BroAudio.Data.AudioEntity` | Low-medium |
| 0.6 | `LocalizationClipStrategy.SelectClip` after `Inject()` with a lambda-supplied clip — sidesteps the missing AssetTable entirely | `Runtime/Utility/ClipSelection/` | Medium |

`Utility.SliderToVolume` / `BroVolumeToSlider` piecewise mapping is **deferred** — it is editor-slider presentation math, not runtime behavior.

## Tier 1 — Core playback (phase 2)

High risk, low timing complexity. Everything here is frame-clock or immediate.

| # | Behavior | Observable | Risk |
|---|---|---|---|
| 1.1 | **Stale handle after recycle is inert, not fatal.** Hold the `IAudioPlayer` past playback end, then call `Stop`/`SetVolume`/`AsBGM`/`.AudioSource` on it | `IsActive` false, `ID == SoundID.Invalid`, no exception, no effect on the pooled player now serving someone else | **Highest** — a stored handle in a MonoBehaviour is the most common real-world pattern, and a regression here is a live NRE in a shipped game |
| 1.2 | **`Stop(All, 0f)` clears every concrete type.** One player per `BroAudioType`, one call, all deactivate | each handle's `IsActive` → false | **Highest** — the fixture's own teardown depends on this; a bug cascades into false results across the whole suite |
| 1.3 | **Pause freezes in place.** Pause → UnPause preserves the playhead, does not re-fire `OnStart`, does not rewind | `IsActive` stays true across the pause; `IsPlaying` toggles; `AudioSource.timeSamples` unchanged | High — a naive Stop/Play reimplementation violates this silently |
| 1.4 | **`IsActive` vs `IsPlaying` windows.** Queued-but-not-drained (active, not playing) and paused (active, not playing) | both properties, one frame apart | High — explicitly load-bearing per the source doc comments |
| 1.5 | **Rejected `Play` returns an inert `Empty.AudioPlayer`.** Force it with a stub `IPlayableValidator` returning false | returned handle inert; a full fluent chain off it never NREs and never returns null | Medium — silent-failure path |
| 1.6 | **Volume composition.** master (mixer dB, separate stage) vs per-`BroAudioType` vs per-`SoundID` vs `clip.Volume × entity.MasterVolume` (baked into one fader) | `AudioMixer.GetFloat("Master")` for master; `IAudioPlayer.GetVolume()` for the linear product | High — gates all audible output, and the four inputs compose at two different levels |
| 1.7 | **Per-type volume's live/future asymmetry.** Setting a type to exactly `1f` is skipped for *future* plays but pushed to *live* ones | live player's volume vs. a subsequently-played one | Medium — subtle and undocumented |
| 1.8 | **Mixer track acquisition and return.** Every player gets a pooled `AudioMixerGroup`; recycling returns it silenced | `player.AudioSource.outputAudioMixerGroup` non-null and named `Track*` | High — routing is the backbone of every other mixer behavior |
| 1.9 | **Pitch via `AudioSource`**, clamped to `[-3, 3]`, plus the deferred-fade path when `SetPitch` precedes playback | `player.AudioSource.pitch` | High — audible and gameplay-relevant |
| 1.10 | **`PitchShiftingSetting.AudioMixer` is a silent no-op** — characterize as-is (see findings) | `AudioSource.pitch` unchanged after `SetPitch` | High as a *finding*, low as a test — one assertion pinning current behavior |
| 1.11 | **Lifecycle callbacks.** `OnStart` once (not re-fired on resume), `OnUpdate` per frame, `OnPause` per transition, `OnEnd` once with a still-valid `SoundID` | counters incremented from the callbacks | Medium-high — primary integration point for game code |

## Tier 2 — Time-dependent (phase 3)

Everything here needs a clock decision per test. Hand writers `WaitDspSeconds` and `WaitUntilOrTimeout` by name.

| # | Behavior | Clock | Risk |
|---|---|---|---|
| 2.1 | **Pause across a handover seam** — pause a looping player at the DSP instant the handover fires | DSP | **Highest** — named in project memory as a live NRE source |
| 2.2 | **Plain loop via player handover** — a *new* `AudioPlayer` continues the sound; `AudioSource.loop` is never set | DSP | High |
| 2.3 | **Seamless loop crossfade seam** — two instances fading in opposite directions across the boundary | DSP seam, frame ramps | High — hardest behavior in the suite to test stably |
| 2.4 | **`Stop` re-entrancy / don't-double-fade** (`IsStopping`, with `FadeData.Immediate` special-cased through) | frame | High — comment-heavy logic citing a specific prior commit |
| 2.5 | **`clip.Delay` vs explicit schedule priority** — explicit wins | DSP | High — already regressed once |
| 2.6 | **Fade in / fade out durations**, plus the explicit-override argument's one-shot consume semantics | frame ramp, DSP gate on natural end | High — hot path, silent failure |
| 2.7 | **Scheduled start / end time** via `ISchedulable` | DSP | Medium |
| 2.8 | **BGM transitions** (sequential vs overlapping per `Transition` mode), `OnBGMChanged`, `AlwaysPlayMusicAsBGM` | frame | High — headline feature |
| 2.9 | **Mid-play pitch change rescales the derived end time** | DSP recompute, synchronous trigger | High — dense arithmetic with a "no API to undo" admission in-comment |
| 2.10 | **Clip `StartPosition` / `EndPosition` trims** | frame / DSP | Medium |
| 2.11 | **Chained playback** — the outro handover is *not* DSP-gated like every other handover | DSP + immediate | Medium |

## Tier 3 — Selection and policy (phase 4)

| # | Behavior | Setup | Risk |
|---|---|---|---|
| 3.1 | **Voice limiting** rejects past the cap, and the count is decremented on end | Requires an explicitly wired `PlaybackGroup` on `_group` — a code-built entity has none | High — headline feature, single mutable `int`, no bounds check |
| 3.2 | **Voice limiting counts at enqueue, not at audible start** | same | Medium — surprising, undocumented, and a plausible "fix" would change it |
| 3.3 | **Comb-filtering rule** — same-frame and queued-frame detection, global vs positioned asymmetry | wired group | High — most branch-dense rule in the codebase |
| 3.4 | **A custom `IPlayableValidator` overrides the group** | none (validator passed to `Play`) | Low, but it is the doorway for the whole extension point |
| 3.5 | **Selection state is per-`AudioEntity`**, shared across concurrent plays of the same `SoundID`, never auto-reset | none | Medium — cross-cutting, affects nearly every other selection test |
| 3.6 | **`AsBGM()` twice returns the same decorator** (idempotent, not stacked); `AsBGM` + `AsDominator` coexist | none | Medium |
| 3.7 | **`RuntimeSetting` toggles that change `Play` behavior** — assert the *off* state as well as the default-on state | fixture already snapshots/restores the asset | Medium |

## Deferred

Real behaviors, deliberately not covered — cost far exceeds the confidence gained. Revisit only if one regresses in the wild.

| Behavior | Why deferred |
|---|---|
| Generic track-pool exhaustion → unrouted playback fallback | Needs 37 concurrent voices. The behavioral fork is real and high-risk, but the setup cost is out of proportion. *Partial substitute:* exhaust the 4-slot **dominator** pool instead, which exercises the same `AudioTrackObjectPool` null-return path cheaply. |
| Virtual-track release / reacquire | Needs actual voice virtualization past Max Real Voices plus a 0.5s grace period — not deterministically forceable in a small scene |
| WebGL volume path | Needs the `UNITY_WEBGL` define; genuinely separate code, untestable in the Editor |
| `Utility.SliderToVolume` / `BroVolumeToSlider` piecewise math | Editor-slider presentation, not runtime behavior |
| `PlaybackGroup.Parent` / `GlobalPlaybackGroup` multi-level fallback | Low-traffic path; not traced end to end |
| Mid-playback `outputAudioMixerGroup` swap glitch behavior | Engine-level, flagged as unverified even in the engine notes |

## Out of scope

| Behavior | Why |
|---|---|
| `LayeredClipStrategy` | Dead code — no references, no enum member routing to it |
| `SeamlessLoopHelper.cs` | File is entirely commented out; the real mechanism lives in `AudioPlayer.Playback.cs` / `.Scheduling.cs` |
| Full `Play()` → `SoundManager` → localized clip resolution | No `AssetTable` exists in the project. The strategy itself is covered at 0.6 |
| Addressables load / preload / cleanup | Phase 5 — needs the user's go-ahead, since it adds addressable entries to the project |
| Editor windows, inspectors, Library Manager | Explicit anti-goal |

---

## Open questions for a live probe

Cheap to settle in the Editor before the phase they block; none of them gate phase 2.

1. Does resuming from `Pause` re-trigger the fade-in block in `PlayControl`? (blocks 1.3 / 2.6)
2. Is `Stop(fadeOut)` mid-fade interruptible by `UnPause()`, or does `IsStopping` block it? (blocks 2.4)
3. Does `clip.Delay` apply on loop iterations after the first when `ChangeClipPerLoop` picks a new clip? (blocks 2.5)
4. Does `SetScheduledStartTime`'s "pauses playback until dspTime" quirk surface in `IAudioPlayer` state, or only in raw `AudioSource` timing? (blocks 2.7)
5. What are the configured `AudioFilterSlope` and dominator-pool sizes in this project's `RuntimeSetting`? (blocks the deferred-pool substitute)
