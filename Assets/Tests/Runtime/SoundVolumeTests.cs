using System.Collections;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// The <see cref="SoundVolume"/> no-code component: a settings array, each entry binding one
    /// BroAudioType to a volume value that is pushed to the per-type system volume on enable, optionally
    /// gated to fire only once, and optionally restored to what it was when the component records it.
    /// <para>
    /// Modeled on SoundSourceTests.cs's structure and idioms. Unlike SoundSource, a <see cref="SoundVolume.Setting"/>
    /// is a plain (non-Unity) serializable class, so it's built directly with <c>new</c> and its private
    /// fields are written the same way SoundSource's are - via TestAudioLibrary.SetPrivateField and each
    /// type's own nested NameOf class.
    /// </para>
    /// <para>
    /// Slider binding (SetVolumeToSlider/OnValueChanged rounding to RoundingDigits) is NOT covered here:
    /// Tests.asmdef has "overrideReferences": true and its "references" list has no entry for
    /// UnityEngine.UI, even though BroAudio.asmdef itself references it (SoundVolume.cs uses
    /// UnityEngine.UI.Slider directly, ungated). BroAudio.asmdef is auto-referenced, so that reference is
    /// picked up there automatically, but overrideReferences on Tests.asmdef turns auto-referencing off,
    /// so Tests.asmdef cannot see UnityEngine.UI without an explicit reference. Adding a package/asmdef
    /// reference is outside this task's boundaries, so every test here builds a Setting with no Slider
    /// assigned (SoundVolume.cs itself null-guards every `if (_slider)` use) and asserts only the
    /// system-volume side of the contract. Report this as an asmdef gap rather than editing it.
    /// </para>
    /// </summary>
    public class SoundVolumeTests : BroAudioTestFixture
    {
        private const float LinearTolerance = 0.01f;

        /// <summary>A single Setting entry with no Slider assigned - the system-volume half of the contract.</summary>
        private static SoundVolume.Setting NewSetting(BroAudioType audioType, float volume)
        {
            var setting = new SoundVolume.Setting();
            TestAudioLibrary.SetPrivateField(setting, SoundVolume.Setting.NameOf.AudioType, audioType);
            TestAudioLibrary.SetPrivateField(setting, SoundVolume.Setting.NameOf.Volume, volume);
            return setting;
        }

        /// <summary>
        /// Builds a tracked SoundVolume with its serialized fields already written.
        /// <para>
        /// Same gotcha as SoundSourceTests.NewSource: AddComponent on an *active* GameObject runs OnEnable
        /// immediately, so the host is built deactivated, fields are written, and only then activated -
        /// otherwise the first OnEnable would fire before _settings is assigned.
        /// </para>
        /// </summary>
        private SoundVolume NewVolume(SoundVolume.Setting[] settings, bool applyOnEnable = false, bool onlyApplyOnce = false, bool resetOnDisable = false)
        {
            GameObject host = Track(new GameObject("SoundVolumeHost"));
            host.SetActive(false);

            SoundVolume volume = host.AddComponent<SoundVolume>();
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.Settings, settings);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.ApplyOnEnable, applyOnEnable);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.OnlyApplyOnce, onlyApplyOnce);
            TestAudioLibrary.SetPrivateField(volume, SoundVolume.NameOf.ResetOnDisable, resetOnDisable);

            host.SetActive(true);
            return volume;
        }

        // Apply On Enable: OnEnable calls Setting.ApplyVolumeToSystem, which is BroAudio.SetVolume(audioType,
        // volume, fadeTime) - a synchronous write into AudioTypePlaybackPreference, so it's observable the
        // same frame via TryGetAudioTypePref, exactly like VolumePitchMixerTests' per-type volume tests.
        [UnityTest]
        public IEnumerator OnEnable_WithApplyOnEnable_PushesTheConfiguredVolumeToTheSystem()
        {
            SoundVolume.Setting setting = NewSetting(BroAudioType.SFX, 0.55f);
            NewVolume(new[] { setting }, applyOnEnable: true);

            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(0.55f, pref.Volume, LinearTolerance, "Apply On Enable must push the Setting's configured volume to the matching BroAudioType.");
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
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(0.4f, pref.Volume, LinearTolerance, "The first OnEnable must still apply.");

            // Move the system volume away from the Setting's value so a silent second apply would be observable.
            BroAudio.SetVolume(BroAudioType.SFX, 1f, 0f);
            yield return WaitFrames(1);

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);
            volume.gameObject.SetActive(true);
            yield return WaitFrames(1);

            Assert.AreEqual(1f, pref.Volume, LinearTolerance, "Only Apply Once must suppress every OnEnable after the first - the volume must stay at what it was set to afterward, not snap back to 0.4.");
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
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(0.2f, pref.Volume, LinearTolerance, "Precondition: the system volume actually changed while enabled.");

            volume.gameObject.SetActive(false);
            yield return WaitFrames(1);

            Assert.AreEqual(0.7f, pref.Volume, LinearTolerance, "Reset On Disable must restore the system volume to what it was when the component was enabled, not to the Setting's own configured 0.4 value.");
        }
    }
}
