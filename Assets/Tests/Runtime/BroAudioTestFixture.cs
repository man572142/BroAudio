using System;
using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Base fixture for every PlayMode test.
    /// <para>
    /// SoundManager is a DontDestroyOnLoad singleton that survives the whole run, so isolation is solved
    /// here once: stop everything, restore the volumes and the on-disk RuntimeSetting, destroy what the
    /// test created. Do not re-solve it per test file.
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

            // AudioMixer.SetFloat silently fails on the first Play Mode frame; SoundManager clears it in Start().
            yield return null;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!SoundManager.HasInstance)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "SoundManager never bootstrapped.");
                yield return null;
            }
            _settingSnapshot = JsonUtility.ToJson(SoundManager.Instance.Setting);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator BroAudioTearDown()
        {
            BroAudio.Stop(BroAudioType.All, 0f);
            yield return WaitFrames(2);

            // RuntimeSetting is a real asset on disk — a test that mutates it must not dirty the project.
            JsonUtility.FromJsonOverwrite(_settingSnapshot, SoundManager.Instance.Setting);

            BroAudio.SetVolume(AudioConstant.FullVolume, 0f);
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                BroAudio.SetVolume(audioType, AudioConstant.FullVolume, 0f);
            }

            // Per-type pitch leaks exactly like per-type volume: SoundManager.SetPitch stores it into
            // AudioTypePlaybackPreference (SoundManager.cs:358), so every *later* player of that type picks
            // it up through SetInitialPitch. Volume only shifts an amplitude, but pitch rescales duration -
            // a leaked 0.5x makes every later clip run twice as long and moves every duration window in the
            // fade, scheduling and loop tests. Mind the argument order: SetPitch(type, pitch, fadeTime) is
            // the current overload (BroAudio.cs:245); SetPitch(pitch, type, fadeTime) is [Obsolete] (:237).
            foreach (BroAudioType audioType in ConcreteAudioTypes)
            {
                BroAudio.SetPitch(audioType, AudioConstant.DefaultPitch, 0f);
            }

            // BroAudio.OnBGMChanged forwards to a *static* event on MusicPlayer — an un-removed handler
            // outlives the test and fires during every later one.
            foreach (Action<IAudioPlayer> handler in _bgmSubscriptions)
            {
                BroAudio.OnBGMChanged -= handler;
            }
            _bgmSubscriptions.Clear();

            foreach (UnityEngine.Object obj in _createdObjects)
            {
                if (obj)
                {
                    UnityEngine.Object.Destroy(obj);
                }
            }
            _createdObjects.Clear();
            yield return null;
        }

        #region Library
        /// <summary>Creates a tracked entity. Configure it further, then wrap it with <see cref="IdOf"/>.</summary>
        protected AudioEntity NewEntity(string name = "TestSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
        {
            AudioEntity entity = TestAudioLibrary.CreateEntity(name, audioType, clips);
            Track(entity);
            return entity;
        }

        /// <summary>Creates a tracked entity and returns its <see cref="SoundID"/> — the common case.</summary>
        protected SoundID NewSound(string name = "TestSfx", BroAudioType audioType = BroAudioType.SFX, params AudioClip[] clips)
            => IdOf(NewEntity(name, audioType, clips));

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
            TestAudioLibrary.SetPrivateField(group, "_maxPlayableCount", (MaxPlayableCountRule)maxPlayableCount);
            TestAudioLibrary.SetPrivateField(group, "_combFilteringTime", (CombFilteringRule)combFilteringTime);
            TestAudioLibrary.SetPrivateField(group, "_ignoreCombFilteringIfSameFrame", ignoreSameFrame);
            TestAudioLibrary.SetPrivateField(group, "_ignoreIfDistanceIsGreaterThan", ignoreDistanceGreaterThan);
            TestAudioLibrary.SetPrivateField(group, "_logCombFilteringWarning", false);
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

        /// <summary>The shared timeout for <see cref="WaitForPlaybackStart"/> and <see cref="WaitForRecycle"/>.</summary>
        protected const float DefaultPlaybackWaitSeconds = 2f;

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