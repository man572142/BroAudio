# Time-dependent behavior

Scope: everything in `AudioPlayer.Playback.cs`, `AudioPlayer.Scheduling.cs`, `FaderModule.cs`, `FadeData.cs`,
`PlaybackPreference.cs`, `PlaybackHandoverData.cs`, `MusicPlayer.cs`, `SoundManager.Playback.cs`, `ISchedulable.cs`.

Three constraints apply to every row below and are not repeated per-row:
1. `BroAudio.Play()` only enqueues; a test must yield at least one frame (`SoundManager.LateUpdate`) before the AudioSource reflects anything.
2. `AudioMixer.SetFloat` is unreliable on the first Play Mode frame; `BroAudioTestFixture` already clears it in `[UnitySetUp]`.
3. DSP time (`AudioSettings.dspTime`) and frame/coroutine time (`Time.deltaTime` / `Time.unscaledDeltaTime` via `Utility.GetDeltaTime()`, selected by `Setting.UpdateMode`) are two different clocks that this code deliberately mixes — see "Fade in / fade out" and "Seamless loop handover seam" below, where a wait gate runs on one clock and the fade itself runs on the other.

Legend for **Clock**: DSP = `AudioSettings.dspTime`; Frame = `Time.deltaTime`/`Time.unscaledDeltaTime` accumulated in a coroutine.

---

## Fade in — from clip's own FadeIn setting

| | |
|---|---|
| Behavior | A clip with `FadeIn > 0` and no explicit override ramps clip volume from 0 to target over that duration on every fresh `Play()`. |
| Observable | Poll `IAudioPlayer.AudioSource` — there is no direct public "clip volume" getter, so the best public-API proxy is the mixer's clip-volume parameter (internal `_clipVolume.Current`) or, more reliably, sample `AudioSource.volume`/mixer send over several frames after Play and assert monotonic increase toward target. `IAudioPlayer.OnUpdate` fires every frame the fade coroutine runs — a test can hook it to sample count and shape. |
| Clock | Frame (coroutine `_clipVolume.Fade` in `FaderModule.Update()`, `AudioPlayer.Playback.cs:174-181`). Tolerance: allow ±2 frames around expected completion; `Mathf.Lerp` + `Ease` is not sample-accurate. |
| Edge cases | `FadeIn == 0` (`FadeData.Immediate`) skips the wait entirely (`TryGetFadeIn` returns false, `Playback.cs:174`); fade-in longer than the clip itself (fade never completes before playback ends — no guard against this in source); resume-from-pause skips fade-in entirely (`isResuming` branch, `Playback.cs:103-117`, never re-enters the fade-in block because `HasStartedPlaying` is already true — confirm by reading the whole `PlayControl` flow, the fade-in block at 174 runs unconditionally after `StartPlaying()` but `_clipVolume` is already at target after a resume so `TryGetFadeIn` should still be evaluated — **flagged below as "could not determine statically"**). |
| Regression risk | High — fade-in is on the hottest path (every `Play()`) and the volume math changed recently per commit history; a broken ease/duration is silent (no exception, just wrong-sounding audio). |

## Fade in — explicit override argument

| | |
|---|---|
| Behavior | `BroAudio.Play(id, fadeIn)` (or `IAudioPlayer.SetFadeInEase` + a later explicit fade) overrides the clip's own FadeIn for exactly one play, then reverts to clip default. |
| Observable | Same proxy as above; additionally compare two consecutive plays of the same `SoundID` — first with override, second without — and assert the second play's ramp duration matches the clip's own `FadeIn`, not the override. |
| Clock | Frame — same fader loop. |
| Edge cases | Override of `0` forces immediate (no fade) even if clip has `FadeIn > 0`; override negative (`FadeData.UseClipSetting = -1`) explicitly falls back to clip setting — this is the sentinel, not a bug, per `FadeData.cs:9,33`. `SetNextFadeIn` sets `_fadeInData.Next`; `TryGetOrConsumeOverride` consumes it exactly once (`FadeData.cs:35-48`), so a second play without a fresh override reverts to base/clip. |
| Regression risk | Medium — the one-shot consume semantics are easy to break by moving where `SetNextFadeIn` is called relative to `PickNewClip`/handover copy. |

## Fade in — easing curve (`SetFadeInEase`)

| | |
|---|---|
| Behavior | `IAudioPlayer.SetFadeInEase(Ease)` changes the interpolation curve, not just linear lerp — e.g. `EaseOutCubic` should reach ~target faster than midpoint of a linear fade. |
| Observable | Sample `OnUpdate` volume proxy at t = 25%/50%/75% of the fade duration and compare shape against `Mathf.Lerp(...).SetEase(ease)` computed independently in the test (the extension is public, `Ami.Extension.EaseExtension`). |
| Clock | Frame. |
| Edge cases | Ease set mid-fade (after `Fade()` already started) — `BeginFade` resets `_elapsedTime` only when `Fade()` is called again, so `SetFadeInEase` before the *next* `Play()` is the only guaranteed application point; changing it mid-fade of the current play has no defined effect in this code path. |
| Regression risk | Low — cosmetic; a wrong curve is not a functional break, just an audibly different fade shape. |

## Fade out — from clip's own FadeOut setting, natural end

| | |
|---|---|
| Behavior | A non-looping clip with `FadeOut > 0` ramps to 0 over that duration ending exactly at `_playbackEndDspTime`. |
| Observable | `OnUpdate` volume proxy; assert the ramp starts at `_playbackEndDspTime - fadeOut` (poll `AudioSettings.dspTime` alongside) and volume reaches ~0 by end. `IAudioPlayer.IsActive`/`IsPlaying` transition to false shortly after (`EndPlaying()` at `Playback.cs:229`). |
| Clock | Mixed: the *wait-to-start-fading* gate is DSP (`while (AudioSettings.dspTime < _playbackEndDspTime - fadeOut)`, `Playback.cs:196`), but the fade's own progress is Frame (`_elapsedTime += Utility.GetDeltaTime()`). Under a frame-rate hitch or `Time.timeScale` change these clocks diverge — this is the single most important row for tolerance design; allow a window of at least one hitch (e.g. 100ms) around the fade-out start boundary. |
| Edge cases | `FadeOut` longer than remaining clip duration — the wait condition is immediately false, fade starts right after Play essentially overlapping the whole clip; `FadeOut == 0` skips straight to end-of-clip wait (`Playback.cs:219-228`, no `TryGetFadeOut`); `Stop()` called mid-natural-fade-out (see "Stop-with-fade" below) races this coroutine via `RestartCoroutine` on `_playbackControlCoroutine` — one cancels the other. |
| Regression risk | High — same "hot path, silent failure" reasoning as fade-in, compounded by the two-clock mix. |

## Fade out — explicit `Stop(fadeOut)` override

| | |
|---|---|
| Behavior | `BroAudio.Stop(id, fadeOut)` or `IAudioStoppable.Stop(fadeOut)` fades out over the given duration regardless of clip's own FadeOut, then deactivates the player. |
| Observable | Public API: call `Stop`, then poll `IAudioPlayer.IsActive` (should stay true until fade completes) and `IsPlaying`; volume proxy for the ramp itself. |
| Clock | Frame (`StopControl` coroutine, `Playback.cs:498-524`). No DSP wait gate here — the fade starts immediately on `Stop()`, unlike the natural end-of-clip case above. |
| Edge cases | `Stop(0)` = `FadeData.Immediate` → no fade, instant `EndPlaying()`; calling `Stop` again while `IsStopping` is already true is a no-op unless the new fade is `0` (`Playback.cs:443-446`, an explicit immediate-stop always wins over an in-flight fade-out); `Stop` during a seamless-loop handover interacts with `_nextPlayer` cleanup (`Playback.cs:487-495`) — see "Stop pre-empts pending handover" below. |
| Regression risk | High — `Stop` is the most-called release verb and the `IsStopping` re-entrancy guard at line 443 is subtle (compare `Mathf.Approximately(overrideFade, FadeData.Immediate)`). |

## Fade out easing (`SetFadeOutEase`)

Same shape as fade-in easing above — mirror the observable/clock/edge cases, applied to `_fadeOutData`/`StopControl`. Regression risk: Low (cosmetic).

## Clip StartPosition (trim from the front)

| | |
|---|---|
| Behavior | `BroAudioClip.StartPosition` (seconds) makes playback begin partway into the underlying `AudioClip` instead of sample 0. |
| Observable | Public: `IAudioPlayer.AudioSource.timeSamples` immediately after the player becomes `IsPlaying` should equal `GetSample(audioClip.frequency, clip.StartPosition)` (`Playback.cs:113`). This is a legitimate use of AudioSource state per the "Unity runtime state" preference ranking. |
| Clock | Frame (one yield after Play to let LateUpdate drain the queue), no DSP dependency for this one value. |
| Edge cases | `StartPosition` beyond clip length (unclamped in source — `GetSample` just multiplies; a runaway value could set `timeSamples` past `audioClip.samples`, engine behavior undefined here — flagged below); `StartPosition == 0` (default, no-op). Interacts with `EndPosition` via `GetPlayableDuration` (see next row) — the two are NOT symmetric: `StartPosition` shifts the read head, `EndPosition` shortens the computed duration, both by straight subtraction (`Utility.cs:59-63`: `audioClip.GetPreciseLength() - clip.StartPosition - clip.EndPosition`). |
| Regression risk | Medium — used by "intro-skip" style content; a broken offset is audible but not exception-throwing. |

## Clip EndPosition (trim from the back)

| | |
|---|---|
| Behavior | `BroAudioClip.EndPosition` (seconds, counted back from the clip's natural end) shortens both the derived-from-clip scheduled end time and the sample count used to recompute remaining time after a pitch change. |
| Observable | Public: total measured playback duration (`Play` timestamp to `IsPlaying` becoming false) should equal `clip.length - StartPosition - EndPosition` (adjusted for pitch — see `PitchAdjusted`, `Playback.cs:382-383`). Internal cross-check if needed: `AudioPlayer.Scheduling.cs:104`, `endSample = audioClip.samples - audioClip.GetTimeSample(_clip.EndPosition)`. |
| Clock | DSP for the derived end time (`_playbackEndDspTime`, `ResolveScheduledTiming`, `Playback.cs:280-308`); the boundary test itself should poll on DSP time, not frame count. |
| Edge cases | `EndPosition` large enough that `StartPosition + EndPosition >= clip.length` → non-positive playable duration (`GetPlayableDuration` can go negative; downstream `PitchAdjusted`/wait-loop behavior with a negative or zero `endDspTime` is not obviously guarded — flagged below); interacts with pitch changes mid-play (`RecalculateScheduledEndTime`, `Scheduling.cs:57-126`, recomputes `endSample` from the *current* `EndPosition` every time it's called, so `EndPosition` set once at play-start stays fixed for the life of that player instance — there's no live "SetEndPosition" API to test a change mid-play). |
| Regression risk | Medium — same mechanism error class as StartPosition, but also feeds the loop-handover seam timing (`_playbackEndDspTime` in `ScheduleNextPlayback`), so a EndPosition bug can misalign loop seams too. |

## Clip Delay (per-clip, not per-call)

| | |
|---|---|
| Behavior | `BroAudioClip.Delay > 0` postpones the *start* of audible playback by that many seconds — but only if the caller didn't already set an explicit scheduled start time. |
| Observable | Public: `IAudioPlayer.IsPlaying` becomes true immediately (matches `PlayScheduled`/`PlayDelayed` semantics per the engine-facts doc), but `AudioSource.isPlaying` audibly starts (or `AudioSource.time` starts advancing from 0) only after `Delay` seconds — poll `AudioSource.time > 0` on the DSP clock. |
| Clock | DSP (`SetClipDelayIfNotScheduled`, `Scheduling.cs:203-208`, sets `_pref.ScheduledStartTime = AudioSettings.dspTime + _clip.Delay`, consumed the same way as an explicit schedule from there on). Tolerance: `AudioConstant.MixerWarmUpTime` (0.1s) plus one frame. |
| Edge cases | **Priority conflict, already characterized** (memory: section 28 / commit 7bdc9431): an explicit `ISchedulable.SetScheduledStartTime`/`SetDelay` call BEFORE `Play()` wins outright — `SetClipDelayIfNotScheduled` only fires `if (_pref.ScheduledStartTime <= 0 ...)` (`Scheduling.cs:205`) — clip.Delay is silently dropped in that case, not added on top. `Delay == 0` no-ops. Interacts with loop handover: only the *first* iteration of a loop consults `clip.Delay` (`isFirstLoopIteration` gate in `ResolveScheduledTiming`, `Playback.cs:289-299`) — subsequent loop iterations schedule off `_playbackEndDspTime`, not `clip.Delay`, even if `ChangeClipPerLoop` picks a new clip with its own `Delay`. This last point is a genuine candidate for "Conflicts observed" — see below. |
| Regression risk | High — the priority rule (explicit schedule beats clip.Delay) has already broken once per the project memory; it is exactly the kind of one-line guard that regresses silently. |

## Scheduled start time — `ISchedulable.SetScheduledStartTime` / `SetDelay`

| | |
|---|---|
| Behavior | Sets an absolute (`SetScheduledStartTime`) or relative-to-now (`SetDelay`) DSP start time. Calling it before the player has ever played schedules a fresh `PlayScheduled`; calling it on an already-playing source pauses playback until the new time (a deliberate, documented quirk — see comment `Scheduling.cs:172-173`: "Some might consider this behavior a feature, so it has been left as is"). |
| Observable | Public: `IAudioPlayer.IsPlaying`/`IsActive` and `AudioSource.isPlaying` (both true immediately per engine facts); `AudioSource.time`/`timeSamples` staying at 0 until the scheduled moment; for the re-schedule-while-playing case, observe the source audibly stalling (time stops advancing) until the new dspTime. |
| Clock | DSP throughout (`Scheduling.cs:160-181`). |
| Edge cases | Called with a past/very-near dspTime (`System.Math.Max(_pref.ScheduledStartTime, dspTime)` in `SchedulePlayback`, `Scheduling.cs:22` — clamps to "now", won't schedule into the past); called twice before playback starts — `_secondsUntilScheduledStart` is adjusted by the delta (`Scheduling.cs:162-166`), not reset, so ordering matters; called on an inactive/never-played player triggers `PlayInternal()` directly (`Scheduling.cs:178`) bypassing the normal enqueue-then-LateUpdate path — a test asserting "Play always needs a frame to take effect" would need an exception note for this entry point. |
| Regression risk | Medium — public API (`ISchedulable` is `internal` but reachable through `BroAudioChainingMethod` extension methods), the "pause on reschedule" behavior is intentional per comment but easy to mistake for a bug in review. |

## Scheduled end time — `ISchedulable.SetScheduledEndTime`

| | |
|---|---|
| Behavior | Sets an absolute DSP end time that is NOT rescaled by later pitch changes (an explicit contract, unlike the clip-duration-derived default). |
| Observable | Public: total playback duration measured against the caller's own dspTime target; combine with a mid-play `SetPitch` call and assert the end time did NOT move (contrast with the "derived from clip" case below, which DOES move). |
| Clock | DSP. |
| Edge cases | The flag `_isEndTimeDerivedFromClip = false` (`Scheduling.cs:186`) is what suppresses rescaling — calling `SetScheduledEndTime` and then triggering a loop handover resets that flag on the *next* player to `true` unconditionally (`ReceiveHandover`, `Playback.cs:389`: "Handover end times always come from a clip-duration computation") — so an explicit end-time contract does NOT survive a loop handover; only the player instance it was set on honors it. |
| Regression risk | Medium — the derived-vs-explicit distinction is load-bearing for the pitch-rescale feature and is easy to invert by mis-ordering the flag write relative to the `AudioSource.SetScheduledEndTime` call. |

## Mid-play pitch change rescaling the derived end time

| | |
|---|---|
| Behavior | Changing pitch mid-play (`IAudioPlayer.SetPitch`) on a clip-duration-derived end time recomputes the DSP end time from the actual playhead position (`RecalculateScheduledEndTime`, `Scheduling.cs:57-126`), so speeding up shortens remaining playback and vice versa — and propagates the shift to a pre-spawned next player in a seamless-loop chain (`_nextPlayer?.ShiftScheduledTimes(delta)`, `Scheduling.cs:125`). |
| Observable | Public: measure wall/DSP duration remaining before and after a `SetPitch` call mid-play; for the loop-propagation case, measure the *second* loop iteration's start dspTime and confirm it moved by the same delta. |
| Clock | DSP for the recompute; the recompute itself is triggered synchronously from `SetPitch` (see `AudioPlayer.Pitch.cs:47,94`), not polled. |
| Edge cases | Pitch set to exactly 0 (`Mathf.Approximately(pitch, 0f)`) → recompute is skipped entirely, "playhead frozen" (`Scheduling.cs:77-81`); negative pitch (reverse) → recompute pushes the hardware `SetScheduledEndTime` far into the future and leaves ending to the frame-based `while` loop instead (`Scheduling.cs:83-93`) — the comment admits "there's no API to clear it," so a reverse-then-forward-again pitch sequence is a plausible source of a stale over-long scheduled end; pitch changed while still inside the warm-up/pre-start window uses a different branch (`Scheduling.cs:97-101`) than pitch changed after the playhead has started moving (`Scheduling.cs:102-113`) — both should be covered as distinct edge cases, not one. |
| Regression risk | High — this is dense, multi-branch arithmetic with an explicit "no API to undo this" admission in a comment; a strong candidate for the hardest bugs in this whole area. |

## Plain looping (`LoopType.Loop`)

| | |
|---|---|
| Behavior | A looping clip hands off to a freshly-extracted `AudioPlayer` at (or, with a clip FadeOut, just after) the natural end of each iteration — NOT via `AudioSource.loop`. |
| Observable | Public: track `IAudioPlayer.ID` or a per-instance marker across `OnStart`/`OnEnd` callbacks and DSP timestamps to confirm iteration N+1 starts at/after iteration N's `_playbackEndDspTime`; `AudioSource.loop` should read `false` throughout (confirms the handover mechanism, not the built-in one) — this AudioSource-state check is cheap and worth including explicitly per the memory note that handover (not `.loop`) is the mechanism. |
| Clock | DSP for the seam itself (`BeginHandover`, `Playback.cs:214-217` for the fade-out case, `:227` for the no-fade-out case); the pre-spawn of the next player runs on a separate coroutine gated by DSP too (`ScheduleNextPlayback`, `Playback.cs:310-364`, waits `while (AudioSettings.dspTime < _playbackEndDspTime - seamlessFadeOut - warmUpTime)`, `:333-336`). |
| Edge cases | `ChangeClipPerLoop` entity flag forces a new clip pick each iteration (`needNewClip`, `Playback.cs:326-328`) — the new clip's own StartPosition/EndPosition/Delay then apply, but recall from the Delay row above that `clip.Delay` is only honored on the very first iteration overall, never on a per-loop clip change; `Stop()` called mid-loop cancels the pending handover coroutine and discards any already-pre-spawned `_nextPlayer` (`StopControl`, `Playback.cs:487-495`) — this is the "stop pre-empts pending handover" case, worth its own explicit test. |
| Regression risk | High per project memory — "loops use player handover... source of pause/resume seam NREs." |

## Seamless looping with a transition time (`LoopType.SeamlessLoop`)

| | |
|---|---|
| Behavior | The transition time from `AudioEntity.HasLoop(out _, out transitionTime)` is applied as BOTH the fade-out of the ending player AND the fade-in of the next (`PlaybackPreference.ApplySeamlessFade`, `PlaybackPreference.cs:107-114`), and the handover to the next player happens BEFORE the fade-out starts (`BeginHandover()` is called at `Playback.cs:204`, immediately before the `_clipVolume.Fade(fadeOut, ...)` call at `:208`) — this is the actual crossfade: two players briefly audible at once. |
| Observable | Public: two-player crossfade is only observable by holding references to both the outgoing and the handed-over `IAudioPlayer` (via `OnEnd`/instance-wrapper tracking) and asserting both report `IsPlaying == true` for a window equal to the transition time. |
| Clock | DSP for the seam boundary; Frame for each side's fade ramp (same two-clock mix as "Fade out" above, doubled — both a fade-out and a fade-in are in flight on two different `AudioPlayer` instances' coroutines simultaneously). |
| Edge cases | Transition time longer than the clip itself — `ApplySeamlessFade` sets both `_fadeInData.Base` and `_fadeOutData.Base` to the same `transitionTime` unconditionally, so a transition time exceeding clip duration produces overlapping/negative wait windows in `ScheduleNextPlayback`'s DSP gate (`Playback.cs:333`, `_playbackEndDspTime - seamlessFadeOut - warmUpTime` could be in the past, meaning the `while` loop exits immediately and the handover fires essentially on the same frame as playback start — worth a dedicated test); pause/resume spanning the handover boundary — the highest-risk edge case per project memory (`looping-via-handover.md`), not something this static read can fully characterize (see below). |
| Regression risk | High — explicitly named as a known NRE source in project memory; the two-clock, two-instance nature makes this the single hardest behavior to write a stable test for. |

## Chained playback (`MulticlipsPlayMode.Chained`: intro → loop → outro)

| | |
|---|---|
| Behavior | Plays clip[Start] once, then hands over to clip[Loop] repeatedly, and on `Stop()` hands over one more time to clip[End] instead of fading the loop clip out in place. |
| Observable | Public: track `ChainedModeStage` indirectly via which clip plays (compare `AudioSource.clip` identity across handovers against the three known test clips) and via `OnStart`/`OnEnd` sequencing; `CanHandoverToEnd()` requires `Entity.Clips.Length >= (int)PlaybackStage.End` i.e. at least 3 clips (`PlaybackPreference.cs:173-174`) — a chained entity with fewer than 3 clips should be characterized as "Stop behaves like a normal fade-out, no outro" rather than throwing. |
| Clock | DSP for all handover seams, same mechanism as looping above (`ScheduleNextPlayback` with `isEnd: true` fires synchronously from `StopControl`, `Playback.cs:480`, not gated by the DSP wait loop that normal loop iterations use — the outro handover is immediate on Stop, not scheduled ahead of time). |
| Edge cases | `Stop()` called during the Start-clip (before ever reaching Loop stage) — `ChainedModeStage` stays `Start`; `CanHandoverToEnd` still permits it since stage isn't `End`/`None` (`PlaybackPreference.cs:179`); `Stop()` called twice in quick succession — second call is a no-op while `IsStopping` unless immediate (see Stop-with-fade edge cases). |
| Regression risk | Medium — less commonly used than plain/seamless loop, but the immediate (non-DSP-gated) outro handover is a different code path from every other handover and easy to leave untested. |

## BGM transitions (`Transition` modes via `IMusicPlayer.SetTransition`)

| | |
|---|---|
| Behavior | `AsBGM().SetTransition(Transition.X, stopMode, overrideFade)` controls how the outgoing `MusicPlayer.CurrentBGMPlayer` stops relative to the incoming one starting: `Immediate` (no fades, sequential), `OnlyFadeIn`, `OnlyFadeOut`, `CrossFade` (both fade, overlapping), `Default` (follow clip's own settings, fade-out-then-fade-in, sequential). |
| Observable | Public: `IAudioPlayer.IsPlaying` on both the old and new BGM player instances over time; for `CrossFade`/`Default` the new player's `PlayControl` explicitly waits (`while (musicPlayer.IsWaitingForTransition) yield return null;`, `Playback.cs:146-149`) before `StartPlaying()` — so for the *sequential* transitions (`Default`, `OnlyFadeOut`) the new BGM is provably silent/not-started until the old one finishes stopping; for `CrossFade`/`OnlyFadeIn` there is no such wait, so overlap is expected and correct. |
| Clock | Frame — `IsWaitingForTransition` is a coroutine-driven bool flipped by `FinishTransition` as an `onFinished` callback off the old player's `Stop()` (`MusicPlayer.cs:85`), itself running the Frame-clock fade-out coroutine described above. |
| Edge cases | `NeedTransition == false` (no prior BGM, or same instance replaying, or prior player already gone) skips the whole transition machinery and sets `CurrentBGMPlayer = Instance` directly (`MusicPlayer.cs:69-74`) — this is the "first BGM ever" and "same clip replayed" cases, both worth their own test; `Transition.Immediate` stops the old BGM with `fadeOut = 0` (immediate) regardless of clip FadeOut settings (`StopCurrentPlayer`, `MusicPlayer.cs:117-119`). |
| Regression risk | High — BGM transitions are a headline feature; the sequential-vs-overlapping distinction between transition modes is exactly the kind of thing a lazy refactor could silently invert. |

## `AlwaysPlayMusicAsBGM` (RuntimeSetting)

| | |
|---|---|
| Behavior | When `Setting.AlwaysPlayMusicAsBGM` is true (default), every `Play()` of a `BroAudioType.Music` sound is auto-wrapped with `AsBGM().SetTransition(Setting.DefaultBGMTransition, Setting.DefaultBGMTransitionTime)` even if the caller never called `AsBGM()` (`SoundManager.Playback.cs:73-76`). |
| Observable | Public: play two `Music`-typed sounds back to back without ever calling `AsBGM()` and confirm the second still triggers a transition (old one stops per `Setting.DefaultBGMTransition`) rather than both playing concurrently; toggle the setting off (it's a plain field on the `RuntimeSetting` ScriptableObject, restorable via the fixture's snapshot/restore already built in `BroAudioTestFixture.BroAudioSetUp`/`TearDown`) and confirm two Music plays now overlap freely. |
| Clock | Same as "BGM transitions" above — this setting only decides *whether* that machinery engages, not its timing. |
| Edge cases | Setting flips mid-test — only affects plays that happen after the flip, not an already-decorated in-flight player; non-`Music` audio types are never affected regardless of the setting. |
| Regression risk | Medium — a global default that quietly changes behavior for every Music play in every other test in this suite if a test forgets to restore it (the fixture already guards this via JSON snapshot/restore, so a test-writer's own risk is "don't bypass the fixture," not "the setting itself is fragile"). |

## `OnBGMChanged` event

| | |
|---|---|
| Behavior | `MusicPlayer.OnBGMChanged` (static, internal) fires exactly once per actual change of `CurrentBGMPlayer`, passing the new player's instance wrapper (or `null` when BGM stops with nothing replacing it). |
| Observable | Internal-only — there is no public re-exposure of this event found in the runtime source searched; a test would need reflection (`typeof(MusicPlayer).GetEvent("OnBGMChanged", ...)` or a `static` field probe) to subscribe, matching the existing reflection-based test-helper pattern in the harness. Flagged under "Could not determine statically" for whether any public wrapper exists elsewhere (e.g. on `BroAudio` facade) — worth a grep in the live Editor/IDE if not found by more searching. |
| Clock | Synchronous — fires immediately inside the `CurrentBGMPlayer` setter (`MusicPlayer.cs:14-23`), no coroutine/DSP involvement itself, only the surrounding transition logic that decides *when* the setter is called is time-dependent (see BGM transitions above). |
| Edge cases | Setting `CurrentBGMPlayer` to the same instance it already is → event does NOT fire (`if (_currentBGMPlayer != value)` guard, `:16`); `UpdateInstance` during a loop/chain handover deliberately writes the backing field directly to skip the event (`MusicPlayer.cs:52-56`, comment: "the logical BGM hasn't changed") — this is an explicit, documented case where a handover does NOT re-fire `OnBGMChanged` even though the underlying `AudioPlayer` instance changed; a naive test asserting "event fires on every instance change" would be wrong here. |
| Regression risk | Medium — low usage surface (internal/static), but the "skip on handover" exception is subtle and worth protecting explicitly since it is exactly the kind of guard a refactor forgets to preserve. |

## Stop with fade — general

Covered above under "Fade out — explicit `Stop(fadeOut)` override" for the ramp itself. Additional cross-cutting facts:

| | |
|---|---|
| Behavior | `Stop()` during an in-flight *natural* fade-out (from reaching end-of-clip) does not double-fade; `StopControl` (`Playback.cs:471`) inspects whether `_clipVolume.IsFadingOut` is already true and, if there's no explicit override and no end-handover just happened, simply waits out the existing fade rather than restarting one (`Playback.cs:504-514`). |
| Observable | Public: call `Stop()` with no explicit fade argument right as a clip nears its natural end-of-clip fade-out; total time-to-silence should match the clip's own `FadeOut`, not `FadeOut` + a second full fade cycle. |
| Clock | Frame, same coroutine reused (not restarted) — `RestartCoroutine` on `_playbackControlCoroutine` in `Stop()` (`Playback.cs:468`) does cancel-and-restart the *outer* `PlayControl`/`StopControl` coroutine, but the *inner* `_clipVolume` fade itself is a separate object (`Fader`) whose own coroutine is untouched unless `_clipVolume.Fade(...)` is called again — hence the "don't double-fade" check is necessary and meaningful. |
| Edge cases | `Stop()` with an explicit override fade WHILE a natural fade-out is in flight — `hasExplicitOverride` forces a fresh `_clipVolume.Fade` with the new duration (`Playback.cs:499,515-523`), overriding rather than waiting; `Stop()` immediately after an end-of-chain or end-of-loop handover already fired — `didHandoverToEnd` suppresses passing `_onUpdate` to the fade call to avoid a stale-callback invariant violation noted in a comment referencing commit `ce15a806` (`Playback.cs:502-503,518`). |
| Regression risk | High — this re-entrancy/don't-double-fade logic is exactly the kind of "characterize, don't fix" territory: it is subtle, comment-heavy, and cites a specific prior commit as the reason it exists. |

## Pause across a handover seam

| | |
|---|---|
| Behavior | Pausing a player mid-clip freezes `AudioSource`; per project memory this is a known source of NullReferenceExceptions when a pause/resume straddles a loop or chain handover boundary. |
| Observable | Public: `IAudioPlayer.IsPlaying`/`AudioSource.isPlaying` around a `Pause()` → wait past where a handover would have fired → `UnPause()`, watching for exceptions in the Editor console (Unity swallows some coroutine exceptions silently — a test should assert no `LogAssert.Expect`-worthy error was logged, using `UnityEngine.TestTools.LogAssert`). |
| Clock | DSP for the handover timing itself, Frame for pause-duration bookkeeping is NOT used — `_pauseDspTime` is a DSP timestamp (`Stop`, `Playback.cs:453,533`) and `RebaseScheduleAfterPause` (`Scheduling.cs:41-52`) slides schedules by DSP delta. |
| Edge cases | This is exactly the scenario the project memory file `looping-via-handover.md` documents in depth — **could not fully re-derive the NRE mechanism from this read alone**; the static code shows `CanHandoverToLoop()` explicitly guards `Entity == null` for "a default/reset pref... belongs to a torn-down player" reachable "when pause/resume lets a recycled player fall into BeginHandover" (`PlaybackPreference.cs:152-157`), which is a real guard against one manifestation, but whether all manifestations are now guarded is not something a static read can confirm. |
| Regression risk | High — named explicitly in project memory as a live risk area; treat as the top candidate for a dedicated, carefully-sequenced test (pause exactly at the DSP instant a handover coroutine would fire). |

---

## Inherently flaky candidates and stabilization notes

- **Fade-out DSP-gate vs. frame-clock fade duration** (both plain and seamless loop): a wait gated on `AudioSettings.dspTime` followed by a ramp measured in `Time.deltaTime` means a single frame hitch during the gate can shift when the ramp starts relative to wall-clock, while the ramp's own duration is frame-accurate. Stabilize with clips long enough (≥2-3s) that a hitch is a small fraction of total duration, and assert ranges/thresholds ("volume is below X by dspTime Y") rather than exact sample equality.
- **Seamless loop crossfade with transition time ≥ clip length**: the DSP wait window in `ScheduleNextPlayback` can be negative, making the pre-spawn/handover timing collapse to "immediately." Use a deliberately short clip and a transition time deliberately longer than it as an explicit edge-case test rather than trying to avoid the condition — the current behavior (immediate handover) is itself worth locking in as a characterization.
- **Mid-play pitch change with negative pitch, then reversed back positive**: comment at `Scheduling.cs:83-93` admits there's no API to un-set an over-long `SetScheduledEndTime`. A test here is not really flaky so much as needing an explicit multi-step pitch sequence and a generous upper-bound timeout (`WaitUntilOrTimeout`) rather than an exact-time assertion.
- **Pause exactly at a handover boundary**: to make this deterministic rather than a race, drive it from `AudioSettings.dspTime` proximity (poll until within N ms of the expected seam, per `WaitDspSeconds`) rather than a fixed real-time delay, and use a clip/transition-time combination long enough to give a wide window to land the `Pause()` call inside.
- **`OnBGMChanged` reflection access**: not flaky per se, but brittle to rename; if a test is written against it, keep the reflection lookup isolated in one helper (mirroring the harness's existing reflection helpers) so a rename only breaks one place.

## Conflicts observed

- **`clip.Delay` on subsequent loop iterations with `ChangeClipPerLoop`**: `ResolveScheduledTiming` only consults `clip.Delay` via `SetClipDelayIfNotScheduled` on the very first loop iteration (gated by `isFirstLoopIteration`, `Playback.cs:289`); `SetClipDelayIfNotScheduled` itself is called unconditionally at the top of `PlayControl` (`Playback.cs:83`) for every handover, including subsequent loop iterations — so it's not obviously true that later iterations ignore a newly-picked clip's `Delay`. This needs a closer read of interaction between `SetClipDelayIfNotScheduled` (per-play) and `ResolveScheduledTiming`'s `isFirstLoopIteration` gate (per-schedule) than a static pass can fully resolve — flagged for live-Editor probing rather than asserted as a definite bug.
- **Fade-in on resume-from-pause**: `PlayControl`'s fade-in block (`Playback.cs:174-181`) is not visibly skipped for the `isResuming` case, yet a resumed player's clip volume should already be at its pre-pause value, not 0. Whether `TryGetFadeIn` naturally returns false here (because no override was set and `_clipVolume` is already at target) or whether resume genuinely re-triggers a fade-in was not something this static read could settle — listed again below.

## Could not determine statically

- Whether `Stop(fadeOut) → UnPause()` (pause requested mid-fade-out) is a supported/characterized sequence, or whether `IsStopping` blocks it entirely — needs a live trace.
- Whether resuming from `Pause` (`IAudioStoppable.UnPause`) re-runs the fade-in block in `PlayControl` (see "Conflicts observed" above) — needs either a live Editor probe (breakpoint/log on `TryGetFadeIn` during a resume) or a quick throwaway PlayMode script, since `_stopMode`/`HasStartedPlaying` state at that point in the coroutine is not fully traceable from source alone.
- Exact interaction of `clip.Delay` with `ChangeClipPerLoop` across loop iterations beyond the first (see "Conflicts observed").
- Whether any public (non-internal) surface re-exposes `MusicPlayer.OnBGMChanged` — grepped the runtime source and found none, but the search was not exhaustive of generated/partial files outside the ones listed in scope.
- ~~`SeamlessLoopHelper.cs` is entirely commented out.~~ **Resolved** (TEST_FINDINGS #4): the dead file has been deleted. The seamless-loop mechanism lives in `AudioPlayer.Playback.cs`/`AudioPlayer.Scheduling.cs`, as it always did.
- Whether `AudioSource.SetScheduledStartTime`'s "pause current playback until dspTime" side effect (the documented `Scheduling.cs:172-173` quirk) actually fires the `_onPaused` callback or any pause-related state — the comment describes engine behavior but the surrounding BroAudio code doesn't visibly hook `_stopMode`/`_onPaused` for this path, meaning a test asserting "player looks paused" via `IAudioPlayer` state (as opposed to raw `AudioSource.isPlaying` timing) might not have anything to observe. Needs a live check.
