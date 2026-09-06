using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
#if PACKAGE_ADDRESSABLES
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace Ami.BroAudio.Tests
{
    /// <summary>
    /// Regenerates the addressable fixtures that <see cref="TestAudioLibrary.AddressableClipGuids"/> points at.
    /// <para>
    /// Every other clip in the suite is built at runtime by <see cref="TestAudioLibrary.CreateClip"/>, but an
    /// addressable one cannot be: an AssetReference resolves its GUID through the AssetDatabase, so the asset
    /// has to exist on disk. These tones are therefore generated here and committed. They live under
    /// <c>Assets/Tests/Fixtures</c> rather than in the demo content because the shipped folders
    /// (<c>Samples</c>, <c>Resources</c>) are committed with a <c>~</c> suffix and Unity never imports them
    /// on CI, which is what left the whole addressable suite failing there.
    /// </para>
    /// <para>
    /// Re-running this is only needed if the .wav files are lost or their group entries are wiped. The GUIDs
    /// come from the committed .meta files, so they survive a regeneration.
    /// </para>
    /// </summary>
    public static class AddressableFixtureGenerator
    {
        private const string FixtureFolder = "Assets/Tests/Fixtures";
        private const float DurationSeconds = 0.5f;
        private const float Amplitude = 0.25f;

        private static readonly (string FileName, string Address, float Frequency)[] Fixtures =
        {
            ("AddressableToneA", "BroAudioTest/ToneA", 440f),
            ("AddressableToneB", "BroAudioTest/ToneB", 660f),
        };

        [MenuItem("Tools/BroAudio/Tests/Regenerate Addressable Fixtures")]
        public static void Regenerate()
        {
            if (!AssetDatabase.IsValidFolder(FixtureFolder))
            {
                AssetDatabase.CreateFolder("Assets/Tests", "Fixtures");
            }

            foreach ((string fileName, string _, float frequency) in Fixtures)
            {
                string path = $"{FixtureFolder}/{fileName}.wav";
                File.WriteAllBytes(path, EncodeSineWav(frequency));
                Debug.Log(Utility.LogTitle + $"Wrote addressable test fixture <b>{path}</b>.");
            }
            AssetDatabase.Refresh();

            RegisterWithAddressables();
        }

        /// <summary>
        /// A mono 16-bit PCM sine at <see cref="TestAudioLibrary.SampleRate"/> — the same tone
        /// <see cref="TestAudioLibrary.CreateClip"/> builds in memory, just encoded as a file.
        /// </summary>
        private static byte[] EncodeSineWav(float frequency)
        {
            int sampleRate = TestAudioLibrary.SampleRate;
            int sampleCount = Mathf.RoundToInt(DurationSeconds * sampleRate);
            int dataBytes = sampleCount * sizeof(short);

            using (var stream = new MemoryStream(44 + dataBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);                          // PCM header size
                writer.Write((short)1);                    // PCM, uncompressed
                writer.Write((short)1);                    // mono
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));  // byte rate
                writer.Write((short)sizeof(short));        // block align
                writer.Write((short)16);                   // bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);

                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = Mathf.Sin(2f * Mathf.PI * frequency * i / sampleRate) * Amplitude;
                    writer.Write((short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue));
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void RegisterWithAddressables()
        {
#if PACKAGE_ADDRESSABLES
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (!settings)
            {
                Debug.LogError(Utility.LogTitle + "AddressableAssetSettings is not found.");
                return;
            }

            foreach ((string fileName, string address, float _) in Fixtures)
            {
                string guid = AssetDatabase.AssetPathToGUID($"{FixtureFolder}/{fileName}.wav");
                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, settings.DefaultGroup);
                entry.address = address;
                Debug.Log(Utility.LogTitle + $"Marked <b>{address}</b> addressable (GUID {guid}).");
            }
            AssetDatabase.SaveAssets();
#else
            Debug.LogWarning(Utility.LogTitle + "Addressables is not installed - the fixtures were written but " +
                             "not marked addressable. Install com.unity.addressables and run this again.");
#endif
        }
    }
}
