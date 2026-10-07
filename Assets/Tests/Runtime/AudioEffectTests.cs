using System;
using System.Collections;
using System.Collections.Generic;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.BroAudio.Tools;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
#if !UNITY_WEBGL
    /// <summary>
    /// Covers two unrelated effect mechanisms: per-player filter components (Add*Effect, OnAudioFilterRead,
    /// GetOutputData) and the mixer-routed BroAudio.SetEffect automation.
    /// </summary>
    public class AudioEffectTests : BroAudioTestFixture
    {
        private const float FrequencyTolerance = 1f;

        private volatile int _capturedChannels = -1;
        private volatile int _capturedBufferLength = -1;

        /// <summary>
        /// Resets the mixer-routed LowPass mid-test; end-of-test cleanup is the base fixture's TearDown.
        /// A default-valued Effect is what clears the type's routing bit (SetEffectMode.Remove). Don't use
        /// SetEffect(new Effect(EffectType.None)) here: it logs on construction and per unresolvable entry.
        /// </summary>
        private static IEnumerator ResetLowPassEffect()
        {
            BroAudio.SetEffect(Effect.ResetLowPass());
            yield return WaitFrames(2);
        }

        /// <summary>
        /// Runs <paramref name="action"/> for <paramref name="waitFrames"/> frames, collecting its Error logs
        /// into <paramref name="taggedErrors"/>. Used instead of LogAssert.Expect because the guards exercised
        /// log an unpinned number of times per call.
        /// <para>
        /// Assert after collecting, never inside the log callback: Unity's log dispatch doesn't expect a
        /// handler to throw. An untagged Error, or any Exception/Assert, still fails the test, since
        /// ignoreFailingMessages would otherwise swallow it.
        /// </para>
        /// </summary>
        private static IEnumerator RunAndCollectBroAudioErrorLogs(Action action, int waitFrames, List<string> taggedErrors)
        {
            List<string> exceptionsAndAsserts = new List<string>();
            void OnLog(string message, string stackTrace, LogType type)
            {
                switch (type)
                {
                    case LogType.Error:
                        taggedErrors.Add(message);
                        break;
                    case LogType.Exception:
                    case LogType.Assert:
                        exceptionsAndAsserts.Add(type + ": " + message);
                        break;
                }
            }

            Application.logMessageReceived += OnLog;
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            // ignoreFailingMessages is static: restore it in finally, or a throwing action leaks it into later tests.
            try
            {
                action();
                for (int i = 0; i < waitFrames; i++)
                {
                    yield return null;
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
                Application.logMessageReceived -= OnLog;
            }

            Assert.IsEmpty(exceptionsAndAsserts,
                "An exception or assert logged while failing messages were ignored must still fail the test: " +
                string.Join(" | ", exceptionsAndAsserts));
            foreach (string message in taggedErrors)
            {
                Assert.IsTrue(message.Contains(Utility.LogTitle), $"An error unrelated to BroAudio's own tagged logging must not be swallowed: {message}");
            }
        }

        [UnityTest]
        public IEnumerator AddLowPassEffect_OnActivePlayer_AttachesFilterAndConfiguresThroughProxy()
        {
            SoundID id = NewSound("LowPassFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.AddLowPassEffect(proxy => proxy.cutoffFrequency = 3000f);
            yield return WaitFrames(1);

            AudioPlayer concrete = InstanceOf(player);
            AudioLowPassFilter filter = concrete.GetComponent<AudioLowPassFilter>();
            Assert.IsTrue(filter, "AddLowPassEffect should attach a real AudioLowPassFilter component to the player's GameObject.");
            Assert.AreEqual(3000f, filter.cutoffFrequency, 0.01f, "The proxy's onSet callback should write straight through to the attached component.");
        }

        [UnityTest]
        public IEnumerator AddEffect_EachVerb_AttachesExactlyOneMatchingFilterComponent()
        {
            SoundID id = NewSound("AllEffectsFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            AudioPlayer concrete = InstanceOf(player);

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
            yield return WaitForPlaybackStart(player);
            AudioPlayer concrete = InstanceOf(player);

            player.AddLowPassEffect();
            yield return WaitFrames(1);

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            player.AddLowPassEffect();
            yield return WaitFrames(1);

            Assert.AreEqual(1, concrete.GetComponents<AudioLowPassFilter>().Length, "A duplicate Add call should not attach a second component.");
        }

        [UnityTest]
        public IEnumerator RemoveLowPassEffect_DestroysComponent_AndWarnsWhenNoneWasAdded()
        {
            SoundID id = NewSound("RemoveFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            AudioPlayer concrete = InstanceOf(player);

            player.AddLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsTrue(concrete.GetComponent<AudioLowPassFilter>(), "Precondition: the filter should be attached before removing it.");

            player.RemoveLowPassEffect();
            yield return WaitFrames(1); // Destroy() is deferred to end of frame
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "RemoveLowPassEffect should destroy the component.");

            LogAssert.Expect(LogType.Warning, TestAudioLibrary.BroAudioLogPrefix);
            player.RemoveLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "Removing again with nothing attached must stay a no-op, not throw or attach anything.");
        }

        [UnityTest]
        public IEnumerator Recycle_AfterAddingEffectsAndFilterReader_DestroysThemAndComesBackClean()
        {
            SoundID id = NewSound("RecycleFx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);
            AudioPlayer concrete = InstanceOf(player);

            player.AddLowPassEffect();
            player.AddChorusEffect();
            player.OnAudioFilterRead((data, channels) => { });
            yield return WaitFrames(1);

            Assert.IsTrue(concrete.GetComponent<AudioLowPassFilter>(), "Precondition: low-pass should be attached before recycling.");
            Assert.IsTrue(concrete.GetComponent<AudioChorusFilter>(), "Precondition: chorus should be attached before recycling.");
            Assert.IsTrue(concrete.GetComponent<AudioFilterReader>(), "Precondition: the filter reader should be attached before recycling.");

            BroAudio.Stop(id, 0f);
            yield return WaitForRecycle(concrete, "the player to recycle after Stop");
            yield return WaitFrames(1); // Destroy() is deferred to end of frame

            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "Recycle should destroy every added effect component.");
            Assert.IsFalse(concrete.GetComponent<AudioChorusFilter>(), "Recycle should destroy every added effect component.");
            Assert.IsFalse(concrete.GetComponent<AudioFilterReader>(), "Recycle should destroy the AudioFilterReader.");

            // The player pool is LIFO and nothing else borrows a player here, so the next Play() reuses this instance.
            SoundID id2 = NewSound("RecycleFx2", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player2 = BroAudio.Play(id2);
            yield return WaitForPlaybackStart(player2, "second playback to start");
            AudioPlayer concrete2 = InstanceOf(player2);

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
            yield return WaitForPlaybackStart(player);
            AudioPlayer concrete = InstanceOf(player);

            BroAudio.Stop(id, 0f);
            yield return WaitForRecycle(concrete, "the player to recycle after Stop");
            yield return WaitFrames(1);

            // Call the concrete player, not the wrapper: the wrapper's IsAvailable() would short-circuit with
            // its own warning before AudioPlayer's !IsActive guard is reached.
            IAudioPlayer inactivePlayer = concrete;

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            inactivePlayer.AddLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "AddLowPassEffect on an inactive player must not attach anything.");

            LogAssert.Expect(LogType.Error, TestAudioLibrary.BroAudioLogPrefix);
            inactivePlayer.RemoveLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "RemoveLowPassEffect on an inactive player must not attach or leave anything behind either.");
        }

        // A handover transfers added effects once per decorator plus once. Unity's refusal of a duplicate filter
        // is an untagged LogType.Log, which can't fail a test: refusals repeat one message, so counting only the
        // largest identical group keeps unrelated untagged logs from shifting the count.
        [UnityTest]
        public IEnumerator Loop_WithAnAddedEffectAndTwoDecorators_CarriesOneFilterAndOneEntryAcrossSeams()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 1f;
            const float CutoffFrequency = 3000f;
            AudioEntity entity = NewEntity("LoopingAddedEffectSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            yield return WaitForPlaybackStart(player, "the looping sound to start playing");

            // Must happen before the first seam: the transfer reads the outgoing list at the seam itself.
            player.AsBGM();
            player.AsDominator();
            player.AddLowPassEffect(proxy => proxy.cutoffFrequency = CutoffFrequency);

            AudioPlayer firstInstance = InstanceOf(player);
            Assert.IsTrue(firstInstance, "Precondition: the handle should resolve to a live player.");
            List<AudioPlayerDecorator> decorators = TestAudioLibrary.GetPrivateField<List<AudioPlayerDecorator>>(
                firstInstance, TestAudioLibrary.Reflected.AudioPlayer.Decorators);
            int decoratorCount = decorators == null ? 0 : decorators.Count;
            Assert.AreEqual(2, decoratorCount, $"Precondition: AsBGM() and AsDominator() should attach one decorator each; observed {decoratorCount}.");
            Assert.AreEqual(1, AddedEffectCount(firstInstance), "Precondition: AddLowPassEffect should record exactly one added effect.");
            Assert.AreEqual(1, firstInstance.GetComponents<AudioLowPassFilter>().Length, "Precondition: AddLowPassEffect should attach exactly one AudioLowPassFilter.");

            List<string> untaggedLogs = new List<string>();
            List<string> untaggedPlainLogs = new List<string>();
            void OnLog(string message, string stackTrace, LogType type)
            {
                if (message.Contains(Utility.LogTitle))
                {
                    return;
                }

                untaggedLogs.Add(type + ": " + message);
                if (type == LogType.Log)
                {
                    untaggedPlainLogs.Add(message);
                }
            }

            Application.logMessageReceived += OnLog;
            AudioPlayer secondInstance = null;
            int refusalsAtFirstSeam = -1;
            int refusalsAtSecondSeam = -1;
            int secondEntries = -1, secondFilters = -1, thirdEntries = -1, thirdFilters = -1;
            float secondCutoff = -1f;
            string secondCutoffs = string.Empty;
            try
            {
                // The transfers and the handle re-point happen in one call, so reading logs when the handle
                // moves covers exactly that seam.
                yield return WaitUntilOrTimeout(() =>
                {
                    AudioPlayer current = InstanceOf(player);
                    return current && current != firstInstance;
                }, "the loop to hand over to a second player, with the handle following it", HandoverWaitSeconds);
                secondInstance = InstanceOf(player);
                int firstSeamLogEnd = untaggedPlainLogs.Count;
                refusalsAtFirstSeam = LargestIdenticalGroup(untaggedPlainLogs, 0, firstSeamLogEnd);
                AudioLowPassFilter[] secondFilterComponents = secondInstance.GetComponents<AudioLowPassFilter>();
                secondEntries = AddedEffectCount(secondInstance);
                secondFilters = secondFilterComponents.Length;
                secondCutoff = secondFilters > 0 ? secondFilterComponents[0].cutoffFrequency : -1f;
                secondCutoffs = DescribeCutoffs(secondFilterComponents);

                yield return WaitUntilOrTimeout(() =>
                {
                    AudioPlayer current = InstanceOf(player);
                    return current && current != secondInstance;
                }, "the loop to hand over to a third player", HandoverWaitSeconds);
                AudioPlayer thirdInstance = InstanceOf(player);
                refusalsAtSecondSeam = LargestIdenticalGroup(untaggedPlainLogs, firstSeamLogEnd, untaggedPlainLogs.Count);
                thirdEntries = AddedEffectCount(thirdInstance);
                thirdFilters = thirdInstance.GetComponents<AudioLowPassFilter>().Length;

                player.Stop(0f);
                yield return null;
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }

            string observed = $"Observed: second player {secondEntries} list entries / {secondFilters} filter(s) " +
                              $"[{secondCutoffs}]Hz; third player {thirdEntries} entries / {thirdFilters} filter(s); " +
                              $"repeated untagged logs {refusalsAtFirstSeam} at the first seam, {refusalsAtSecondSeam} at the second; " +
                              $"every untagged log [{string.Join(" | ", untaggedLogs)}].";

            Assert.AreEqual(1, secondFilters,
                $"Unity keeps one AudioLowPassFilter per GameObject, so the voice carries a single filter. {observed}");
            Assert.AreEqual(CutoffFrequency, secondCutoff, 1f,
                $"The one filter that did attach must carry the added effect's settings over the seam. {observed}");
            Assert.AreEqual(1, secondEntries,
                $"Repeated transfers must not append entries for a filter the player already has. {observed}");
            Assert.LessOrEqual(refusalsAtFirstSeam, 1,
                $"No transfer may be refused by Unity; a lone untagged log can be unrelated. {observed}");

            Assert.AreEqual(1, thirdFilters,
                $"Still one filter on the voice after the second seam. {observed}");
            Assert.AreEqual(1, thirdEntries,
                $"Still one entry after the second seam: the list must not grow per seam. {observed}");
            Assert.LessOrEqual(refusalsAtSecondSeam, 1,
                $"No transfer may be refused by Unity at the second seam either. {observed}");
        }

        /// <summary>Non-generic IList: the element type is a private struct.</summary>
        private static int AddedEffectCount(AudioPlayer player)
        {
            IList list = TestAudioLibrary.GetPrivateField<IList>(player, TestAudioLibrary.Reflected.AudioPlayer.AddedEffects);
            return list == null ? 0 : list.Count;
        }

        /// <summary>Size of the largest group of identical messages in <paramref name="logs"/>[start, end).</summary>
        private static int LargestIdenticalGroup(List<string> logs, int start, int end)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            int largest = 0;
            for (int i = start; i < end; i++)
            {
                counts.TryGetValue(logs[i], out int count);
                count++;
                counts[logs[i]] = count;
                largest = Math.Max(largest, count);
            }
            return largest;
        }

        private static string DescribeCutoffs(AudioLowPassFilter[] filters)
        {
            List<string> cutoffs = new List<string>(filters.Length);
            foreach (AudioLowPassFilter filter in filters)
            {
                cutoffs.Add(filter.cutoffFrequency.ToString("F0"));
            }
            return string.Join(", ", cutoffs);
        }

        [UnityTest]
        public IEnumerator OnAudioFilterRead_WhilePlaying_ReceivesNonEmptyBufferFromAudioThread()
        {
            _capturedChannels = -1;
            _capturedBufferLength = -1;

            SoundID id = NewSound("FilterReadFx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            player.OnAudioFilterRead((data, channels) =>
            {
                // Audio thread: never Assert here; stash into volatile fields for the main thread.
                _capturedChannels = channels;
                _capturedBufferLength = data.Length;
            });

            yield return WaitUntilOrTimeout(() => _capturedBufferLength >= 0, "OnAudioFilterRead to fire at least once", DefaultPlaybackWaitSeconds);

            Assert.Greater(_capturedChannels, 0, "channels should be > 0 while the source is playing.");
            Assert.Greater(_capturedBufferLength, 0, "the buffer passed to the callback should be non-empty.");
        }

        [UnityTest]
        public IEnumerator GetOutputData_WhilePlaying_ReturnsTheSourcesNonSilentSignal()
        {
            yield return RequireRealtimeAudioClock();

            SoundID id = NewSound("OutputDataSfx", BroAudioType.SFX, NewClip(3f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            // The test clip is a 0.25-amplitude sine, so any live window holds samples well above 0.01.
            float[] samples = new float[1024];
            yield return WaitUntilOrTimeout(() =>
            {
                player.GetOutputData(samples, 0);
                return Array.Exists(samples, sample => Mathf.Abs(sample) > 0.01f);
            }, "GetOutputData to hand back the playing source's non-silent signal", DefaultPlaybackWaitSeconds);
        }

        [UnityTest]
        public IEnumerator SetEffect_LowPass_WritesFrequencyToMixerAndRoutesFuturePlayersThroughEffectSend()
        {
            BroAudio.SetEffect(Effect.LowPass(800f)); // fadeTime 0 -> Tweak() applies immediately, no wait needed
            yield return WaitFrames(1);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float freq));
            Assert.AreEqual(800f, freq, FrequencyTolerance, "SetEffect(Effect.LowPass) should move the exposed mixer parameter to the requested frequency.");

            // Pins the future-player half; SetEffect_ScopedToMusic_ReroutesLivePlayerOfThatTypeOnly pins live players.
            SoundID id = NewSound("EffectSendFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioPlayer concrete = InstanceOf(player);
            Assert.IsTrue(concrete.IsUsingTrackEffect, "A player started after SetEffect(LowPass) should route through the effect send channel.");
            Assert.AreNotEqual(EffectType.None, concrete.CurrentActiveTrackEffects & EffectType.LowPass, "LowPass should be part of the player's active track effects.");

            // The flags are bookkeeping; the mixer is what is heard. Catches a player left on both (doubled) or
            // neither (silent) even with the flags right.
            ReadTrackAndSend(concrete, out string trackName, out float trackDb, out float sendDb);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, sendDb, DecibelTolerance,
                $"A player routed through the effect send should carry its level on {trackName}{BroName.EffectParaNameSuffix}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, trackDb, DecibelTolerance,
                $"A player routed through the effect send should leave its dry track {trackName} muted, or it is heard twice.");

            yield return ResetLowPassEffect();
            yield return WaitUntilOrTimeout(() => !concrete.IsUsingTrackEffect,
                "the reset to take the live player off the effect send", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float resetFreq));
            Assert.AreEqual(AudioConstant.MaxFrequency, resetFreq, FrequencyTolerance, "The reset should put Effect_LowPass back to its default.");
            ReadTrackAndSend(concrete, out _, out float trackDbAfterReset, out float sendDbAfterReset);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, trackDbAfterReset, DecibelTolerance,
                $"After the reset the player's level should be back on its dry track {trackName}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, sendDbAfterReset, DecibelTolerance,
                $"After the reset {trackName}{BroName.EffectParaNameSuffix} should be muted again.");
        }

        // HighPass resolves its own parameter in GetEffectParameterName, so the LowPass test above does not cover it.
        [UnityTest]
        public IEnumerator SetEffect_HighPass_WritesFrequencyToBothPolesAndRoutesFuturePlayersThroughEffectSend()
        {
            const float Cutoff = 1200f;
            string secondaryParaName = BroName.HighPassParaName + "2";
            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            Assert.AreEqual(FilterSlope.FourPole, SoundManager.Instance.Setting.AudioFilterSlope,
                "Precondition: the factory slope is FourPole, which drives Effect_HighPass2 alongside Effect_HighPass.");
            Assert.IsTrue(mixer.GetFloat(BroName.LowPassParaName, out float lowPassBefore));

            BroAudio.SetEffect(Effect.HighPass(Cutoff)); // fadeTime 0 -> Tweak() applies immediately, no wait needed
            yield return WaitFrames(1);

            Assert.IsTrue(mixer.GetFloat(BroName.HighPassParaName, out float primary));
            Assert.IsTrue(mixer.GetFloat(secondaryParaName, out float secondary),
                "Effect_HighPass2 should be a real exposed parameter on the mixer.");
            Assert.AreEqual(Cutoff, primary, FrequencyTolerance, "SetEffect(Effect.HighPass) should move Effect_HighPass to the requested frequency.");
            Assert.AreEqual(Cutoff, secondary, FrequencyTolerance, "Under the FourPole slope SetEffect(Effect.HighPass) should also move Effect_HighPass2.");
            Assert.IsTrue(mixer.GetFloat(BroName.LowPassParaName, out float lowPassAfter));
            Assert.AreEqual(lowPassBefore, lowPassAfter, FrequencyTolerance, "A HighPass effect must leave Effect_LowPass untouched.");

            SoundID id = NewSound("HighPassSendFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioPlayer concrete = InstanceOf(player);
            Assert.IsTrue(concrete.IsUsingTrackEffect, "A player started after SetEffect(HighPass) should route through the effect send channel.");
            Assert.AreNotEqual(EffectType.None, concrete.CurrentActiveTrackEffects & EffectType.HighPass, "HighPass should be part of the player's active track effects.");

            ReadTrackAndSend(concrete, out string trackName, out float trackDb, out float sendDb);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, sendDb, DecibelTolerance,
                $"A player routed through the effect send should carry its level on {trackName}{BroName.EffectParaNameSuffix}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, trackDb, DecibelTolerance,
                $"A player routed through the effect send should leave its dry track {trackName} muted, or it is heard twice.");

            BroAudio.SetEffect(Effect.ResetHighPass());
            yield return WaitUntilOrTimeout(() => !concrete.IsUsingTrackEffect,
                "the HighPass reset to take the live player off the effect send", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(mixer.GetFloat(BroName.HighPassParaName, out float resetPrimary));
            Assert.IsTrue(mixer.GetFloat(secondaryParaName, out float resetSecondary));
            Assert.AreEqual(AudioConstant.MinFrequency, resetPrimary, FrequencyTolerance, "The reset should put Effect_HighPass back to its default.");
            Assert.AreEqual(AudioConstant.MinFrequency, resetSecondary, FrequencyTolerance, "The reset should put Effect_HighPass2 back to its default too.");
            ReadTrackAndSend(concrete, out _, out float trackDbAfterReset, out float sendDbAfterReset);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, trackDbAfterReset, DecibelTolerance,
                $"After the reset the player's level should be back on its dry track {trackName}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, sendDbAfterReset, DecibelTolerance,
                $"After the reset {trackName}{BroName.EffectParaNameSuffix} should be muted again.");
        }

        [UnityTest]
        public IEnumerator SetEffect_ScopedToMusic_ReroutesLivePlayerOfThatTypeOnly()
        {
            SoundID musicId = NewSound("ScopedBgm", BroAudioType.Music, NewClip(4f));
            SoundID sfxId = NewSound("ScopedSfx", BroAudioType.SFX, NewClip(4f));
            IAudioPlayer musicPlayer = BroAudio.Play(musicId);
            IAudioPlayer sfxPlayer = BroAudio.Play(sfxId);
            yield return WaitForPlaybackStart(musicPlayer, "the Music playback to start");
            yield return WaitForPlaybackStart(sfxPlayer, "the SFX playback to start");

            AudioPlayer music = InstanceOf(musicPlayer);
            AudioPlayer sfx = InstanceOf(sfxPlayer);
            Assert.IsFalse(music.IsUsingTrackEffect, "Precondition: the Music player should start on its plain track.");
            Assert.IsFalse(sfx.IsUsingTrackEffect, "Precondition: the SFX player should start on its plain track.");

            BroAudio.SetEffect(Effect.LowPass(800f), BroAudioType.Music);
            yield return WaitUntilOrTimeout(() => music.IsUsingTrackEffect,
                "the already-playing Music player to be re-routed through the effect send", DefaultPlaybackWaitSeconds);

            Assert.AreNotEqual(EffectType.None, music.CurrentActiveTrackEffects & EffectType.LowPass,
                "LowPass should be part of the live Music player's active track effects.");
            Assert.AreEqual(EffectType.None, sfx.CurrentActiveTrackEffects,
                "An effect scoped to Music must not re-route a live SFX player.");

            ReadTrackAndSend(music, out string musicTrack, out float musicTrackDb, out float musicSendDb);
            ReadTrackAndSend(sfx, out string sfxTrack, out float sfxTrackDb, out float sfxSendDb);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, musicSendDb, DecibelTolerance,
                $"The re-routed Music player's level should move onto {musicTrack}{BroName.EffectParaNameSuffix}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, musicTrackDb, DecibelTolerance,
                $"The re-routed Music player's dry track {musicTrack} should be muted, or it is heard twice.");
            Assert.AreEqual(AudioConstant.FullDecibelVolume, sfxTrackDb, DecibelTolerance,
                $"The SFX player's level should stay on its dry track {sfxTrack}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, sfxSendDb, DecibelTolerance,
                $"The SFX player's send {sfxTrack}{BroName.EffectParaNameSuffix} should stay silent.");

            // Poll: the Remove path defers re-routing to the automation helper's onReset callback.
            yield return ResetLowPassEffect();
            yield return WaitUntilOrTimeout(() => !music.IsUsingTrackEffect,
                "the reset to remove the Music player's track effect", DefaultPlaybackWaitSeconds);

            Assert.AreEqual(EffectType.None, music.CurrentActiveTrackEffects, "The reset should leave the Music player with no active track effect.");
            Assert.AreEqual(EffectType.None, sfx.CurrentActiveTrackEffects, "The reset should leave the untouched SFX player clear as well.");

            ReadTrackAndSend(music, out _, out float musicTrackDbAfterReset, out float musicSendDbAfterReset);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, musicTrackDbAfterReset, DecibelTolerance,
                $"After the reset the Music player's level should be back on its dry track {musicTrack}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, musicSendDbAfterReset, DecibelTolerance,
                $"After the reset {musicTrack}{BroName.EffectParaNameSuffix} should be muted again.");
        }

        /// <summary>
        /// Reads the two mixer parameters that can carry a track's level: the dry track (its group's name) and
        /// its effect send (that name plus <see cref="BroName.EffectParaNameSuffix"/>).
        /// </summary>
        private static void ReadTrackAndSend(AudioPlayer player, out string trackName, out float trackDb, out float sendDb)
        {
            AudioMixerGroup track = player.GetComponent<AudioSource>().outputAudioMixerGroup;
            Assert.IsTrue(track, "Precondition: the player must hold a pooled mixer track.");
            trackName = track.name;
            AudioMixer mixer = SoundManager.Instance.AudioMixer;
            Assert.IsTrue(mixer.GetFloat(trackName, out trackDb), $"{trackName} must be an exposed mixer parameter.");
            Assert.IsTrue(mixer.GetFloat(trackName + BroName.EffectParaNameSuffix, out sendDb),
                $"{trackName}{BroName.EffectParaNameSuffix} must be an exposed mixer parameter.");
        }

        [UnityTest]
        public IEnumerator SetEffect_VolumeOnNonDominator_LogsErrorAndLeavesMixerUntouched()
        {
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassBefore));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassBefore));

            // characterizes: the Dominator-only guard logs from inside the automation pipeline rather than
            // rejecting up front. The untouched mixer is the contract; the log is checked by type and tag only.
            List<string> taggedErrors = new List<string>();
            yield return RunAndCollectBroAudioErrorLogs(() => BroAudio.SetEffect(new Effect(EffectType.Volume)), 2, taggedErrors);

            Assert.IsNotEmpty(taggedErrors, "SetEffect(Volume) on a non-Dominator effect should log at least one error from the Dominator-only guard.");

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassAfter));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassAfter));
            Assert.AreEqual(lowPassBefore, lowPassAfter, FrequencyTolerance, "The unrelated LowPass parameter must be untouched.");
            Assert.AreEqual(highPassBefore, highPassAfter, FrequencyTolerance, "The unrelated HighPass parameter must be untouched.");
        }

        // regression: Effect.LowPass's fadeTime defaults to 0, so Tweak yields nothing and
        // TweakTrackParameter drains its WaitableList synchronously inside StartCoroutine, before SetEffect
        // returns. The chained ForSeconds/Until/While must still work rather than index an empty WaitableList.
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
        }

        [UnityTest]
        public IEnumerator SetEffect_LowPass_ForSeconds_AutoResetsToMaxFrequencyAfterDuration()
        {
            IAutoResetWaitable waitable = BroAudio.SetEffect(Effect.LowPass(700f, 0.1f));
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float v);
                return Mathf.Abs(v - 700f) <= FrequencyTolerance;
            }, "the LowPass fade to reach 700Hz", DefaultPlaybackWaitSeconds);

            // holdEnd uses the waitable's own clock (Time.time at the ForSeconds call, constant within a frame),
            // so no frame before it may see a reset. Keep the hold at 1s or more so a slow frame can't leave it unsampled.
            const float HoldSeconds = 1.5f;
            float holdEnd = Time.time + HoldSeconds;
            waitable.ForSeconds(HoldSeconds);

            int heldSamples = 0;
            float lowestHeld = float.MaxValue;
            float highestHeld = float.MinValue;
            while (Time.time < holdEnd)
            {
                Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float held));
                lowestHeld = Mathf.Min(lowestHeld, held);
                highestHeld = Mathf.Max(highestHeld, held);
                heldSamples++;
                yield return null;
            }

            Assert.Greater(heldSamples, 0, "The hold window should have been sampled at least once.");
            Assert.AreEqual(700f, lowestHeld, FrequencyTolerance,
                $"ForSeconds should hold the requested value for the whole duration (read {lowestHeld:F0}-{highestHeld:F0}Hz over {heldSamples} frames).");
            Assert.AreEqual(700f, highestHeld, FrequencyTolerance,
                $"ForSeconds should hold the requested value for the whole duration (read {lowestHeld:F0}-{highestHeld:F0}Hz over {heldSamples} frames).");

            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float v);
                return Mathf.Abs(v - AudioConstant.MaxFrequency) <= FrequencyTolerance;
            }, "ForSeconds to auto-reset Effect_LowPass to its default once the duration elapses", DefaultPlaybackWaitSeconds);
        }

        // Weaker first, then stronger: the stronger one restarts the tween and the held weaker one waits under it.
        // Non-zero fades on purpose: a zero-fade Tweak never yields, which moves where the queue drains (see
        // SetEffect_WithDefaultZeroFade_ThenForSeconds_AutoResetsWithoutThrowing).
        [UnityTest]
        public IEnumerator SetEffect_StrongerLowPassOverAHeldWeakerOne_TakesOverThenDropsBackToTheWeakerValueUntilReleased()
        {
            const float WeakCutoff = 2000f;
            const float StrongCutoff = 500f;
            const float FadeSeconds = 0.1f;
            const float HoldSeconds = 1.5f;
            AudioMixer mixer = SoundManager.Instance.AudioMixer;

            // A local flag, not a playing sound, so the hold doesn't depend on the audio clock. Released in
            // finally: a weaker effect left held would queue every later, weaker LowPass behind it.
            bool holdWeak = true;
            try
            {
                BroAudio.SetEffect(Effect.LowPass(WeakCutoff, FadeSeconds)).While(() => holdWeak);
                yield return WaitUntilOrTimeout(() => LowPassReads(mixer, WeakCutoff),
                    "the held weaker LowPass to reach its cutoff", DefaultPlaybackWaitSeconds);

                // Same clock and frame as the waitable's own EndTime, so no frame before strongHoldEnd may see the drop.
                float strongHoldEnd = Time.time + HoldSeconds;
                BroAudio.SetEffect(Effect.LowPass(StrongCutoff, FadeSeconds)).ForSeconds(HoldSeconds);
                yield return WaitUntilOrTimeout(() => LowPassReads(mixer, StrongCutoff),
                    "the stronger LowPass to take over from the held weaker one", DefaultPlaybackWaitSeconds);
                yield return AssertLowPassHoldsUntil(mixer, StrongCutoff, strongHoldEnd,
                    "The stronger effect should hold for its whole ForSeconds duration");

                // The way back must come straight down from the stronger cutoff: a reset to the default on the way,
                // even for a frame, reads above the weaker cutoff.
                float highestOnTheWayBack = float.MinValue;
                yield return WaitUntilOrTimeout(() =>
                {
                    mixer.GetFloat(BroName.LowPassParaName, out float v);
                    highestOnTheWayBack = Mathf.Max(highestOnTheWayBack, v);
                    return Mathf.Abs(v - WeakCutoff) <= FrequencyTolerance;
                }, "Effect_LowPass to drop back to the still-held weaker cutoff once the stronger one ends", DefaultPlaybackWaitSeconds);
                Assert.LessOrEqual(highestOnTheWayBack, WeakCutoff + FrequencyTolerance,
                    $"The stronger effect's end must hand over to the queued weaker one without lifting the filter (read up to {highestOnTheWayBack:F0}Hz).");

                yield return AssertLowPassHoldsUntil(mixer, WeakCutoff, Time.time + HoldSeconds,
                    "The weaker effect should keep holding while its While condition is true");

                holdWeak = false;
                yield return WaitUntilOrTimeout(() => LowPassReads(mixer, AudioConstant.MaxFrequency),
                    "releasing the last queued effect to auto-reset Effect_LowPass to its default", DefaultPlaybackWaitSeconds);
            }
            finally
            {
                holdWeak = false;
            }
        }

        private static bool LowPassReads(AudioMixer mixer, float cutoff)
            => mixer.GetFloat(BroName.LowPassParaName, out float v) && Mathf.Abs(v - cutoff) <= FrequencyTolerance;

        /// <summary>Samples Effect_LowPass every frame until Time.time reaches <paramref name="endTime"/>; each must read <paramref name="cutoff"/>.</summary>
        private static IEnumerator AssertLowPassHoldsUntil(AudioMixer mixer, float cutoff, float endTime, string message)
        {
            int samples = 0;
            float lowest = float.MaxValue;
            float highest = float.MinValue;
            while (Time.time < endTime)
            {
                Assert.IsTrue(mixer.GetFloat(BroName.LowPassParaName, out float held));
                lowest = Mathf.Min(lowest, held);
                highest = Mathf.Max(highest, held);
                samples++;
                yield return null;
            }

            Assert.Greater(samples, 0, "The hold window should have been sampled at least once.");
            string observed = $" (read {lowest:F0}-{highest:F0}Hz over {samples} frames).";
            Assert.AreEqual(cutoff, lowest, FrequencyTolerance, message + observed);
            Assert.AreEqual(cutoff, highest, FrequencyTolerance, message + observed);
        }

        // Pins TEST_FINDINGS #71. The explicit reset at the end is both the contrast and this test's cleanup,
        // so VerifyGlobalStateRestored judges only real leaks.
        [UnityTest]
        [Category("Finding_71")]
        public IEnumerator SetEffect_LowPass_ForSeconds_ResetsTheParameterButLeavesTheTypeRoutedThroughTheEffectSend()
        {
            const float HoldSeconds = 0.2f;
            SoundID liveId = NewSound("TimedEffectLiveSfx", BroAudioType.SFX, NewClip(6f));
            SoundID laterId = NewSound("TimedEffectLaterSfx", BroAudioType.SFX, NewClip(6f));
            IAudioPlayer livePlayer = BroAudio.Play(liveId);
            yield return WaitForPlaybackStart(livePlayer, "the player that is live through the timed effect to start");
            AudioPlayer live = InstanceOf(livePlayer);

            WaitForSeconds hold = BroAudio.SetEffect(Effect.LowPass(700f)).ForSeconds(HoldSeconds);
            yield return WaitUntilOrTimeout(() => live.IsUsingTrackEffect,
                "the live player to be routed through the effect send while the effect holds", DefaultPlaybackWaitSeconds);

            yield return hold;
            yield return WaitUntilOrTimeout(() =>
            {
                SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float v);
                return Mathf.Abs(v - AudioConstant.MaxFrequency) <= FrequencyTolerance;
            }, "ForSeconds to auto-reset Effect_LowPass to its default", DefaultPlaybackWaitSeconds);

            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref sfxPref));
            Assert.AreNotEqual(EffectType.None, sfxPref.EffectType & EffectType.LowPass,
                "characterizes: the auto-reset leaves LowPass set on the SFX type's stored effect preference.");
            Assert.IsTrue(live.IsUsingTrackEffect,
                "characterizes: the player that was live through the timed effect is still on the effect send after the reset.");

            IAudioPlayer laterPlayer = BroAudio.Play(laterId);
            yield return WaitForPlaybackStart(laterPlayer, "a player started after the reset to start");
            AudioPlayer later = InstanceOf(laterPlayer);
            Assert.AreNotEqual(EffectType.None, later.CurrentActiveTrackEffects & EffectType.LowPass,
                "characterizes: a player started after the effect reset still takes LowPass from the type preference.");
            ReadTrackAndSend(later, out string trackName, out float trackDb, out float sendDb);
            Assert.AreEqual(AudioConstant.FullDecibelVolume, sendDb, DecibelTolerance,
                $"characterizes: the later player's level is carried on {trackName}{BroName.EffectParaNameSuffix}.");
            Assert.AreEqual(AudioConstant.MinDecibelVolume, trackDb, DecibelTolerance,
                $"characterizes: the later player's dry track {trackName} is muted.");

            yield return ResetLowPassEffect();
            yield return WaitUntilOrTimeout(() => !live.IsUsingTrackEffect && !later.IsUsingTrackEffect,
                "an explicit default-valued SetEffect to take both players off the effect send", DefaultPlaybackWaitSeconds);
            Assert.AreEqual(EffectType.None, sfxPref.EffectType & EffectType.LowPass,
                "An explicit default-valued SetEffect clears the type's LowPass bit.");
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
        }

        [UnityTest]
        public IEnumerator SetEffect_None_IsNotAResetAll_LogsErrorsAndLeavesTheMixerParameterUntouched()
        {
            BroAudio.SetEffect(Effect.LowPass(800f));
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float moved));
            Assert.AreEqual(800f, moved, FrequencyTolerance, "Precondition: the LowPass parameter should have moved off its default.");

            // characterizes: EffectType.None carries no mixer parameter, so ResetAllEffect resolves none for any
            // tracked effect: it logs and starts no fade. A reset-all, if wanted, belongs in the public API.
            // Logs are checked for type and BroAudio's tag only, never wording or count.
            List<string> taggedErrors = new List<string>();
            yield return RunAndCollectBroAudioErrorLogs(() => BroAudio.SetEffect(new Effect(EffectType.None)), 2, taggedErrors);

            Assert.IsNotEmpty(taggedErrors, "SetEffect(EffectType.None) should log an error for the tracked effect it cannot resolve.");
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float after));
            Assert.AreEqual(800f, after, FrequencyTolerance,
                "SetEffect(EffectType.None) must not reset a tracked effect's mixer parameter.");
            // No ResetLowPassEffect() cleanup needed: the base fixture's teardown resets the mixer parameters.
        }
    }
#endif
}