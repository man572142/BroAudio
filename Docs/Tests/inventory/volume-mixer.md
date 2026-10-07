# Volume, pitch, mixer and effects

## Volume composition

Volume composes at two levels: a linear product inside each player, and master volume downstream in the mixer graph.

- **Per-player product** (`AudioPlayer.UpdateVolume`): `_clipVolume.Current * _trackVolume.Current * _audioTypeVolume.Current`.
  - `_clipVolume` is seeded at play start (`SetupClipVolume`) from `_clip.Volume * entity.GetMasterVolume()`; clip and entity volume are one fader from then on and cannot be told apart.
  - `_trackVolume` is set by `BroAudio.SetVolume(SoundID, ...)`, on active players of that ID only; nothing persists for later plays.
  - `_audioTypeVolume` is set by `BroAudio.SetVolume(BroAudioType, ...)`, which both stores `_audioTypePref[type].Volume` for future plays (applied in `PlayControl` unless `_audioTypeVolume.IsFading`) and pushes to live players of matching type.
  - The product goes to the mixer track parameter in dB. With no mixer group (`TryGetMixerAndTrack` fails) it goes to `AudioSource.volume`, linear and outside the mixer, so master volume does not reach that voice.
- **Master volume** (`SoundManager.SetMasterVolume`, also `BroAudio.SetVolume(vol)` and `SetVolume(BroAudioType.All, ...)`): writes the mixer's `BroName.MasterTrackName` parameter in dB. It is not a term in the player product, so `IAudioPlayer.GetVolume()` never reflects it.

### Master volume attenuates every voice

- **Observable**: `SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out db)` against `vol.ToDecibel()`. Assert `GetFloat`'s return: `SafeSetFloat`/`SafeGetFloat` no-op silently on a missing exposed parameter.
- **Edge cases**: `0` clamps to `AudioConstant.MinVolume` before the log; values above 1 boost (`ToDecibel`'s `allowBoost` defaults to true); a fade lerps in dB space, unlike player fades.

### Per-type volume reaches live and future players

- **Observable**: live players through their mixer parameter; future plays through `SoundManager.Instance.TryGetAudioTypePref(type, out pref).Volume`.
- **Edge cases**: the stored pref and live players agree at every value including exactly `1f`; composite flags (`SFX | UI`) match via `targetType.Contains(player.ID.ToAudioType())`.

### Per-SoundID volume reaches every active instance of that ID

- **Observable**: each matching player's mixer parameter, or `AudioSource.volume` if unrouted.
- **Edge cases**: several overlapping instances of one ID are all set; a later play of the ID starts at default.

### Clip and entity volume are one fader

- **Observable**: `IAudioPlayer.GetVolume()` right after start, before any `SetVolume`, equals `_clip.Volume * entity.GetMasterVolume()`.
- **Edge cases**: `RandomFlag.Volume` re-rolls the entity volume each play; a clip `FadeIn` starts this fader at 0.

### Volume fades are linear on the fader, converted to dB per frame

- **Behavior**: player fades lerp linearly on the `Fader`; the mixer receives each frame's value converted to dB, so the audible curve is not a linear dB ramp.
- **Observable**: sample the track parameter during a `SetVolume(id, v, fadeTime)` fade and compare against `Mathf.Lerp(fromLinear, toLinear, t).ToDecibel()`.
- **Edge cases**: fading toward 0 ends at `MinVolume` (about −80 dB), not true silence.

### Fade ease follows direction; a reversed fade continues from where it is

- **Behavior**: `SetVolumeInternal` picks `SoundManager.FadeInEase` or `FadeOutEase` from `Current < target`. `Fader.SetTarget` re-anchors the origin at `Current`.
- **Observable**: fade up then down: the mixer parameter moves monotonically each way.
- **Edge cases**: reversing mid-fade continues from the current value; it does not rewind to the original start.

### Unrouted players after a track pool is exhausted

- **Behavior**: past a pool's groups, `AudioTrackObjectPool.CreateObject` logs a tagged warning and returns null; the player plays with no mixer group, its volume on `AudioSource.volume`, outside master volume and any track effect.
- **Observable**: `AudioSource.outputAudioMixerGroup == null` and `AudioSource.volume` equals the linear product. Cheapest way to force it: count `AudioMixer.FindMatchingGroups(BroName.DominatorTrackName)` and play one more dominator than that (`AsDominator()` in the same frame as `Play`).
- **Edge cases**: the warning needs `LogAssert.Expect`; Generic and Dominator pools have separate capacities and separate warning texts.

### WebGL computes volume without the mixer

- **Behavior**: under `UNITY_WEBGL`, `UpdateWebGLVolume()` writes `AudioSource.volume` from the product times `SoundManager.Instance.WebGLMasterVolume`, and master volume fades run a separate coroutine. Not reachable from Editor Play Mode.

### Virtual-track release keeps loudness continuous

- **Behavior**: a player virtualized for `VirtualReleaseGracePeriod` returns its track (`ReleaseVirtualTrack`) and puts the current linear product on `AudioSource.volume`; on reacquire (`ReacquireVirtualTrack`) it resets `AudioSource.volume` to full and `UpdateVolume()` writes the mixer again.
- **Edge cases**: needs a genuinely virtualized voice (more voices than Max Real Voices), which is hard to force in a small scene.

### AudioSource.volume on routed vs unrouted players

- **Observable**: after `SetVolume(0.5f, 0f)`, a routed player's `AudioSource.volume` stays 1 and its track reads `0.5f.ToDecibel()`; an unrouted player's `AudioSource.volume` reads `0.5`.

## Pitch

### SetPitch drives AudioSource.pitch

- **Behavior**: clamps to `[MinAudioSourcePitch, MaxAudioSourcePitch]` silently. A fade runs `PitchControl` with `Ease.Linear` hard-coded, ignoring the volume eases.
- **Observable**: `player.AudioSource.pitch` after `SetPitch(id, pitch, 0f)`.
- **Edge cases**: a fade requested before the player has started is postponed (`_pendingPitchFadeTime`) and consumed in `SetInitialPitch`.

### SetPitch before play fades from the base pitch

- **Observable**: on the first playing frame `AudioSource.pitch` equals the base pitch (`GetBasePitch`), then moves toward the target.
- **Edge cases**: an explicit pre-play `SetPitch` replaces the entity's pitch randomization outright.

### SetPitch recalculates the scheduled end time

- **Behavior**: `RecalculateScheduledEndTime()` runs on an immediate `SetPitch` and on every `PitchControl` frame. See time-dependent.md for the arithmetic.
- **Edge cases**: only players with a real `ScheduledEndTime` (looping or scheduled) show it.

### Per-type and master pitch

- **Behavior**: per-type pitch follows the per-type volume pattern (stored in `_audioTypePref[type].Pitch` and pushed to live players). Master pitch has no master stage: it writes every concrete type's pref and reaches future players through them.
- **Observable**: `TryGetAudioTypePref(type, out pref).Pitch`; live players' `AudioSource.pitch`.
- **Edge cases**: the `[Obsolete]` `SetPitch(float, BroAudioType)` behaves exactly like `SetPitch(BroAudioType, float)`.

## Mixer track acquisition, routing and dominator path

### Each player acquires a pooled mixer track at play start

- **Behavior**: `SetupAudioTrack` → `Mixer.GetTrack(TrackType)`, Generic by default; `AsDominator()` switches `TrackType` to `Dominator` (a separate, smaller pool) before acquisition.
- **Observable**: after start, `AudioSource.outputAudioMixerGroup` is non-null and names a Generic track.

### Returned tracks go back to the right pool, silenced

- **Behavior**: `IAudioMixerPool.ReturnTrack` recycles to the Generic or Dominator pool. `SilenceTrackBeforeReturn` first sets the track (and its send, if `IsUsingTrackEffect`) to `MinDecibelVolume`, so the next borrower does not inherit this player's level.
- **Observable**: sequential plays past the pool size get reused groups, never null or a warning.

### A track effect moves volume to the send parameter

- **Behavior**: while the player uses a track effect, `VolumeParaName` is the send (`GetSendParaName()`, track name + `BroName.EffectParaNameSuffix`) instead of the track parameter; `SetTrackEffect` swaps live via `mixer.ChangeChannel`, carrying the current dB value across.
- **Observable**: with an effect on, the track parameter sits at `MinDecibelVolume` and the send carries the level; turning the effect off swaps back with no jump.

### Dominator flips the main mix between two exposed tracks

- **Behavior**: while a dominator effect (`SetEffect(effect)` with `effect.IsDominator`) tweaks or holds, `BroName.MainTrackName` drops to `MinDecibelVolume` and `MainDominatedTrackName` rises to `FullDecibelVolume`; it reverts once `EffectAutomationHelper`'s waitable list drains.
- **Edge cases**: `DominatorPlayer.QuietOthers/LowPassOthers/HighPassOthers` apply the effect to everything except the dominator (`SetAllEffectExceptDominator`); `othersVol` must be in `(0, 1]` and frequencies must pass `AudioExtension.IsValidFrequency`.

### IAutoResetWaitable resets the effect when its condition is met

- **Observable**: after `ForSeconds`/`Until`/`While` is satisfied, the effect parameter returns to `GetEffectDefaultValue` (`FullDecibelVolume` for Volume, `MaxFrequency` for LowPass, `MinFrequency` for HighPass) with no further call.
- **Edge cases**: decorating the waitable after the effect has started tweaking warns and no-ops (`DecorateTweakingWaitable`); a timed reset of a non-dominator effect restores only the parameter — the type stays routed through the send; with `Setting.AudioFilterSlope == FourPole`, LowPass/HighPass also write a second parameter (name + `"2"`).

### EffectType.Volume is dominator-only

- **Observable**: a non-dominator Volume effect logs "is only supported on Dominator" and the parameter name resolves to `string.Empty` — a no-op.

### A more intense effect restarts the tweak; a less intense one waits

- **Behavior**: `EffectAutomationHelper` restarts toward a more intense target and queues a less intense one (`Effect.CompareTo` / `IsMoreIntenseThan`).
- **Edge cases**: LowPass compares inverted (lower frequency is more intense, `CompareTo * -1`).

## EditMode unit-test candidates

Pure functions, no `SoundManager` or coroutine:

- `AudioExtension.ToDecibel(vol, allowBoost = true)` — boundaries at 0, 1, 10, above 10.
- `AudioExtension.ToNormalizeVolume(dB, allowBoost = true)` — early return at max dB; round trip `x.ToDecibel().ToNormalizeVolume() ≈ x` over `[MinVolume, MaxVolume]`.
- `AudioExtension.ClampNormalize` / `ClampDecibel` — their `allowBoost` defaults to **false**, unlike `ToDecibel`/`ToNormalizeVolume`; pin each default.
- `Utility.SliderToVolume` / `VolumeToSlider` — every `SliderType` branch, boost on and off.
- `Utility.BroVolumeToSlider` / `SliderToBroVolume` — every `BroVolumeSplitPoints` boundary plus slider 0 and 1; round trip.
- `Utility.GetBroVolumeStep(allowBoost)` — step count with and without boost.
- `Effect.CompareTo` / `IsMoreIntenseThan` (LowPass inversion) and `Effect.IsDefault()` per type.
- `Effect.Value` setter — Volume stores dB; LowPass/HighPass drop out-of-range writes silently.
- `AudioExtension.IsValidFrequency` — boundaries at `MinFrequency`/`MaxFrequency`; logs and returns false outside.
- `AudioTypePlaybackPreference`'s static `OnSetVolume`/`OnSetpitch`/`OnSetEffect`, especially `SetEffect`'s `Add`/`Remove`/`Override` flag arithmetic.

## Further behaviors

- **Per-type `SetVolume` / `SetPitch` with a flag that is not a concrete type.** `BroAudioType.None` warns
  and changes nothing. Unity's "Everything" (-1) is treated as `All`: `SetVolume` writes only the master
  volume, and `SetPitch` reaches live players and every concrete type's pref.
- **Per-`SoundID` pitch** reaches only that ID's live players and is not stored for future plays.
- **The entity's authored `Pitch`** reaches `AudioSource.pitch`, and a per-type pitch replaces it rather
  than scaling it.
- **Per-play randomization** (`RandomFlags`, `PitchRandomRange`, `VolumeRandomRange`) is drawn at each
  `Play`, within base ± range/2.
- **A per-type effect** re-routes only the live players of that type through the effect send; players of
  other types stay on their dry track.
