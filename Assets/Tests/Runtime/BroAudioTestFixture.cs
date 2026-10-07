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
    /// Base fixture for every PlayMode test, and the one home of test isolation: SoundManager is a
    /// DontDestroyOnLoad singleton, so <see cref="BroAudioTearDown"/> resets all shared state
    /// unconditionally (a half-failed test is the likeliest leaker), verifies it, and fails the leaking
    /// test by name. Do not re-solve isolation per test file.
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

        /// <summary>Tight: fadeTime 0 uses Fader.Complete, so linear volume products are exact multiplication.</summary>
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
            // The test scene is empty and Unity warns on every voice without a listener. Run-wide; never destroyed.
            if (!_listener)
            {
                GameObject listenerObject = new GameObject("TestAudioListener");
                UnityEngine.Object.DontDestroyOnLoad(listenerObject);
                _listener = listenerObject.AddComponent<AudioListener>();
            }

#if BroAudio_InitManually
            // Nothing auto-bootstraps under this define, so the suite calls Init() itself. Guarded: Init()
            // has no "already have one" check and would leak a second manager.
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
        /// <see cref="DefaultPlaybackGroup"/> with factory field values, i.e. what users ship (a 0.04s
        /// comb-filtering window, same-frame plays not exempt, no voice limit). Fresh per test.
        /// <para>
        /// It reaches an entity only through <see cref="AudioAsset.PlaybackGroup"/> or as a custom group's
        /// fallback parent: <see cref="NewEntity"/>/<see cref="NewSound"/> have no AudioAsset and stay outside
        /// it; <see cref="NewAssetBackedEntity"/>/<see cref="NewAssetBackedSound"/> play under it.
        /// </para>
        /// </summary>
        protected DefaultPlaybackGroup FactoryGlobalPlaybackGroup { get; private set; }

        /// <summary>
        /// Puts every observable RuntimeSetting field at its factory value. The asset is gitignored, so each
        /// checkout's copy differs, while the suite's timing windows derive from the factory curves. A test
        /// needing another value sets it in its body; TearDown restores the developer's asset.
        /// <para>
        /// ResetToFactorySettings covers the playback toggles; the rest are written here (the obsolete
        /// CombFilteringPreventionInSeconds is read by nothing). DefaultAudioPlayerPoolSize is read only at
        /// bootstrap, so its reset reaches only a manager rebuilt mid-run; no test depends on that cap.
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
        /// Realtime budget per drain: several times the longest fade a test leaves in flight (~1s), yet
        /// short enough to report a leak instead of hanging the run.
        /// </summary>
        private const float DrainTimeoutSeconds = 5f;

        /// <summary>
        /// Identical Master readings that end the drain once movement was seen. Longer than
        /// <see cref="QuietMasterFrames"/>: near its end a fade's ease can repeat a float on adjacent frames.
        /// </summary>
        private const int SteadyMasterFrames = 5;

        /// <summary>
        /// Identical Master readings that end the drain when no movement was seen - the cost every test
        /// pays. A running fade moves far more than epsilon per frame, so one comparison detects it.
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
            // Collected, not asserted on the spot: an Assert.Fail would skip the resets below. Reported last.
            List<string> leaks = new List<string>();

            // First: under the factory update mode every fade runs on Time.deltaTime, so nothing below can
            // drain at timeScale 0. Derived [UnityTearDown]s run BEFORE this one, so a fixture that pauses
            // and then waits on scaled time in its own teardown must restore timeScale itself.
            Time.timeScale = 1f;

            BroAudio.Stop(BroAudioType.All, 0f);
            yield return DrainAudioPlayers(leaks);

            // After the player drain, so nothing playing loses its clip; before the resets below, because an
            // OnDisable can write global state (SoundVolume's Reset On Disable) and would overwrite them.
            // Destroy is deferred to the end of the frame, hence the yield.
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

            // RuntimeSetting is an on-disk asset; a test that mutates it must not dirty the project.
            if (SoundManager.HasInstance && _settingSnapshot != null)
            {
                JsonUtility.FromJsonOverwrite(_settingSnapshot, SoundManager.Instance.Setting);
            }

            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                BroAudio.SetVolume(audioType, AudioConstant.FullVolume, 0f);
            }

            // Per-type pitch persists for every later player of that type, and rescales duration - a leaked
            // 0.5x doubles every later clip and breaks duration windows. SetPitch(pitch, type, fadeTime) is
            // the [Obsolete] overload; mind the argument order.
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
        /// Checks that the per-type preferences TearDown resets blindly read their defaults, and that the
        /// dominator's Main_LowPass / Main_HighPass were reverted by DominatorPlayer's own automation.
        /// Polled: a filter tween or dominator revert lands on a later frame. A survivor is reported, and the
        /// dominator parameters forced back so one broken revert is not inherited by every later test.
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
        /// Fails the leaking test by name - surfacing later, a leak looks like an unrelated flake. Only
        /// warns if the test already failed: a mid-body failure is expected to leave playback running.
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
        /// Waits until every checked-out AudioPlayer is recycled. Stop(All, 0f) alone doesn't prove it: a
        /// scheduled or paused voice, or a Chained entity's End clip handed to a new player during
        /// SoundManager.Stop's loop, can outlive the call. So Stop is re-issued per frame; that terminates
        /// because the handed-over player is at PlaybackStage.End, where CanHandoverToEnd() is false.
        /// </summary>
        private static IEnumerator DrainAudioPlayers(List<string> leaks)
        {
            // Every teardown step no-ops without a manager (TeardownTests destroys it) rather than touching
            // the throwing SoundManager.Instance; none may rely on that fixture restoring it first.
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

                // Usually costs no frame: a zero-fade Stop recycles synchronously.
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
        /// Waits until nothing writes the Master mixer parameter: the SetVolume(FullVolume, 0f) below cannot
        /// cancel a fade a test left in flight (TEST_FINDINGS #51).
        /// <para>
        /// Waits for the reading to stop moving, not for a value: a live fade rewrites Master every frame, so
        /// a steady reading is the coroutine's observable end however it ended. Inert on WebGL, where
        /// SetMasterVolume fades the players instead of this parameter.
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
                // Nothing observable; tests that care assert on Master themselves.
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
                    requiredSteadyFrames = SteadyMasterFrames;
                    steadyFrames = 0;
                }
                lastDb = db;
            }
        }

#if !UNITY_WEBGL
        /// <summary>
        /// Resets both halves of what SetEffect changes: the Effect_* mixer parameters, and the in-memory
        /// per-type EffectType bit every later Play() of that type reads (outside the RuntimeSetting snapshot).
        /// <para>
        /// Default-valued ResetLowPass()/ResetHighPass() make SetEffect pick SetEffectMode.Remove, which
        /// clears the bit. Don't use SetEffect(new Effect(EffectType.None)): it logs errors (on construction,
        /// and per unresolvable tracked effect AudioEffectTests leaves registered), which fail the test
        /// being torn down.
        /// </para>
        /// <para>
        /// Dominator parameters are left to DominatorPlayer's own revert, checked by
        /// <see cref="VerifyGlobalStateRestored"/>. The reset is polled rather than waited out for fixed
        /// frames, so a tweaker still mid-fade or a queued auto-reset waitable is reported, not missed.
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

            // The resets above write the second pole only if the slope is FourPole right now, so a test that
            // moved it under FourPole and switched to TwoPole would leave it moved. Same defaults as the primary.
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
        /// Every AudioPlayer checked out of SoundManager's pool - playing, scheduled, paused or mid-handover.
        /// Uses an InternalsVisibleTo accessor, so a pool refactor breaks the build, not the tests. Needs a
        /// live manager.
        /// </summary>
        protected static IReadOnlyList<AudioPlayer> CurrentAudioPlayers()
        {
            return SoundManager.Instance.GetCurrentAudioPlayers();
        }

        #region Library
        /// <summary>
        /// Creates a tracked entity; wrap it with <see cref="IdOf"/>. No AudioAsset, so no playback group
        /// applies unless wired explicitly - which lets most tests replay one ID in quick succession.
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
        /// Creates a tracked entity owned by a tracked <see cref="AudioAsset"/>, the shipped shape. Plays under
        /// <see cref="FactoryGlobalPlaybackGroup"/>, so two plays of one ID in the same frame or within 0.04s
        /// are rejected - see DefaultPlaybackGroupTests.
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
        /// The concrete <see cref="AudioPlayer"/> behind a handle's <see cref="AudioPlayerInstanceWrapper"/>,
        /// or null once recycled - for what the public surface can't observe (GetComponent, Transform). A
        /// looping handle's target changes at every seam (UpdateInstance).
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
        /// Budget for a short state flip not gated by a fade, handover seam or Addressables load (IsPlaying /
        /// IsActive toggling, a mixer parameter landing). A wait bounded by the test's own timed data uses
        /// that arithmetic instead (e.g. <c>fadeTime + 1f</c>).
        /// </summary>
        protected const float DefaultPlaybackWaitSeconds = 2f;

        /// <summary>
        /// Budget for a value that converges over several frames and is not bounded by a test-local duration
        /// (a SpectrumAnalyzer band settling, an explicit schedule racing a longer clip.Delay).
        /// </summary>
        protected const float RampConvergenceWaitSeconds = 3f;

        /// <summary>
        /// Budget for a loop/BGM/dominator handover or crossfade - one seam (<c>AudioConstant.MixerWarmUpTime</c>
        /// plus the transition). Multiply it for more seams, e.g. <c>HandoverWaitSeconds * 2</c>.
        /// </summary>
        protected const float HandoverWaitSeconds = 5f;

        /// <summary>
        /// Budget for an Addressables load or preload - IO-bound. Tests that wait on this belong under
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
        /// DSP clock rate as a multiple of wall time (1 on a real output device; negative until
        /// <see cref="MeasureAudioClockRate"/> runs). Shared so <see cref="AudioClockProbeTests"/> reports the
        /// same number the ignore gate decides on.
        /// </summary>
        protected static float AudioClockRate => _audioClockRate;

        /// <summary>Measures the DSP clock against wall time once per run, cached in <see cref="AudioClockRate"/>.</summary>
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
        /// Gate for tests that need the voice to advance in real time: without an audio output device (CI)
        /// the DSP clock is decoupled from wall time, so a voice can start and finish between two frames.
        /// <para>
        /// Ignores rather than fails, so a local deviceless run stays green; <see cref="AudioClockProbeTests"/>
        /// turns the condition into a CI failure so a lost device cannot silently skip these tests.
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
        /// Waits on the DSP clock. Only for DSP-scheduled state (timeSamples, scheduled start/end); fades,
        /// pitch ramps and other FaderModule values run on the frame clock, so time those with WaitForSeconds -
        /// without an audio device the two clocks run at different rates.
        /// </summary>
        /// <param name="timeout">
        /// Realtime deadline; negative derives a generous one from <paramref name="seconds"/>. It only names a
        /// stalled clock - the DSP clock is never slower than wall time in practice.
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