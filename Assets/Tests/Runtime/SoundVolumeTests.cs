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
    /// Pins the <see cref="SoundVolume"/> component's two halves: the system volume (per-type prefs, or the
    /// Master parameter for <see cref="BroAudioType.All"/>) and the bound <see cref="Slider"/>. A
    /// <see cref="SoundVolume.Setting"/> is a plain class, so it's built with <c>new</c>.
    /// <para>
    /// Tests.asmdef must reference UnityEngine.UI itself: asmdef references are not transitive.
    /// </para>
    /// </summary>
    public class SoundVolumeTests : BroAudioTestFixture
    {
        // Rounding moves a slider value by at most 5e-4; anything looser would accept unrounded values too.
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

        /// <summary>A bare 0..1 Slider - all SoundVolume touches. Slider requires a RectTransform.</summary>
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
        /// Builds a tracked SoundVolume. The host starts inactive: AddComponent on an active GameObject runs
        /// OnEnable before the fields are written.
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
        [UnityTest]
        public IEnumerator OnEnable_WithApplyOnEnable_PushesTheConfiguredVolumeToTheSystem()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.55f);
            NewVolume(new[] { setting }, applyOnEnable: true);

            yield return WaitFrames(1);

            Assert.AreEqual(0.55f, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "Apply On Enable must push the Setting's configured volume to the matching BroAudioType.");
        }

        [UnityTest]
        public IEnumerator OnEnable_WithoutApplyOnEnable_LeavesTheSystemVolumeAlone()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.2f);
            NewVolume(new[] { setting }, applyOnEnable: false);

            yield return WaitFrames(2);

            Assert.AreEqual(AudioConstant.FullVolume, SystemVolumeOf(BroAudioType.SFX), LinearTolerance,
                "A SoundVolume with Apply On Enable off must not write anything on enable.");
        }

        // _hasApplyOnce is one flag per component, not per Setting.
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

        // Control case for TEST_FINDINGS #36.
        [UnityTest]
        [Category("Finding_36")]
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

        // Pins TEST_FINDINGS #36.
        [UnityTest]
        [Category("Finding_36")]
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

        // Pins TEST_FINDINGS #37.
        [UnityTest]
        [Category("Finding_37")]
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

        // The origin is the system's per-type volume at enable, not the Setting's own value.
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
        // Linear, no boost: 0.5 converts to ~0.49995, which rounding moves to 0.5.
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

        // BroVolume maps dB split points onto even steps, so 0.5 sits far above where Linear puts it.
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

        // The slider is rewound with SetValueWithoutNotify, so the rewind does not re-enter OnValueChanged.
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

        // Null-guarded slider paths let a Setting serve as a scripted volume preset.
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
