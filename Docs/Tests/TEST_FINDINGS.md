# BroAudio Test Findings

Behavior/doc conflicts and rough edges found while building the regression suite.

Fixed findings move to [FIXED_ISSUES.md](FIXED_ISSUES.md), and leave it once the fix ships on `main`. A
number in neither file is retired, not free: a new finding takes a number above every one ever used.
Tests and code comments cite findings by number, so sections are never renumbered or merged.

Number 68 is withdrawn: it described Shuffle not playing each clip once per cycle, which the maintainer
confirmed is not part of its contract.

Numbers 8, 12, 13, 21–25, 27, 29, 31, 32, 34, 38, 46, 50, 54, 56 and 62 are withdrawn on review: each was
intended behavior, unreachable from production code, or too minor to track (cosmetic layout drift, a lost
sample, a log-prefix sweep). The tests that characterized them stay, untagged.

The tests that pin finding N carry `[Category("Finding_N")]`, so `-testCategory Finding_14` selects
everything that pins #14, in either suite. `FindingCoverageTests` holds this file to the coverage rules in
[ADDING_A_TEST.md §6](ADDING_A_TEST.md#6-findings).

| # | Area | Finding | Status |
|---|---|---|---|
| 9 | Clip selection | `ShuffleClipStrategy` **can** repeat the previous clip, contradicting its documented contract | Open, characterized |
| 10 | Clip selection | `out index` disagrees with the returned clip in `Shuffle` | Open, characterized |
| 11 | Music | `OnBGMChanged` fires twice per swap, once with a `null` argument | Open, characterized |
| 14 | Addressables | `AutomaticallyUnloadUnusedAddressableAudioClipsAfter` does not control the unload delay | Open, characterized |
| 26 | Editor / Clip editing | `Reverse` transposes stereo channels | Open, characterized |
| 35 | MonoComponent / SoundSource | `Stop On Disable` silently does nothing when the object is disabled in the frame it was enabled | Open, characterized |
| 36 | MonoComponent / SoundVolume | `Only Apply Once` applies only the *first* settings entry, on the very first enable | Open, characterized |
| 37 | MonoComponent / SoundVolume | A setting typed `BroAudioType.All` writes the master volume, which `Reset On Disable` cannot restore | Open, characterized |
| 39 | MonoComponent / SpectrumAnalyzer | A band narrower than one FFT bin makes RMS/Average divide by zero, and the band runs away to one end of the scale | Open, characterized |
| 40 | MonoComponent / SpectrumAnalyzer | Every band draws a `Weighted` field in the inspector that the runtime never reads | Open, characterized |
| 41 | Playback / Stop | `Stop(onFinished)` on a recycled handle drops the callback silently | Open, characterized |
| 42 | Decorators / Dominator | `AsDominator()` after playback started cannot re-route the player, so it filters itself | Open, characterized |
| 43 | Decorators / Dominator | `QuietOthers` with a zero fade time is overwritten by `SwitchMainTrackMode`, so nothing ducks | Open, characterized |
| 44 | Decorators / Dominator | A looping dominator's incoming player takes a generic track at the first seam, so it ducks itself | Open, characterized |
| 48 | Teardown | `BroAudio.SetEffect` is not `Manager?.`-gated like the other release verbs, so it throws once the manager is gone | Open, characterized |
| 51 | Volume / Master | A zero-fade `SetVolume` cannot cancel an in-flight master fade, so the old ramp keeps writing | Open, not pinned |
| 55 | Pitch | A per-type pitch **replaces** the entity's authored pitch instead of scaling it | Open, characterized |
| 57 | Volume / Fade | A timed per-type `SetVolume` ramps live players but stores the target instantly, so a sound started mid-fade begins at the end value | Open, characterized |
| 58 | Looping / Stop | `Stop` with a fade on a looping sound goes silent at the current iteration's end, while the handle stays active for the rest of the fade | Open, characterized |
| 59 | Looping / Seamless | A `SeamlessLoop` whose `TransitionTime` outlasts the clip loops once per `TransitionTime`, not once per clip | Open, characterized |
| 61 | Editor / Clip editing | A failed `Trim` leaves a zeroed sample buffer behind that later edits apply to | Open, characterized |
| 65 | Playback / Pause | `UnPause` during a Pause fade-out leaves `IsStopping` set, so every later faded `Stop` is discarded | Open, characterized |
| 66 | Addressables | A key that fails to load throws out of `PlayControl` and strands the player active and silent | Open, characterized |
| 67 | Music / StopMode | `StopMode.Mute` has no path that unmutes, and a muted player is never recycled when its clip ends | Open, characterized |
| 70 | Easing | `SetFadeInEase`/`SetFadeOutEase` have no effect on the clip's own authored FadeIn/FadeOut | Open, characterized |
| 71 | Effects | A timed non-dominator `SetEffect` resets the mixer parameter but leaves the type routed through the effect send | Open, characterized |
| 72 | Pitch / Handover | A `SetPitch` after the next loop player is pre-spawned does not reach it, so the loop reverts at the seam | Open, not pinned |

---

## 9. `ShuffleClipStrategy` can repeat the previous clip

**Where:** `ShuffleClipStrategy`

`MulticlipsPlayMode.Shuffle` is documented as "not repeating with the previous one", but `_lastUsed` is
refreshed only at pool exhaustion and in the fallback scan, never after an ordinary in-cycle hit, so two
consecutive `SelectClip` calls can return the same clip.

Status: Open, characterized. Pinned by
`ClipSelectionTests.SelectClip_CanRepeatTheImmediatelyPreviousClip_ContradictingDocumentedIntent`, which
proves the gap exists rather than asserting the documented invariant.

## 10. `out index` disagrees with the returned clip in `Shuffle`

**Where:** `ShuffleClipStrategy.SelectClip`

The fallback scan keeps advancing `index` while probing, then returns the clip found at the earlier index.
`VelocityClipStrategy` had the same defect on its "above every threshold" fallthrough; that half is fixed.

Editor-only blast radius: the `PickNewClip(context, out index)` overload is consumed only by
`EntityReplayRequest` and `AudioEntityEditor`, so the inspector can highlight one clip row while
previewing another. Runtime playback uses the overloads that discard the index.

Status: Open, characterized. Pinned by
`ClipSelectionTests.SelectClip_WhenFallbackScanRuns_OutIndexCanDisagreeWithTheReturnedClip`.

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

## 26. `Reverse` transposes stereo channels

**Where:** `AudioClipEditingHelper.Reverse`

`Array.Reverse` on the interleaved sample array swaps L and R as well as reversing time.

Status: Open, characterized. Pinned by `ClipEditingTests.Reverse_Stereo_ReversesRawArraySoChannelsAreTransposed`.

## 35. `SoundSource`'s Stop On Disable is skipped for a sound that is still queued

**Where:** `SoundSource.OnDisable`

The guard requires `CurrentPlayer.IsPlaying` (`AudioSource.isPlaying`), but `BroAudio.Play` only enqueues;
`SoundManager.LateUpdate` starts the voice. Disabling the object in the same frame it was enabled (a pooled
object spawned and despawned at once) skips the stop, and the queued sound then plays to completion (a loop,
indefinitely) unless something calls `Stop` on the component before the next enable replaces `CurrentPlayer`. With a non-zero `Delay`, `OnEnable` goes through `SetDelay` →
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

## 55. A per-type pitch replaces the entity's authored pitch instead of scaling it

**Where:** `AudioPlayer.GetBasePitch`

When the per-type pitch is not the default, the entity's authored `Pitch` is ignored: after
`SetPitch(SFX, 0.5f)` an entity authored at 1.5 plays at 0.5, not 0.75, and its random range applies around
the per-type value. Volume layers multiply instead. Invisible at the default pitch, which new entities use.

Status: Open, characterized. Pinned by
`AuthoredPitchAndRandomizationTests.Play_WithAuthoredEntityPitch_ReachesAudioSourceAndIsReplacedNotScaledByTypePitch`,
which asserts 0.5 and explicitly not 0.75.

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
