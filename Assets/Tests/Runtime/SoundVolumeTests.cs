using System;
using System.Collections;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SoundVolume"/> no-code component: a settings array, each entry binding one
    /// BroAudioType to a volume value that is pushed to the per-type system volume on enable, optionally
    /// mirrored on a UI Slider, optionally gated to fire only once, and optionally restored to what it was
    /// when the component recorded it.
    /// <para>
    /// Modeled on SoundSourceTests.cs's structure and idioms. Unlike SoundSource, a <see cref="SoundVolume.Setting"/>
    /// is a plain (non-Unity) serializable class, so it's built directly with <c>new</c> and its private
    /// fields are written the same way SoundSource's are - via TestAudioLibrary.SetPrivateField and each
    /// type's own nested NameOf class.
    /// </para>
    /// <para>
    /// The component has two halves and both are covered here. The *system* half is observable through
    /// <see cref="SoundManager.TryGetAudioTypePref"/> (per-type) or the mixer's Master parameter (the
    /// composite <see cref="BroAudioType.All"/> case), exactly as in VolumePitchMixerTests. The *slider*
    /// half needs <c>UnityEngine.UI</c>, which Tests.asmdef now references: asmdef references are not
    /// transitive, so referencing BroAudio does not bring UnityEngine.UI into scope even though
    /// SoundVolume.cs uses <see cref="Slider"/> directly and ungated.
    /// </para>
    /// </summary>
    public class SoundVolumeTests : BroAudioTestFixture
    {
        // SetVolumeToSlider rounds to SoundVolume.RoundingDigits (3), which can only ever move a slider
        // value by up to 5e-4. Asserting the rounded expectation this tightly is what separates "rounded"
        // from "not rounded" - a looser tolerance would accept both.
        private const float RoundingTolerance = 1e-5f;

        /// <summary>A single Setting entry, with an optional Slider bound to it.</summary>
        private static SoundVolume.Setting NewSetting(BroAudioType audioType, float volume, Slider slider = null)
        {
            var setting = new SoundVolume.Setting();
            TestAudioLibrary.SetPrivateField(setting, SoundVolume.Setting.NameOf.AudioType, audioType);
            TestAudioLibrary.SetPrivateField(setting, SoundVolume.Setting.NameOf.Volume, volume);
            TestAudioLibrary.SetPrivateField(setting, SoundVolume.Setting.NameOf.Slider, slider);
            return setting;
        }

        /// <summary>The Setting's own current volume - written by the slider listener, with no getter of its own.</summary>
        private static float VolumeOf(SoundVolume.Setting setting)
            => TestAudioLibrary.GetPrivateField<float>(setting, SoundVolume.Setting.NameOf.Volume);

        /// <summary>
        /// A bare 0..1 Slider with no fill or handle - enough for value, normalizedValue and onValueChanged,
        /// which is all SoundVolume touches. RectTransform is passed up front because Slider requires one.
        /// </summary>
        private Slider NewSlider(float value = 0f)
        {
            GameObject host = Track(new GameObject("VolumeSlider", typeof(RectTransform)));
            Slider slider = host.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.SetValueWithoutNotify(value);
            return slider;
        }

        /// <summary>
        /// Builds a tracked SoundVolume with its serialized fields already written.
        /// <para>
        /// Same gotcha as SoundSourceTests.NewSource: AddComponent on an *active* GameObject runs OnEnable
        /// immediately, so the host is built deactivated, fields are written, and only then activated -
        /// otherwise the first OnEnable would fire before _settings is assigned.
        /// </para>
        /// </summary>
        private SoundVolume NewVolume(SoundVolume.Setting[] settings, bool applyOnEnable = false, bool onlyApplyOnce = false,
            bool resetOnDisable = false, SliderType sliderType = SliderType.BroVolume, bool allowBoost = false)
        {
            GameObject host = Track(new GameObject("SoundVolumeHost"));
            host.SetActive(false);

            SoundVolume volume = host.AddComponent<SoundVolume>();
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.Settings, settings);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.ApplyOnEnable, applyOnEnable);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.OnlyApplyOnce, onlyApplyOnce);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.ResetOnDisable, resetOnDisable);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.SliderType, sliderType);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.AllowBoost, allowBoost);

            host.SetActive(true);
            return volume;
        }

        private static float SystemVolumeOf(BroAudioType audioType)
        {
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref),
                $"No playback preference exists for {audioType}.");
            return pref.Volume;
        }

        #region Apply to the system
        // Apply On Enable: OnEnable calls Setting.ApplyVolumeToSystem, which is BroAudio.SetVolume(audioType,
        // volume, fadeTime) - a synchronous write into AudioTypePlaybackPreference, so it's observable the
        // same frame via TryGetAudioTypePref, exactly like VolumePitchMixerTests' per-type volume tests.
        [UnityTest]
        public IEnumerator OnEnable_WithApplyOnEnable_PushesTheConfiguredVolumeToTheSystem()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.55f);
            NewVolume(new[] { setting }, applyOnEnable: true);

            yield return WaitFrames(1);

            Assert.AreEqual(0.55f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Apply On Enable must push the Setting's configured volume to the matching BroAudioType.");
        }

        // Without Apply On Enable the component is inert until something moves its slider: enabling it must
        // not touch the system volume at all.
        [UnityTest]
        public IEnumerator OnEnable_WithoutApplyOnEnable_LeavesTheSystemVolumeAlone()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.2f);
            NewVolume(new[] { setting }, applyOnEnable: false);

            yield return WaitFrames(2);

            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "A SoundVolume with Apply On Enable off must not write anything on enable.");
        }

        // Only Apply Once: SoundVolume._hasApplyOnce is a single flag on the component (not per-Setting),
        // set the first time OnEnable applies, so a second OnEnable is silently skipped for the rest of
        // that component's life - mirrors SoundSourceTests' OnlyPlayOnce idiom.
        [UnityTest]
        public IEnumerator OnEnable_WithOnlyApplyOnce_NeverReappliesOnASecondEnable()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.4f);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: true, onlyApplyOnce: true);

            yield return WaitFrames(1);
            Assert.AreEqual(0.4f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance, "The first OnEnable must still apply.");

            // Move the system volume away from the Setting's value so a silent second apply would be observable.
            BroAudio.SetVolume(BroAudioType.SFX, 1f, 0f);
            yield return WaitFrames(1);

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);
            volume.gameObject.SetActive(true);
            yield return WaitFrames(1);

            Assert.AreEqual(1f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Only Apply Once must suppress every OnEnable after the first - the volume must stay at what it was set to afterward, not snap back to 0.4.");
        }

        // Every entry in the settings array is applied on enable, each to its own BroAudioType. This is the
        // control case for the Only Apply Once defect pinned below.
        [UnityTest]
        public IEnumerator OnEnable_WithSeveralSettings_AppliesEveryOneOfThem()
        {
            SoundVolume.Setting music = NewSetting(BroAudioType.Music, 0.2f);
            SoundVolume.Setting sfx = NewSetting(BroAudioType.SFX, 0.3f);
            SoundVolume.Setting ui = NewSetting(BroAudioType.UI, 0.4f);
            NewVolume(new[] { music, sfx, ui }, applyOnEnable: true);

            yield return WaitFrames(1);

            Assert.AreEqual(0.2f, SystemVolumeOf(BroAudioType.Music), LinearTolerance, "The first setting must be applied.");
            Assert.AreEqual(0.3f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance, "The second setting must be applied.");
            Assert.AreEqual(0.4f, SystemVolumeOf(BroAudioType.UI), LinearTolerance, "The third setting must be applied.");
        }

        // TEST_FINDINGS #36. _hasApplyOnce is set *inside* the per-setting loop but gates that same loop, so
        // the first entry consumes the one allowed apply and every later entry is skipped - on the very
        // first enable, not just on re-enables. Characterized, not fixed.
        [UnityTest]
        public IEnumerator OnEnable_WithOnlyApplyOnceAndSeveralSettings_AppliesOnlyTheFirstEntry()
        {
            SoundVolume.Setting music = NewSetting(BroAudioType.Music, 0.2f);
            SoundVolume.Setting sfx = NewSetting(BroAudioType.SFX, 0.3f);
            NewVolume(new[] { music, sfx }, applyOnEnable: true, onlyApplyOnce: true);

            yield return WaitFrames(1);

            Assert.AreEqual(0.2f, SystemVolumeOf(BroAudioType.Music), LinearTolerance, "The first setting is applied normally.");
            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Characterizes TEST_FINDINGS #36: _hasApplyOnce is raised by the first entry, so every later entry of the same array is skipped on the first enable.");
        }

        // A Setting's audio type is a [Flags] value, and SetVolume fans a composite flag out over every
        // concrete type it contains (SoundManager.SetPlaybackPrefByType -> ForeachConcreteAudioType).
        [UnityTest]
        public IEnumerator OnEnable_WithACompositeAudioType_AppliesToEveryTypeInTheFlag()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.Music | BroAudioType.UI, 0.3f);
            NewVolume(new[] { setting }, applyOnEnable: true);

            yield return WaitFrames(1);

            Assert.AreEqual(0.3f, SystemVolumeOf(BroAudioType.Music), LinearTolerance, "Music is in the flag, so it must be set.");
            Assert.AreEqual(0.3f, SystemVolumeOf(BroAudioType.UI), LinearTolerance, "UI is in the flag, so it must be set.");
            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "SFX is not in the flag and must be left alone.");
        }

        // TEST_FINDINGS #37. BroAudioType.All is a legal inspector choice, but the two halves of the
        // component read it differently: ApplyVolumeToSystem lands on SoundManager.SetMasterVolume (a mixer
        // parameter), while RecordOrigin/ResetToOrigin only ever walk the per-type preferences. So the
        // master volume is written on enable and never restored on disable.
        [UnityTest]
        public IEnumerator OnEnable_WithAllAudioType_WritesTheMasterVolumeThatResetOnDisableCannotRestore()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.All, 0.3f);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: true, resetOnDisable: true);

            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float appliedDb));
            Assert.AreEqual(0.3f.ToDecibel(), appliedDb, DecibelTolerance,
                "BroAudioType.All routes to the master volume rather than to the per-type preferences.");
            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "The per-type preferences are untouched by a master-volume write.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.MasterTrackName, out float afterDisableDb));
            Assert.AreEqual(0.3f.ToDecibel(), afterDisableDb, DecibelTolerance,
                "Characterizes TEST_FINDINGS #37: Reset On Disable restores per-type volumes only, so an All-typed setting leaves the master volume where it put it.");
        }

        // Reset On Disable: OnEnable's RecordOrigin snapshots the *system's* current per-type volume for
        // every concrete type matching the Setting's BroAudioType (OriginVolumeRecorder reads
        // SoundManager.TryGetAudioTypePref at that moment); OnDisable's ResetToOrigin writes those
        // snapshotted values back via BroAudio.SetVolume, regardless of whatever changed volume in between.
        [UnityTest]
        public IEnumerator OnDisableWithResetOnDisable_RestoresTheSystemVolumeRecordedAtEnable()
        {
            BroAudio.SetVolume(BroAudioType.SFX, 0.7f, 0f); // the "original" system volume, set before the component exists
            yield return WaitFrames(1);

            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.4f); // the Setting's own value is unrelated to the recorded system origin
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: false, resetOnDisable: true);
            yield return WaitFrames(1); // OnEnable's RecordOrigin snapshots 0.7 for SFX

            BroAudio.SetVolume(BroAudioType.SFX, 0.2f, 0f); // mutate the system volume while the component is enabled
            yield return WaitFrames(1);
            Assert.AreEqual(0.2f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance, "Precondition: the system volume actually changed while enabled.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            Assert.AreEqual(0.7f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Reset On Disable must restore the system volume to what it was when the component was enabled, not to the Setting's own configured 0.4 value.");
        }

        // Without Reset On Disable nothing is recorded and nothing is restored - disabling leaves whatever
        // the component (or anyone else) last applied.
        [UnityTest]
        public IEnumerator OnDisable_WithoutResetOnDisable_LeavesTheAppliedVolumeInPlace()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.35f);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: true, resetOnDisable: false);
            yield return WaitFrames(1);

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            Assert.AreEqual(0.35f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Reset On Disable is off, so the applied volume must survive the component being disabled.");
        }
        #endregion

        #region Slider binding
        // The slider mirrors the volume through Utility.VolumeToSlider for the component's SliderType, then
        // rounds to SoundVolume.RoundingDigits. Linear + no boost is the one mapping whose expected value is
        // readable by eye: 0.5 volume sits at InverseLerp(MinVolume, FullVolume, 0.5) ~ 0.49995, which is
        // exactly the case the rounding moves to 0.5.
        [UnityTest]
        public IEnumerator OnEnable_WithApplyOnEnable_MovesTheBoundSliderToTheRoundedSliderValue()
        {
            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.5f, slider);
            NewVolume(new[] { setting }, applyOnEnable: true, sliderType: SliderType.Linear);

            yield return WaitFrames(1);

            float unrounded = Utility.VolumeToSlider(SliderType.Linear, 0.5f, false);
            float expected = (float)Math.Round(unrounded, SoundVolume.RoundingDigits);
            Assert.AreEqual(expected, slider.value, RoundingTolerance,
                $"Apply On Enable must place the slider at the rounded slider value ({expected}), not at the raw conversion ({unrounded}).");
        }

        // The SliderType is the component's, not the slider's, and it is a real curve rather than a
        // pass-through: the same 0.5 volume that sits at ~0.5 on a Linear slider sits far higher on a
        // BroVolume one, because BroVolume maps decibel split points onto even slider steps.
        [UnityTest]
        public IEnumerator OnEnable_WithABroVolumeSliderType_PlacesTheSliderOnTheBroVolumeCurve()
        {
            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.5f, slider);
            NewVolume(new[] { setting }, applyOnEnable: true, sliderType: SliderType.BroVolume);

            yield return WaitFrames(1);

            float expected = (float)Math.Round(Utility.VolumeToSlider(SliderType.BroVolume, 0.5f, false), SoundVolume.RoundingDigits);
            Assert.AreEqual(expected, slider.value, RoundingTolerance, "A BroVolume slider must be placed by the BroVolume conversion.");
            Assert.Greater(slider.value, 0.6f,
                "Sanity check on the curve rather than the call: -6dB is five of BroVolume's six no-boost steps up, nowhere near a linear 0.5.");
        }

        // The other direction: moving the slider runs Setting.OnValueChanged, which converts back through
        // the same SliderType and writes both the Setting's own volume and the system volume.
        [UnityTest]
        public IEnumerator SliderMoved_ConvertsBackAndPushesTheVolumeToTheSystem()
        {
            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 1f, slider);
            NewVolume(new[] { setting }, applyOnEnable: false, sliderType: SliderType.Linear);
            yield return WaitFrames(1);

            slider.value = 0.25f; // notifies, unlike SetValueWithoutNotify
            yield return WaitFrames(1);

            float expected = Utility.SliderToVolume(SliderType.Linear, 0.25f, false);
            Assert.AreEqual(expected, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Moving the slider must push the converted volume to the Setting's BroAudioType.");
            Assert.AreEqual(expected, VolumeOf(setting), LinearTolerance,
                "The Setting must also keep the converted volume, so a later ResetToOrigin has something to undo.");
        }

        // Allow Boost widens the top of the range past full volume: on a Linear slider the maximum maps to
        // AudioConstant.MaxVolume instead of FullVolume, and nothing clamps it back down on the way to the
        // per-type preference.
        [UnityTest]
        public IEnumerator SliderMovedToMaximum_WithAllowBoost_PushesTheBoostedVolume()
        {
            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 1f, slider);
            NewVolume(new[] { setting }, applyOnEnable: false, sliderType: SliderType.Linear, allowBoost: true);
            yield return WaitFrames(1);

            slider.value = 1f;
            yield return WaitFrames(1);

            Assert.AreEqual(AudioConstant.MaxVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "With Allow Boost on, the top of a Linear slider is MaxVolume, and the per-type preference stores it unclamped.");
        }

        // The listener is added in OnEnable and removed in OnDisable, so a disabled component must stop
        // reacting to its slider entirely.
        [UnityTest]
        public IEnumerator SliderMoved_AfterDisable_NoLongerReachesTheSystem()
        {
            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 1f, slider);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: false, sliderType: SliderType.Linear);
            yield return WaitFrames(1);

            slider.value = 0.3f;
            yield return WaitFrames(1);
            Assert.AreEqual(0.3f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance, "Precondition: the listener is live while enabled.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            slider.value = 0.9f;
            yield return WaitFrames(1);

            Assert.AreEqual(0.3f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "OnDisable removes the slider listener, so a later slider move must not reach the system.");
        }

        // Reset On Disable rewinds both halves at once: the Setting's volume (and with it the slider
        // position) goes back to the value it was enabled with, and the system goes back to what it read
        // before the component ever applied. The slider is rewound with SetValueWithoutNotify, so the
        // rewind itself does not re-enter OnValueChanged.
        [UnityTest]
        public IEnumerator OnDisable_WithResetOnDisable_RewindsBothTheSliderAndTheSystem()
        {
            BroAudio.SetVolume(BroAudioType.SFX, 0.9f, 0f);
            yield return WaitFrames(1);

            Slider slider = NewSlider();
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.4f, slider);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: true, resetOnDisable: true, sliderType: SliderType.Linear);
            yield return WaitFrames(1);

            slider.value = 0.8f; // the player drags it somewhere else
            yield return WaitFrames(1);
            Assert.AreEqual(0.8f, VolumeOf(setting), LinearTolerance, "Precondition: the drag moved the Setting's volume.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            float expectedSlider = (float)Math.Round(Utility.VolumeToSlider(SliderType.Linear, 0.4f, false), SoundVolume.RoundingDigits);
            Assert.AreEqual(0.4f, VolumeOf(setting), LinearTolerance, "ResetToOrigin restores the volume the Setting was enabled with.");
            Assert.AreEqual(expectedSlider, slider.value, RoundingTolerance, "The bound slider is rewound to match the restored volume.");
            Assert.AreEqual(0.9f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "The system goes back to the volume recorded at enable (0.9), not to the Setting's own restored 0.4.");
        }

        // Every slider path is null-guarded (`if (_slider)`), which is what lets a Setting be used purely as
        // a scripted volume preset. Enabling, applying and disabling with no slider must stay silent.
        [UnityTest]
        public IEnumerator SettingWithNoSlider_AppliesAndResetsWithoutTouchingAnySlider()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.45f);
            SoundVolume volume = NewVolume(new[] { setting }, applyOnEnable: true, resetOnDisable: true);
            yield return WaitFrames(1);

            Assert.AreEqual(0.45f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance, "A sliderless Setting still applies to the system.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "And it still restores the recorded system volume on disable.");
        }
        #endregion
    }
}
