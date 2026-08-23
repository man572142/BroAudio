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
        protected static readonly BroAudioType[] ConcreteAudioTypes =
        {
            BroAudioType.Music, BroAudioType.UI, BroAudioType.Ambience, BroAudioType.SFX, BroAudioType.VoiceOver,
        };

        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();
        private string _settingSnapshot;

        [UnitySetUp]
        public IEnumerator BroAudioSetUp()
        {
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

        /// <summary>Registers an object for destruction in TearDown.</summary>
        protected T Track<T>(T obj) where T : UnityEngine.Object
        {
            _createdObjects.Add(obj);
            return obj;
        }
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

        /// <summary>Waits on the DSP clock — the clock scheduling, seamless loops and handovers actually run on.</summary>
        protected static IEnumerator WaitDspSeconds(double seconds)
        {
            double target = AudioSettings.dspTime + seconds;
            while (AudioSettings.dspTime < target)
            {
                yield return null;
            }
        }
        #endregion
    }
}
