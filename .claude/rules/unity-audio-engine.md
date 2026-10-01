---
paths:
  - "Assets/BroAudio/Runtime/**/*.cs"
  - "Assets/Tests/**/*.cs"
---
# Unity Audio Engine Behavior (empirical)

Hand-verified engine behavior, distilled from the maintainer's research notes. Where these conflict with Unity's documentation, trust these — several were confirmed against Unity's source. They explain many BroAudio design decisions; still, re-verify version-sensitive items before building new behavior on top of one.

## Play APIs & voices
- `Play()`, `PlayOneShot()`, `PlayScheduled(dspTime now)`, and `PlayDelayed(0)` are the same internal mechanism and start audible playback at the same time.
- The AudioClip is snapshotted into the engine at the call — clearing `AudioSource.clip` afterwards does NOT stop playback. Every other post-play operation on the source (`Stop`, `Pause`, volume, etc.) still affects the live voice.
- One voice per AudioSource — only `PlayOneShot` can stack multiple voices on a single source.
- `Play()` called in `Update`, `LateUpdate`, end-of-frame, or `OnDestroy` all become audible at the same point: audio consumes state in [`PostLateUpdate.UpdateAudio`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PlayerLoop.PostLateUpdate.UpdateAudio.html).

## Scheduling
- `PlayScheduled` requires `clip` assigned before the call; assignments to `clip` between the call and the scheduled start are ignored.
- `PlayScheduled` occupies an AudioVoice from the moment of the call, before audible playback begins.
- `isPlaying` becomes `true` immediately on calling `PlayScheduled` or `PlayDelayed`, not at audible start.
- `SetScheduledStartTime` on an already-playing source acts as "pause for (target − dspTime)"; passing the current dspTime does nothing (pauses 0 s).
- `PlayDelayed` can also be scheduled.

## Seeking & position
- Setting `time`/`timeSamples` before `Play()` starts playback from that position. Calling `Play()` again on a playing source resets to 0 — mid-playback seeks must assign `time`/`timeSamples` WITHOUT re-calling `Play()`.
- After a clip finishes, `timeSamples` rests at the sample playback started from (started at 100 → ends back at 100), not 0.
- `time` is always 0 before the first `Play()`.

## Volume & mixer
- `AudioSource.volume` is linear (scales the clip data directly); only AudioMixer parameters take the logarithmic/dB conversion.
- `AudioMixer.SetFloat` silently fails in `Awake`/`OnEnable` on the first Play Mode frame ([discussion](https://discussions.unity.com/t/audiomixer-setfloat-doesnt-work-on-awake/579429)) — bootstrap ordering matters.
- `AudioMixer.SetFloat` with a null parameter name crashes the engine ([issue](https://issuetracker.unity3d.com/issues/unity-crashes-when-audiomixer-dot-setfloat-name-parameter-is-null)).
- Mixer Sends clamp to 0 dB in the GUI but accept up to +20 dB via `SetFloat`.

## Pitch
- Pitch is implemented as playback-speed change. Negative pitch rewinds; rewinding back to `time == 0` fires `Stop()`, and the source will NOT resume even after pitch is set positive again.
- The same effect applied via an AudioSource filter component vs. the AudioMixer sounds audibly different.

## 3D sound & rolloff
- A 3D source (`spatialBlend` 3D) beyond MaxDistance — even fully inaudible — still plays and holds an AudioVoice.
- Logarithmic rolloff ignores MaxDistance entirely: the curve extends to 10000, where the computed volume floors at 0.0001 (≈ −80 dB). In that mode MaxDistance only scales the CurveEditor view. Unity's MaxDistance docs are wrong on this.
- The 3D sound settings are just AnimationCurves; audibility beyond MaxDistance is determined by the final keyframe's value — Linear goes silent there only because its last key is 0.
- `isVirtual` is `false` before the first play and `true` whenever the source is stopped after having played, regardless of voice counts. Inaudibility only raises virtualization *priority* — a sound is not virtualized merely for being inaudible; virtualization happens when voices exceed Max Real Voices.

## Reverb
- Reverb is produced regardless of whether the *source* is in a ReverbZone; whether it's *heard* depends on the AudioListener's position. A 2D UI sound gets reverb when the listener stands in a zone. `reverbZoneMix` is the per-source amount.
- `AudioReverbFilter` is a per-object component that applies reverb directly, like the other filter components.

## Filters
- HighPassFilter: fast cutoff-frequency sweeps pop/click ([issue](https://issuetracker.unity3d.com/issues/theres-a-pop-sound-when-changing-the-high-pass-audio-filters-cutoff-frequency-from-a-lower-to-a-higher-value)).
- Resonant Low/High-pass (components and mixer effects): Resonance Q range is 1–10, so there is always some boost — easy to distort. The "Simple" variants are weak (per FMOD: two single-pole RC modules); the resonant ones are likely 4-pole. For 4-pole without resonance, chain two 2-pole filters.
- Adding `AudioLowPassFilter` surfaces a low-pass curve in the AudioSource CurveEditor (high-pass gets no such curve).

## Audio thread & clip data
- `OnAudioFilterRead` fires once per buffer — every bufferSize/sampleRate seconds (1024/48000 ≈ 21.3 ms) — on the audio thread, independent of fps, and fires whenever an AudioSource sits on the same GameObject, playing or not. Its `data[]` is interleaved across channels. `AudioSettings.dspTime` and `AudioSource.timeSamples` update on the same cadence (hence dspTime's repeating decimals).
- `AudioClip.samples` counts per-channel sample frames (mono length). `GetData`/`SetData` buffers are interleaved across ALL channels, while their `offsetSamples` argument is per-channel.

## Editor & timing quirks
- `AudioSource.isPlaying` returns `false` while the Editor is paused or frame-stepping — affects Play Mode tests that pause.
- `WaitForEndOfFrame` is wildly late on the game's first frame; wait at least one frame before relying on it.

## Unverified / conflicting
- Switching `outputAudioMixerGroup` mid-playback: one observation says it silences the sound, another says post-play source operations (including the group switch) affect the live voice normally. Re-verify on the current Unity version before relying on either.
