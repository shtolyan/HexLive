#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §67.7: губы по ЗАПЕЧЁННЫМ таймлайнам визем. Рядом с каждым
    /// voice_-WAV лежит .vis-сайдкар (magic HXLS, печёт
    /// _ArtSource/Voice/bake_lipsync.py — офлайн-порт бывшего
    /// uLipSync-анализа): 60 кадров/с, на кадр — ratio 14 Daz-визем + volume.
    /// Рантайм лишь сэмплирует таймлайн по позиции FMOD-канала и сглаживает
    /// SmoothDamp'ом — ни PCM, ни MFCC, ни Unity-аудио. Конец таймлайна
    /// гарантированно нулевой, поэтому «ходит с открытым ртом после реплики»
    /// невозможен по построению.
    /// </summary>
    public sealed class NpcVoiceLipSync : MonoBehaviour
    {
        // Суффиксы Daz-визем в порядке индексов .vis (== порядок фонем в
        // _ArtSource/Voice/lipsync_profile.json: A I U E O P F S SH T R L K TH).
        // Полное имя блендшейпа ищем по суффиксу — префикс у Daz-поколений
        // разный (Genesis3Female__… сегодня), а виземы одни и те же.
        private static readonly string[] VisemeSuffixes =
        {
            "eCTRLvAA", "eCTRLvIY", "eCTRLvUW", "eCTRLvEE", "eCTRLvOW",
            "eCTRLvM", "eCTRLvF", "eCTRLvS", "eCTRLvSH", "eCTRLvT",
            "eCTRLvER", "eCTRLvL", "eCTRLvK", "eCTRLvTH",
        };

        private const int VisemeCount = 14;
        private const int HeaderBytes = 14;
        private const ushort FormatVersion = 1;
        private const float Smoothness = 0.06f; // как у прежнего uLipSyncBlendShape

        private sealed class Timeline
        {
            public byte[] Frames = System.Array.Empty<byte>(); // fc × (14+1)
            public int FrameCount;
            public int Fps;
            public int SourceSamples;
        }

        // Кэш сайдкаров: весь банк (1020 реплик) — ~2.6 МБ, ограничен размером
        // корпуса, так что потолок не нужен (в отличие от покойного PCM-кэша,
        // который держал распакованные WAV и дорастал до сотен МБ).
        private static readonly Dictionary<string, Timeline?> Cache = new();
        private static readonly HashSet<string> Warned = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Cache.Clear();
            Warned.Clear();
        }

        private SkinnedMeshRenderer? _face;
        private readonly int[] _shapeIndex = new int[VisemeCount];
        private readonly float[] _weights = new float[VisemeCount];
        private readonly float[] _weightVels = new float[VisemeCount];
        private readonly float[] _targets = new float[VisemeCount];
        private float _volume;
        private float _volumeVel;
        private float _volumeTarget;
        private Timeline? _timeline;
        private FmodSfx.Loop _handle;
        private bool _idle = true;      // всё в нуле — Update/LateUpdate спят
        private bool _applyFinal;       // последний нулевой прогон в LateUpdate

        /// <summary>Найти виземы на меше головы и включиться.</summary>
        public void Construct(SkinnedMeshRenderer face)
        {
            if (face.sharedMesh == null)
            {
                Debug.LogWarning("[NpcVoiceLipSync] no face mesh — lip sync off");
                enabled = false;
                return;
            }

            _face = face;
            var mesh = face.sharedMesh;
            var mapped = 0;
            for (var v = 0; v < VisemeCount; v++)
            {
                _shapeIndex[v] = -1;
            }

            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var shapeName = mesh.GetBlendShapeName(i);
                for (var v = 0; v < VisemeCount; v++)
                {
                    if (shapeName.EndsWith(VisemeSuffixes[v], System.StringComparison.Ordinal))
                    {
                        if (_shapeIndex[v] < 0)
                        {
                            mapped++;
                        }

                        _shapeIndex[v] = i;
                    }
                }
            }

            if (mapped == 0)
            {
                // Примитивные капсулы-заглушки — молча без рта нельзя, но и
                // спамить не о чем: у настоящих голов виземы есть всегда.
                Debug.LogWarning($"[NpcVoiceLipSync] {face.name}: no viseme blendshapes matched");
                enabled = false;
            }
        }

        /// <summary>Реплика пошла — сэмплируем её таймлайн по каналу.</summary>
        public void Speak(ref FmodSfx.Loop handle)
        {
            if (!enabled || string.IsNullOrEmpty(handle.File))
            {
                return;
            }

            _timeline = LoadTimeline(handle.File!);
            _handle = handle;
            _idle = false;

            // Перегенерированный WAV без пере-бейка = рассинхрон губ; сайдкар
            // несёт длину исходника, канал знает длину реального файла.
            if (_timeline != null)
            {
                var wavMs = FmodSfx.GetLengthMs(ref handle);
                var visMs = (long)_timeline.SourceSamples * 1000 / 44100;
                if (wavMs > 0 && System.Math.Abs(wavMs - visMs) > 60)
                {
                    WarnOnce(handle.File!,
                        $"stale .vis ({visMs} ms) vs wav ({wavMs} ms) — rebake voices");
                    _timeline = null;
                }
            }
        }

        private void Update()
        {
            if (_idle)
            {
                return;
            }

            var speaking = false;
            if (_timeline != null)
            {
                var ms = FmodSfx.GetPlaybackMs(ref _handle);
                if (ms >= 0)
                {
                    SampleTimeline(_timeline, ms, out speaking);
                }
            }

            if (!speaking)
            {
                _timeline = null;
                for (var v = 0; v < VisemeCount; v++)
                {
                    _targets[v] = 0f;
                }
            }

            // Математика прежнего uLipSyncBlendShape: SmoothDamp к таргетам,
            // нормировка суммой (результат кладётся ОБРАТНО в вес — так делал
            // оригинал, воспроизводим), volume отдельным SmoothDamp.
            var sum = 0f;
            for (var v = 0; v < VisemeCount; v++)
            {
                _weights[v] = Mathf.SmoothDamp(
                    _weights[v], _targets[v], ref _weightVels[v], Smoothness);
                sum += _weights[v];
            }

            if (sum > 0f)
            {
                for (var v = 0; v < VisemeCount; v++)
                {
                    _weights[v] /= sum;
                }
            }

            var volumeTarget = speaking ? _volumeTarget : 0f;
            _volume = Mathf.SmoothDamp(_volume, volumeTarget, ref _volumeVel, Smoothness);

            // Рот закрылся — паркуемся до следующей реплики (один финальный
            // нулевой прогон, дальше ни Update, ни LateUpdate не работают).
            if (!speaking && _volume < 0.001f)
            {
                for (var v = 0; v < VisemeCount; v++)
                {
                    _weights[v] = 0f;
                    _weightVels[v] = 0f;
                }

                _volume = 0f;
                _volumeVel = 0f;
                _idle = true;
                _applyFinal = true;
            }
        }

        private void LateUpdate()
        {
            if (_idle && !_applyFinal)
            {
                return;
            }

            _applyFinal = false;
            if (_face == null)
            {
                return;
            }

            for (var v = 0; v < VisemeCount; v++)
            {
                if (_shapeIndex[v] >= 0)
                {
                    _face.SetBlendShapeWeight(_shapeIndex[v], _weights[v] * _volume * 100f);
                }
            }
        }

        private void SampleTimeline(Timeline t, long ms, out bool speaking)
        {
            var f = ms * t.Fps / 1000f;
            var i0 = Mathf.Min((int)f, t.FrameCount - 1);
            var i1 = Mathf.Min(i0 + 1, t.FrameCount - 1);
            var frac = Mathf.Clamp01(f - i0);
            var stride = VisemeCount + 1;
            var a = i0 * stride;
            var b = i1 * stride;
            for (var v = 0; v < VisemeCount; v++)
            {
                _targets[v] = Mathf.Lerp(t.Frames[a + v], t.Frames[b + v], frac) / 255f;
            }

            _volumeTarget =
                Mathf.Lerp(t.Frames[a + VisemeCount], t.Frames[b + VisemeCount], frac) / 255f;
            speaking = true;
        }

        private static Timeline? LoadTimeline(string wavPath)
        {
            var path = Path.ChangeExtension(wavPath, ".vis");
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            Timeline? timeline = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                timeline = Parse(bytes, path);
            }
            catch (System.Exception e)
            {
                WarnOnce(path, e.Message);
            }

            Cache[path] = timeline;
            return timeline;
        }

        private static Timeline? Parse(byte[] bytes, string path)
        {
            if (bytes.Length < HeaderBytes ||
                bytes[0] != 'H' || bytes[1] != 'X' || bytes[2] != 'L' || bytes[3] != 'S')
            {
                WarnOnce(path, "not a HXLS sidecar");
                return null;
            }

            var version = System.BitConverter.ToUInt16(bytes, 4);
            int visemes = bytes[6];
            int fps = bytes[7];
            int frameCount = System.BitConverter.ToUInt16(bytes, 8);
            var sourceSamples = System.BitConverter.ToInt32(bytes, 10);
            var stride = visemes + 1;
            if (version != FormatVersion || visemes != VisemeCount || fps <= 0 ||
                frameCount < 1 || bytes.Length != HeaderBytes + frameCount * stride)
            {
                WarnOnce(path, $"bad .vis (v{version}, {visemes} visemes, {frameCount} frames, {bytes.Length} B)");
                return null;
            }

            var frames = new byte[frameCount * stride];
            System.Array.Copy(bytes, HeaderBytes, frames, 0, frames.Length);
            return new Timeline
            {
                Frames = frames,
                FrameCount = frameCount,
                Fps = fps,
                SourceSamples = sourceSamples,
            };
        }

        private static void WarnOnce(string path, string reason)
        {
            if (Warned.Add(path))
            {
                Debug.LogWarning($"[NpcVoiceLipSync] {path}: {reason} — lips stay still");
            }
        }
    }
}
