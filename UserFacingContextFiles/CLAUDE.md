# Using BroAudio

BroAudio is the audio middleware installed in this project. This file is about **using** it from gameplay code — playing and controlling sounds, and wiring the no-code components. It does not cover modifying the package itself.

All public API is in the `Ami.BroAudio` namespace:

```csharp
using Ami.BroAudio;
```

## Data model (read this first)

- Sounds are authored in the **Library Manager** (`Tools > BroAudio > Library Manager`), not in code. It produces `AudioAsset` library files under `Assets/BroAudio/AudioAssets/` (or the consumer's chosen folder).
- Each row in a library is an **AudioEntity**: a named entry with an `AudioType`, one or more `AudioClip`s, and per-clip settings (volume, fade, loop, delay…).
- A **`SoundID`** is the runtime handle to one entity. It is a serializable struct — expose it as a field and pick the entity from the inspector dropdown:

```csharp
public class Gun : MonoBehaviour
{
    [SerializeField] private SoundID _fireSound; // dropdown lists all library entities

    public void Fire() => BroAudio.Play(_fireSound);
}
```

- `BroAudioType` is a `[Flags]` enum: `Music`, `UI`, `Ambience`, `SFX`, `VoiceOver`, `All`. Used for bulk control (stop/pause/volume per category).
- Initialization is automatic — the system boots itself on first use. Only call `BroAudio.Init()` if the project defines `BroAudio_InitManually`.

## Playing — the `BroAudio` facade

`BroAudio.Play(...)` returns an `IAudioPlayer` you can chain on. Overloads pick the spatial mode:

```csharp
BroAudio.Play(id);                 // 2D / global
BroAudio.Play(id, worldPosition);  // 3D one-shot at a Vector3
BroAudio.Play(id, followTarget);   // 3D, follows a Transform continuously
BroAudio.Play(id, fadeIn: 0.5f);   // optional fade-in seconds (also on the 3D overloads)
id.Play();                         // SoundID extension, equivalent to BroAudio.Play(id)
```

Stop / pause by id or by type. Release verbs are null-safe and may be called during teardown:

```csharp
BroAudio.Stop(id);                 BroAudio.Stop(id, fadeOut: 1f);
BroAudio.Stop(BroAudioType.SFX);   BroAudio.Stop(BroAudioType.All);
BroAudio.Pause(id);  BroAudio.UnPause(id);
```

Volume and pitch (target the master bus, a type, or a specific id):

```csharp
BroAudio.SetVolume(0.5f);                        // master (0~10, 1 = unity gain)
BroAudio.SetVolume(BroAudioType.Music, 0.3f, fadeTime: 1f);
BroAudio.SetVolume(id, 0.8f, fadeTime: 0.2f);
BroAudio.SetPitch(BroAudioType.All, 1.2f);       // pitch range -3~3, default 1
```

Queries: `id.IsValid()`, `BroAudio.HasAnyPlayingInstances(id)`, `BroAudio.TryGetEntityInfo(id, out var info)`.

## The fluent `IAudioPlayer` chain

The object returned by `Play()` exposes a fluent API. Chain calls immediately after `Play()`:

```csharp
BroAudio.Play(_music)
    .AsBGM()                              // -> IMusicPlayer: auto-transitions when the next BGM plays
    .SetTransition(Transition.CrossFade)
    .SetVolume(0.7f, fadeTime: 2f);

BroAudio.Play(_sfx)
    .SetVolume(0.8f)
    .SetPitch(Random.Range(0.9f, 1.1f))
    .SetDelay(0.1f)
    .OnEnd(id => Debug.Log($"{id} finished"));

BroAudio.Play(_voice)
    .AsDominator()                        // -> IPlayerEffect: ducks everything else while playing
    .QuietOthers(0.2f, fadeTime: 0.3f);   // also LowPassOthers / HighPassOthers
```

- Lifecycle callbacks: `OnStart`, `OnUpdate`, `OnPause`, `OnEnd`. Easing: `SetFadeInEase` / `SetFadeOutEase`.
- Per-player effects: `AddLowPassEffect`, `AddHighPassEffect`, `AddReverbEffect`, `AddEchoEffect`, `AddChorusEffect`, `AddDistortionEffect` (each with a matching `Remove…`). Not available on WebGL.
- Scheduling: `SetScheduledStartTime(dspTime)` / `SetScheduledEndTime(dspTime)` for sample-accurate timing.

Global effects (not per-player) go through `BroAudio.SetEffect`, built from `Effect` factory methods:

```csharp
BroAudio.SetEffect(Effect.LowPass(800f, fadeTime: 0.5f));   // muffle everything
BroAudio.SetEffect(Effect.ResetLowPass(0.5f), BroAudioType.SFX);
```

## No-code MonoComponents

For designers / quick wiring, add these via the `Add Component > BroAudio` menu instead of scripting:

- **`SoundSource`** — plays a chosen `SoundID` on enable. Inspector toggles: play-on-enable, stop-on-disable, play-once, position mode (Global / FollowGameObject / StayHere), delay. Exposes `Play()`, `Stop()`, `SetVolume()`, `CurrentPlayer`.
- **`SoundVolume`** — binds UI `Slider`s to per-`BroAudioType` volume (options menu sliders), with apply/reset-on-enable.
- **`SpectrumAnalyzer`** — exposes frequency-band amplitudes via an `OnUpdate` event for audio-reactive visuals.

## Optional: Addressables & Localization

These APIs exist **only when** the matching Unity package is installed (`com.unity.addressables` / `com.unity.localization`); don't reference them unless the consumer project has the package, or the project won't compile.

```csharp
// Addressables (manual load/release of an entity's clips)
var handle = await id.LoadAssetAsync().Task;   // also LoadAllAssetsAsync
id.ReleaseAsset();                             // also ReleaseAllAssets
bool ready = BroAudio.IsLoaded(id);

// Localization (clip swaps with the active locale)
id.LocalizedAudioChanged += BroAudio.PlayOnLocalizedAudioChanged;
```

## Pitfalls

- A pooled player from `Play()` is recycled when it finishes — it's safe to cache an `IAudioPlayer`, but don't assume it's still your sound across frames. Re-check `IsPlaying` / `IsActive`, or re-`Play()`.
- An unset or invalid `SoundID` (`SoundID.Invalid`) makes `Play` a silent no-op. Author the entity in the Library Manager and assign it; gate on `id.IsValid()` if it may be unset.
- `BroAudio.Play(...)` throws if the manager isn't initialized — don't call it from `OnDestroy`/`OnApplicationQuit`. Stop/Pause/SetVolume are safe there (they no-op when torn down).
- Don't bypass BroAudio with a raw `AudioSource` + `id.GetAudioClip()` — you'd lose pooling, mixing, fading, and the playback rules. Route playback through the facade.

## Boundaries

- ✅ Reference sounds with a serialized `SoundID` field; author entities in the Library Manager.
- ⚠️ Adding/renaming sound entities is an authoring task in the Library Manager, not a code edit — ask before changing the data model other code relies on.
- 🚫 Never hand-edit `AudioAsset` `.asset` library files (YAML/GUIDs — they break silently) or call the internal `SoundManager` directly; use the `BroAudio` facade.

Full docs: https://man572142s-organization.gitbook.io/broaudio/
