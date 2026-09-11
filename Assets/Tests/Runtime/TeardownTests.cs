using System;
using System.Collections;
using Ami.BroAudio.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Covers the one path nothing else in the suite exercises: OnDestroy/OnApplicationQuit teardown,
    /// where SoundManager can already be gone by the time other code still tries to talk to it.
    /// <para>
    /// Three contracts, straight from CLAUDE.md's "Runtime architecture" notes:
    /// (1) every release verb on the static <see cref="BroAudio"/> facade (Stop/Pause/UnPause/SetVolume/
    /// SetPitch, including the <see cref="BroAudioType"/> overloads) goes through the null-safe
    /// <c>BroAudio.Manager</c> (BroAudio.cs:31), so a destroyed manager makes them silent no-ops;
    /// (2) <c>BroAudio.Play</c> is the deliberate opposite - it goes through the throwing
    /// <c>SoundManager.Instance</c> (BroAudio.cs:44,56,68), so it must throw <see cref="BroAudioException"/>
    /// instead; (3) a caller that kept an <see cref="IAudioPlayer"/> handle from before the manager died
    /// must not get a crash out of touching it afterward - this file pins what that handle's own release
    /// verbs actually do, which (see the finding on
    /// <see cref="StaleHandle_HeldAcrossManagerDestruction_ReleaseVerbsThrowInsteadOfSilentlyNoOp"/> below)
    /// is not what (3) would predict.
    /// </para>
    /// <para>
    /// <b>What SoundManager does NOT have:</b> there is no <c>OnApplicationQuit</c> and no "is quitting"
    /// flag anywhere in the Runtime assembly (grepped) - the only teardown hook is <c>OnDestroy</c>
    /// (SoundManager.cs:144-160). Every test below reaches it by destroying the manager directly rather
    /// than by simulating application quit, which Unity's Test Framework has no hook for anyway.
    /// </para>
    /// <para>
    /// <b>Destroy/restore strategy:</b> SoundManager is a DontDestroyOnLoad singleton bootstrapped once per
    /// PlayMode run (SoundManager.cs:16-19 <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c>); every
    /// later PlayMode test depends on it being alive. <see cref="DestroyManagerImmediate"/> destroys the
    /// whole GameObject (not just the component) so the pooled/active <see cref="AudioPlayer"/> children
    /// parented under its transform (AudioPlayerObjectPool.cs:34) die in the same synchronous call rather
    /// than being left behind as an orphaned, half-alive hierarchy that would outlive the test. <see
    /// cref="RestoreSoundManagerAfterTest"/> is a <c>[UnityTearDown]</c> on this class specifically so it
    /// runs before the base <see cref="BroAudioTestFixture.BroAudioTearDown"/> - NUnit/UTF run TearDown
    /// methods most-derived-class-first - which matters because that base teardown dereferences
    /// <c>SoundManager.Instance.Setting</c> unconditionally and would itself throw on a manager this file
    /// left destroyed. <c>SoundManager.Setting</c> (SoundManager.Setting.cs:17) lazily resolves via
    /// <c>Resources.Load&lt;RuntimeSetting&gt;</c>, which Unity caches by path, so the freshly re-Init'd
    /// manager's Setting is the exact same on-disk asset the base fixture already snapshotted - nothing
    /// extra to reconcile there.
    /// </para>
    /// </summary>
    public class TeardownTests : BroAudioTestFixture
    {
        /// <summary>
        /// Destroys the live SoundManager's whole GameObject and asserts the destruction actually took.
        /// <para>
        /// <see cref="SoundManager.HasInstance"/> (SoundManager.cs:59) is a bare <c>_instance != null</c>
        /// check with no explicit nulling anywhere in <c>OnDestroy</c> - it only reports the destruction
        /// correctly because <see cref="UnityEngine.Object"/> overloads <c>==</c>/<c>!=</c> to treat a
        /// destroyed native object as "null" (Unity's fake-null). <c>DestroyImmediate</c>,
        /// not <c>Destroy</c>, is used so that flip - and the cascaded destruction
        /// of every pooled/active AudioPlayer parented under the manager's transform - is guaranteed to have
        /// already happened by the time this method returns, rather than deferred to the end of the frame.
        /// The post-destroy assert exists so a break in that fake-null assumption fails loudly right here,
        /// instead of quietly invalidating every assertion later in whichever test called this.
        /// </para>
        /// </summary>
        private static void DestroyManagerImmediate()
        {
            Assert.IsTrue(SoundManager.HasInstance, "Precondition failed: SoundManager must be alive before a test can destroy it.");

            UnityEngine.Object.DestroyImmediate(SoundManager.Instance.gameObject);

            Assert.IsFalse(SoundManager.HasInstance,
                "SoundManager.HasInstance must go false immediately after DestroyImmediate - if this fails, the fake-null " +
                "assumption every other assertion in this file relies on is wrong.");
        }

        /// <summary>
        /// Re-bootstraps SoundManager whenever a test in this file left it destroyed, so every later
        /// PlayMode test in the run still gets a working singleton. Runs before the base fixture's own
        /// TearDown (see the class doc comment for why that ordering matters).
        /// </summary>
        [UnityTearDown]
        public IEnumerator RestoreSoundManagerAfterTest()
        {
            if (!SoundManager.HasInstance)
            {
                // Init() (SoundManager.cs:19) unconditionally Instantiates a new prefab instance and
                // overwrites the static _instance - it has no "already have one" guard - so this must only
                // ever run when HasInstance is false, or it would leak a second manager into the scene.
                SoundManager.Init();

                // Awake runs synchronously inside Init() (the clone is activated before Init returns), but
                // Start() - which clears _waitForAudioMixerParametersAvailable - is deferred to the next
                // frame, and AudioMixer.SetFloat silently fails in Awake/OnEnable on the first Play Mode
                // frame (unity-audio-engine.md). BroAudioSetUp waits the same one frame after bootstrap.
                yield return null;
            }

            Assert.IsTrue(SoundManager.HasInstance,
                "TeardownTests destroyed SoundManager and failed to restore it - every later PlayMode test will now fail to bootstrap.");
        }

        // Item 1 (CLAUDE.md's teardown-asymmetry note) and the single most important behavior in this
        // file: every Stop/Pause/UnPause/SetVolume/SetPitch overload on the static BroAudio facade goes
        // through the null-safe `Manager?.` (BroAudio.cs:76-246), so once SoundManager is destroyed each
        // one is a silent no-op instead of a throw or NullReferenceException. "Quitting the game must not
        // throw" is one user-meaningful behavior, so looping over the verbs here is one test, not
        // one-per-method (the suite's own anti-goal).
        //
        // Regression this catches: swap any one `Manager?.X(...)` back to `SoundManager.Instance.X(...)`
        // - the exact mistake CLAUDE.md calls out by name - and that verb's SoundManager.Instance getter
        // (SoundManager.cs:55) throws BroAudioException here instead of no-op'ing, failing this test on
        // that specific verb.
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

        // Item 2: Play is deliberately NOT Manager?.-gated (BroAudio.cs:44,56,68 all read
        // `SoundManager.Instance.Play(...)`), so a destroyed manager must surface as a real
        // BroAudioException instead of returning null/Empty - callers that never call BroAudio.Init()
        // in manual-init mode need that exception to know playback isn't available yet. Asserting the
        // exception TYPE only (never message text) per the suite's own anti-goals.
        //
        // Regression this catches: swap any of these to the null-safe `Manager?.` pattern "for consistency"
        // with the release verbs above, and Play would start silently returning null/Empty instead of
        // throwing - this test fails the instant that happens.
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

#if !UNITY_WEBGL
        // Finding, not part of the task's original item list: BroAudio.SetEffect (BroAudio.cs:296,301,
        // #if !UNITY_WEBGL) is grouped with Stop/Pause/SetVolume/SetPitch as a "release verb" by
        // CLAUDE.md's teardown note, but the source does not treat it that way - both overloads read
        // `SoundManager.Instance.SetEffect(...)` directly, the same throwing accessor Play uses, not
        // `Manager?.`. So unlike every verb in ReleaseVerbs_OnBroAudioFacade_WithManagerDestroyed_
        // AreSilentNoOps above, SetEffect actually throws once the manager is destroyed. Characterizing
        // the actual behavior here; reported as a possible inconsistency (see the report for this task).
        [UnityTest]
        public IEnumerator SetEffect_OnBroAudioFacade_WithManagerDestroyed_ThrowsBroAudioException()
        {
            DestroyManagerImmediate();

            Assert.Throws<BroAudioException>(() => BroAudio.SetEffect(default(Effect)),
                "characterizes: BroAudio.SetEffect is not Manager?.-gated like the other release verbs - it throws once SoundManager is destroyed instead of no-op'ing.");

            yield break;
        }
#endif

        // Item 3, and the most consequential finding in this file. The task's premise was that a release
        // verb called on an IAudioPlayer handle held from before the manager died would mirror the
        // facade's no-op contract (PlaybackLifecycleTests.StaleHandle_AfterRecycle_IsInertNotFatal already
        // pins that shape for a merely-*recycled* handle, with SoundManager still alive). That is NOT what
        // happens here, where SoundManager itself is also gone:
        // <para>
        // AudioPlayerInstanceWrapper.Stop/Pause/UnPause/SetVolume/SetPitch (AudioPlayerInstanceWrapper.cs:
        // 38,39,44,48,50) all read the base class's `Instance` property (InstanceWrapper.cs:8), which calls
        // `IsAvailable()` with its default `logWarning: true` (InstanceWrapper.cs:15). Because the pooled
        // AudioPlayer behind this handle is parented under the SoundManager's own transform
        // (AudioPlayerObjectPool.cs:34), destroying the manager destroys that AudioPlayer in the same call,
        // so `_instance != null` is false and `IsAvailable()` calls `LogInstanceIsNull()`. AudioPlayerInstance
        // Wrapper overrides that hook (AudioPlayerInstanceWrapper.cs:19-26) to read
        // `SoundManager.Instance.Setting.LogAccessRecycledPlayerWarning` - the *throwing* static accessor,
        // not the null-safe `BroAudio.Manager` the facade itself uses - and evaluating `SoundManager.Instance`
        // there throws BroAudioException before the log's own condition is even checked. So every release
        // verb on a handle in this exact shape (its own player died together with the manager) throws
        // instead of no-op'ing - the "asymmetry is intentional" contract CLAUDE.md documents at the facade
        // level does not hold one layer down, at the player-handle layer.
        // </para>
        // <para>
        // IsActive/IsPlaying are the contrast: they call `IsAvailable(false)` (no logging) and short-circuit
        // via `&&` before ever touching `Instance` again (AudioPlayerInstanceWrapper.cs:31,32), so they stay
        // safe. That precise split - some members safe, most not - is what makes this a real defect rather
        // than a uniformly-broken feature.
        // </para>
        // Regression this test catches: if AudioPlayerInstanceWrapper.LogInstanceIsNull were ever fixed to
        // use a null-safe accessor (matching the facade's own Manager?. contract), these release verbs would
        // stop throwing and start silently no-op'ing - this test's Assert.Throws would then fail, which is
        // exactly the signal a future fix (and an updated characterization here) would need.
        [UnityTest]
        public IEnumerator StaleHandle_HeldAcrossManagerDestruction_ReleaseVerbsThrowInsteadOfSilentlyNoOp()
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
                Assert.Throws<BroAudioException>(() => verb(),
                    "characterizes: a release verb on a handle whose backing AudioPlayer died together with SoundManager throws " +
                    "via AudioPlayerInstanceWrapper.LogInstanceIsNull -> SoundManager.Instance, not a silent no-op.");
            }

            // The contrast that makes this a targeted defect rather than a blanket one: these two use the
            // non-logging IsAvailable(false) overload and stay safe even in the exact same destroyed state.
            Assert.DoesNotThrow(() => { _ = player.IsActive; }, "IsActive must stay safe (IsAvailable(false) short-circuits before touching Instance again).");
            Assert.DoesNotThrow(() => { _ = player.IsPlaying; }, "IsPlaying must stay safe for the same reason as IsActive.");
            Assert.IsFalse(player.IsActive, "A handle whose backing player died with the manager must read back as inactive.");
        }
    }
}