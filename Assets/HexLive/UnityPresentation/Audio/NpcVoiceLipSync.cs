#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §67.7: lip sync for the FMOD voice lines. uLipSync normally eats
    /// the Unity audio thread (OnAudioFilterRead) — but Unity audio is OFF
    /// here (FMOD owns playback), so this component is the replacement feeder:
    /// it decodes the voice WAV once, then every frame pushes the PCM window
    /// the FMOD channel just played (channel.getPosition is the clock) into
    /// uLipSync.OnDataReceived. The stock analysis (MFCC → phoneme) and
    /// uLipSyncBlendShape (phoneme → Daz viseme blendshapes) run unchanged.
    /// Mirrors molly_copy's LipSyncController wiring — same profile, same
    /// Genesis3Female__eCTRLv* viseme map, found suffix-first so a future
    /// actor with a different Daz generation prefix still binds.
    /// </summary>
    public sealed class NpcVoiceLipSync : MonoBehaviour
    {
        // Фонема анализатора → суффикс Daz-виземы (полное имя ищем на меше).
        private static readonly (string phoneme, string suffix)[] PhonemeMap =
        {
            ("A", "eCTRLvAA"), ("I", "eCTRLvIY"), ("U", "eCTRLvUW"),
            ("E", "eCTRLvEE"), ("O", "eCTRLvOW"),
            ("P", "eCTRLvM"), ("F", "eCTRLvF"), ("S", "eCTRLvS"),
            ("SH", "eCTRLvSH"), ("T", "eCTRLvT"), ("R", "eCTRLvER"),
            ("L", "eCTRLvL"), ("K", "eCTRLvK"), ("TH", "eCTRLvTH"),
        };

        // PCM-кэш реплик: файлы маленькие (≤4.2 с моно), персонажей четверо —
        // держим всё, что уже звучало (≈15 МБ на всю колонию максимум).
        private static readonly Dictionary<string, float[]> PcmCache = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => PcmCache.Clear();

        private uLipSync.uLipSync? _analyzer;
        private FmodSfx.Loop _handle;
        private float[]? _samples;
        private int _fedSamples;
        private const int SampleRate = 44100; // все voice_-WAV авторим в 44.1k моно

        /// <summary>Собрать анализатор и маппинг на виземы головы.</summary>
        public void Construct(SkinnedMeshRenderer face)
        {
            var profile = Resources.Load<uLipSync.Profile>("HexLive/Audio/VoiceLipSyncProfile");
            if (profile == null || face.sharedMesh == null)
            {
                Debug.LogWarning("[NpcVoiceLipSync] no profile or face mesh — lip sync off");
                enabled = false;
                return;
            }

            _analyzer = gameObject.AddComponent<uLipSync.uLipSync>();
            _analyzer.profile = profile;
            _analyzer.overrideSampleRate = SampleRate; // Unity audio off → свой такт
            _analyzer.outputSoundGain = 1f;

            var blend = gameObject.AddComponent<uLipSync.uLipSyncBlendShape>();
            blend.skinnedMeshRenderer = face;
            blend.usePhonemeBlend = true;
            blend.smoothness = 0.06f;
            blend.maxBlendShapeValue = 100f;
            blend.minVolume = -3f;
            blend.maxVolume = -1f;

            // Имена блендшейпов ищем по суффиксу — префикс у Daz-поколений
            // разный (Genesis3Female__… сегодня), а виземы одни и те же.
            var mesh = face.sharedMesh;
            var byName = new Dictionary<string, string>();
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var shapeName = mesh.GetBlendShapeName(i);
                foreach (var (_, suffix) in PhonemeMap)
                {
                    if (shapeName.EndsWith(suffix, System.StringComparison.Ordinal))
                    {
                        byName[suffix] = shapeName;
                    }
                }
            }

            var mapped = 0;
            foreach (var (phoneme, suffix) in PhonemeMap)
            {
                if (byName.TryGetValue(suffix, out var shapeName))
                {
                    blend.AddBlendShape(phoneme, shapeName);
                    mapped++;
                }
            }

            _analyzer.onLipSyncUpdate.AddListener(blend.OnLipSyncUpdate);
            if (mapped == 0)
            {
                Debug.LogWarning($"[NpcVoiceLipSync] {face.name}: no viseme blendshapes matched");
                enabled = false;
            }
        }

        /// <summary>Реплика пошла — начинаем кормить анализатор её PCM.</summary>
        public void Speak(ref FmodSfx.Loop handle)
        {
            if (_analyzer == null || string.IsNullOrEmpty(handle.File))
            {
                return;
            }

            _samples = LoadPcm(handle.File!);
            _handle = handle;
            _fedSamples = 0;
        }

        private void Update()
        {
            if (_samples == null || _analyzer == null)
            {
                return;
            }

            var ms = FmodSfx.GetPlaybackMs(ref _handle);
            if (ms < 0)
            {
                // Реплика закончилась — тишина закрывает рот (smoothness).
                _samples = null;
                return;
            }

            var playedTo = Mathf.Min((int)((long)ms * SampleRate / 1000), _samples.Length);
            var count = playedTo - _fedSamples;
            if (count <= 0)
            {
                return;
            }

            // После фриза не скармливаем гору разом — анализатору всё равно
            // важно только последнее окно.
            const int maxChunk = SampleRate / 5;
            if (count > maxChunk)
            {
                _fedSamples = playedTo - maxChunk;
                count = maxChunk;
            }

            var chunk = new float[count];
            System.Array.Copy(_samples, _fedSamples, chunk, 0, count);
            _analyzer.OnDataReceived(chunk, 1);
            _fedSamples = playedTo;
        }

        // Наши voice_-файлы — WAV PCM16 mono 44.1k (мы же их и пишем в
        // конвейере §67.6), поэтому парсер минимальный: ищем data-чанк.
        private static float[]? LoadPcm(string path)
        {
            if (PcmCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var offset = FindDataChunk(bytes);
                if (offset < 0)
                {
                    return null;
                }

                var size = System.BitConverter.ToInt32(bytes, offset + 4);
                var start = offset + 8;
                size = Mathf.Min(size, bytes.Length - start);
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = System.BitConverter.ToInt16(bytes, start + i * 2) / 32768f;
                }

                PcmCache[path] = samples;
                return samples;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[NpcVoiceLipSync] can't read {path}: {e.Message}");
                return null;
            }
        }

        private static int FindDataChunk(byte[] bytes)
        {
            // RIFF: чанки со смещения 12; "data" может идти не первым.
            var i = 12;
            while (i + 8 <= bytes.Length)
            {
                if (bytes[i] == 'd' && bytes[i + 1] == 'a' &&
                    bytes[i + 2] == 't' && bytes[i + 3] == 'a')
                {
                    return i;
                }

                var chunkSize = System.BitConverter.ToInt32(bytes, i + 4);
                i += 8 + chunkSize + (chunkSize & 1);
            }

            return -1;
        }
    }
}
