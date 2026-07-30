#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §67: thin FMOD Core wrapper — 3D one-shots and loops straight from
    /// StreamingAssets/HexLive/Sfx, no Studio banks required. Files follow the
    /// "<id>_<n>.(ogg|wav)" convention; every listed variant of an id is loaded
    /// at Prewarm and a random one plays per call (with a small pitch jitter),
    /// so repeated chops/steps never machine-gun the same sample.
    /// The FMOD Studio bank pipeline can replace this later without touching
    /// call sites — they only know ids like <see cref="Sfx.ChopWood"/>.
    /// </summary>
    public static class FmodSfx
    {
        // ---- sound ids (also the StreamingAssets file-name prefixes) ----
        public static class Sfx
        {
            public const string ChopWood = "chop_wood";       // топор по стволу
            public const string ChopCoco = "chop_coco";       // нож/удар по кокосу
            public const string MineStone = "mine_stone";     // кирка по камню
            public const string Hammer = "hammer";            // молоток на стройке
            public const string HitFlesh = "hit_flesh";       // удар по плоти
            public const string BodyFall = "body_fall";       // падение тела
            public const string Swing = "swing";              // вжух замаха
            public const string ChopAccent = "chop_accent";   // финальный треск дерева
            public const string TreeCreak = "tree_creak";     // скрип падающего ствола
            public const string StepGrass = "step_grass";
            public const string StepSand = "step_sand";
            public const string StepWater = "step_water";
            public const string WolfGrowl = "wolf_growl";
            public const string WolfBite = "wolf_bite";
            public const string WolfHowl = "wolf_howl";
            public const string HurtF = "hurt_f";
            public const string DeathF = "death_f";
            public const string Splash = "splash";
            public const string Gecko = "gecko";
            public const string LoopWaves = "loop_waves";
            public const string LoopJungleDay = "loop_jungle_day";
            public const string LoopCrickets = "loop_crickets";
            public const string LoopFire = "loop_fire";
            public const string LoopRain = "loop_rain";
        }

        private readonly struct Def
        {
            public readonly float Volume;      // базовый гейн
            public readonly float MinDist;     // 3D: полная громкость ближе этого
            public readonly float MaxDist;     // 3D: тишина дальше этого
            public readonly float PitchJitter; // ± доля случайного питча
            public readonly bool Loop;
            public readonly bool Spatial;      // false = 2D (глобальный эмбиент)

            public Def(float volume, float minDist, float maxDist,
                float pitchJitter = 0.06f, bool loop = false, bool spatial = true)
            {
                Volume = volume; MinDist = minDist; MaxDist = maxDist;
                PitchJitter = pitchJitter; Loop = loop; Spatial = spatial;
            }
        }

        // Дистанции в world units (актёр ~0.6 wu ростом, камера 3..25 wu).
        private static readonly Dictionary<string, Def> Defs = new()
        {
            [Sfx.ChopWood] = new Def(0.80f, 1.2f, 26f),
            [Sfx.ChopCoco] = new Def(0.70f, 1.2f, 22f),
            [Sfx.MineStone] = new Def(0.80f, 1.2f, 26f),
            [Sfx.Hammer] = new Def(0.70f, 1.2f, 24f),
            [Sfx.HitFlesh] = new Def(0.85f, 1.2f, 28f),
            [Sfx.BodyFall] = new Def(0.80f, 1.2f, 24f),
            [Sfx.Swing] = new Def(0.50f, 1.0f, 18f, 0.10f),
            [Sfx.ChopAccent] = new Def(0.90f, 1.5f, 30f),
            [Sfx.TreeCreak] = new Def(0.90f, 1.5f, 30f, 0.08f),
            [Sfx.StepGrass] = new Def(0.32f, 0.8f, 12f, 0.12f),
            [Sfx.StepSand] = new Def(0.32f, 0.8f, 12f, 0.12f),
            [Sfx.StepWater] = new Def(0.40f, 0.8f, 14f, 0.12f),
            [Sfx.WolfGrowl] = new Def(0.85f, 1.5f, 34f, 0.05f),
            [Sfx.WolfBite] = new Def(0.90f, 1.5f, 30f, 0.05f),
            [Sfx.WolfHowl] = new Def(0.70f, 6.0f, 70f, 0.04f),
            [Sfx.HurtF] = new Def(0.85f, 1.5f, 30f, 0.05f),
            [Sfx.DeathF] = new Def(0.95f, 2.0f, 40f, 0.03f),
            [Sfx.Splash] = new Def(0.80f, 1.2f, 24f, 0.08f),
            [Sfx.Gecko] = new Def(0.45f, 4.0f, 40f, 0.06f),
            [Sfx.LoopWaves] = new Def(0.70f, 4.0f, 42f, 0f, loop: true),
            // Птицы приглушены до уровня, выставленного в FMOD Studio (мастер
            // события -7.96 dB + фейдер трека -11.5 dB = -19.46 dB = 0.106).
            // Пока раннтайм не читает банки, эта таблица — единственный рабочий
            // регулятор громкости; Studio держит то же значение (см. CLAUDE.md).
            [Sfx.LoopJungleDay] = new Def(0.106f, 0f, 0f, 0f, loop: true, spatial: false),
            [Sfx.LoopCrickets] = new Def(0.40f, 0f, 0f, 0f, loop: true, spatial: false),
            [Sfx.LoopFire] = new Def(0.60f, 0.9f, 13f, 0f, loop: true),
            [Sfx.LoopRain] = new Def(0.50f, 0f, 0f, 0f, loop: true, spatial: false),
        };

        /// <summary>Handle of a running loop (campfire crackle, ambience beds).</summary>
        public struct Loop
        {
            internal FMOD.Channel Channel;
            // §67.12: когда звук идёт СОБЫТИЕМ Studio, ручка — это инстанс
            // события, а не канал. Голоса остаются на канале: липсинку нужен
            // конкретный файл и позиция воспроизведения (§67.7).
            internal FMOD.Studio.EventInstance Event;
            internal bool IsEvent;
            internal bool Valid;
            public bool IsValid => Valid;
            /// <summary>Файл сыгранного варианта (§67.7: липсинк читает его PCM).</summary>
            public string File;
        }

        // §67.6: character voice lines auto-register from the file scan —
        // any "voice_<char>_<emotion>_<n>" group gets this shared def, so a
        // new colonist's folder needs zero code.
        private static readonly Def VoiceDef = new(0.75f, 1.2f, 22f, 0.03f);

        // §67.10: файлы РОВНО этой группы — стем без последнего "_<вариант>"
        // должен совпадать с ид-ом. Простая маска "id_*.*" тут ошибается:
        // общий банк эмоции voice_molly_sad проглотил бы и voice_molly_sad_thirst_0
        // (реплику под конкретный повод), и фолбэк перестал бы быть фолбэком.
        private static string[] ExactGroupFiles(string dir, string id)
        {
            var keep = new List<string>();
            foreach (var file in Directory.GetFiles(dir, id + "_*.*", SearchOption.AllDirectories))
            {
                if (!IsAudioFile(file))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                var cut = stem.LastIndexOf('_');
                if (cut == id.Length && stem.StartsWith(id, System.StringComparison.Ordinal))
                {
                    keep.Add(file);
                }
            }

            return keep.ToArray();
        }

        // Рядом с каждым звуком Unity кладёт "<файл>.meta". Без этого фильтра
        // они уходят в createSound, FMOD перебирает на них ВСЕ кодеки (ogg →
        // s3m → xm → it → midi) и сыпет ошибками в консоль — 800+ бесполезных
        // попыток на прогреве. Звук при этом работал: неудачные просто
        // пропускались, поэтому баг и жил незаметно.
        private static bool IsAudioFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ext.Equals(".wav", System.StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".ogg", System.StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, FMOD.Sound[]> Sounds = new();
        private static readonly Dictionary<string, string[]> Paths = new();
        private static readonly Dictionary<string, Def> LoadedDefs = new();
        private static bool _ready;
        private static bool _failed;
        private static FMOD.ChannelGroup _master;
        private static System.Random _rng = new(9257);

        // Editor "no domain reload" play mode: FMOD's system is torn down each
        // exit-play, our cached handles die with it — start clean every run.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Sounds.Clear();
            Paths.Clear();
            LoadedDefs.Clear();
            _ready = false;
            _failed = false;
            _rng = new System.Random(9257);
        }

        /// <summary>Load every catalog sound up front (spec §41.4 warm-up rule:
        /// a lazy load mid-combat once cost a 2.4 s freeze).</summary>
        public static void Prewarm()
        {
            if (_ready || _failed)
            {
                return;
            }

            try
            {
                var core = FMODUnity.RuntimeManager.CoreSystem;
                core.getMasterChannelGroup(out _master);
                // Доплер выключен (RTS-камера), метрика дистанций 1:1 в wu.
                core.set3DSettings(0f, 1f, 1f);

                var dir = Path.Combine(Application.streamingAssetsPath, "HexLive/Sfx");

                LoadedDefs.Clear();
                foreach (var (id, def) in Defs)
                {
                    LoadedDefs[id] = def;
                }

                // Голосовые банки персонажей — по факту наличия файлов.
                // §67.10: реплики живут в подпапках Voices/<char>/ (их сотни на
                // персонажа), поэтому скан РЕКУРСИВНЫЙ. Ид группы по-прежнему
                // читается из имени файла, путь не важен.
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "voice_*", SearchOption.AllDirectories))
                    {
                        // Только настоящие звуки: ".wav.meta" дал бы группу
                        // "voice_<char>_<группа>_0.wav" — мусорный ид.
                        if (!IsAudioFile(file))
                        {
                            continue;
                        }

                        var stem = Path.GetFileNameWithoutExtension(file);
                        var cut = stem.LastIndexOf('_');
                        if (cut <= 0)
                        {
                            continue;
                        }

                        LoadedDefs.TryAdd(stem[..cut], VoiceDef);
                    }
                }

                foreach (var (id, def) in LoadedDefs)
                {
                    var files = Directory.Exists(dir)
                        ? ExactGroupFiles(dir, id)
                        : System.Array.Empty<string>();
                    System.Array.Sort(files);
                    var list = new List<FMOD.Sound>(files.Length);
                    var pathList = new List<string>(files.Length);
                    foreach (var file in files)
                    {
                        var mode = FMOD.MODE.CREATESAMPLE
                            | (def.Loop ? FMOD.MODE.LOOP_NORMAL : FMOD.MODE.LOOP_OFF)
                            | (def.Spatial
                                ? FMOD.MODE._3D | FMOD.MODE._3D_LINEARSQUAREROLLOFF
                                : FMOD.MODE._2D);
                        if (core.createSound(file, mode, out var sound) != FMOD.RESULT.OK)
                        {
                            continue;
                        }

                        if (def.Spatial)
                        {
                            sound.set3DMinMaxDistance(def.MinDist, def.MaxDist);
                        }

                        list.Add(sound);
                        pathList.Add(file);
                    }

                    if (list.Count == 0)
                    {
                        Debug.LogWarning($"[FmodSfx] no files for '{id}' in {dir}");
                        continue;
                    }

                    Sounds[id] = list.ToArray();
                    Paths[id] = pathList.ToArray();
                }

                _ready = true;
            }
            catch (System.Exception e)
            {
                // Без FMOD игра живёт молча — не спамим, помечаем и выходим.
                _failed = true;
                Debug.LogError($"[FmodSfx] init failed, sound disabled: {e.Message}");
            }
        }

        /// <summary>The camera is the ears: position + orientation each frame.</summary>
        public static void UpdateListener(Transform camera)
        {
            if (!_ready)
            {
                return;
            }

            var pos = ToFmod(camera.position);
            var vel = default(FMOD.VECTOR);
            var fwd = ToFmod(camera.forward.normalized);
            var up = ToFmod(camera.up.normalized);
            FMODUnity.RuntimeManager.CoreSystem.set3DListenerAttributes(
                0, ref pos, ref vel, ref fwd, ref up);
        }

        /// <summary>Есть ли такой звук (учитывая голосовые группы из скана).</summary>
        public static bool HasSound(string id) => _ready && Sounds.ContainsKey(id);

        // ---- §67.12: маршрут через события FMOD Studio --------------------
        // Раннтайм теперь ГРУЗИТ банки, поэтому громкость/эффекты/дистанции
        // берутся из Studio-проекта — там их и крутит звуковик, в том числе
        // вживую через Live Update. Если события нет (банк не собран, ид
        // отсутствует), звук играется по-старому из StreamingAssets: тишины
        // из-за рассинхрона проекта и кода быть не должно.
        private static readonly Dictionary<string, string> EventPaths = new();

        private static string EventPathFor(string id)
        {
            if (EventPaths.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var folder = id.StartsWith("voice_", System.StringComparison.Ordinal)
                ? "Voices"
                : LoadedDefs.TryGetValue(id, out var d) && d.Loop ? "Ambience" : "SFX";
            var path = $"event:/{folder}/{id}";

            // Голоса намеренно НЕ идут событиями (см. Loop.Event) — липсинк.
            var usable = !id.StartsWith("voice_", System.StringComparison.Ordinal) &&
                         FMODUnity.RuntimeManager.StudioSystem.getEvent(path, out _) == FMOD.RESULT.OK;
            EventPaths[id] = usable ? path : null;
            return EventPaths[id];
        }

        /// <summary>Fire-and-forget 3D one-shot: random variant, pitch jitter.</summary>
        public static void Play(string id, Vector3 position, float volumeGain = 1f)
        {
            if (_ready && EventPathFor(id) is { } path)
            {
                var inst = FMODUnity.RuntimeManager.CreateInstance(path);
                inst.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(position));
                if (!Mathf.Approximately(volumeGain, 1f))
                {
                    inst.setVolume(volumeGain);
                }

                inst.start();
                inst.release(); // освободится сама, когда доиграет
                return;
            }

            PlayTracked(id, position, volumeGain);
        }

        /// <summary>One-shot с хэндлом — говорящая не начинает новую реплику,
        /// пока звучит предыдущая (IsPlaying).</summary>
        public static Loop PlayTracked(string id, Vector3 position, float volumeGain = 1f)
        {
            if (!_ready || !Sounds.TryGetValue(id, out var variants))
            {
                return default;
            }

            var def = LoadedDefs[id];
            var pick = _rng.Next(variants.Length);
            var sound = variants[pick];
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.playSound(sound, _master, true, out var channel) != FMOD.RESULT.OK)
            {
                return default;
            }

            if (def.Spatial)
            {
                var pos = ToFmod(position);
                var vel = default(FMOD.VECTOR);
                channel.set3DAttributes(ref pos, ref vel);
            }

            channel.setVolume(def.Volume * volumeGain);
            if (def.PitchJitter > 0f)
            {
                channel.setPitch(1f + ((float)_rng.NextDouble() * 2f - 1f) * def.PitchJitter);
            }

            channel.setPaused(false);
            return new Loop
            {
                Channel = channel,
                Valid = true,
                File = Paths.TryGetValue(id, out var paths) ? paths[pick] : null,
            };
        }

        /// <summary>Позиция воспроизведения хэндла в мс (-1 = не играет) —
        /// §67.7: точные часы для липсинк-фидера.</summary>
        public static int GetPlaybackMs(ref Loop handle)
        {
            if (!_ready || !handle.Valid)
            {
                return -1;
            }

            if (handle.Channel.getPosition(out var ms, FMOD.TIMEUNIT.MS) != FMOD.RESULT.OK)
            {
                return -1;
            }

            return (int)ms;
        }

        /// <summary>Длительность звучащего сэмпла хэндла в мс (-1 = неизвестно)
        /// — §67.8: столько лицо держит эмоцию реплики.</summary>
        public static int GetLengthMs(ref Loop handle)
        {
            if (!_ready || !handle.Valid ||
                handle.Channel.getCurrentSound(out var sound) != FMOD.RESULT.OK)
            {
                return -1;
            }

            return sound.getLength(out var ms, FMOD.TIMEUNIT.MS) == FMOD.RESULT.OK
                ? (int)ms
                : -1;
        }

        /// <summary>Хэндл ещё звучит? (сброшенный/чужой хэндл — false).</summary>
        public static bool IsPlaying(ref Loop handle)
        {
            if (!_ready || !handle.Valid)
            {
                return false;
            }

            var res = handle.Channel.isPlaying(out var playing);
            if (res != FMOD.RESULT.OK || !playing)
            {
                handle = default;
                return false;
            }

            return true;
        }

        /// <summary>Start a loop (ambience bed / campfire crackle). Volume 0 is
        /// fine — ambience crossfades ride SetLoopVolume every frame.</summary>
        public static Loop StartLoop(string id, Vector3 position, float volumeGain = 1f)
        {
            if (!_ready || !Sounds.TryGetValue(id, out var variants))
            {
                return default;
            }

            if (EventPathFor(id) is { } eventPath)
            {
                var inst = FMODUnity.RuntimeManager.CreateInstance(eventPath);
                inst.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(position));
                inst.setVolume(Mathf.Clamp01(volumeGain));
                inst.start();
                return new Loop { Event = inst, IsEvent = true, Valid = true };
            }

            var def = LoadedDefs[id];
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.playSound(variants[0], _master, true, out var channel) != FMOD.RESULT.OK)
            {
                return default;
            }

            if (def.Spatial)
            {
                var pos = ToFmod(position);
                var vel = default(FMOD.VECTOR);
                channel.set3DAttributes(ref pos, ref vel);
            }

            channel.setVolume(def.Volume * volumeGain);
            channel.setPaused(false);
            return new Loop { Channel = channel, Valid = true };
        }

        public static void MoveLoop(ref Loop loop, Vector3 position)
        {
            if (!loop.Valid)
            {
                return;
            }

            if (loop.IsEvent)
            {
                loop.Event.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(position));
                return;
            }

            var pos = ToFmod(position);
            var vel = default(FMOD.VECTOR);
            loop.Channel.set3DAttributes(ref pos, ref vel);
        }

        /// <summary>volumeGain 0..1 поверх базовой громкости id из каталога.</summary>
        public static void SetLoopVolume(ref Loop loop, string id, float volumeGain)
        {
            if (!loop.Valid)
            {
                return;
            }

            if (loop.IsEvent)
            {
                // Базовую громкость держит само событие в Studio — здесь только
                // кроссфейд эмбиента (день/ночь/дождь).
                loop.Event.setVolume(Mathf.Clamp01(volumeGain));
                return;
            }

            var baseVolume = LoadedDefs.TryGetValue(id, out var def) ? def.Volume : 1f;
            loop.Channel.setVolume(baseVolume * Mathf.Clamp01(volumeGain));
        }

        public static void StopLoop(ref Loop loop)
        {
            if (!loop.Valid)
            {
                return;
            }

            if (loop.IsEvent)
            {
                loop.Event.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                loop.Event.release();
                loop = default;
                return;
            }

            loop.Channel.stop();
            loop = default;
        }

        private static FMOD.VECTOR ToFmod(Vector3 v) =>
            new() { x = v.x, y = v.y, z = v.z };
    }
}
