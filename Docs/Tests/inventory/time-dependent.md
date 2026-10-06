# Time-dependent behavior

Fades and pitch ramps advance on the frame clock (`Utility.GetDeltaTime()`); start/end schedules, loop seams and handovers run on `AudioSettings.dspTime`. Where one behavior mixes the two, its entry says so.

## Fade in — from clip's own FadeIn setting

- **Behavior**: a clip with `FadeIn > 0` ramps clip volume from 0 to target on every fresh play.
- **Observable**: `IAudioPlayer.GetVolume()` sampled over the fade rises monotonically; `OnUpdate` fires each frame the fade runs.
- **Edge cases**: `FadeIn == 0` skips the fade (`TryGetFadeIn` returns false); a fade longer than the clip never completes (no guard); a resume from pause re-runs the clip fade-in from silence (`SetupClipVolume` and `TryGetFadeIn` run on the `isResuming` path) unless `UnPause(fadeIn)` overrides it.

## Fade in — explicit override argument

- **Behavior**: `BroAudio.Play(id, fadeIn)` overrides the clip's `FadeIn` for that one play.
- **Observable**: play the same ID with an override, then without; the second ramp matches the clip's `FadeIn`.
- **Edge cases**: an override of `0` (`FadeData.Immediate`) forces no fade; a negative override (`FadeData.UseClipSetting`) falls back to the clip. `SetNextFadeIn` stores the override and `TryGetOrConsumeOverride` consumes it once.

## Fade in — easing curve (`SetFadeInEase`)

- **Behavior**: `IAudioPlayer.SetFadeInEase(Ease)` changes the fade's interpolation curve.
- **Observable**: sample at 25/50/75 % of the fade and compare against `Mathf.Lerp(from, to, t.SetEase(ease))` (`Ami.Extension.EaseExtension` is public).
- **Edge cases**: the ease applies when a fade starts, so setting it mid-fade does not reshape the running fade; it shapes only explicit fades (`Play(id, fadeIn)`, `Stop(fadeOut)`) — a clip's authored `FadeIn`/`FadeOut` keep the factory ease.

## Fade out — from clip's own FadeOut setting, natural end

- **Behavior**: a non-looping clip with `FadeOut > 0` ramps to 0, ending at `_playbackEndDspTime`. The wait before the ramp is DSP-gated (`dspTime < _playbackEndDspTime - fadeOut`); the ramp itself is frame-driven.
- **Observable**: volume starts falling about `fadeOut` before the clip's end and reaches about 0 by the end; `IsActive`/`IsPlaying` go false after `EndPlaying()`.
- **Edge cases**: a `FadeOut` longer than the clip starts the ramp right after play; `FadeOut == 0` waits straight for the end; a `Stop()` during the natural fade — see "Stop with fade — general".

## Fade out — explicit `Stop(fadeOut)` override

- **Behavior**: `Stop(id, fadeOut)` or `player.Stop(fadeOut)` fades over the given time regardless of the clip's `FadeOut`, then deactivates. The ramp starts immediately (`StopControl`), with no DSP gate.
- **Observable**: `IsActive` stays true until the fade completes; volume ramps down.
- **Edge cases**: `Stop(0)` ends at once; a second `Stop` while `IsStopping` is ignored unless its fade is exactly `FadeData.Immediate`; `Stop` during a loop cancels the pending handover — see "Plain looping".

## Fade out easing (`SetFadeOutEase`)

Mirrors fade-in easing, applied to `_fadeOutData` in `StopControl`.

## Clip StartPosition (trim from the front)

- **Behavior**: `BroAudioClip.StartPosition` (seconds) starts playback partway into the clip.
- **Observable**: `AudioSource.timeSamples` on the first playing frame equals `GetSample(audioClip.frequency, clip.StartPosition)`.
- **Edge cases**: unclamped — a value past the clip length sets `timeSamples` past `audioClip.samples`; the playable duration is `audioClip.GetPreciseLength() - StartPosition - EndPosition` (`GetPlayableDuration`).

## Clip EndPosition (trim from the back)

- **Behavior**: `BroAudioClip.EndPosition` (seconds before the clip's end) shortens the clip-derived end time and the sample count used when pitch changes recompute it.
- **Observable**: start-to-end duration on the DSP clock equals `clip.length - StartPosition - EndPosition`, pitch-adjusted (`PitchAdjusted`). `RecalculateScheduledEndTime` uses `endSample = audioClip.samples - GetTimeSample(EndPosition)`.
- **Edge cases**: `StartPosition + EndPosition >= clip.length` gives a non-positive duration, unguarded downstream; `EndPosition` also sets loop seam timing (`_playbackEndDspTime` in `ScheduleNextPlayback`).

## Clip Delay (per-clip, not per-call)

- **Behavior**: `BroAudioClip.Delay > 0` postpones the start (`SetClipDelayIfNotScheduled` sets `ScheduledStartTime = dspTime + Delay`) unless a start time is already set.
- **Observable**: `IsPlaying` is true from the call; the hold shows only on the playhead (`timeSamples` stays at the start sample until the delay elapses).
- **Edge cases**: an explicit `SetScheduledStartTime`/`SetDelay` before the queue drains wins outright — `clip.Delay` is dropped, not added. Only a loop's first iteration is delayed: each later iteration's player is handed a `ScheduledStartTime` at the seam, so `SetClipDelayIfNotScheduled` skips — including a newly picked clip under `ChangeClipPerLoop`.

## Scheduled start time — `ISchedulable.SetScheduledStartTime` / `SetDelay`

- **Behavior**: sets an absolute (`SetScheduledStartTime`) or relative (`SetDelay`) DSP start. On a source not yet playing it calls `PlayInternal()` directly, bypassing the queue; on a playing source it calls `AudioSource.SetScheduledStartTime`, stalling playback until that time (kept intentionally). The `ISchedulable` members are `internal`, reached through `BroAudioChainingMethod`.
- **Observable**: the playhead stays at the start sample until the scheduled time. On a reschedule while playing, `IsPlaying`/`IsActive` stay true and `OnPause` does not fire — the stall shows only on the raw `AudioSource` playhead.
- **Edge cases**: a past time clamps to now (`Math.Max` in `SchedulePlayback`); a second call before start adjusts `_secondsUntilScheduledStart` by the delta rather than resetting it.

## Scheduled end time — `ISchedulable.SetScheduledEndTime`

- **Behavior**: sets an absolute DSP end that later pitch changes do not rescale (`_isEndTimeDerivedFromClip = false`).
- **Observable**: playback ends at the given time; a mid-play `SetPitch` does not move it.
- **Edge cases**: a loop handover sets `_isEndTimeDerivedFromClip = true` on the next player (`ReceiveHandover`), so the explicit end applies only to the player it was set on.

## Mid-play pitch change rescaling the derived end time

- **Behavior**: `SetPitch` mid-play on a clip-derived end time recomputes the DSP end from the playhead (`RecalculateScheduledEndTime`): faster pitch shortens the remainder, slower lengthens it. The shift propagates to a pre-spawned next loop player (`_nextPlayer?.ShiftScheduledTimes(delta)`).
- **Observable**: remaining DSP duration before vs. after `SetPitch`; for loops, the second iteration's start moves by the same delta.
- **Edge cases**:
  - Pitch 0 skips the recompute (playhead frozen); a paused player is skipped too.
  - Negative pitch pushes the hardware scheduled end past any possible stop and leaves ending to the frame loop; nothing clears that push, so a negative-then-positive sequence needs a generous upper-bound timeout rather than an exact-time assertion.
  - A change inside the warm-up window (before the playhead moves) takes a separate branch from one after.
  - A per-handle `SetPitch` after the next loop player is pre-spawned (within `ScheduledPlaybackWarmUpTime` of the seam) shifts that player's schedule but does not re-pitch it, so from the seam the loop plays at the old pitch. A per-type `SetPitch` reaches every active player, the pre-spawned one included.

## Plain looping (`LoopType.Loop`)

- **Behavior**: each iteration hands over to a freshly extracted `AudioPlayer` at the end (or, with a clip `FadeOut`, just after) — never `AudioSource.loop`. `ScheduleNextPlayback` pre-spawns the next player on a DSP gate (`_playbackEndDspTime - seamlessFadeOut - warmUpTime`); `BeginHandover` fires at the seam.
- **Observable**: `AudioSource.loop` is false throughout; iteration N+1 starts at or after iteration N's end on the DSP clock; the caller's handle keeps driving the sound across seams.
- **Edge cases**: `ChangeClipPerLoop` re-picks a clip each iteration, whose `StartPosition`/`EndPosition` then apply (but not its `Delay`); `Stop()` mid-loop cancels the pending handover and discards a pre-spawned `_nextPlayer` (`StopControl`).

## Seamless looping with a transition time (`LoopType.SeamlessLoop`)

- **Behavior**: `PlaybackPreference.ApplySeamlessFade` uses the transition time as both the outgoing fade-out and the incoming fade-in, and `BeginHandover()` runs before the fade-out starts, so two players are audible together for the transition time.
- **Observable**: the outgoing and incoming players both report `IsPlaying` for a window equal to the transition time.
- **Edge cases**: a transition longer than the clip puts the pre-spawn gate in the past, so the handover fires almost at once; the loop period stretches from the clip length to the transition time while the player count stays bounded. Pause inside the crossfade: see lifecycle.md.

## Chained playback (`MulticlipsPlayMode.Chained`: intro → loop → outro)

- **Behavior**: plays clip[Start] once, hands over to clip[Loop] repeatedly, and on `Stop()` hands over to clip[End] instead of fading the loop. The outro handover fires synchronously from `StopControl` (`ScheduleNextPlayback(isEnd: true)`), not on a DSP gate.
- **Observable**: `AudioSource.clip` identity across handovers, and `OnStart`/`OnEnd` order.
- **Edge cases**: fewer than 3 clips (`CanHandoverToEnd`) — `Stop` is a normal fade-out with no outro; `Stop()` during the intro still hands over to the outro; a second `Stop()` is ignored while `IsStopping` unless immediate.

## BGM transitions (`Transition` modes via `IMusicPlayer.SetTransition`)

- **Behavior**: `AsBGM().SetTransition(transition, stopMode, overrideFade)` decides how the outgoing `MusicPlayer.CurrentBGMPlayer` stops relative to the incoming one. `Default` and `OnlyFadeOut` fade the old one out and the new one waits (`IsWaitingForTransition`, cleared by `FinishTransition` as the old player's `onFinished`). `Immediate` and `OnlyFadeIn` stop the old one with no fade; `CrossFade` fades both, overlapping. The new BGM fades in under `OnlyFadeIn`, `Default` and `CrossFade`.
- **Observable**: `IsPlaying` on the old and new handles over time: under the waiting modes the new BGM has not started until the old one finishes; under `CrossFade` they overlap and the old one ends once its fade completes.
- **Edge cases**: `NeedTransition == false` (no prior BGM, the same instance replaying, or the prior player gone) skips the machinery and sets `CurrentBGMPlayer` directly.

## `AlwaysPlayMusicAsBGM` (RuntimeSetting)

- **Behavior**: when on, every `Play()` of a `BroAudioType.Music` sound is wrapped with `AsBGM().SetTransition(Setting.DefaultBGMTransition, Setting.DefaultBGMTransitionTime)`.
- **Observable**: two Music plays without `AsBGM()` transition (the first stops per the default); with the setting off they overlap.
- **Edge cases**: flipping the setting affects only later plays; non-Music types are never wrapped.

## `OnBGMChanged` event

- **Behavior**: `BroAudio.OnBGMChanged` forwards to the static `MusicPlayer.OnBGMChanged`, raised synchronously by the `CurrentBGMPlayer` setter with the new wrapper, or `null` when BGM stops with no replacement.
- **Observable**: subscribe through the fixture's `SubscribeBgmChanged` (a static event needs unsubscribing). A swap raises it twice: `null`, then the new player.
- **Edge cases**: assigning the same instance does not raise it; `UpdateInstance` during a loop/chain handover writes the backing field directly, so a handover never raises it.

## Stop with fade — general

- **Behavior**: a `Stop()` with no override during a natural end-of-clip fade-out waits out the running fade instead of starting a new one (`_clipVolume.IsFadingOut` check in `StopControl`). Restarting the outer coroutine leaves the `Fader`'s own fade untouched, so the check is what prevents a double fade.
- **Observable**: `Stop()` near the end of a clip with `FadeOut`: time to silence matches the clip's `FadeOut`.
- **Edge cases**: an explicit override during a natural fade starts a fresh fade with the new duration; after an end-of-chain handover (`didHandoverToEnd`) the fade runs without `_onUpdate`, since the next player owns that dispatch; `Stop()`/`Pause()` with no argument use the clip's `FadeOut` (`FadeData.UseClipSetting`); a faded `Stop` on a loop goes silent at the end of the current iteration instead of fading.

## Further behaviors

- **What a handover carries across a loop seam.** Pitch (including below 1, with the seam period measured
  on the DSP clock), the follow target, an in-flight volume fade spanning several iterations, a fixed
  position and the per-type track effect all ride across each seam onto the next player.
- **`OnBGMChanged` at a looping BGM's seam.** It does not fire, while `CurrentBGMPlayer` follows the
  handover to the new player.
