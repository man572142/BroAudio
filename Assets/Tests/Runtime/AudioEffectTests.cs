using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ami.BroAudio.Data;
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
    /// audio-thread OnAudioFilterRead callback and GetOutputData tap), and the mixer-routed BroAudio.SetEffect automation
    /// (exposed parameter writes, the Dominator-only EffectType.Volume guard, ForSeconds auto-reset,
    /// and the FourPole secondary parameter).
    /// </summary>
    public class AudioEffectTests : BroAudioTestFixture
    {
        private const float FrequencyTolerance = 1f;

        // Anchored on the constant every BroAudio log is tagged with (Utility.LogTitle), not on any one
        // message's wording - the log's TYPE plus this tag is the contract; the sentence is not
        // (Docs/GOAL.md anti-goal: "Asserting on log text"). Regex.Escape because the tag's rich-text markup
        // ("[BroAudio]" among it) contains regex metacharacters.
        private static readonly Regex BroAudioLogPrefix = new Regex(Regex.Escape(Utility.LogTitle));

        private volatile int _capturedChannels = -1;
        private volatile int _capturedBufferLength = -1;

        /// <summary>
        /// Resets the mixer-routed LowPass in the middle of a test, for the one test that has to observe
        /// the reset itself. Cleaning up afterwards is no longer this fixture's job, and it declares no
        /// [UnityTearDown] of its own: BroAudioTestFixture's TearDown resets Effect_LowPass and
        /// Effect_HighPass (plus their FourPole second poles) after every PlayMode test, unconditionally,
        /// and waits for the parameters to actually read their defaults instead of a fixed frame count.
        /// <para>
        /// SetEffect's Add/Override modes for a non-default value permanently flip a bit in the SFX-type's
        /// stored EffectType pref (read by every future Play() via AudioPlayer.Playback.cs's SetTrackEffect)
        /// - state the base fixture's Setting/volume snapshot-restore does not know about, because it lives
        /// on a separate in-memory AudioTypePlaybackPreference, not on the RuntimeSetting asset. Handing
        /// SetEffect a *default-valued* Effect is what clears it, because SoundManager then picks
        /// SetEffectMode.Remove. SetEffect(new Effect(EffectType.None)) would clear every tracked effect in
        /// one call, but it logs on construction and again for any unresolvable tracked entry, so it stays
        /// out of every shared cleanup path.
        /// </para>
        /// <para>
        /// Correct under either slope: whether the second pole is written too is decided per call from
        /// Setting.AudioFilterSlope, and that field lives on the RuntimeSetting object the base fixture's
        /// JSON snapshot restores after the effect reset, never before it.
        /// </para>
        /// </summary>
        private static IEnumerator ResetLowPassEffect()
        {
            BroAudio.SetEffect(Effect.ResetLowPass());
            yield return WaitFrames(2);
        }

        /// <summary>
        /// Runs <paramref name="action"/> and waits <paramref name="waitFrames"/> frames while collecting
        /// every Error-type log it produces into <paramref name="taggedErrors"/>, instead of quoting a
        /// specific sentence with LogAssert.Expect. Two callers need this rather than a single Expect: the
        /// EffectAutomationHelper guards this exercises (GetEffectParameterName / ResetAllEffect's unresolved
        /// entries) can log more than once per call - an implementation detail (the exact count, through an
        /// internal tween coroutine) this suite does not pin down.
        /// <para>
        /// LogAssert.ignoreFailingMessages is scoped to just this call so it never leaks into the rest of the
        /// test, and it never silently hides an unrelated bug: the assertion that every captured Error carries
        /// <see cref="Utility.LogTitle"/> runs after the collection loop, on the main thread, rather than from
        /// inside the logMessageReceived callback - Unity's log dispatch does not expect a handler to throw.
        /// A message that does not carry the tag is a genuinely unrelated failure and fails the test.
        /// </para>
        /// </summary>
        private static IEnumerator RunAndCollectBroAudioErrorLogs(Action action, int waitFrames, List<string> taggedErrors)
        {
            void OnLog(string message, string stackTrace, LogType type)
            {
                if (type == LogType.Error)
                {
                    taggedErrors.Add(message);
                }
            }

            Application.logMessageReceived += OnLog;
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            // try/finally, not a plain pair of statements: ignoreFailingMessages is static, so an action that
            // throws would otherwise leave every later test in the run unable to fail on an unexpected log.
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

            LogAssert.Expect(LogType.Warning, BroAudioLogPrefix);
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

            LogAssert.Expect(LogType.Warning, BroAudioLogPrefix);
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

            // The player pool (ObjectPool<T>) is a plain List<T> where both Extract() and Recycle() operate
            // on the last index - i.e. LIFO. This test is the only thing borrowing/returning a player, so the
            // very next Play() must hand this exact instance back.
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

            // Go through the recycled concrete AudioPlayer directly, not the AudioPlayerInstanceWrapper that
            // BroAudio.Play() returned - the wrapper's own IsAvailable() would short-circuit into a different
            // "this audio player has been recycled" warning instead of AudioPlayer's own !IsActive guard.
            IAudioPlayer inactivePlayer = concrete;

            LogAssert.Expect(LogType.Error, BroAudioLogPrefix);
            inactivePlayer.AddLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "AddLowPassEffect on an inactive player must not attach anything.");

            LogAssert.Expect(LogType.Error, BroAudioLogPrefix);
            inactivePlayer.RemoveLowPassEffect();
            yield return WaitFrames(1);
            Assert.IsFalse(concrete.GetComponent<AudioLowPassFilter>(), "RemoveLowPassEffect on an inactive player must not attach or leave anything behind either.");
        }

        // Characterizes TEST_FINDINGS #45: at every loop seam, AudioPlayerInstanceWrapper.UpdateInstance runs
        // TransferAddedEffectComponents once for each decorator and then once more for itself.
        // AudioPlayerDecorator *is* an AudioPlayerInstanceWrapper, so the caller's wrapper's
        // `decorator.UpdateInstance(newInstance)` re-enters the same override while the decorator's own Instance
        // still points at the outgoing player. With N decorators the outgoing player's added-effect list is
        // copied N+1 times.
        // <para>
        // Unity allows one AudioLowPassFilter per GameObject, so only the first AddComponent succeeds: the voice
        // keeps a single filter, and every other attempt returns null and logs Unity's own untagged refusal.
        // SetAddedEffectComponents appends an entry for every attempt anyway (TransferValueTo ignores the null
        // target), so the incoming list holds N+1 entries per outgoing entry, and the next seam iterates all of
        // them. With 2 decorators and one added filter the list is 3 long after the first seam and 9 after the
        // second, and the seams log 2 and then 8 refusals: the log output grows threefold per iteration for as
        // long as the loop runs.
        // </para>
        // <para>
        // The list is private and nothing public exposes it, so it is read by reflection; the filter count and
        // the number of untagged logs are the external half. The refusals are counted, not matched by text.
        // Unity logs them as LogType.Log, which cannot fail a test, so no log handling has to be relaxed.
        // </para>
        [UnityTest]
        [Category("Finding_45")]
        public IEnumerator Loop_WithAnAddedEffectAndTwoDecorators_MultipliesTheEffectListAtEachSeamWhileUnityKeepsOneFilter()
        {
            yield return RequireRealtimeAudioClock();

            const float ClipSeconds = 1f;
            const float CutoffFrequency = 3000f;
            AudioEntity entity = NewEntity("LoopingAddedEffectSfx", BroAudioType.SFX, NewClip(ClipSeconds));
            TestAudioLibrary.SetPrivateField(entity, nameof(AudioEntity.Loop), true);
            IAudioPlayer player = BroAudio.Play(IdOf(entity));
            yield return WaitForPlaybackStart(player, "the looping sound to start playing");

            // Well inside the first iteration: the first seam is a warm-up time plus one clip away, and the
            // transfer reads the outgoing player's list at the seam, not when the next player is requested.
            player.AsBGM();
            player.AsDominator();
            player.AddLowPassEffect(proxy => proxy.cutoffFrequency = CutoffFrequency);

            AudioPlayer firstInstance = InstanceOf(player);
            Assert.IsNotNull(firstInstance, "Precondition: the handle should resolve to a live player.");
            List<AudioPlayerDecorator> decorators = TestAudioLibrary.GetPrivateField<List<AudioPlayerDecorator>>(
                firstInstance, TestAudioLibrary.Reflected.AudioPlayer.Decorators);
            int decoratorCount = decorators == null ? 0 : decorators.Count;
            Assert.AreEqual(2, decoratorCount, $"Precondition: AsBGM() and AsDominator() should attach one decorator each; observed {decoratorCount}.");
            Assert.AreEqual(1, AddedEffectCount(firstInstance), "Precondition: AddLowPassEffect should record exactly one added effect.");
            Assert.AreEqual(1, firstInstance.GetComponents<AudioLowPassFilter>().Length, "Precondition: AddLowPassEffect should attach exactly one AudioLowPassFilter.");

            int copiesPerSeam = decoratorCount + 1;
            int entriesAfterFirstSeam = copiesPerSeam;
            int entriesAfterSecondSeam = entriesAfterFirstSeam * copiesPerSeam;

            List<string> untaggedLogs = new List<string>();
            void OnLog(string message, string stackTrace, LogType type)
            {
                if (!message.Contains(Utility.LogTitle))
                {
                    untaggedLogs.Add(type + ": " + message);
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
                // UpdateInstance runs the transfers and re-points the handle in the same call, so the log count
                // read on the frame the handle moves covers exactly that seam.
                yield return WaitUntilOrTimeout(() =>
                {
                    AudioPlayer current = InstanceOf(player);
                    return current && current != firstInstance;
                }, "the loop to hand over to a second player, with the handle following it", HandoverWaitSeconds);
                secondInstance = InstanceOf(player);
                refusalsAtFirstSeam = untaggedLogs.Count;
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
                refusalsAtSecondSeam = untaggedLogs.Count - refusalsAtFirstSeam;
                thirdEntries = AddedEffectCount(thirdInstance);
                thirdFilters = thirdInstance.GetComponents<AudioLowPassFilter>().Length;

                // Stopped before the next seam, which would log 26 more refusals.
                player.Stop(0f);
                yield return null;
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }

            string observed = $"Observed: second player {secondEntries} list entries / {secondFilters} filter(s) " +
                              $"[{secondCutoffs}]Hz; third player {thirdEntries} entries / {thirdFilters} filter(s); " +
                              $"untagged logs {refusalsAtFirstSeam} at the first seam, {refusalsAtSecondSeam} at the second " +
                              $"[{string.Join(" | ", untaggedLogs)}].";

            Assert.AreEqual(1, secondFilters,
                $"Unity keeps one AudioLowPassFilter per GameObject, so the voice carries a single filter. {observed}");
            Assert.AreEqual(CutoffFrequency, secondCutoff, 1f,
                $"The one filter that did attach must carry the added effect's settings over the seam. {observed}");
            Assert.AreEqual(entriesAfterFirstSeam, secondEntries,
                $"characterizes: the transfer runs once per decorator plus once ({copiesPerSeam} times), and every attempt " +
                $"appends an entry whether or not its component attached. {observed}");
            Assert.AreEqual(copiesPerSeam - 1, refusalsAtFirstSeam,
                $"characterizes: each attempt after the first is refused by Unity, with its own untagged log. {observed}");

            Assert.AreEqual(1, thirdFilters,
                $"Still one filter on the voice after the second seam. {observed}");
            Assert.AreEqual(entriesAfterSecondSeam, thirdEntries,
                $"characterizes: every entry is copied {copiesPerSeam} times again, so the list multiplies at each seam. {observed}");
            Assert.AreEqual(entriesAfterSecondSeam - 1, refusalsAtSecondSeam,
                $"characterizes: and so do Unity's refusals - all but one of the second seam's attempts are rejected. {observed}");
        }

        /// <summary>
        /// Length of the player's private added-effect list, the one the next seam's transfer iterates.
        /// Read as a non-generic IList because its element type is a private struct.
        /// </summary>
        private static int AddedEffectCount(AudioPlayer player)
        {
            IList list = TestAudioLibrary.GetPrivateField<IList>(player, TestAudioLibrary.Reflected.AudioPlayer.AddedEffects);
            return list == null ? 0 : list.Count;
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
                // OnAudioFilterRead fires on the audio thread - never call NUnit.Assert here. Stash into
                // volatile fields and assert from the test body once control is back on the main thread.
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

            // The fixture's clip is a 0.25-amplitude sine, so any live window holds samples well above 0.01;
            // a no-op or misrouted call leaves the buffer all zeros.
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

            // SetEffect does two things: it updates the per-type pref that's read when a NEW player
            // starts (AudioPlayer.Playback.cs: SetTrackEffect(audioTypePref.EffectType, Add)), and
            // SoundManager.SetPlayerEffect re-routes every player of the target type that is already
            // playing. This test pins the future-player half; the live re-route and the type scoping
            // are covered by SetEffect_ScopedToMusic_ReroutesLivePlayerOfThatTypeOnly below.
            SoundID id = NewSound("EffectSendFx", BroAudioType.SFX, NewClip(2f));
            IAudioPlayer player = BroAudio.Play(id);
            yield return WaitForPlaybackStart(player);

            AudioPlayer concrete = InstanceOf(player);
            Assert.IsTrue(concrete.IsUsingTrackEffect, "A player started after SetEffect(LowPass) should route through the effect send channel.");
            Assert.AreNotEqual(EffectType.None, concrete.CurrentActiveTrackEffects & EffectType.LowPass, "LowPass should be part of the player's active track effects.");
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

            // characterizes: SoundManager.SetPlayerEffect walks GetCurrentAudioPlayers() and calls
            // player.SetTrackEffect(effectType, mode) on every active non-Dominator player whose audio
            // type the target type contains - so a player that was ALREADY playing gets re-routed too,
            // and a player of any other type is left alone.
            BroAudio.SetEffect(Effect.LowPass(800f), BroAudioType.Music);
            yield return WaitUntilOrTimeout(() => music.IsUsingTrackEffect,
                "the already-playing Music player to be re-routed through the effect send", DefaultPlaybackWaitSeconds);

            Assert.AreNotEqual(EffectType.None, music.CurrentActiveTrackEffects & EffectType.LowPass,
                "LowPass should be part of the live Music player's active track effects.");
            Assert.AreEqual(EffectType.None, sfx.CurrentActiveTrackEffects,
                "An effect scoped to Music must not re-route a live SFX player.");

            // The facade has no Reset* verb of its own (BroAudio.cs exposes only the two SetEffect
            // overloads) - resetting means handing SetEffect a default-valued Effect. Effect.ResetLowPass()
            // is default, so SoundManager picks SetEffectMode.Remove, and that mode defers SetPlayerEffect
            // to the automation helper's onReset callback rather than running it inline; hence the poll.
            yield return ResetLowPassEffect();
            yield return WaitUntilOrTimeout(() => !music.IsUsingTrackEffect,
                "the reset to remove the Music player's track effect", DefaultPlaybackWaitSeconds);

            Assert.AreEqual(EffectType.None, music.CurrentActiveTrackEffects, "The reset should leave the Music player with no active track effect.");
            Assert.AreEqual(EffectType.None, sfx.CurrentActiveTrackEffects, "The reset should leave the untouched SFX player clear as well.");
        }

        [UnityTest]
        public IEnumerator SetEffect_VolumeOnNonDominator_LogsErrorAndLeavesMixerUntouched()
        {
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassBefore));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassBefore));

            // characterizes: EffectType.Volume is only meaningful on a Dominator. A plain SetEffect(Volume)
            // call still runs the whole automation pipeline and logs from inside it (GetEffectParameterName),
            // rather than rejecting the call up front. The state below (mixer untouched) is the real
            // contract; the log is only checked for TYPE and BroAudio's own tag, never its wording.
            List<string> taggedErrors = new List<string>();
            yield return RunAndCollectBroAudioErrorLogs(() => BroAudio.SetEffect(new Effect(EffectType.Volume)), 2, taggedErrors);

            Assert.IsNotEmpty(taggedErrors, "SetEffect(Volume) on a non-Dominator effect should log at least one error from the Dominator-only guard.");

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float lowPassAfter));
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.HighPassParaName, out float highPassAfter));
            Assert.AreEqual(lowPassBefore, lowPassAfter, FrequencyTolerance, "The unrelated LowPass parameter must be untouched.");
            Assert.AreEqual(highPassBefore, highPassAfter, FrequencyTolerance, "The unrelated HighPass parameter must be untouched.");
        }

        // regression (FIXED_ISSUES #17): Effect.LowPass's fadeTime defaults to 0, so Tweak yields nothing and
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

            yield return waitable.ForSeconds(0.2f);
            yield return WaitFrames(3); // let the internal WaitUntil(IsFinished)-driven reset coroutine catch up

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float resetFreq));
            Assert.AreEqual(AudioConstant.MaxFrequency, resetFreq, FrequencyTolerance, "ForSeconds should auto-reset the parameter back to its default once the duration elapses.");
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
        public IEnumerator SetEffect_None_ResetsEveryTrackedEffectParameterToItsDefault()
        {
            BroAudio.SetEffect(Effect.LowPass(800f));
            yield return WaitFrames(1);
            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float moved));
            Assert.AreEqual(800f, moved, FrequencyTolerance, "Precondition: the LowPass parameter should have moved off its default.");

            // Two unrelated warts make SetEffect(None) noisy, neither is what this test is about:
            // new Effect(EffectType.None) logs from Effect's Value setter, and an earlier test in the same
            // Editor session can leave a tweaker registered for an effect whose parameter never resolves
            // (EffectType.Volume on a non-Dominator), which the reset loop then logs once per entry. Both are
            // asserted BroAudio-tagged rather than blanket-swallowed, so a genuinely unrelated error would
            // still fail this test.
            List<string> taggedErrors = new List<string>();
            yield return RunAndCollectBroAudioErrorLogs(() => BroAudio.SetEffect(new Effect(EffectType.None)), 2, taggedErrors);

            Assert.IsTrue(SoundManager.Instance.AudioMixer.GetFloat(BroName.LowPassParaName, out float reset));
            Assert.AreEqual(AudioConstant.MaxFrequency, reset, FrequencyTolerance,
                "SetEffect(EffectType.None) should reset every tracked effect's mixer parameter back to its default.");
            // No ResetLowPassEffect() cleanup needed: the None path also overrides the per-type pref to None.
        }
    }
#endif
}