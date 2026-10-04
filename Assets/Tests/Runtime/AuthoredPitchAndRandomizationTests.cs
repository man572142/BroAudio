using System.Collections;
using Ami.BroAudio.Data;
using Ami.BroAudio.Runtime;
using Ami.Extension;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Pins how an entity's authored pitch meets the per-type pitch (TEST_FINDINGS #55, #56), and that each
    /// Play draws its own half-range jitter from <see cref="AudioEntity.GetRandomValue(float, float)"/> for
    /// pitch and volume. Authored values sit off 1, where "replace" and "multiply" would read the same.
    /// </summary>
    public class AuthoredPitchAndRandomizationTests : BroAudioTestFixture
    {
        /// <summary>Draws per randomization assertion; odds in <see cref="AssertDrawsJitterWithinHalfRange"/>.</summary>
        private const int RandomDrawCount = 16;

        /// <summary>
        /// Slack on the inclusive bounds: Random.Range can return its max and <c>base - range/2</c> is not
        /// exactly representable. Still ~2000x below the 0.2 overshoot of a half-range mutation.
        /// </summary>
        private const float BoundaryTolerance = 0.0001f;

        [UnityTest]
        [Category("Finding_55")]
        public IEnumerator Play_WithAuthoredEntityPitch_ReachesAudioSourceAndIsReplacedNotScaledByTypePitch()
        {
            // Pins TEST_FINDINGS #55. Distinct outcomes: entity wins 1.5, type wins 0.5 (today), multiply 0.75.
            const float EntityPitch = 1.5f;
            const float TypePitch = 0.5f;
            const float MultipliedPitch = EntityPitch * TypePitch; // 0.75 - the value a "make it compose" refactor would produce

            // 5s: even at pitch 1.5 (~3.3s) neither play reaches its scheduled end before it is read.
            AudioEntity entity = TestAudioLibrary.CreateEntityWithPitch("AuthoredPitchSfx", BroAudioType.SFX, EntityPitch, NewClip(5f));
            Track(entity);
            SoundID id = IdOf(entity);

            // A leaked SFX pitch would send the first play down GetBasePitch's other branch.
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref prefBefore));
            Assert.AreEqual(AudioConstant.DefaultPitch, prefBefore.Pitch, LinearTolerance,
                "This test needs the SFX per-type pitch at its default before it starts; an earlier test leaked one.");

            IAudioPlayer authoredPlayer = BroAudio.Play(id);
            yield return WaitForPlaybackStart(authoredPlayer, "the authored-pitch playback to start");

            Assert.AreEqual(EntityPitch, authoredPlayer.AudioSource.pitch, LinearTolerance,
                "A freshly played entity with an authored pitch should put that pitch on its AudioSource, not the default 1.");

            // Retire it first: SetPitch(type, ...) also retargets live players, bypassing GetBasePitch.
            BroAudio.Stop(id, 0f);
            yield return WaitForRecycle(authoredPlayer, "the authored-pitch player to recycle");

            try
            {
                BroAudio.SetPitch(BroAudioType.SFX, TypePitch, 0f);
                yield return WaitFrames(1);

                IAudioPlayer typePitchPlayer = BroAudio.Play(id);
                yield return WaitForPlaybackStart(typePitchPlayer, "the per-type-pitch playback to start");

                Assert.AreEqual(TypePitch, typePitchPlayer.AudioSource.pitch, LinearTolerance,
                    "With a non-default per-type pitch, GetBasePitch returns that pitch verbatim - the entity's authored pitch is discarded, not scaled.");
                Assert.That(typePitchPlayer.AudioSource.pitch, Is.Not.EqualTo(MultipliedPitch).Within(LinearTolerance),
                    "Pinned deliberately: the entity pitch and the per-type pitch do NOT compose. If this fails, the behavior was changed to multiply - update the test on purpose, do not loosen it.");

                BroAudio.Stop(id, 0f);
                yield return WaitForRecycle(typePitchPlayer, "the per-type-pitch player to recycle");
            }
            finally
            {
                // Self-sufficient restore: a leaked type pitch rescales every later SFX play.
                BroAudio.SetPitch(BroAudioType.SFX, AudioConstant.DefaultPitch, 0f);
            }
        }

        [UnityTest]
        public IEnumerator Play_WithRandomPitchAndVolumeFlags_JittersWithinHalfRangePerPlay()
        {
            // Bands: pitch [1.3, 1.7], volume [0.55, 0.65]. Each break fails differently: jitter dropped ->
            // zero spread; half missing -> half the draws leave the band; ranges swapped -> pitch spread <= 0.1
            // (why the ranges are unequal); wrong base (DefaultPitch) -> pitch in [0.8, 1.2].
            const float BasePitch = 1.5f;
            const float PitchRandomRange = 0.4f;
            const float BaseMasterVolume = 0.6f;
            const float VolumeRandomRange = 0.1f;

            AudioEntity entity = TestAudioLibrary.CreateRandomizedEntity("RandomJitterSfx", BroAudioType.SFX,
                RandomFlag.Pitch | RandomFlag.Volume, BasePitch, PitchRandomRange, BaseMasterVolume, VolumeRandomRange, NewClip(5f));
            Track(entity);
            SoundID id = IdOf(entity);

            // A leaked per-type pitch or volume would shift the bands.
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(AudioConstant.DefaultPitch, pref.Pitch, LinearTolerance,
                "This test needs the SFX per-type pitch at its default; an earlier test leaked one.");
            Assert.AreEqual(AudioConstant.FullVolume, pref.Volume, LinearTolerance,
                "This test needs the SFX per-type volume at its default; an earlier test leaked one.");

            float[] pitches = new float[RandomDrawCount];
            float[] volumes = new float[RandomDrawCount];

            for (int i = 0; i < RandomDrawCount; i++)
            {
                // One draw per Play; retired before the next so draws are independent and pools don't grow.
                IAudioPlayer player = BroAudio.Play(id);
                yield return WaitForPlaybackStart(player, $"randomized playback #{i} to start");

                pitches[i] = player.AudioSource.pitch;

                // GetVolume(), not AudioSource.volume: on a mixer track the volume goes to the dB parameter.
                // Every other factor is 1, so this is GetMasterVolume()'s draw.
                volumes[i] = player.GetVolume();

                BroAudio.Stop(id, 0f);
                yield return WaitForRecycle(player, $"randomized player #{i} to recycle");
            }

            AssertDrawsJitterWithinHalfRange(pitches, BasePitch, PitchRandomRange, "AudioSource.pitch");
            AssertDrawsJitterWithinHalfRange(volumes, BaseMasterVolume, VolumeRandomRange, "IAudioPlayer.GetVolume()");
        }

        /// <summary>
        /// Asserts all <paramref name="draws"/> lie within <c>base +/- range/2</c> and are not all equal.
        /// <para>
        /// Odds over N = 16 draws: the band check cannot fail on correct code, and a "half missing" mutation
        /// escapes it with p = 2^-16 ~ 1.5e-5. The range/4 spread threshold false-fails correct code with
        /// p = <c>N*r^(N-1) - (N-1)*r^N</c> ~ 1.5e-8 (r = 0.25), and catches dropped or quartered jitter always.
        /// </para>
        /// </summary>
        private static void AssertDrawsJitterWithinHalfRange(float[] draws, float baseValue, float range, string what)
        {
            float half = range * 0.5f;
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < draws.Length; i++)
            {
                Assert.GreaterOrEqual(draws[i], baseValue - half - BoundaryTolerance,
                    $"{what} draw #{i} ({draws[i]}) fell below base - range/2 ({baseValue} - {half}). " +
                    "GetRandomValue must jitter by at most half the authored range either side of the authored base.");
                Assert.LessOrEqual(draws[i], baseValue + half + BoundaryTolerance,
                    $"{what} draw #{i} ({draws[i]}) rose above base + range/2 ({baseValue} + {half}). " +
                    "GetRandomValue must jitter by at most half the authored range either side of the authored base.");

                min = Mathf.Min(min, draws[i]);
                max = Mathf.Max(max, draws[i]);
            }

            Assert.Greater(max - min, range * 0.25f,
                $"{what} barely varied across {draws.Length} plays (min {min}, max {max}). Every play must draw " +
                "its own random value; a constant reading means the randomization never ran or its range collapsed.");
        }

        // Pins TEST_FINDINGS #56: master SetPitch writes every concrete type's pref.
        [UnityTest]
        [Category("Finding_56")]
        public IEnumerator SetPitch_Master_StoresIntoEveryConcreteTypePrefAndReachesFuturePlayers()
        {
            const float MasterPitch = 0.5f;

            try
            {
                BroAudio.SetPitch(MasterPitch); // no type argument => BroAudioType.All, FadeTime_Immediate
                yield return WaitFrames(1);

                foreach (BroAudioType audioType in ConcreteAudioTypes)
                {
                    Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(audioType, out IAudioPlaybackPref pref),
                        $"Every concrete audio type should have a playback pref; {audioType} had none.");
                    Assert.AreEqual(MasterPitch, pref.Pitch, LinearTolerance,
                        $"Master pitch is applied per concrete type, so {audioType}'s stored pref should carry it.");
                }

                // No SetPitch(type, ...) in the suite targets Ambience, so the pitch can only come via its pref.
                SoundID ambienceId = NewSound("MasterPitchAmbience", BroAudioType.Ambience, NewClip(5f));
                IAudioPlayer player = BroAudio.Play(ambienceId);
                yield return WaitForPlaybackStart(player, "the ambience playback to start");

                Assert.AreEqual(MasterPitch, player.AudioSource.pitch, LinearTolerance,
                    "A player started after a master SetPitch should pick the value up from its type's pref via GetBasePitch.");
            }
            finally
            {
                foreach (BroAudioType audioType in ConcreteAudioTypes)
                {
                    BroAudio.SetPitch(audioType, AudioConstant.DefaultPitch, 0f);
                }
            }
        }
    }
}