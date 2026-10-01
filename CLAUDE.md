# BroAudio

Audio middleware for Unity. The project under `Assets/BroAudio/` **is** the package — that subtree is exactly what consumers install, and it's effectively the whole codebase (the rest of `Assets/` holds only its `.meta`). It ships from one source down two channels: a UPM package (`com.ami.broaudio`) and a Unity Asset Store `.unitypackage`, exported via `PackageExporter` (gated behind `#if BroAudio_DevOnly`). Current version lives in `Assets/BroAudio/package.json`; user-facing changes are summarized in `Docs/RELEASE_NOTES.md`.

## Commands
No CLI build/test pipeline — everything runs from the Unity Editor (Unity 6000.3).
- Library Manager (primary authoring window): `Tools > BroAudio > Library Manager`
- Preferences (feature toggles): `Tools > BroAudio > Preferences`
- Tests: `Window > General > Test Runner` (Unity Test Framework). Tests, when present, live under `Assets/Tests/` — check that folder first; a branch may have none. CLI form: `Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults results.xml -quit`
- Regenerate audio proxies: BroAudio Dev Tools window.
- Player build / package export: `BroProjectBuilder.Build()` and `PackageExporter` in `Editor/DevTools/`.

## Tech Stack
C#, Unity. Developed on Unity 6 (6000.3.9f1), but the distributed package declares a minimum of `2020.3` (`Assets/BroAudio/package.json`) — keep runtime code within that API floor rather than reaching for newer-Editor-only APIs.

## Assemblies
Two assemblies; put new files (and their `using` directives) in the matching one:
- `BroAudio` (`Assets/BroAudio/Runtime/`) — all platforms, auto-referenced.
- `BroAudioEditor` (`Assets/BroAudio/Editor/`) — Editor only. Editor-only runtime code can instead live behind `#if UNITY_EDITOR`.

Namespaces are `Ami.*` (`Ami.BroAudio` public API, `Ami.BroAudio.Runtime` internals, `Ami.BroAudio.Data` data + ScriptableObjects, `Ami.BroAudio.Tools`, generic `Ami.Extension`) and track neither the package id nor the folder layout — e.g. `Ami.BroAudio.Data` types live under `Runtime/DataStruct/`.

## Optional packages (compile-gated)
`versionDefines` in the runtime asmdef define `PACKAGE_ADDRESSABLES` (from `com.unity.addressables`) and `PACKAGE_LOCALIZATION` (from `com.unity.localization`) only when those packages are installed. Support lives in partial files suffixed `.Addressables.cs` / `.Localization.cs`, wrapped in the matching `#if`.

IMPORTANT: code that touches Addressables or Localization APIs must stay inside the matching `#if` block and the matching `.Addressables.cs` / `.Localization.cs` partial — otherwise compilation breaks when the package is absent.

Opt-in manual init: defining `BroAudio_InitManually` skips the `[RuntimeInitializeOnLoadMethod]` auto-bootstrap and requires an explicit `BroAudio.Init()`.

## Partial classes
Many classes are split by feature into suffixed files — most heavily `AudioPlayer` and `SoundManager`. Put new feature code in the matching partial and keep the no-suffix file for core/shared members. Grep across all partials before assuming a member is missing or adding a duplicate, and read but never extend `*_LEGACY_DEPRECATED.cs` files.

## Runtime architecture (gotchas, not obvious from one file)
- The static `BroAudio` facade (`Runtime/BroAudio.cs`) delegates everything to `SoundManager`. Play verbs call `SoundManager.Instance` (throws if uninitialized); release verbs use the null-safe `BroAudio.Manager` because they can run during `OnDestroy`/`OnApplicationQuit`. Don't swap these — the asymmetry is intentional for teardown ordering.
- `SoundManager` is a `MonoBehaviour` singleton bootstrapped from `Resources/SoundManager.prefab`. It owns the player pool, the mixer-group pools, the shared `AudioMixer`, and the `IAudioMixerPool` impl that players recycle through.
- `AudioPlayer.Recycle()` routes through `MixerPool.ReturnPlayer(this)`. Driving `AudioPlayer` directly in tests/tooling without a real `SoundManager` requires supplying an `IAudioMixerPool` test double.
- `PlaybackPreference`'s constructor reaches `SoundManager.FadeInEase` — constructing one needs a live `SoundManager`.
- `Fader.StopCoroutine()` defensively no-ops when `SoundManager.Instance` is null (relied on during teardown).
- Behavior modes are layered via `AudioPlayerDecorator` subclasses (`MusicPlayer`, `DominatorPlayer`) — `AsBGM()`/`AsDominator()` attach a decorator, not inheritance. Clip selection is a strategy pattern under `Runtime/Utility/ClipSelection/`.
- `BroAudioClip` is a **class** (not struct) with a public `Delay` field; `AudioPlayer._pref` is a `PlaybackPreference` struct.

## Unity audio engine facts (empirical)
Hand-verified engine behavior that shapes this codebase — where it conflicts with Unity's docs, trust these. Full notes in `.claude/rules/unity-audio-engine.md` (auto-loads when editing Runtime or test code).
- `AudioSource.volume` is linear; only AudioMixer parameters take the dB/logarithmic conversion.
- All play APIs (`Play`, `PlayOneShot`, `PlayScheduled`, `PlayDelayed`) share one mechanism and snapshot the clip at call time — clearing `clip` doesn't stop the voice, but other source operations still affect it. One voice per source, except `PlayOneShot`.
- `PlayScheduled` needs `clip` assigned before the call, holds an AudioVoice from the call, and reports `isPlaying == true` immediately (as does `PlayDelayed`).
- Seek by assigning `time`/`timeSamples`; re-calling `Play()` on a playing source resets to 0. After a clip finishes, `timeSamples` rests at the start sample, not 0.
- `AudioMixer.SetFloat` silently fails in `Awake`/`OnEnable` on the first Play Mode frame, and crashes the engine if the parameter name is null.

## Logging & errors
- Prefix every log with `Utility.LogTitle` (the `[BroAudio]` rich-text tag): `Debug.LogError(Utility.LogTitle + "...")`. Don't emit bare `Debug.Log*`.
- There is one custom exception, `BroAudioException` — reuse it instead of adding new exception types. Throw only for genuine setup/programmer errors (e.g. uninitialized manager); for expected "not found / invalid" gameplay paths, log and return gracefully (often via a `TryGet*` bool pattern) rather than throwing.

## Null-safety & teardown
- For `UnityEngine.Object` references use Unity's null semantics (`if (obj)`), not `== null`, so destroyed-but-not-null objects are caught.
- Code reachable during `OnDestroy`/`OnApplicationQuit` must treat "already torn down" as a silent no-op. Mirror the existing guards — the null-safe `BroAudio.Manager` (returns null when there's no instance) and `InstanceWrapper<T>.IsAvailable()` — instead of dereferencing `SoundManager.Instance` directly in teardown paths.
- For `[Flags]` enums use the `FlagsExtension` helpers / the `Contains` extension rather than `Enum.HasFlag` (avoids boxing).

## Editor conventions
- User-facing editor strings are **not hardcoded**: add an entry to the `Instruction` enum (respecting its numeric range grouping) and fetch it via `BroInstructionHelper.GetText(Instruction.X)`, backed by the `BroInstruction` asset.
- Persistent, shareable settings/toggles belong in the `EditorSetting` ScriptableObject (`BroEditorUtility.EditorSetting`; runtime-facing config via `RuntimeSetting`). Only per-developer, non-VCS state (e.g. last-edited asset) goes in `EditorPrefs`, keyed by `PlayerSettings.productGUID`.
- Custom inspectors/windows derive from `MiEditor` / `MiEditorWindow` (auto wide-mode + rect line-counting); prefer the rect-layout helpers in `EditorScriptingExtension` over ad-hoc `EditorGUILayout`. Reference serialized properties through each type's nested `NameOf` class, not string literals.

## Auto-generated code
`Runtime/Player/AutoGeneratedCode/` (AudioSource and audio-effect filter proxies) is produced by `Editor/DevTools/AudioProxyModifierCodeGenerator.cs` via the Dev Tools window. Change the generator and regenerate — don't hand-edit the output.

## Style
Defer to `.editorconfig` (4-space indent, CRLF, no final newline; `_camelCase` private fields, `PascalCase` types/public fields, `I`-prefixed interfaces; explicit type over `var` for built-ins). Public serialized data prefers `[field: SerializeField] public T X { get; private set; }`.

## Enforced by hooks (`.claude/settings.json`)
These run automatically — don't restate them as rules, just know they'll block/act:
- Hand-edits to Unity YAML assets (`.prefab`, `.unity`, `.asset`, `.mat`, `.controller`, `.anim`, `.playable`, `.mixer`, `.overrideController`) and `.meta` files are blocked.
- Edits under `AutoGeneratedCode/` are blocked.
- Edits to `ProjectSettings/` and `Packages/manifest.json` are blocked (shared config).
- New `.cs` / `.asmdef` files get a `.meta` with a fresh GUID auto-generated on write.

## Boundaries
- ✅ Always: read the governing `.asmdef` before editing files in that assembly so `using` directives match.
- ⚠️ Ask first: adding package dependencies, creating new `.asmdef` files, schema changes to ScriptableObjects that already have saved instances.
- 🚫 Never: force-push or push to `main`.

## Definition of Done
1. No compiler errors in changed files (full verification requires the Unity Editor).
2. New scripts have their paired `.meta` (the hook generates it; confirm in Unity on next refresh).
3. Addressables/Localization code compiles with those packages absent (stays behind the `#if`).
4. All `.claude/settings.json` hooks pass.
