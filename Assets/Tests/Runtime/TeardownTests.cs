using System;
using System.Collections;
using System.Reflection;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins behavior once SoundManager is destroyed: facade release verbs no-op, <c>BroAudio.Play</c> throws
    /// <see cref="BroAudioException"/>, and a pre-teardown <see cref="IAudioPlayer"/> handle's release verbs no-op.
    /// <para>
    /// SoundManager's only teardown hook is <c>OnDestroy</c>, so tests destroy the manager directly;
    /// <see cref="RestoreSoundManagerAfterTest"/> must re-bootstrap it because every later PlayMode test needs it.
    /// A re-Init'd manager resolves the same cached <c>RuntimeSetting</c> asset the base fixture snapshotted.
    /// </para>
    /// </summary>
    public class TeardownTests : BroAudioTestFixture
    {
        /// <summary>
        /// Destroys the manager's whole GameObject immediately, so the AudioPlayers parented under it die in the
        /// same call. <see cref="SoundManager.HasInstance"/> flips only via Unity's fake-null (<c>OnDestroy</c>
        /// never nulls <c>_instance</c>); the post-destroy assert fails here if that stops holding.
        /// </summary>
        private static void DestroyManagerImmediate()
        {
            Assert.IsTrue(SoundManager.HasInstance, "Precondition failed: SoundManager must be alive before a test can destroy it.");

            UnityEngine.Object.DestroyImmediate(SoundManager.Instance.gameObject);

            Assert.IsFalse(SoundManager.HasInstance,
                "SoundManager.HasInstance must go false immediately after DestroyImmediate - if this fails, the fake-null " +
                "assumption every other assertion in this file relies on is wrong.");
        }

        /// <summary>Re-bootstraps SoundManager if a test left it destroyed.</summary>
        [UnityTearDown]
        public IEnumerator RestoreSoundManagerAfterTest()
        {
            if (!SoundManager.HasInstance)
            {
                // Init() has no already-alive guard: called with a live manager it leaks a second one.
                SoundManager.Init();

                // Start(), which unblocks mixer parameter writes, runs next frame; BroAudioSetUp waits the same frame.
                yield return null;
            }

            Assert.IsTrue(SoundManager.HasInstance,
                "TeardownTests destroyed SoundManager and failed to restore it - every later PlayMode test will now fail to bootstrap.");
        }

        // One behavior ("teardown must not throw"), so one test loops every overload; a verb switched from
        // Manager?. to SoundManager.Instance fails on its own label.
        [UnityTest]
        public IEnumerator ReleaseVerbs_OnBroAudioFacade_WithManagerDestroyed_AreSilentNoOps()
        {
            SoundID id = NewSound("TeardownReleaseVerbSfx", BroAudioType.SFX, NewClip(1f));

            DestroyManagerImmediate();

            (Action Verb, string Label)[] releaseVerbs =
            {
                (() => BroAudio.Stop(id), "Stop(SoundID)"),
                (() => BroAudio.Stop(id, 0.5f), "Stop(SoundID, fadeOut)"),
                (() => BroAudio.Stop(BroAudioType.SFX), "Stop(BroAudioType)"),
                (() => BroAudio.Stop(BroAudioType.SFX, 0.5f), "Stop(BroAudioType, fadeOut)"),
                (() => BroAudio.Pause(id), "Pause(SoundID)"),
                (() => BroAudio.Pause(id, 0.5f), "Pause(SoundID, fadeOut)"),
                (() => BroAudio.UnPause(id), "UnPause(SoundID)"),
                (() => BroAudio.UnPause(id, 0.5f), "UnPause(SoundID, fadeIn)"),
                (() => BroAudio.Pause(BroAudioType.SFX), "Pause(BroAudioType)"),
                (() => BroAudio.Pause(BroAudioType.SFX, 0.5f), "Pause(BroAudioType, fadeOut)"),
                (() => BroAudio.UnPause(BroAudioType.SFX), "UnPause(BroAudioType)"),
                (() => BroAudio.UnPause(BroAudioType.SFX, 0.5f), "UnPause(BroAudioType, fadeIn)"),
                (() => BroAudio.SetVolume(0.5f), "SetVolume(vol)"),
                (() => BroAudio.SetVolume(0.5f, 0.5f), "SetVolume(vol, fadeTime)"),
                (() => BroAudio.SetVolume(BroAudioType.SFX, 0.5f), "SetVolume(BroAudioType, vol)"),
                (() => BroAudio.SetVolume(BroAudioType.SFX, 0.5f, 0.5f), "SetVolume(BroAudioType, vol, fadeTime)"),
                (() => BroAudio.SetVolume(id, 0.5f), "SetVolume(SoundID, vol)"),
                (() => BroAudio.SetVolume(id, 0.5f, 0.5f), "SetVolume(SoundID, vol, fadeTime)"),
                (() => BroAudio.SetPitch(id, 1.2f), "SetPitch(SoundID, pitch)"),
                (() => BroAudio.SetPitch(id, 1.2f, 0.5f), "SetPitch(SoundID, pitch, fadeTime)"),
                (() => BroAudio.SetPitch(1.2f), "SetPitch(pitch)"),
                (() => BroAudio.SetPitch(1.2f, 0.5f), "SetPitch(pitch, fadeTime)"),
                (() => BroAudio.SetPitch(BroAudioType.SFX, 1.2f), "SetPitch(BroAudioType, pitch)"),
                (() => BroAudio.SetPitch(BroAudioType.SFX, 1.2f, 0.5f), "SetPitch(BroAudioType, pitch, fadeTime)"),
            };

            foreach ((Action verb, string label) in releaseVerbs)
            {
                Assert.DoesNotThrow(() => verb(), $"BroAudio.{label} must be a silent no-op once SoundManager is destroyed.");
            }

            yield break;
        }

        // Play throws on purpose: manual-init callers need the exception to know playback isn't available.
        // Fails if Play is moved to Manager?. "for consistency" with the release verbs.
        [UnityTest]
        public IEnumerator Play_OnBroAudioFacade_WithManagerDestroyed_ThrowsBroAudioException()
        {
            SoundID id = NewSound("TeardownPlaySfx", BroAudioType.SFX, NewClip(1f));

            DestroyManagerImmediate();

            Assert.Throws<BroAudioException>(() => BroAudio.Play(id), "Play(id) must throw once SoundManager is destroyed, not silently return.");
            Assert.Throws<BroAudioException>(() => BroAudio.Play(id, Vector3.zero), "Play(id, position) must throw once SoundManager is destroyed.");
            Assert.Throws<BroAudioException>(() => BroAudio.Play(id, (Transform)null), "Play(id, followTarget) must throw once SoundManager is destroyed.");

            yield break;
        }

        // BroAudio_InitManually only strips the auto-bootstrap attribute; BroAudio.Init() forwards to
        // SoundManager.Init(). Its branches run only in a build with the project-wide define set.
        [UnityTest]
        public IEnumerator Init_WithManagerAbsent_BootstrapsAManagerThatPlays_AndAutoBootstrapTracksTheManualInitDefine()
        {
            RuntimeInitializeOnLoadMethodAttribute autoBootstrap = typeof(SoundManager)
                .GetMethod(nameof(SoundManager.Init))
                .GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
#if BroAudio_InitManually
            Assert.IsNull(autoBootstrap, "BroAudio_InitManually must remove SoundManager.Init's auto-bootstrap.");
#else
            Assert.IsNotNull(autoBootstrap, "Without BroAudio_InitManually, SoundManager.Init must auto-bootstrap.");
            Assert.AreEqual(RuntimeInitializeLoadType.BeforeSceneLoad, autoBootstrap.loadType);
#endif

            SoundID id = NewSound("ManualInitSfx", BroAudioType.SFX, NewClip(1f));
            DestroyManagerImmediate();

#if BroAudio_InitManually
            BroAudio.Init();
#else
            SoundManager.Init();
#endif
            // Same one-frame wait as RestoreSoundManagerAfterTest.
            yield return null;

            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player, "a sound to play on the freshly initialized manager");
        }

#if !UNITY_WEBGL
        // Pins TEST_FINDINGS #48.
        [UnityTest]
        [Category("Finding_48")]
        public IEnumerator SetEffect_OnBroAudioFacade_WithManagerDestroyed_ThrowsBroAudioException()
        {
            DestroyManagerImmediate();

            Assert.Throws<BroAudioException>(() => BroAudio.SetEffect(default(Effect)),
                "characterizes: BroAudio.SetEffect is not Manager?.-gated like the other release verbs - it throws once SoundManager is destroyed instead of no-op'ing.");

            yield break;
        }
#endif

        // The shape matters: the handle's AudioPlayer dies with the manager (it is parented under it); a
        // merely-recycled handle with the manager alive is covered by PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal.
        [UnityTest]
        public IEnumerator StaleHandle_HeldAcrossManagerDestruction_ReleaseVerbsNoOp()
        {
            SoundID id = NewSound("TeardownStaleHandleSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player, "the handle's player to actually start before the manager dies under it");

            DestroyManagerImmediate();

            Action[] releaseVerbsOnHandle =
            {
                () => player.Stop(),
                () => player.Pause(),
                () => player.UnPause(),
                () => player.SetVolume(0.5f, 0f),
                () => player.SetPitch(1.2f, 0f),
            };

            foreach (Action verb in releaseVerbsOnHandle)
            {
                Assert.DoesNotThrow(() => verb(),
                    "A release verb on a handle whose backing AudioPlayer died together with SoundManager must be a silent no-op.");
            }

            // Contrast: same destroyed state, non-logging path.
            Assert.DoesNotThrow(() => { _ = player.IsActive; }, "IsActive must stay safe (IsAvailable(false) short-circuits before touching Instance again).");
            Assert.DoesNotThrow(() => { _ = player.IsPlaying; }, "IsPlaying must stay safe for the same reason as IsActive.");
            Assert.IsFalse(player.IsActive, "A handle whose backing player died with the manager must read back as inactive.");
        }

        // The Fader is built directly because no production path reaches the guard:
        // every real Fader's AudioPlayer dies with the manager.
        [UnityTest]
        public IEnumerator Fader_CompleteWithManagerDestroyed_IsADefensiveNoOp()
        {
            RecordingAudioBus bus = new RecordingAudioBus();
            Fader fader = new Fader(1f, bus);

            DestroyManagerImmediate();

            Assert.DoesNotThrow(() => fader.Complete(0.25f),
                "With SoundManager destroyed, Fader.StopCoroutine must not reach the throwing SoundManager.Instance.");
            Assert.AreEqual(0.25f, fader.Current, "Complete still applies its value; only the coroutine stop is skipped.");

            yield break;
        }

        /// <summary>Counts the volume pushes a <see cref="Fader"/> makes, so a no-op path can be told from a completed one.</summary>
        private sealed class RecordingAudioBus : IAudioBus
        {
            public int UpdateCount { get; private set; }

            public void UpdateVolume(bool forceUpdate = false) => UpdateCount++;
        }
    }
}