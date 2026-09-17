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
    /// The two authored pitch/randomization inputs no runtime test moves off their defaults, and therefore
    /// the two the suite cannot currently notice the loss of. Companion to <see cref="AuthoredVolumeTests"/>,
    /// which does the same for clip/master volume.
    /// <para>
    /// 1. <b>The entity's authored pitch, and what the per-type pitch does to it.</b> AudioPlayer.Pitch.cs's
    /// <c>GetBasePitch</c> does not compose the two: when the per-type pitch is anything but exactly 1 it
    /// returns <c>entity.GetRandomValue(audioTypePlaybackPref.Pitch, RandomFlag.Pitch)</c> - the entity's own
    /// <see cref="AudioEntity.Pitch"/> is *replaced*, not scaled - and only when the per-type pitch is
    /// <see cref="AudioConstant.DefaultPitch"/> does it fall through to <c>entity.GetPitch()</c>. Every other
    /// runtime test leaves the entity at pitch 1, where "replace" and "multiply" produce the same number.
    /// </para>
    /// <para>
    /// 2. <b>Per-play randomization.</b> <see cref="AudioEntity.GetRandomValue(float, float)"/> is
    /// <c>baseValue + Random.Range(-range * 0.5f, range * 0.5f)</c> - a *half*-range either side of the base.
    /// Its arithmetic is unit-tested in EditMode, but nothing checks that a Play actually draws from it:
    /// pitch through <c>GetBasePitch</c> -> <c>AudioSource.pitch</c>, volume through
    /// <c>GetMasterVolume()</c> -> <c>SetupClipVolume</c> -> the player's linear product.
    /// </para>
    /// <para>
    /// Characterization only: every assertion below pins what the code does today. See
    /// Docs/TEST_FINDINGS.md for the conflicts these tests record.
    /// </para>
    /// </summary>
    public class AuthoredPitchAndRandomizationTests : BroAudioTestFixture
    {
        /// <summary>
        /// How many independent draws each randomization assertion is made over. See
        /// <see cref="Play_WithRandomPitchAndVolumeFlags_JittersWithinHalfRangePerPlay"/> for why 16.
        /// </summary>
        private const int RandomDrawCount = 16;

        /// <summary>
        /// Slack on the *inclusive* half-range bounds only. Random.Range(float, float) can return its max,
        /// and `base + (-range * 0.5f)` is not exactly representable (1.5f - 0.2f lands a few ulps off 1.3f),
        /// so a legitimate draw can sit an ulp outside the arithmetic bound. 1e-4 covers that by orders of
        /// magnitude while staying ~2000x smaller than the 0.2 overshoot a half-range mutation would produce.
        /// </summary>
        private const float BoundaryTolerance = 0.0001f;

        [UnityTest]
        [Category("Finding_55")]
        public IEnumerator Play_WithAuthoredEntityPitch_ReachesAudioSourceAndIsReplacedNotScaledByTypePitch()
        {
            // Characterizes TEST_FINDINGS #55: 1.5 and 0.5 are chosen so the three outcomes are three
            // different numbers - "entity pitch wins" reads 1.5, "type pitch wins" (what the code does) reads
            // 0.5, and "the two multiply" would read 0.75. All three are further apart than LinearTolerance,
            // so no two can be confused.
            const float EntityPitch = 1.5f;
            const float TypePitch = 0.5f;
            const float MultipliedPitch = EntityPitch * TypePitch; // 0.75 - the value a "make it compose" refactor would produce

            // 5s: at pitch 1.5 the clip's playable duration shrinks to ~3.3s and at 0.5 it stretches to 10s,
            // so neither play can reach its DSP-scheduled end before it is read.
            AudioEntity entity = TestAudioLibrary.CreateEntityWithPitch("AuthoredPitchSfx", BroAudioType.SFX, EntityPitch, NewClip(5f));
            Track(entity);
            SoundID id = IdOf(entity);

            // Precondition, stated rather than assumed: SoundManager's per-type prefs live for the whole run,
            // so a leaked SFX pitch from an earlier test would silently send the first play down the *other*
            // branch of GetBasePitch and make the assertion below meaningless.
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref prefBefore));
            Assert.AreEqual(AudioConstant.DefaultPitch, prefBefore.Pitch, LinearTolerance,
                "This test needs the SFX per-type pitch at its default before it starts; an earlier test leaked one.");

            IAudioPlayer authoredPlayer = BroAudio.Play(id);
            yield return WaitForPlaybackStart(authoredPlayer, "the authored-pitch playback to start");

            // Would this pass if GetBasePitch ignored the entity and always used the per-type pitch? No - it
            // would read 1. This is the half of the gap that makes the entity's authored pitch observable at all.
            Assert.AreEqual(EntityPitch, authoredPlayer.AudioSource.pitch, LinearTolerance,
                "A freshly played entity with an authored pitch should put that pitch on its AudioSource, not the default 1.");

            // Stop before touching the per-type pitch: SoundManager.SetPitch(type, ...) also pushes the new
            // value to every *live* player of that type (via IAudioPlayer.SetPitch, which sets TargetPitch and
            // bypasses GetBasePitch entirely). Retiring this player first keeps the second half of the test on
            // the SetInitialPitch/GetBasePitch path it is actually about.
            BroAudio.Stop(id, 0f);
            yield return WaitForRecycle(authoredPlayer, "the authored-pitch player to recycle");

            try
            {
                BroAudio.SetPitch(BroAudioType.SFX, TypePitch, 0f);
                yield return WaitFrames(1);

                IAudioPlayer typePitchPlayer = BroAudio.Play(id);
                yield return WaitForPlaybackStart(typePitchPlayer, "the per-type-pitch playback to start");

                // The pinned half of TEST_FINDINGS #55: the per-type pitch REPLACES the authored entity pitch.
                // Would this pass if GetBasePitch multiplied the two instead? No - it would read 0.75.
                // Would it pass if GetBasePitch ignored the per-type pitch? No - it would read 1.5.
                Assert.AreEqual(TypePitch, typePitchPlayer.AudioSource.pitch, LinearTolerance,
                    "With a non-default per-type pitch, GetBasePitch returns that pitch verbatim - the entity's authored pitch is discarded, not scaled.");
                Assert.That(typePitchPlayer.AudioSource.pitch, Is.Not.EqualTo(MultipliedPitch).Within(LinearTolerance),
                    "Pinned deliberately: the entity pitch and the per-type pitch do NOT compose. If this fails, the behavior was changed to multiply - update the test on purpose, do not loosen it.");

                BroAudio.Stop(id, 0f);
                yield return WaitForRecycle(typePitchPlayer, "the per-type-pitch player to recycle");
            }
            finally
            {
                // Self-sufficient restore. A leaked per-type pitch rescales the duration of every later SFX
                // play in the run, so it must not depend on the base fixture's teardown also doing it.
                BroAudio.SetPitch(BroAudioType.SFX, AudioConstant.DefaultPitch, 0f);
            }
        }

        [UnityTest]
        public IEnumerator Play_WithRandomPitchAndVolumeFlags_JittersWithinHalfRangePerPlay()
        {
            // The fixture is picked so that every way the randomization can break reads as a different failure:
            //
            //   pitch:  base 1.5, range 0.4 -> draws must land in [1.3, 1.7]
            //   volume: base 0.6, range 0.1 -> draws must land in [0.55, 0.65]
            //
            // * jitter dropped entirely (GetRandomValue returns baseValue, or the RandomFlags check inverted
            //   so it never fires): all 16 draws are the identical authored base -> the spread assertion fails.
            //   Both bases are also off the suite-wide default of 1, so the failure message names a real value
            //   rather than the "1" every other entity in the suite already reads.
            // * the half missing (`float half = range;`): pitch draws spread over [1.1, 1.9], of which only
            //   half fall inside [1.3, 1.7]; volume likewise. See the probability note below.
            // * the two ranges swapped in GetRandomValue's switch: pitch would draw from 0.1 (spread <= 0.1,
            //   failing the pitch spread assertion outright, since a 0.1-wide uniform cannot span more than
            //   0.1) and volume from 0.4 (only 25% of draws inside [0.55, 0.65]). Deliberately unequal ranges
            //   are what make that mutation visible at all.
            // * the jitter applied to the wrong base (e.g. AudioConstant.DefaultPitch instead of the entity's):
            //   pitch draws land in [0.8, 1.2], entirely outside [1.3, 1.7].
            const float BasePitch = 1.5f;
            const float PitchRandomRange = 0.4f;
            const float BaseMasterVolume = 0.6f;
            const float VolumeRandomRange = 0.1f;

            AudioEntity entity = TestAudioLibrary.CreateRandomizedEntity("RandomJitterSfx", BroAudioType.SFX,
                RandomFlag.Pitch | RandomFlag.Volume, BasePitch, PitchRandomRange, BaseMasterVolume, VolumeRandomRange, NewClip(5f));
            Track(entity);
            SoundID id = IdOf(entity);

            // Same precondition as above, for both layers this test reads through: a leaked per-type pitch
            // would move GetBasePitch onto its other branch, and a leaked per-type volume would multiply into
            // GetVolume() and shift the whole volume band.
            Assert.IsTrue(SoundManager.Instance.TryGetAudioTypePref(BroAudioType.SFX, out IAudioPlaybackPref pref));
            Assert.AreEqual(AudioConstant.DefaultPitch, pref.Pitch, LinearTolerance,
                "This test needs the SFX per-type pitch at its default; an earlier test leaked one.");
            Assert.AreEqual(AudioConstant.FullVolume, pref.Volume, LinearTolerance,
                "This test needs the SFX per-type volume at its default; an earlier test leaked one.");

            float[] pitches = new float[RandomDrawCount];
            float[] volumes = new float[RandomDrawCount];

            for (int i = 0; i < RandomDrawCount; i++)
            {
                // One draw per Play: SetInitialPitch calls GetBasePitch once and SetupClipVolume calls
                // Entity.GetMasterVolume() once, both before the voice starts. Each iteration is retired
                // before the next begins so the draws are independent and the player/track pools do not grow.
                IAudioPlayer player = BroAudio.Play(id);
                yield return WaitForPlaybackStart(player, $"randomized playback #{i} to start");

                pitches[i] = player.AudioSource.pitch;

                // GetVolume() rather than AudioSource.volume: the play holds a pooled mixer track, so
                // UpdateVolume writes the composed value to the mixer's dB parameter and never touches
                // AudioSource.volume - see AuthoredVolumeTests for the same reasoning. clip.Volume and both
                // other faders are at 1 here, so this reads Entity.GetMasterVolume()'s draw directly.
                volumes[i] = player.GetVolume();

                BroAudio.Stop(id, 0f);
                yield return WaitForRecycle(player, $"randomized player #{i} to recycle");
            }

            AssertDrawsJitterWithinHalfRange(pitches, BasePitch, PitchRandomRange, "AudioSource.pitch");
            AssertDrawsJitterWithinHalfRange(volumes, BaseMasterVolume, VolumeRandomRange, "IAudioPlayer.GetVolume()");
        }

        /// <summary>
        /// Asserts that <paramref name="draws"/> are all inside <c>base +/- range/2</c> and that they are not
        /// all the same value.
        /// <para>
        /// Anti-flake budget, with N = <see cref="RandomDrawCount"/> = 16 uniform draws of width
        /// <c>range</c>:
        /// </para>
        /// <list type="bullet">
        /// <item>The band assertion cannot fail on correct code at all - GetRandomValue cannot produce a value
        /// outside the half-range - so <see cref="BoundaryTolerance"/> only has to absorb float rounding on the
        /// bound itself. Against the "half missing" mutation each draw is inside with probability 1/2, so the
        /// mutation escapes with probability 2^-16 ~ 1.5e-5.</item>
        /// <item>The spread threshold is a quarter of the authored range. On correct code the probability that
        /// all 16 draws land inside any window that narrow is
        /// <c>N*r^(N-1) - (N-1)*r^N</c> with <c>r = 0.25</c>, i.e. ~1.5e-8 - a false failure roughly once in
        /// 67 million runs. Against "jitter dropped" the spread is exactly 0, and against a range narrowed to
        /// a quarter or less the spread cannot reach the threshold at all, so both are caught with certainty
        /// rather than probabilistically.</item>
        /// </list>
        /// A larger N would buy more power against the "half missing" mutation, but 16 plays already costs
        /// ~5 frames each; 1.5e-5 is far below the noise floor of anything else in a PlayMode run.
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

        // Characterizes TEST_FINDINGS #56: SetVolume and SetPitch part company on BroAudioType.All.
        // SoundManager.SetVolume(vol, All, fade) short-circuits into SetMasterVolume and never touches a
        // per-type pref (pinned by
        // VolumePitchMixerTests.SetVolume_Master_WritesDirectlyToMixerAndNeverEntersLinearProduct), while
        // SoundManager.SetPitch has no such branch - it runs SetPlaybackPrefByType over every concrete type.
        // So "master pitch" is really "every type's pitch at once", and it reaches a later play through
        // exactly the GetBasePitch branch the first test above characterizes. Nothing covered that path.
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

                // Ambience is never named by any SetPitch(type, ...) call in this suite, so reading the master
                // pitch off a fresh Ambience player proves the value travelled through the per-type pref rather
                // than through some type the test itself touched.
                SoundID ambienceId = NewSound("MasterPitchAmbience", BroAudioType.Ambience, NewClip(5f));
                IAudioPlayer player = BroAudio.Play(ambienceId);
                yield return WaitForPlaybackStart(player, "the ambience playback to start");

                // Would this pass if GetBasePitch's non-default-type-pitch branch were removed? No - the
                // entity's own pitch is 1, so it would read 1 instead of 0.5.
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