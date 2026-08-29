using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
#if !UNITY_WEBGL
    /// <summary>
    /// Covers the two unrelated effect mechanisms: per-player Unity filter components added through
    /// AddChorusEffect/AddLowPassEffect/etc. (attach/duplicate/remove guards, recycle cleanup, the
    /// audio-thread OnAudioFilterRead callback), and the mixer-routed BroAudio.SetEffect automation
    /// (exposed parameter writes, the Dominator-only EffectType.Volume guard, ForSeconds auto-reset,
    /// and the FourPole secondary parameter).
    /// </summary>
    public class AudioEffectTests : BroAudioTestFixture
    {
        private const float FrequencyTolerance = 1f;

        private volatile int _capturedChannels = -1;
        private volatile int _capturedBufferLength = -1;

        /// <summary>Reaches through the wrapper BroAudio.Play() returns to the concrete MonoBehaviour, so
        /// tests can GetComponent() on it. Safe because SoundManager.Playback always hands back exactly a
        /// new AudioPlayerInstanceWrapper(player) for a plain (non-BGM) SFX play.</summary>
        private static AudioPlayer Underlying(IAudioPlayer player) => (AudioPlayer)(AudioPlayerInstanceWrapper)player;

        /// <summary>
        /// SetEffect's Add/Override modes for a non-default value permanently flip a bit in the SFX-type's
        /// stored EffectType pref (read by every future Play() via AudioPlayer.Playback.cs's SetTrackEffect)
        /// - state this fixture's own Setting/volume snapshot-restore does not know about. Any test that
        /// moves the mixer-routed LowPass effect away from its default must undo it explicitly here, or it
        /// leaks into later tests in this Editor session.
        /// <para>
        /// This targets LowPass only. SetEffect(new Effect(EffectType.None)) resets every tracked effect at
        /// once, but it logs on construction and for any unresolvable tracked entry, so it stays out of the
        /// shared cleanup path.
        /// </para>
        /// </summary>
        private static IEnumerator ResetLowPassEffect()
        {
            BroAudio.SetEffect(Effect.ResetLowPass());
            yield return WaitFrames(2);
        }

        [UnityTest]
        public IEnumerator AddLowPassEffect_OnActivePlayer_AttachesFilterAndConfiguresThroughProxy()
        {
            SoundID id = NewSound("LowPassFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            player.AddLowPassEffect(proxy => proxy.cutoffFrequency = 3000f);
            yield return WaitFrames(1);

            AudioPlayer concrete = Underlying(player);
            AudioLowPassFilter filter = concrete.GetComponent<AudioLowPassFilter>();
            Assert.IsTrue(filter, "AddLowPassEffect should attach a real AudioLowPassFilter component to the player's GameObject.");
            Assert.AreEqual(3000f, filter.cutoffFrequency, 0.01f, "The proxy's onSet callback should write straight through to the attached component.");
        }

        [UnityTest]
        public IEnumerator AddEffect_EachVerb_AttachesExactlyOneMatchingFilterComponent()
        {
            SoundID id = NewSound("AllEffectsFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            AudioPlayer concrete = Underlying(player);

            (Action<IAudioPlayer> AddEffect, Type ComponentType)[] verbs =
            {
                (p => p.AddChorusEffect(), typeof(AudioChorusFilter)),
                (p => p.AddDistortionEffect(), typeof(AudioDistortionFilter)),
                (p => p.AddEchoEffect(), typeof(AudioEchoFilter)),
                (p => p.AddHighPassEffect(), typeof(AudioHighPassFilter)),
                (p => p.AddLowPassEffect(), typeof(AudioLowPassFilter)),
                (p => p.AddReverbEffect(), typeof(AudioReverbFilter)),
            };

            foreach ((Action<IAudioPlayer> addEffect, Type componentType) in verbs)
            {
                addEffect(player);
                yield return WaitFrames(1);
                Component[] components = concrete.GetComponents(componentType);
                Assert.AreEqual(1, components.Length, $"{componentType.Name} should be attached exactly once by its Add*Effect verb.");
            }
        }

        [UnityTest]
        public IEnumerator AddLowPassEffect_CalledTwice_LogsWarningAndKeepsSingleComponent()
        {
            SoundID id = NewSound("DuplicateFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            AudioPlayer concrete = Underlying(player);

            player.AddLowPassEffect();
            yield return WaitFrames(1);

            LogAssert.Expect(LogType.Warning, new Regex("AudioLowPassFilter already exists"));
            player.AddLowPassEffect();
            yield return WaitFrames(1);

            Assert.AreEqual(1, concrete.GetComponents<AudioLowPassFilter>().Length, "A duplicate Add call should not attach a second component.");
        }

        [UnityTest]
        public IEnumerator RemoveLowPassEffect_DestroysComponent_AndWarnsWhenNoneWasAdded()
        {
            SoundID id = NewSound("RemoveFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            AudioPlayer concrete = Underlying(player);

            player.AddLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsTrue(concrete.GetComponent<AudioLowPassFilter>(), "Precondition: the filter should be attached before removing it.");

            player.RemoveLowPassEffect();
            yield return WaitFrames(1); // Destroy() is deferred to end of frame
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "RemoveLowPassEffect should destroy the component.");

            LogAssert.Expect(LogType.Warning, new Regex("No effects to remove"));
            player.RemoveLowPassEffect();
            yield return WaitFrames(1);
        }

        [UnityTest]
        public IEnumerator Recycle_AfterAddingEffectsAndFilterReader_DestroysThemAndComesBackClean()
        {
            SoundID id = NewSound("RecycleFx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            AudioPlayer concrete = Underlying(player);

            player.AddLowPassEffect();
            player.AddChorusEffect();
            player.OnAudioFilterRead((data, channels) => { });
            yield return WaitFrames(1);

            Assert.IsTrue(concrete.GetComponent<AudioLowPassFilter>(), "Precondition: low-pass should be attached before recycling.");
            Assert.IsTrue(concrete.GetComponent<AudioChorusFilter>(), "Precondition: chorus should be attached before recycling.");
            Assert.IsTrue(concrete.GetComponent<AudioFilterReader>(), "Precondition: the filter reader should be attached before recycling.");

            BroAudio.Stop(id, 0f);
            yield return WaitUntilOrTimeout(() => !concrete.IsActive, "the player to recycle after Stop", 2f);
            yield return WaitFrames(1); // Destroy() is deferred to end of frame

            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "Recycle should destroy every added effect component.");
            Assert.IsFalse(concrete.GetComponent<AudioChorusFilter>(), "Recycle should destroy every added effect component.");
            Assert.IsFalse(concrete.GetComponent<AudioFilterReader>(), "Recycle should destroy the AudioFilterReader.");

            // The player pool (ObjectPool<T>) is a plain List<T> where both Extract() and Recycle() operate
            // on the last index - i.e. LIFO. This test is the only thing borrowing/returning a player, so the
            // very next Play() must hand this exact instance back.
            SoundID id2 = NewSound("RecycleFx2", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player2 = BroAudio.Play(id2);
            yield return WaitUntilOrTimeout(() => player2.IsPlaying, "second playback to start", 2f);
            AudioPlayer concrete2 = Underlying(player2);

            Assert.AreSame(concrete, concrete2, "The pool should hand the just-recycled player back on the very next Play().");
            Assert.IsFalse(concrete2.GetComponent<AudioLowPassFilter>(), "A recycled-and-reused player must come back with zero leaked filter components.");
            Assert.IsFalse(concrete2.GetComponent<AudioChorusFilter>(), "A recycled-and-reused player must come back with zero leaked filter components.");
            Assert.IsFalse(concrete2.GetComponent<AudioFilterReader>(), "A recycled-and-reused player must come back with zero leaked filter components.");
        }

        [UnityTest]
        public IEnumerator AddAndRemoveEffect_OnRecycledPlayer_LogsErrorAndAttachesNothing()
        {
            SoundID id = NewSound("InactiveFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);
            AudioPlayer concrete = Underlying(player);

            BroAudio.Stop(id, 0f);
            yield return WaitUntilOrTimeout(() => !concrete.IsActive, "the player to recycle after Stop", 2f);
            yield return WaitFrames(1);

            // Go through the recycled concrete AudioPlayer directly, not the AudioPlayerInstanceWrapper that
            // BroAudio.Play() returned - the wrapper's own IsAvailable() would short-circuit into a different
            // "this audio player has been recycled" warning instead of AudioPlayer's own !IsActive guard.
            IAudioPlayer inactivePlayer = concrete;

            LogAssert.Expect(LogType.Error, new Regex("Cannot add AudioLowPassFilter to inactive audio player"));
            inactivePlayer.AddLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "AddLowPassEffect on an inactive player must not attach anything.");

            LogAssert.Expect(LogType.Error, new Regex("Cannot remove AudioLowPassFilter from inactive audio player"));
            inactivePlayer.RemoveLowPassEffect();
            yield return WaitFrames(1);
        }

        [UnityTest]
        public IEnumerator OnAudioFilterRead_WhilePlaying_ReceivesNonEmptyBufferFromAudioThread()
        {
            _capturedChannels = -1;
            _capturedBufferLength = -1;

            SoundID id = NewSound("FilterReadFx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            player.OnAudioFilterRead((data, channels) =>
            {
                // OnAudioFilterRead fires on the audio thread - never call NUnit.Assert here. Stash into
                // volatile fields and assert from the test body once control is back on the main thread.
                _capturedChannels = channels;
                _capturedBufferLength = data.Length;
            });

            yield return WaitUntilOrTimeout(() => _capturedBufferLength >= 0, "OnAudioFilterRead to fire at least once", 2f);

            Assert.Greater(_capturedChannels, 0, "channels should be > 0 while the source is playing.");
            Assert.Greater(_capturedBufferLength, 0, "the buffer passed to the callback should be non-empty.");
        }

        [UnityTest]
        public IEnumerator SetEffect_LowPass_WritesFrequencyToMixerAndRoutesFuturePlayersThroughEffectSend()
        {
            BroAudio.SetEffect(Effect.LowPass(800f)); // fadeTime 0 -> Tweak() applies immediately, no wait needed
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float freq));
            Assert.AreEqual(800f, freq, FrequencyTolerance, "SetEffect(Effect.LowPass) should move the exposed mixer parameter to the requested frequency.");

            // SetEffect only updates the per-type pref that's read when a NEW player starts
            // (AudioPlayer.Playback.cs: SetTrackEffect(audioTypePref.EffectType, Add)) - it does not
            // retroactively re-route a player that was already playing before SetEffect was called.
            SoundID id = NewSound("EffectSendFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitUntilOrTimeout(() => player.IsPlaying, "playback to start", 2f);

            AudioPlayer concrete = Underlying(player);
            Assert.IsTrue(concrete.IsUsingTrackEffect, "A player started after SetEffect(LowPass) should route through the effect send channel.");
            Assert.AreNotEqual(EffectType.None, concrete.CurrentActiveTrackEffects & EffectType.LowPass, "LowPass should be part of the player's active track effects.");

            yield return ResetLowPassEffect();
        }

        [UnityTest]
        public IEnumerator SetEffect_VolumeOnNonDominator_LogsErrorAndLeavesMixerUntouched()
        {
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassBefore));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassBefore));

            List<string> logs = new List<string>();
            void OnLog(string message, string stackTrace, LogType type) => logs.Add(message);
            Application.logMessageReceived += OnLog;
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true; // the exact error count through the internal tween coroutine is an implementation detail we don't want to pin down

            // characterizes: EffectType.Volume is only meaningful on a Dominator. A plain SetEffect(Volume)
            // call still runs the whole automation pipeline and logs "only supported on Dominator" from
            // inside it (GetEffectParameterName), rather than rejecting the call up front.
            BroAudio.SetEffect(new Effect(EffectType.Volume));
            yield return WaitFrames(2);

            Application.logMessageReceived -= OnLog;
            LogAssert.ignoreFailingMessages = previousIgnore;

            Assert.IsTrue(logs.Exists(m => m.Contains("only supported on Dominator")), "SetEffect(Volume) on a non-Dominator effect should log the Dominator-only error.");

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassAfter));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassAfter));
            Assert.AreEqual(lowPassBefore, lowPassAfter, FrequencyTolerance, "The unrelated LowPass parameter must be untouched.");
            Assert.AreEqual(highPassBefore, highPassAfter, FrequencyTolerance, "The unrelated HighPass parameter must be untouched.");
        }

        // regression: Effect.LowPass's fadeTime defaults to 0, so Tweak yields nothing and TweakTrackParameter
        // drained its WaitableList synchronously inside StartCoroutine - before SetEffect returned - leaving the
        // chained ForSeconds/Until/While to index WaitableList[-1] and throw.
        [UnityTest]
        public IEnumerator SetEffect_WithDefaultZeroFade_ThenForSeconds_AutoResetsWithoutThrowing()
        {
            IAutoResetWaitable waitable = BroAudio.SetEffect(Effect.LowPass(700f));

            WaitForSeconds hold = null;
            Assert.DoesNotThrow(() => hold = waitable.ForSeconds(0.2f),
                "The documented chaining form must work on Effect.LowPass's default zero fadeTime.");

            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float movedFreq));
            Assert.AreEqual(700f, movedFreq, FrequencyTolerance, "A zero fadeTime still applies the parameter right away.");

            yield return hold;
            yield return WaitFrames(3); // let the internal WaitUntil(IsFinished)-driven reset coroutine catch up

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float resetFreq));
            Assert.AreEqual(AudioConstant.MaxFrequency, resetFreq, FrequencyTolerance,
                "ForSeconds on a zero-fade effect should still auto-reset the parameter once the duration elapses.");

            yield return ResetLowPassEffect();
        }

        [UnityTest]
        public IEnumerator SetEffect_LowPass_ForSeconds_AutoResetsToMaxFrequencyAfterDuration()
        {
            IAutoResetWaitable waitable = BroAudio.SetEffect(Effect.LowPass(700f, 0.1f));
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float v);
                return Mathf.Abs(v - 700f) <= FrequencyTolerance;
            }, "the LowPass fade to reach 700Hz", 2f);

            yield return waitable.ForSeconds(0.2f);
            yield return WaitFrames(3); // let the internal WaitUntil(IsFinished)-driven reset coroutine catch up

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float resetFreq));
            Assert.AreEqual(AudioConstant.MaxFrequency, resetFreq, FrequencyTolerance, "ForSeconds should auto-reset the parameter back to its default once the duration elapses.");

            yield return ResetLowPassEffect();
        }

        [UnityTest]
        public IEnumerator SetEffect_LowPass_WithFourPoleSlope_AlsoWritesSecondaryParameter()
        {
            string secondaryParaName = BroName.LowPassParaName + "2";
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(secondaryParaName, out float secondaryBefore),
                "Effect_LowPass2 should be a real exposed parameter on the mixer regardless of the current slope.");

            SoundManager.Instance.Setting.AudioFilterSlope = FilterSlope.TwoPole;
            BroAudio.SetEffect(Effect.LowPass(500f));
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(secondaryParaName, out float secondaryAfterTwoPole));
            Assert.AreEqual(secondaryBefore, secondaryAfterTwoPole, FrequencyTolerance, "TwoPole slope should leave the secondary parameter untouched.");

            SoundManager.Instance.Setting.AudioFilterSlope = FilterSlope.FourPole;
            BroAudio.SetEffect(Effect.LowPass(650f));
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float primary));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(secondaryParaName, out float secondary));
            Assert.AreEqual(650f, primary, FrequencyTolerance);
            Assert.AreEqual(650f, secondary, FrequencyTolerance, "FourPole slope should also write the secondary (Effect_LowPass2) parameter.");

            yield return ResetLowPassEffect();
        }

        [UnityTest]
        public IEnumerator SetEffect_None_ResetsEveryTrackedEffectParameterToItsDefault()
        {
            BroAudio.SetEffect(Effect.LowPass(800f));
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float moved));
            Assert.AreEqual(800f, moved, FrequencyTolerance, "Precondition: the LowPass parameter should have moved off its default.");

            // Two unrelated warts make SetEffect(None) noisy, neither is what this test is about:
            // new Effect(EffectType.None) logs from Effect's Value setter, and an earlier test in the same
            // Editor session can leave a tweaker registered for an effect whose parameter never resolves
            // (EffectType.Volume on a non-Dominator), which the reset loop then logs once per entry.
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            BroAudio.SetEffect(new Effect(EffectType.None));
            yield return WaitFrames(2);

            LogAssert.ignoreFailingMessages = previousIgnore;

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float reset));
            Assert.AreEqual(AudioConstant.MaxFrequency, reset, FrequencyTolerance,
                "SetEffect(EffectType.None) should reset every tracked effect's mixer parameter back to its default.");
            // No ResetLowPassEffect() cleanup needed: the None path also overrides the per-type pref to None.
        }
    }
#endif
}