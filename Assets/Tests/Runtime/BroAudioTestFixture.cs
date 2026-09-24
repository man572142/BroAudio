using System;
using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Base fixture for every PlayMode test.
    /// <para>
    /// SoundManager is a DontDestroyOnLoad singleton that survives the whole run, so isolation is solved
    /// here once: unpause the game clock, stop everything and wait until the player pool has drained,
    /// destroy what the test created, wait out the master fade, put the mixer's effect parameters, the
    /// on-disk RuntimeSetting and the volumes back, then check that the global state reads its defaults.
    /// Do not re-solve it per test file.
    /// </para>
    /// <para>
    /// Every reset here is unconditional - a test that failed half-way through is the one most likely to
    /// have left something behind - and each drain reports what survived rather than assuming it died,
    /// naming the test that leaked it (see <see cref="BroAudioTearDown"/>). Each of them costs nothing
    /// (the player pool) or one or two frames (the mixer parameters) in the ordinary case where the test
    /// left nothing behind; only an actual leak makes teardown wait.
    /// </para>
    /// <para>
    /// Timing rule: fade progress accumulates capped Time.deltaTime (at most Time.maximumDeltaTime, ~0.333s,
    /// per frame) while timeouts and the DSP clock run on wall time, so one slow frame can move a sample point
    /// a third of a second against its boundary. Keep every decisive assertion window at least 1s wide.
    /// </para>
    /// </summary>
    public abstract class BroAudioTestFixture
    {
        /// <summary>Concrete audio types, i.e. All without the composite flag.</summary>
        protected static readonly BroAudioType[] ConcreteAudioTypes = TestAudioLibrary.ConcreteAudioTypes;

        /// <summary>
        /// Linear volume products are exact float multiplication (fadeTime 0 uses Fader.Complete), so a
        /// tight tolerance is fine.
        /// </summary>
        protected const float LinearTolerance = 0.01f;

        /// <summary>dB values go through a log conversion plus a mixer round-trip, so they need a looser tolerance.</summary>
        protected const float DecibelTolerance = 0.1f;

        private static AudioListener _listener;

        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();
        private readonly List<Action<IAudioPlayer>> _bgmSubscriptions = new List<Action<IAudioPlayer>>();
        private string _settingSnapshot;

        [UnitySetUp]
        public IEnumerator BroAudioSetUp()
        {
            // The PlayMode test scene is empty, so Unity warns on every voice unless a listener exists.
            // One DontDestroyOnLoad listener serves the whole run; it is deliberately never destroyed.
            if (!_listener)
            {
                GameObject listenerObject = new GameObject("TestAudioListener");
                UnityEngine.Object.DontDestroyOnLoad(listenerObject);
                _listener = listenerObject.AddComponent<AudioListener>();
            }

#if BroAudio_InitManually
            // Under manual init nothing bootstraps the manager on its own - the wait below would time out and
            // every PlayMode test, TeardownTests' BroAudio_InitManually branches included, would fail in setup.
            // A project on this define calls BroAudio.Init() itself; the suite does the same, once. Guarded
            // because Init() has no "already have one" check and would leak a second manager.
            if (!SoundManager.HasInstance)
            {
                BroAudio.Init();
            }
#endif

            // AudioMixer.SetFloat silently fails on the first Play Mode frame; SoundManager clears it in Start().
            yield return null;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!SoundManager.HasInstance)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "SoundManager never bootstrapped.");
                yield return null;
            }

            _settingSnapshot = JsonUtility.ToJson(SoundManager.Instance.Setting);
            FactoryGlobalPlaybackGroup = Track(ScriptableObject.CreateInstance<DefaultPlaybackGroup>());
            FactoryGlobalPlaybackGroup.name = BroName.GlobalPlaybackGroupName;
            ApplyFactoryRuntimeSetting(SoundManager.Instance.Setting, FactoryGlobalPlaybackGroup);
            yield return null;
        }

        /// <summary>
        /// The <see cref="RuntimeSetting.GlobalPlaybackGroup"/> every test runs under: a fresh
        /// <see cref="DefaultPlaybackGroup"/> with its field initializers untouched, which is exactly what
        /// BroUserDataGenerator creates for a new project - so the suite runs the configuration users ship
        /// (a 0.04s comb-filtering window, same-frame plays not exempt, no voice limit), not a null group.
        /// <para>
        /// It reaches an entity only through <see cref="AudioAsset.PlaybackGroup"/> or as the parent a custom
        /// group falls back to for a rule it does not override. <see cref="NewEntity"/>/<see cref="NewSound"/>
        /// build entities with no AudioAsset and so stay outside it; build with
        /// <see cref="NewAssetBackedEntity"/>/<see cref="NewAssetBackedSound"/> to play under it, as every
        /// Library Manager entity does. Fresh per test and destroyed in TearDown, so its playing count and
        /// lazily built rule list never carry over.
        /// </para>
        /// </summary>
        protected DefaultPlaybackGroup FactoryGlobalPlaybackGroup { get; private set; }

        /// <summary>
        /// Puts every RuntimeSetting field a test can observe at its factory value, after the snapshot above has
        /// saved the developer's own. The asset lives under the gitignored Resources folder, so each checkout
        /// carries its own copy: CI generates a factory one, while a developer's may have a different fade ease,
        /// update mode or global playback group. Timing windows across the suite are derived from the factory
        /// curves, and a test must see the same settings on every machine. A test that needs another value sets
        /// it in its own body; TearDown puts the developer's asset back either way.
        /// <para>
        /// ResetToFactorySettings covers the playback toggles; the fields it leaves alone are written here. The
        /// obsolete CombFilteringPreventionInSeconds is read by nothing. DefaultAudioPlayerPoolSize is read only
        /// when a SoundManager bootstraps: the run's first manager has already read the developer's value by the
        /// time this runs, so the reset reaches only a manager rebuilt mid-run (TeardownTests re-initializes one),
        /// which then caps its idle player pool at the factory size instead of the developer's. Nothing in the
        /// suite depends on that cap: the pool instantiates a player whenever none is idle.
        /// </para>
        /// </summary>
        private static void ApplyFactoryRuntimeSetting(RuntimeSetting setting, PlaybackGroup globalPlaybackGroup)
        {
#if UNITY_EDITOR
            setting.ResetToFactorySettings();
#endif
            setting.LogAccessRecycledPlayerWarning = true;
            setting.UpdateMode = RuntimeSetting.FactorySettings.UpdateMode;
            setting.GlobalPlaybackGroup = globalPlaybackGroup;
            setting.AddressablesNonPreloadedLogLevel = RuntimeSetting.FactorySettings.AddressablesNonPreloadedLogLevel;
        }

        /// <summary>
        /// Realtime budget for each of the two drains below. Both only have to outlast state a test left
        /// in flight on purpose - the longest fade any fixture here starts is about a second - so this is
        /// several times the worst case: wide enough that a slow frame cannot trip it, tight enough to
        /// report the leak instead of hanging the run.
        /// </summary>
        private const float DrainTimeoutSeconds = 5f;

        /// <summary>
        /// Identical Master readings that end the drain once movement has been seen. A longer quiet run is
        /// demanded there because a fade's own ease can land two adjacent frames on the same float near the
        /// end of its curve, which would read as settled while the ramp is still going.
        /// </summary>
        private const int SteadyMasterFrames = 5;

        /// <summary>
        /// Identical Master readings that end the drain when no movement was ever seen - the case for
        /// almost every test in the suite, which never touches the master volume at all. Two rather than
        /// <see cref="SteadyMasterFrames"/>, because this is the price every test pays: a fade that is
        /// genuinely running moves the parameter by far more than a float epsilon per frame, so one
        /// comparison is enough to notice it and switch to the longer count.
        /// </summary>
        private const int QuietMasterFrames = 2;

        /// <summary>
        /// The suffix EffectAutomationHelper.Tweak appends for the second pole of a FourPole filter, i.e.
        /// the Effect_LowPass2 / Effect_HighPass2 exposed parameters.
        /// </summary>
        private const string SecondaryEffectParaSuffix = "2";

        /// <summary>Hz. A cutoff this close to its default is back at its default for isolation purposes.</summary>
        private const float EffectFrequencyTolerance = 1f;

        /// <summary>Prefix for the teardown's own log lines, so they are greppable and never mistaken for BroAudio's.</summary>
        private const string TestLogTitle = "[BroAudioTests] ";

        [UnityTearDown]
        public IEnumerator BroAudioTearDown()
        {
            // Collected rather than asserted on the spot: an Assert.Fail here would abandon the rest of
            // this method, and the resets below are exactly what keeps one leak from becoming every later
            // test's problem. Everything runs; the report is the last thing this method does.
            List<string> leaks = new List<string>();

            // First, because nothing below can drain while the game is paused: under the factory-default
            // AudioMixerUpdateMode.Normal, Utility.GetDeltaTime() returns Time.deltaTime, which is 0 at
            // timeScale 0, so every fade freezes instead of finishing. It also keeps the next test's first
            // WaitForSeconds from hanging forever. Note that derived [UnityTearDown]s run BEFORE this one,
            // so a fixture that pauses the game and then waits on *scaled* time in its own teardown still
            // has to restore timeScale itself.
            Time.timeScale = 1f;

            BroAudio.Stop(BroAudioType.All, 0f);
            yield return DrainAudioPlayers(leaks);

            // Before any global state is put back, because a component's OnDisable is itself a writer of
            // global state: a SoundVolume with Reset On Disable calls BroAudio.SetVolume per type from there.
            // Destroyed after the resets below, it would overwrite them and hand its volumes to the next test.
            // Destroy is deferred to the end of the frame, hence the frame. After the player drain, though, so
            // nothing still playing loses its clip or entity underneath it.
            foreach (UnityEngine.Object obj in _createdObjects)
            {
                if (obj)
                {
                    UnityEngine.Object.Destroy(obj);
                }
            }
            _createdObjects.Clear();
            yield return null;

            // After the destruction, so a master fade that an OnDisable started is drained too.
            yield return DrainMasterVolumeFade(leaks);

#if !UNITY_WEBGL
            // Deliberately before the RuntimeSetting restore below: whether resetting a low/high pass also
            // covers the filter's second pole is decided from Setting.AudioFilterSlope as it stands now.
            yield return ResetTrackEffects(leaks);
#endif

            // RuntimeSetting is a real asset on disk - a test that mutates it must not dirty the project.
            // Guarded rather than a bare SoundManager.Instance: that accessor throws once the manager is
            // gone, and this method has to stay a silent no-op both for TeardownTests (which destroys it;
            // its own [UnityTearDown] restores it first, but nothing here may depend on that ordering) and
            // for a run where BroAudioSetUp never got a manager to snapshot in the first place.
            if (SoundManager.HasInstance && _settingSnapshot != null)
            {
                JsonUtility.FromJsonOverwrite(_settingSnapshot, SoundManager.Instance.Setting);
            }

            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                BroAudio.SetVolume(audioType, AudioConstant.FullVolume, 0f);
            }

            // Per-type pitch leaks exactly like per-type volume: SoundManager.SetPitch stores it into
            // AudioTypePlaybackPreference, so every *later* player of that type picks
            // it up through SetInitialPitch. Volume only shifts an amplitude, but pitch rescales duration -
            // a leaked 0.5x makes every later clip run twice as long and moves every duration window in the
            // fade, scheduling and loop tests. Mind the argument order: SetPitch(type, pitch, fadeTime) is
            // the current overload; SetPitch(pitch, type, fadeTime) is [Obsolete].
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                BroAudio.SetPitch(audioType, AudioConstant.DefaultPitch, 0f);
            }

            // BroAudio.OnBGMChanged forwards to a *static* event on MusicPlayer - an un-removed handler
            // outlives the test and fires during every later one.
            foreach (Action<IAudioPlayer> handler in _bgmSubscriptions)
            {
                BroAudio.OnBGMChanged -= handler;
            }
            _bgmSubscriptions.Clear();

            yield return VerifyGlobalStateRestored(leaks);

            ReportLeaks(leaks);
        }

        /// <summary>
        /// Checks that the global state TearDown resets blindly actually reads its default afterwards - the
        /// per-type playback preferences, and the dominator's Main_LowPass / Main_HighPass parameters, which
        /// nothing here resets because DominatorPlayer's automation is supposed to revert them itself once
        /// the drain has stopped its player.
        /// <para>
        /// Polled rather than read once: the per-type volume and pitch are written synchronously above and
        /// normally cost no frame, but a filter tween or a dominator's auto-revert finishes on a later frame.
        /// What survives <see cref="DrainTimeoutSeconds"/> is a leak - either a writer the resets above do not
        /// reach, or a revert that never happened - and is reported by name. The dominator parameters are
        /// then forced back so one broken revert is not inherited by every later test.
        /// </para>
        /// </summary>
        private static IEnumerator VerifyGlobalStateRestored(List<string> leaks)
        {
            if (!SoundManager.HasInstance)
            {
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + DrainTimeoutSeconds;
            List<string> drift = DescribeGlobalStateDrift();
            while (drift.Count > 0)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    leaks.Add($"global state still off its default {DrainTimeoutSeconds}s after TearDown reset it: " +
                              string.Join(", ", drift));
#if !UNITY_WEBGL
                    AudioMixer mixer = SoundManager.Instance.AudioMixer;
                    mixer.SafeSetFloat(BroName.Dominator_LowPassParaName, AudioConstant.MaxFrequency);
                    mixer.SafeSetFloat(BroName.Dominator_HighPassParaName, AudioConstant.MinFrequency);
#endif
                    yield break;
                }

                yield return null;
                if (!SoundManager.HasInstance)
                {
                    yield break;
                }
                drift = DescribeGlobalStateDrift();
            }
        }

        /// <summary>One entry per piece of global state that does not read its default right now.</summary>
        private static List<string> DescribeGlobalStateDrift()
        {
            List<string> drift = new List<string>();
            SoundManager manager = SoundManager.Instance;

            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                if (!manager.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref))
                {
                    continue;
                }

                if (!Mathf.Approximately(pref.Volume, AudioConstant.FullVolume))
                {
                    drift.Add($"{audioType} volume {pref.Volume:F3}");
                }

                if (!Mathf.Approximately(pref.Pitch, AudioConstant.DefaultPitch))
                {
                    drift.Add($"{audioType} pitch {pref.Pitch:F3}");
                }

#if !UNITY_WEBGL
                // Only the two bits ResetTrackEffects clears. EffectType.Volume can be left set on a type by a
                // non-dominator SetEffect(Volume), which nothing resets and nothing reads back.
                EffectType filterBits = pref.EffectType & (EffectType.LowPass | EffectType.HighPass);
                if (filterBits != EffectType.None)
                {
                    drift.Add($"{audioType} still routed through the effect track for {filterBits}");
                }
#endif
            }

#if !UNITY_WEBGL
            AudioMixer mixer = manager.AudioMixer;
            if (mixer)
            {
                if (!IsEffectParameterDefault(mixer, BroName.Dominator_LowPassParaName, AudioConstant.MaxFrequency))
                {
                    mixer.SafeGetFloat(BroName.Dominator_LowPassParaName, out float lowPass);
                    drift.Add($"{BroName.Dominator_LowPassParaName} {lowPass:F0}Hz");
                }

                if (!IsEffectParameterDefault(mixer, BroName.Dominator_HighPassParaName, AudioConstant.MinFrequency))
                {
                    mixer.SafeGetFloat(BroName.Dominator_HighPassParaName, out float highPass);
                    drift.Add($"{BroName.Dominator_HighPassParaName} {highPass:F0}Hz");
                }
            }
#endif
            return drift;
        }

        /// <summary>
        /// Fails the test that leaked, naming it and what survived - a leak has to stop the run at its
        /// source, because by the time it shows up it looks like an unrelated flake somewhere else.
        /// <para>
        /// When the test had already failed, this only warns. NUnit would not lose the original failure
        /// either way (RecordTearDownException prepends the existing message and appends "TearDown : ..."),
        /// but a test that failed mid-body is *expected* to leave playback running, and burying the real
        /// assertion under a teardown error it caused itself helps nobody.
        /// </para>
        /// </summary>
        private static void ReportLeaks(List<string> leaks)
        {
            if (leaks.Count == 0)
            {
                return;
            }

            string report = $"Test isolation leak left by '{TestContext.CurrentContext.Test.Name}': " +
                            string.Join(" | ", leaks) +
                            " - the next test would have inherited it.";

            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
            {
                // Warning, not error: LogAssert turns an unexpected error log into a second failure.
                Debug.LogWarning(TestLogTitle + report + " (Reported as a warning only because the test had already failed - fix the failure first.)");
                return;
            }

            Assert.Fail(report);
        }

        /// <summary>
        /// Waits until SoundManager's player pool is actually empty, i.e. every AudioPlayer that was
        /// checked out has been recycled.
        /// <para>
        /// Stop(All, 0f) alone does not prove that: a scheduled or paused voice, and above all a Chained
        /// entity - whose Stop hands its End clip over to a brand new player that SoundManager.Stop's own
        /// backwards loop cannot reach, because it is appended while that loop is running - can outlive the
        /// call. So the stop is re-issued every frame until the list is empty. That terminates: the
        /// handed-over player is already at PlaybackStage.End, where CanHandoverToEnd() is false.
        /// </para>
        /// </summary>
        private static IEnumerator DrainAudioPlayers(List<string> leaks)
        {
            // TeardownTests destroys the manager; its own [UnityTearDown] restores it before this one runs, but
            // nothing here may depend on that - a missing manager has no pool to leak.
            if (!SoundManager.HasInstance)
            {
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + DrainTimeoutSeconds;
            while (true)
            {
                if (!SoundManager.HasInstance)
                {
                    yield break;
                }
                IReadOnlyList<AudioPlayer> players = CurrentAudioPlayers();

                // Costs no frame at all in the ordinary case: an immediate Stop recycles a player inside
                // the call (TryGetFadeOut is false for fadeTime 0, so StopControl reaches EndPlaying ->
                // Recycle before it ever yields), so the caller's Stop above has usually already emptied
                // the list by the time this first reads it.
                if (players.Count == 0)
                {
                    yield break;
                }

                if (Time.realtimeSinceStartup > deadline)
                {
                    leaks.Add($"{players.Count} audio player(s) still checked out of the pool after {DrainTimeoutSeconds}s of " +
                              $"Stop(All, 0f): {DescribePlayers(players)}");
                    yield break;
                }

                BroAudio.Stop(BroAudioType.All, 0f);
                yield return null;
            }
        }

        /// <summary>Only for the leak report - never on the drain's per-frame path.</summary>
        private static string DescribePlayers(IReadOnlyList<AudioPlayer> players)
        {
            List<string> descriptions = new List<string>(players.Count);
            foreach (AudioPlayer player in players)
            {
                // Unity's own null semantics: a destroyed-but-not-null player is still a leak worth naming.
                if (!player)
                {
                    descriptions.Add("<destroyed player still in the pool's checked-out list>");
                    continue;
                }

                descriptions.Add($"{player.name} (ID:{player.ID}, IsActive:{player.IsActive}, IsPlaying:{player.IsPlaying})");
            }
            return string.Join(", ", descriptions);
        }

        /// <summary>
        /// Waits until nothing is writing the Master mixer parameter any more.
        /// <para>
        /// SoundManager.SetMasterVolume only stops a running fade coroutine on its `fadeTime != 0f` branch,
        /// and returns early when the parameter already reads the requested value - so neither the
        /// SetVolume(FullVolume, 0f) below nor a plain frame wait can cancel a fade a test left in flight,
        /// and it would go on moving Master while the next test asserts on it (Docs/TEST_FINDINGS.md #51).
        /// </para>
        /// <para>
        /// Waiting for the reading to stop moving, rather than for a particular value, is deliberate: a
        /// live fade rewrites Master every frame, so a steady reading is the observable end of the
        /// coroutine whether it completed, was never started, or is still mid-ramp. The quiet run required
        /// widens from <see cref="QuietMasterFrames"/> to <see cref="SteadyMasterFrames"/> as soon as any
        /// movement is seen, so the test that never touched the master volume pays two frames and only a
        /// test that really left a fade running pays for the careful reading.
        /// </para>
        /// <para>
        /// Non-WebGL only in effect: the WebGL branch of SetMasterVolume fades WebGLMasterVolume and the
        /// players' own volumes instead of this parameter. Nothing here misbehaves there, it just has
        /// nothing to observe - and the PlayMode suite runs in the Editor.
        /// </para>
        /// </summary>
        private static IEnumerator DrainMasterVolumeFade(List<string> leaks)
        {
            if (!SoundManager.HasInstance)
            {
                yield break;
            }

            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            if (!mixer || !mixer.SafeGetFloat(BroName.MasterTrackName, out float lastDb))
            {
                // Nothing observable to drain (no mixer, or Master isn't exposed). Every test that cares
                // asserts on that parameter itself, so this stays quiet rather than failing here too.
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + DrainTimeoutSeconds;
            int requiredSteadyFrames = QuietMasterFrames;
            int steadyFrames = 0;
            while (steadyFrames < requiredSteadyFrames)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    leaks.Add($"the master volume was still being rewritten every frame after {DrainTimeoutSeconds}s " +
                              $"(last read {lastDb:F2}dB), i.e. a SetMasterVolume fade is still running");
                    yield break;
                }

                yield return null;
                if (!mixer.SafeGetFloat(BroName.MasterTrackName, out float db))
                {
                    continue;
                }

                if (Mathf.Approximately(db, lastDb))
                {
                    steadyFrames++;
                }
                else
                {
                    // A coroutine is provably writing this parameter, so from here on only a long quiet
                    // run counts as the end of it.
                    requiredSteadyFrames = SteadyMasterFrames;
                    steadyFrames = 0;
                }
                lastDb = db;
            }
        }

#if !UNITY_WEBGL
        /// <summary>
        /// Puts the mixer-routed effects back to their defaults - both halves of what SetEffect changes:
        /// the exposed Effect_* parameters, and the per-type AudioTypePlaybackPreference.EffectType bit
        /// that every future Play() of that type reads through AudioPlayer.Playback's SetTrackEffect. That
        /// pref lives on an in-memory AudioTypePlaybackPreference, not on the RuntimeSetting asset, so the
        /// JSON snapshot restore knows nothing about it.
        /// <para>
        /// Effect.ResetLowPass()/ResetHighPass() are *default-valued* effects, so SoundManager.SetEffect
        /// picks SetEffectMode.Remove, which is what clears that bit. Deliberately NOT
        /// SetEffect(new Effect(EffectType.None)): that would reset every tracked effect in one call, but
        /// `new Effect(EffectType.None)` logs an error from Effect's Value setter as it is constructed, and
        /// ResetAllEffect logs another for every tracked effect whose parameter does not resolve - an
        /// EffectType.Volume entry on a non-Dominator, which AudioEffectTests leaves registered for the
        /// rest of the Editor session. An unexpected error log fails the very test being torn down, so a
        /// shared cleanup path cannot use it.
        /// </para>
        /// <para>
        /// The dominator parameters (Main_LowPass / Main_HighPass / Main_Dominated) are not reset here:
        /// DominatorPlayer chains its effect with .While(PlayerIsPlaying), so the automation is supposed to
        /// reset them itself once the drain above has stopped the player. That this happened for Main_LowPass
        /// and Main_HighPass is checked, not assumed, by <see cref="VerifyGlobalStateRestored"/>.
        /// </para>
        /// <para>
        /// The reset is then verified rather than waited out for a fixed number of frames. A zero-fade
        /// Tweak drains its WaitableList synchronously inside StartCoroutine (Docs/FIXED_ISSUES.md #17), so
        /// the parameters are normally already back before the first read and this costs no frame at all;
        /// what a fixed wait would silently miss is the case that matters, a tweaker still working through
        /// a fade or a pending auto-reset waitable, which SetEffectTrackParameter queues behind rather than
        /// restarting.
        /// </para>
        /// </summary>
        private static IEnumerator ResetTrackEffects(List<string> leaks)
        {
            if (!SoundManager.HasInstance)
            {
                yield break;
            }

            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            BroAudio.SetEffect(Effect.ResetLowPass());
            BroAudio.SetEffect(Effect.ResetHighPass());

            // The second pole is only written when Setting.AudioFilterSlope is FourPole at the moment of
            // the reset (EffectAutomationHelper.GetEffectParameterName decides that per call), so the two
            // resets above cannot be relied on to have covered it - a test that moved it under FourPole and
            // then left the slope at TwoPole would leave it moved. These two writes are the same defaults
            // EffectAutomationHelper.GetEffectDefaultValue resets the primary parameter to, and are a no-op
            // when nothing touched them.
            mixer.SafeSetFloat(BroName.LowPassParaName + SecondaryEffectParaSuffix, AudioConstant.MaxFrequency);
            mixer.SafeSetFloat(BroName.HighPassParaName + SecondaryEffectParaSuffix, AudioConstant.MinFrequency);

            float deadline = Time.realtimeSinceStartup + DrainTimeoutSeconds;
            while (!IsEffectParameterDefault(mixer, BroName.LowPassParaName, AudioConstant.MaxFrequency) ||
                   !IsEffectParameterDefault(mixer, BroName.HighPassParaName, AudioConstant.MinFrequency))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    mixer.SafeGetFloat(BroName.LowPassParaName, out float lowPass);
                    mixer.SafeGetFloat(BroName.HighPassParaName, out float highPass);
                    leaks.Add($"the mixer's effect parameters never came back to their defaults after {DrainTimeoutSeconds}s " +
                              $"(Effect_LowPass {lowPass:F0}Hz, Effect_HighPass {highPass:F0}Hz), i.e. a SetEffect tween or " +
                              "an auto-reset waitable is still running");
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>
        /// True when the parameter reads its default - or cannot be read at all, which is not this
        /// method's problem to report: a mixer without that exposed parameter has nothing to leak.
        /// </summary>
        private static bool IsEffectParameterDefault(AudioMixer mixer, string parameterName, float defaultValue)
            => !mixer.SafeGetFloat(parameterName, out float value)
               || Mathf.Abs(value - defaultValue) <= EffectFrequencyTolerance;
#endif

        /// <summary>
        /// SoundManager's live player list: every AudioPlayer currently checked out of the pool, whether it
        /// is playing, scheduled, paused or mid-handover. Read through the internal accessor the runtime
        /// assembly exposes to this one (InternalsVisibleTo), so a refactor of the pool breaks the build
        /// instead of every test. Needs a live manager.
        /// </summary>
        protected static IReadOnlyList<AudioPlayer> CurrentAudioPlayers()
        {
            return SoundManager.Instance.GetCurrentAudioPlayers();
        }

        #region Library
        /// <summary>
        /// Creates a tracked entity. Configure it further, then wrap it with <see cref="IdOf"/>. It has no
        /// AudioAsset, so no playback group applies to it unless one is wired onto it explicitly - which is what
        /// lets most of the suite play one ID twice in quick succession. <see cref="NewAssetBackedEntity"/>
        /// builds the shipped shape instead.
        /// </summary>
        protected AudioEntity NewEntity(string name = "TestSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
        {
            AudioEntity entity = TestAudioLibrary.CreateEntity(name, audioType, clips);
            Track(entity);
            return entity;
        }

        /// <summary>Creates a tracked entity and returns its <see cref="SoundID"/> — the common case.</summary>
        protected SoundID NewSound(string name = "TestSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
            => IdOf(NewEntity(name, audioType, clips));

        /// <summary>
        /// Creates a tracked entity owned by its own tracked <see cref="AudioAsset"/>, as every entity authored
        /// in the Library Manager is. Unlike <see cref="NewEntity"/>, it plays under
        /// <see cref="FactoryGlobalPlaybackGroup"/>: the asset links it on first use. Two plays of the same ID in
        /// one frame, or within 0.04s of each other, are therefore rejected - see DefaultPlaybackGroupTests.
        /// </summary>
        protected AudioEntity NewAssetBackedEntity(string name = "TestAssetSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
        {
            AudioAsset asset = Track(TestAudioLibrary.CreateAudioAsset(name + "Asset"));
            return Track(TestAudioLibrary.CreateAssetBackedEntity(name, audioType, asset, clips));
        }

        /// <summary>Creates a tracked, AudioAsset-backed entity and returns its <see cref="SoundID"/>. See <see cref="NewAssetBackedEntity"/>.</summary>
        protected SoundID NewAssetBackedSound(string name = "TestAssetSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
            => IdOf(NewAssetBackedEntity(name, audioType, clips));

        protected static SoundID IdOf(AudioEntity entity) => new SoundID(entity);

        protected AudioClip NewClip(float seconds = 1f, string name = "TestClip")
            => Track(TestAudioLibrary.CreateClip(seconds, name));

        /// <summary>
        /// Subscribes to <see cref="BroAudio.OnBGMChanged"/> and unsubscribes automatically in TearDown.
        /// Always use this rather than `BroAudio.OnBGMChanged +=` — the underlying event is static.
        /// </summary>
        protected void SubscribeBgmChanged(Action<IAudioPlayer> handler)
        {
            BroAudio.OnBGMChanged += handler;
            _bgmSubscriptions.Add(handler);
        }

        /// <summary>
        /// Builds a fresh, tracked DefaultPlaybackGroup with only the rule(s) a test cares about enabled.
        /// _logCombFilteringWarning is always off - the warning is log noise, not the behavior under test.
        /// </summary>
        protected DefaultPlaybackGroup NewGroup(int maxPlayableCount = -1, float combFilteringTime = 0f,
            bool ignoreSameFrame = false, float ignoreDistanceGreaterThan = 0f)
        {
            DefaultPlaybackGroup group = Track(ScriptableObject.CreateInstance<DefaultPlaybackGroup>());
            TestAudioLibrary.SetPrivateField(group, TestAudioLibrary.Reflected.DefaultPlaybackGroup.MaxPlayableCount, (MaxPlayableCountRule)maxPlayableCount);
            TestAudioLibrary.SetPrivateField(group, TestAudioLibrary.Reflected.DefaultPlaybackGroup.CombFilteringTime, (CombFilteringRule)combFilteringTime);
            TestAudioLibrary.SetPrivateField(group, TestAudioLibrary.Reflected.DefaultPlaybackGroup.IgnoreCombFilteringIfSameFrame, ignoreSameFrame);
            TestAudioLibrary.SetPrivateField(group, TestAudioLibrary.Reflected.DefaultPlaybackGroup.IgnoreIfDistanceIsGreaterThan, ignoreDistanceGreaterThan);
            TestAudioLibrary.SetPrivateField(group, TestAudioLibrary.Reflected.DefaultPlaybackGroup.LogCombFilteringWarning, false);
            return group;
        }

        /// <summary>Registers an object for destruction in TearDown.</summary>
        protected T Track<T>(T obj) where T : UnityEngine.Object
        {
            _createdObjects.Add(obj);
            return obj;
        }

        /// <summary>
        /// The concrete <see cref="AudioPlayer"/> a caller's handle currently resolves to, or null once it
        /// has been recycled. Reaches through the <see cref="AudioPlayerInstanceWrapper"/> that
        /// BroAudio.Play() returns, for the cases where the public surface genuinely cannot observe the
        /// result — GetComponent() on the player's MonoBehaviour, or its Transform.
        /// <para>
        /// A looping handle changes what this returns at every seam — that is what UpdateInstance does.
        /// </para>
        /// </summary>
        protected static AudioPlayer InstanceOf(IAudioPlayer player)
            => player is AudioPlayerInstanceWrapper wrapper ? (AudioPlayer)wrapper : null;
        #endregion

        #region Waiting
        /// <summary>
        /// BroAudio.Play only enqueues — SoundManager.LateUpdate starts the voice. Always yield before asserting.
        /// </summary>
        protected static IEnumerator WaitFrames(int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
            }
        }

        /// <summary>Polls per frame until the condition holds, failing the test on timeout. Prefer this over WaitForSeconds.</summary>
        protected static IEnumerator WaitUntilOrTimeout(Func<bool> condition, string message, float timeout = 5f)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail($"Timed out after {timeout}s waiting for: {message}");
                }
                yield return null;
            }
        }

        /// <summary>
        /// The shared timeout for <see cref="WaitForPlaybackStart"/> and <see cref="WaitForRecycle"/>, and the
        /// default budget for a short state flip elsewhere in the suite (IsPlaying/IsActive toggling, a mixer
        /// parameter landing on a requested value) that is not itself gated by a fade, a handover seam or an
        /// Addressables load. Name a call site's own arithmetic (e.g. <c>fadeTime + 1f</c>) instead of this
        /// constant whenever the wait is genuinely bounded by the test's own timed data - this one is for
        /// everything that budget doesn't cover.
        /// </summary>
        protected const float DefaultPlaybackWaitSeconds = 2f;

        /// <summary>
        /// Budget for a value that ramps or converges rather than flipping in one frame and is not bounded by
        /// a test-local duration - a SpectrumAnalyzer band settling into its attack/decay target, an explicit
        /// schedule racing a longer clip.Delay. Wider than <see cref="DefaultPlaybackWaitSeconds"/> because the
        /// target is approached over several frames, not reached in the one right after a state change.
        /// </summary>
        protected const float RampConvergenceWaitSeconds = 3f;

        /// <summary>
        /// Budget for a loop/BGM/dominator handover or crossfade to complete - spans at least one seam
        /// (<c>AudioConstant.MixerWarmUpTime</c> plus however long the transition itself runs), so it needs
        /// more headroom than a plain state flip. Multiply it (rather than inventing a new constant) for a
        /// wait that has to outlast more than one seam, e.g. <c>HandoverWaitSeconds * 2</c>.
        /// </summary>
        protected const float HandoverWaitSeconds = 5f;

        /// <summary>
        /// Budget for an Addressables load, preload handle or group preload to finish - network/IO-bound, so
        /// the most generous budget in the suite. Tests that wait on this belong under
        /// <c>[Category("Slow")]</c>.
        /// </summary>
        protected const float SlowAddressableWaitSeconds = 10f;

        /// <summary>Shorthand for <see cref="WaitUntilOrTimeout"/> on <paramref name="player"/>.IsPlaying — the most common wait in this suite.</summary>
        protected static IEnumerator WaitForPlaybackStart(IAudioPlayer player, string what = "playback to start", float timeout = DefaultPlaybackWaitSeconds)
            => WaitUntilOrTimeout(() => player.IsPlaying, what, timeout);

        /// <summary>Shorthand for <see cref="WaitUntilOrTimeout"/> on !<paramref name="player"/>.IsActive.</summary>
        protected static IEnumerator WaitForRecycle(IAudioPlayer player, string what = "player to be recycled", float timeout = DefaultPlaybackWaitSeconds)
            => WaitUntilOrTimeout(() => !player.IsActive, what, timeout);

        private static float _audioClockRate = -1f;

        /// <summary>How far the DSP clock may drift from wall time before it counts as unrealtime.</summary>
        protected const float RealtimeAudioClockTolerance = 0.1f;

        /// <summary>
        /// The DSP clock's rate as a multiple of wall time, as last measured by
        /// <see cref="MeasureAudioClockRate"/>; negative before the first measurement. 1 means a real
        /// output device. Exposed so <see cref="AudioClockProbeTests"/> can report on the same number the
        /// ignore gate below decides on, rather than measuring a second, possibly different one.
        /// </summary>
        protected static float AudioClockRate => _audioClockRate;

        /// <summary>
        /// Measures the DSP clock against wall time, once per run — the result is cached in
        /// <see cref="AudioClockRate"/> and every later call returns immediately.
        /// </summary>
        protected static IEnumerator MeasureAudioClockRate()
        {
            if (_audioClockRate < 0f)
            {
                double dspStart = AudioSettings.dspTime;
                float realStart = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(0.25f);
                _audioClockRate = (float)((AudioSettings.dspTime - dspStart) / (Time.realtimeSinceStartup - realStart));
            }
        }

        /// <summary>
        /// A machine with no audio output device (CI runners) runs the engine's DSP clock decoupled from
        /// wall time, so a voice can start and finish between two frames. Tests that need the voice itself
        /// to advance in real time gate on this; the rate is measured once and reused for the whole run.
        /// <para>
        /// This ignores rather than fails, so a local run without an audio device stays green.
        /// <see cref="AudioClockProbeTests"/> is what turns the same condition into a CI failure — without
        /// it, an image that lost its audio device silently skips every test that gates on this.
        /// </para>
        /// </summary>
        protected static IEnumerator RequireRealtimeAudioClock()
        {
            yield return MeasureAudioClockRate();

            if (Mathf.Abs(_audioClockRate - 1f) > RealtimeAudioClockTolerance)
            {
                Assert.Ignore($"DSP clock runs at {_audioClockRate:F2}x wall time (no audio output device) - this test needs a realtime audio clock.");
            }
        }

        /// <summary>
        /// Waits on the DSP clock — the clock scheduling, seamless loops and handovers actually run on.
        /// <para>
        /// Only for asserting on DSP-scheduled state (timeSamples, scheduled start/end). Fades, pitch ramps
        /// and every other FaderModule-driven value run on the frame clock (Utility.GetDeltaTime), so time
        /// those with WaitForSeconds — a machine with no audio device runs the two clocks at different rates.
        /// </para>
        /// </summary>
        /// <param name="timeout">
        /// Realtime deadline. Negative derives one from <paramref name="seconds"/>, generously: the DSP clock
        /// is never slower than wall time in practice (a machine with no audio device runs it hundreds of
        /// times faster, returning almost immediately), so the derived deadline only has to outlast a
        /// realtime clock plus scheduling noise — it exists to name a stalled clock, not to time anything.
        /// </param>
        protected static IEnumerator WaitDspSeconds(double seconds, float timeout = -1f)
        {
            if (timeout < 0f)
            {
                timeout = ((float)seconds * 4f) + 5f;
            }

            float deadline = Time.realtimeSinceStartup + timeout;
            double dspStart = AudioSettings.dspTime;
            double target = dspStart + seconds;
            while (AudioSettings.dspTime < target)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Assert.Fail($"DSP clock stalled: after {timeout}s of wall time it had advanced only " +
                                $"{AudioSettings.dspTime - dspStart:F3}s of the {seconds}s waited for.");
                }
                yield return null;
            }
        }
        #endregion
    }
}