---
layout:
  width: default
  title:
    visible: true
  description:
    visible: false
  tableOfContents:
    visible: true
  outline:
    visible: true
  pagination:
    visible: true
  metadata:
    visible: true
  tags:
    visible: true
  actions:
    visible: true
---

# Release Notes

## **Ver. 3.2.2 (**[**Github,**](https://github.com/man572142/Bro_Audio/releases/tag/3.2.2) [**Unity Asset Store)**](https://assetstore.unity.com/packages/tools/audio/bro-audio-257362)

#### Fixes

* Fix mid-play SetPitch() leaving sample-accurate loop/scheduled-end times stale, causing loop-seam gaps or skipped audio.
* Fix SetPitch(pitch, fadeTime) called before/during playback start snapping to the target pitch before fading from default.
* Fix explicit SetPitch() being re-randomized by the entity's pitch randomization instead of used verbatim.
* Fix AudioMixer.SetFloat() silently failing on the game's first Awake()/OnEnable() (long-standing Unity issue).
* Fix a double-teardown "Invalid target player" error on players scheduled with SetScheduledEndTime().
* Fix Library Manager entity list/SoundID dropdown not refreshing after duplicating an entity outside the asset output path.

## Ver. 3.2.1 **(**[**Github,** ](https://github.com/man572142/Bro_Audio/releases/tag/3.2.1)**,** [**Unity Asset Store)**](https://assetstore.unity.com/packages/tools/audio/bro-audio-257362)**)**

#### Fixes & Improvements

* Fix WebGL compile error.
* Sequence play mode no longer throws when no valid clip is available.
* Avoid redundant synchronous loads for Addressable AudioClips that are still loading.
* Guard against a missing `AddressableAssetSettings` asset when toggling a clip's Addressable state.
* Fix a typo in the track-limit warning message.

## Ver. 3.2.0&#x20;

**New Features**

* **Unity** [**Localization**](../core-features/localization.md) **integration** — drive an entity's clips through locale-aware Asset Tables, with a deferred preload API and `SubscribeLocalizedAudioChanged` / `UnsubscribeLocalizedAudioChanged` events.
* [**Change Clip Per Loop**](../core-features/library-manager/design-the-sound/seamless-loop.md#change-clip-per-loop) — looping entities with multiple clips can pick a new clip on every loop iteration.
* **Sample-accurate** [**Seamless Loop**](../core-features/library-manager/design-the-sound/seamless-loop.md) — seamless loops are now driven by `AudioSource.PlayScheduled` with a handover between players for sample-accurate loop boundaries.
* Added `IAudioPlayer.GetVolume()` for reading the player's current volume.
* **Editor crossfade preview** for Chained play mode combined with Seamless Loop.
* All BroAudio windows are now also accessible under **Window > Audio > BroAudio**, alongside the existing **Tools > BroAudio** menu.

**Improvements & Optimizations**

* The [Play Mode](../core-features/library-manager/design-the-sound/#playmode) dropdown is now always visible in the Library Manager.
* Volume changes via `IAudioPlayer.SetVolume()` now carry across loop handovers, so fades stay smooth across sample-accurate loop boundaries.
* [**Virtual AudioTrack recycling**](../designs/audio-mixer.md#virtual-tracks) for generic players — players release their mixer track while inaudible and reacquire one on demand, reducing the number of live mixer tracks.
* Renamed the two Addressable toggles for clarity — the Clips tab toggle is now [**Addressable Audio Clips**](../core-features/addressables.md) and the Overall tab toggle is now **Addressable Entity Asset** (now hidden by default; re-enable under **Tools > BroAudio > Preferences > Addressable Settings**).
* `StopControl` now honors `overrideFade` while a natural fade-out is already in progress, so an explicit stop can shorten or lengthen an in-flight fade.
* Reworked the Chained play mode editor preview to better match runtime behavior (the End clip now plays on stop, and only transitions on the entity preview button).
* Release verbs are now guarded against a torn-down `SoundManager`, so shutdown / domain-reload paths no longer throw.

**Bug Fixes**

* Fixed an IMGUI layout exception when switching `MulticlipsPlayMode`.
* Suppressed the "invalid ID" error when a player is stopped in the same frame it was played.
* Fixed follow-target plays being treated as global by the comb-filtering distance rule.

## Ver. 3.1.2 **(**[**Github**](https://github.com/man572142/Bro_Audio/releases/tag/3.1.1)**,** [**Unity Asset Store**](https://assetstore.unity.com/packages/tools/audio/bro-audio-257362)**)**

#### Upgrading from v2.x.x to v3.x.x

SoundID has been completely rearchitected from an integer-based struct to a direct AudioEntity ScriptableObject reference. That means no more loading every audio asset into memory on startup whether you use it or not. Better runtime performance, better memory usage.

**Breaking Change Notice:** Every SoundID in your project will need to be migrated. But don't worry, a SoundID Upgrader will automatically launch on update and handle the migration for you.\
⚠️Before updating, make sure you have a project backup or everything committed to version control.

**Fixes & Improvements**

* Fix infinite loop in SoundIDUpgrader causing memory exhaustion while searching in the VisualTreeAsset

## Ver. **3.1.1**

**Fixes & Improvements**

* Automatically migrates legacy BroAudio folder layout (Core/Scripts) to the new UPM-compatible structure (Editor/ and Runtime/)
* Enhanced SoundIDUpgrader to only search relevant asset types
* Updated all demo scripts to support both legacy Input system and new Input System
* Ensured PlayableDirector.stopped event is properly unsubscribed in OnDestroy()
* Fixed PlaybackGroupEditor cleanup to prevent memory leaks
* Removed automatic default asset output folder creation feature from initialization

## Ver. **3.1.0**

#### ⚠️ Breaking Change — SoundID Architecture Overhaul

Previously, BroAudio loaded every audio asset into memory on startup regardless of whether it was used. In this update, SoundID has been rearchitected from an integer-based struct to a direct AudioEntity ScriptableObject reference, improving both runtime efficiency and memory usage.\
However, this is a non-backwards-compatible change that affects every SoundID in your project. A migration tool (SoundID Upgrader) will automatically launch and guide you through the upgrade process.

🛑 Before updating, please make sure you have:

* A backup of your project, OR
* All changes committed to version control

#### Fixes & Improvements

* Allow existing AudioEntity assets to be reassigned to an AudioAsset via drag-and-drop
* Fixed position not initialized before playback in Transform-follow mode

## **Ver. 2.2.2**

**New Features**

* Added the OnPause() event to IAudioPlayer
* Added a Prompt For Path On Asset Creation option to Preferences
* Added drag-and-drop support for adding audio assets to the library asset list header

**Fixes & Improvements**

* Fixed an error that occurred when calling UnPause() on a player that was not paused
* Fixed an issue where playback was not scheduled immediately
* Fixed an exception when playing music in chained mode without an end clip
* Fixed a missing HelpBox close button icon in Unity 6.2
* Removed obsolete API IAudioPlayer.OnEndPlaying (use OnEnd instead)
* Use the clip directory as the default save path in the Clip Editor’s Save As dialog
* Ensure UpdateVolume only runs after playback starts
* Clarified the Additional Notes text for Setup Wizard configuration settings

## **Ver. 2.2.1**

**New Features**

* Added a fade-in parameter to the [Play()](../reference/api-documentation/class/broaudio.md#playback) method for overriding a clip’s fade-in setting on start.
* Added SetFadeInEase and SetFadeOutEase to IAudioPlayer for customizing fade easing functions.
* Added [TryGetEntityInfo](../reference/api-documentation/class/broaudio.md#others) for retrieving an AudioEntity’s settings.
* Added a **Manual Initialization** option to **Preferences**.

**Fixes & Improvements**

* Made **Virtual Track Count** editable in [**Preferences**](../core-features/customization.md).
* Fixed an issue where **comb-filtering prevention** wasn’t working properly.
* Fixed **BGM** fading out unexpectedly on the first loop after a transition.
* Fixed an issue where calling UnPause() unexpectedly altered the seamless loop’s fade-in time.

## **Ver. 2.2.0**

**Setup Wizard**\
A quick wizard to help you set preferences so you don’t miss any of BroAudio’s customizable features.

**Individual Audio Effects**\
You can now add audio effects to individual audio players using these new methods:

* `AddChorusEffect`
* `AddDistortionEffect`
* `AddReverbEffect`
* `AddEchoEffect`
* `AddHighPassEffect`
* `AddLowPassEffect`

**This update also brings the** [**Low Pass Filter Curve**](https://docs.unity3d.com/6000.1/Documentation/Manual/class-AudioLowPassFilter.html) **to the** [**Spatial Settings**](../core-features/library-manager/design-the-sound/spatial-and-mix.md#introduction)**.**

_**Other New Features**_

* Drag and drop audio clips directly onto specific audio assets in the Library Manager.
* Added a rule to skip comb filtering prevention when the audio source distance is greater than a specified value.
* Added a closable help box to the Library Manager.

#### Fixes & Improvements

* Improved volume slider binding to use normalized values.
* Fixed an exception that occurred when previewing audio from the SoundID field while the Library Manager was open.

#### Breaking Changes

* The `IPlayableValidator.IsPlayable` method signature has changed to: `IsPlayable(SoundID id, Vector3 position)`
* By default, the comb filtering rule is ignored when the distance between sounds is greater than 0.1.

## **Ver. 2.1.1**

* Only schedule the AudioSource when a clip is assigned to avoid warnings in Unity 6
* Upgrade SoundID usage finder tool
* Add null checks and refactor background logo handling

## **Ver. 2.1.0**

**Chained Play Mode**\
Plays a start sound when triggered, then loops a base sound. An end sound is played when stopped.

**Editor Audio Preview Improvements**\
The preview now responds in real time to volume and pitch adjustments, offering a smoother and more intuitive experience.

* Added copy and paste support for AudioEntity properties ([gif](https://cdn.discordapp.com/attachments/1159325793370001448/1378313619539890327/propertyCopyAndPaste.gif?ex=687eb9d6\&is=687d6856\&hm=f7e67207f347e796fe45b3d031627458d530ccf7725024239cfd01a753b7964b&))
* Added a playback delay option when using "Play On Enable" in the **SoundSource** component
* Added `SetPitch(SoundID)` method
* Added `HasAnyPlayingInstances()` method to check if a sound is currently playing anywhere
* Added a “Find SoundID Usage” tool in the editor ([screenshot](https://media.discordapp.net/attachments/1159329242203570206/1396507366643470408/image.png?ex=687e5656\&is=687d04d6\&hm=ac48efedee32a25daa353b460dd6a83549a1779574169dc34d9084d4a0dd0b4e&=\&format=webp\&quality=lossless\&width=734\&height=629))
* Added a context menu to the asset list in **LibraryManager**

#### Fixes & Improvements

* Fixed exception thrown when calling `Pause()` a second time
* Fixed an issue where the Clip Editor didn't reset values after saving
* Fixed Clip Editor preferences not applying correctly
* Fixed an issue where the `[Tooltip]` attribute was ignored on **SoundID** fields
* Fixed Shuffle Play Mode not reselecting the last used clip after the first selection
* Prevented unexpected clip changes caused by Unity reinitializing the `PropertyDrawer`

## **Ver. 2.0.8**

* Fixed build failure caused by compilation errors

## **Ver. 2.0.7**

* Fixed unpause not working as expected in `SoundSource` component
* Fixed an issue where `SetScheduledEndTime` wasn't working as intended
* Added `IsLoaded()` method for checking whether an entity is loaded by the Addressables
* Added `SoundVolume` and `SpectrumAnalyzer` to the right-click game object creation menu
* Added Pause/Unpause example to the demo scene
* Improved the audio clip picking logic to prevent the clip sequencer and shuffle from updating when the audio clip is unavailable
* Improved error handling for the audio player

## **Ver. 2.0.6**

* Added an automatic process to fix duplicate SoundIDs.
* Fixed LibraryManager unexpectedly getting focus after OnPostprocessAllAssets.
* Exposed copy ID to clipboard feature.

## **Ver. 2.0.5**

* Fixed an issue with the audio player object pool not working properly
* Fixed an issue where the instance wrapper was not properly recycled
* Slightly reduced the overhead of assigning an audio track to the audio player
* Added editor support for user-customizable fields in SoundSource

## **Ver. 2.0.4**

* Fixed an issue where copying or duplicating an entity resulted in an invalid `SoundID`

## **Ver. 2.0.3**

#### Behavior Changes

* The `Play()` method no longer resumes paused audio. Use `UnPause()` instead.

#### New Features

* Added a configurable audio UpdateMode to support unscaled time transitions.
* Added undo/redo functionality for volume adjustments in the Clip Editor.
* Added `Pause()` and `UnPause()` methods to the `SoundSource` component.
* Added a URP demo scene and reduced demo texture sizes.

#### Fixes & Improvements

* GC has been significantly reduced across the asset.
* Improved audio track handling by directly adjusting the volume on the `AudioSource` when all available audio tracks have been used up.
* Fixed a `StackOverflowException` that occurred when the topmost rule was not overridden.
* Fixed an issue where setting the master volume had no effect in WebGL.

## **Ver. 2.0.2**

#### Behavior Changes

* The `Play()` method on the **SoundSource** component has changed to use the current `Position Mode` settings instead of defaulting to global.
* **SoundSource** component now stops the current playback before playing a new one to prevent losing control and reference to the previous play.

#### Fixes & Improvements

* Added `PlayGlobally()` to **SoundSource** to play sounds globally, ignoring `Position Mode`.
* Added missing Undo/Redo functionality to the ClipEditor.
* Added `Pause()` and `UnPause()` public static methods with an audio type parameter.
* Added open the last edited asset preference option for LibraryManager.
* Added a preference setting for showing the play button when entities are collapsed in LibraryManager.
* Exposed the override playback group on SoundSource component and added tooltips for all properties.
* Fixed an issue where audio track changed unexpectedly after unpausing.
* Fixed a LayoutWindow exception at project launch by removing an unnecessary asset-saving process.
* Fixed comb filtering time not working correctly when set to zero.
* Fixed a warning appearing incorrectly when SoundSource tried to display the debugging player.
* Renamed and added a tooltip for the spatial setting field.
* Removed the non-2D spatialBlend warning.
* Improved public API summaries.

## **Ver. 2.0.1**

* Fixed a stack overflow issue that occurred when no groups were assigned
* Fixed an issue where [PlaybackGroup](../core-features/playback-group.md) was unintentionally saved to the ScriptableObject in play mode
* Fixed a conflict in LibraryManager where the Delete key was used for both deleting entities and removing object references
* Fixed an issue where the dominator was not set to the correct track

## **Ver. 2.0.0**

#### New Features

* Added [**Playback Group**](../core-features/playback-group.md) feature
* Added support for [**Addressables**](../core-features/addressables.md)
* [**SpectrumAnalyzer**](../core-features/no-code-components/spectrum-analyzer.md) component is now fully released

#### New APIs

* Added `SetScheduledStartTime()` and `SetScheduledEndTime()` to **IAudioPlayer** for scheduling playback
* Added `SetDelay()` to **IAudioPlayer** for delaying playback
* Added `CurrentPlayingClip` property to **IAudioPlayer** for accessing clip information (e.g., `StartPosition`, `FadeOut`, etc.)
* Added `IsPlaying()` property to **SoundSource** component

#### Improvements

* Added a **\[None]** option to the **SoundID** dropdown menu
* Clicking on the label now expands an audio entity in Library Manager
* Added right-click menus and shortcuts in LibraryManager for **duplicating** and **removing** entities
* Implemented an automatic cleanup process for demo audio assets if they are not imported
* Added **undo/redo** functionality to the **Preferences** window
* Added editor preferences to the **Audio Clip Editor**
* Reordered [`SetPitch()`](../reference/api-documentation/class/broaudio.md#public-methods) method arguments to fix inconsistency with other APIs
* Improved user data generation process
* Various code refactoring and optimizations

#### Fixes

* Fixed an issue where changing the master volume in WebGL did not work as expected
* Fixed APIs that couldn't avoid accessing a recycled player
* Fixed an issue where editor assets were mistakenly included in the build; they have been moved to **Editor/Resources**
* Fixed **MusicPlayer** and **DominatorPlayer** not updating their instances during seamless looping
* Fixed an issue where an audio player couldn't be stopped if it was still waiting for delayed playback

## **Ver. 1.15**

* Fixed `Pause()` and `Stop()` method's override fadieTime doesn't work with a value of 0
* Fixed drag and drop with MultipleForEach option doesn't work as intended
* Added a debug object reference to SoundID

## **Ver. 1.14**

* Fixed `SoundID` doesn't work as intended when it's in an array
* Fixed `OnEnd` is executed on the first loop of the play

## **Ver. 1.13**

* Fixed the volume dominator that wasn't working as intended
* Fixed the GUIStyle failing to initialize during project launch
* Fixed player never finishes playing when the start position is greater than 0
* Improved BroAudioType check

## **Ver. 1.12**

**Breaking Change**

The minimum supported Unity version is now set to 2020.2, and the code base has been upgraded to C# 8.0.

**Play Mode**

* Added velocity play mode.
* Added APIs for resetting clip sequence/shuffle data.

**No-Code Components**

* Added `SoundVolume` component to help create volume configurations without coding.
* Added a preview version of `SpectrumAnalyzer` component.

**API**

* Added `GetAudioClip` and `Play` extension method to `SoundID`.
* Added message type methods:`OnStart`, `OnUpdate`, `OnEnd`, and `OnAudioFilterRead` to `IAudioPlayer`.
* Added audio analysis methods: `GetOutputData` and `GetSpectrumData` to `IAudioPlayer`.
* Added `UnPause()` methods.
* Added `AudioSourceProxy` to `IAudioPlayer` to safely access `AudioSource` and Unity APIs.
* Deprecated `OnEndPlaying` event (replaced with `OnEnd`).

**Other New Things**

* Added a runtime-only audio player object field in the sound source inspector for debugging purposes.
* Added expand/collapse all entities feature to `LibraryManager`.

**Fixes and Improvements**

* Fixed missing `BroAudioData` when upgrading from a different path.
* Improved GUI UX and drawing precision.
* Fixed issue where preview audio with pitch did not work as intended during fading processes.
* Updated demo scene to showcase all new features.
* Fixed slider float field clipping when the `[Volume]` and `[Frequency]` attributes were used in a sub-structure.
* Improved shuffle picking algorithm to prevent repetition.

## **Ver. 1.11**

* Fixed an issue where the fading used for BGM transitions was applied to later loops.
* Fixed SoundID dropdown menu failed to open if there were any empty audio assets.
* Fixed SoundManager would overwrite the current reference of BroAudioData after upgrading BroAudio.

## **Ver. 1.10**

* Fixed issue where switching tabs in the LibraryManager caused the scroll position to change significantly.
* Fixed SetEffect() with Dominator resulting the effect doesn’t get reset issue.
* Fixed audioTypeVolume not resetting when the player finishes playing.
* Added a preview button to the SoundID inspector.
* Added an ID to the entity option menu for debugging purposes.
* Added a fade-out setting for the “Stop On Disable” feature on the SoundSource.
* Changed the audio player follow target design to fix the issue with players being destroyed.

## **Ver. 1.09**

* Fixed SetVolume with fading doesn’t work as intended
* Avoid making changes to source control after using the audio preview

## **Ver. 1.08**

* Added LibraryManager shortcut to SoundID dropdown button in the inspector
* Added SoundSource to GameObject menu item for easy creation
* Added SoundSource replay mechanic
* Fixed popup sound issue when previewing audio in the LibraryManger
* Fixed audio type set volume not working as intended and refactored the volume system
* Fixed set master volume in WebGL not working as intended
* Tidy up script assemblies and namespaces
* Performance optimizations and GC reduction

## **Ver. 1.07**

* Fixed comb filtering prevention not working as intended
* Change the logo file and GUID to avoid errors when building the project

## **Ver. 1.06**

**Fixes**

* Fixed changes to BroAudioData isn't saved to the disk
* Fixed pitch setting does not apply to preview audio completely
* Changed SoundSource script icon to the non-Editor one

**Improvements**

* Added WebGL constraint on volume attribute
* Added a known issues page to the info window

## **Ver. 1.05**

New Features

* **Preview Audio**: Preview auido in edit-mode with the current setting like fade-in, fade-out, random… etc.
* **Shuffle Play Mode**: Plays audio clips randomly, ensuring that no clip is repeated until all clips in the list have been played
* **Quick AudioEnity Preference Setting**: a new button is added to the entity view in the LibraryManager, which allows you to remove entity, and change the displayed(exposed) properties setting of the AudioType

New APIs and functionalities

* **Added SetPitch() to BroAudio and IAudioPlayer**, and implemented it in the Demo
* Added OnEndPlaying event to IAudioPlayer
* Extended SoundID’s functionality
* Added pitch attribute

Improvements

* **Implemented functionality to auto-generate user data to avoid overwriting existing data when upgrading BroAudio**
* Reduced playback latency and improve accuracy by moving the play function from next frame to the end of the current frame
* Added audio player pool size field to Preference and slightly improve the player pool performance
* Always display(expose) non-default property value in the LibraryManager
* Increased volume random range slider accuracy
* Copied Logo\_Main.png to non-Editor folder and update Demo scene reference

Fixes

* Fixed compile error occur when building the project
* Fixed comb filtering prevention rejecting all playback when Time.timeScale = 0
* Fixed SetVolume() not applying to the seamless loop
* Fixed asset renaming doesn't work as intended after Unity 2023
* Fixed asset deleting doesn’t work as intended

## **Ver. 1.04**

* Adjust the public methods of SoundSource to better match the intended design.
* Improved the AudioPlyer object pool design to avoid accessing a recycled object.
* Added a warning when trying to access a recycled player, and a preference setting related to it

## **Ver. 1.03**

* Fix compile errors in WebGL

## **Ver. 1.02**

* Fix errors occur if there are any missing auido assets when opening Library Manager
* Fix error occurs when stopping a sound manually
* Fix comb filitering cannot be disable
* Fix error occurs after renaming a asset due to the unmatch filename issue
* Change log comb filtering warning message’s default setting
* Add a clickable link to documentation for comb filtering instruction
* Add Stop() and Pause() methods to IAudioPlayer

## **Ver. 1.01**

* Solved the Unity 2021 Reverb Zone Issue
* Fixed issue where entities could not be reordered in the LibraryManager
* Removed BroLog.dll
* Performance optimization on the editor
