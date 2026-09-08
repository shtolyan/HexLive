#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §67/§152: thin FMOD Core wrapper over Player-owned audio. Files
    /// with the same logical prefix form the random variants of one sound;
    /// SFX, voices, music and Studio banks ship in StreamingAssets and never
    /// wait for the live-content registry.
    /// The FMOD Studio bank pipeline can replace this later without touching
    /// call sites — they only know ids like <see cref="Sfx.ChopWood"/>.
    /// </summary>
    public static class FmodSfx
    {
        // ---- stable logical sound-group ids ----
        public static class Sfx
        {
            public const string ChopWood = "chop_wood";       // топор по стволу
            public const string ChopCoco = "chop_coco";       // нож/удар по кокосу
            public const string MineStone = "mine_stone";     // кирка по камню
            public const string Hammer = "hammer";            // молоток на стройке
            public const string HitFlesh = "hit_flesh";       // удар по плоти (общий)
            // §104 r5: чем именно попали. Вид выбирает по HitWeaponId снапшота:
            // пустой id — кулак, клинковое снаряжение — лезвие, зубы зверя
            // остаются на wolf_bite. Раньше человеческий удар не звучал вовсе —
            // hit_flesh играл только на отрыв конечности, разделку
            // туши и на удар девушки ПО ВОЛКУ.
            public const string HitPunch = "hit_punch";       // кулаком по телу
            public const string HitBlade = "hit_blade";       // клинком по телу
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
            // Рядом с hit_flesh: тот же план, чуть разный характер. Джиттер
            // высоты — чтобы серия ударов не звучала магнитофонной петлёй.
            [Sfx.HitPunch] = new Def(0.85f, 1.2f, 28f, 0.07f),
            [Sfx.HitBlade] = new Def(0.85f, 1.2f, 28f, 0.06f),
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
            // Баг #213: 13 wu заканчивались ниже обычной RTS-камеры, поэтому
            // видимый костёр практически всегда звучал как немой. Оставляем
            // источник локальным, но даём треску рабочее окно приближения.
            [Sfx.LoopFire] = new Def(0.85f, 1.2f, 28f, 0f, loop: true),
            [Sfx.LoopRain] = new Def(0.50f, 0f, 0f, 0f, loop: true, spatial: false),
        };

        /// <summary>Handle of a running loop (campfire crackle, ambience beds).</summary>
        public struct Loop
        {
            internal FMOD.Channel Channel;
            internal FMOD.Sound OwnedSound;
            internal bool OwnsSound;
            // §67.12: когда звук идёт СОБЫТИЕМ Studio, ручка — это инстанс
            // события, а не канал. Голоса остаются на канале: липсинку нужен
            // конкретный файл и позиция воспроизведения (§67.7).
            internal FMOD.Studio.EventInstance Event;
            internal bool IsEvent;
            internal bool Valid;
            public bool IsValid => Valid;
            /// <summary>Файл сыгранного варианта (§67.7: липсинк берёт его
            /// атомарно привязанный vis-сайдкар с таймлайном визем).</summary>
            public string File;
            public string VisemeFile;
        }

        // §67.6: character voice lines auto-register from the file scan —
        // any "voice_<char>_<emotion>_<n>" group gets this shared def, so a
        // new colonist's folder needs zero code.
        private static readonly Def VoiceDef = new(0.75f, 1.2f, 22f, 0.03f);

        private static readonly Dictionary<string, FMOD.Sound[]> Sounds = new();
        private static readonly Dictionary<string, string[]> Paths = new();
        private static readonly Dictionary<string, string> VisemePaths = new();
        private static readonly Dictionary<string, Def> LoadedDefs = new();
        private static bool _ready;
        private static bool _failed;
        private static bool _loading;
        private static FMOD.ChannelGroup _master;
        private static System.Random _rng = new(9257);

        public enum VolumeCategory { Voices, Music, Environment }
        private static FMOD.ChannelGroup _voicesGroup, _environmentGroup;
        private static readonly List<(FMOD.Studio.EventInstance Instance, VolumeCategory Category, float Gain)> MixedEvents = new();
        private static readonly float[] UserVolumes = { 1f, 1f, 1f };
        private static bool _volumesLoaded;

        public static float GetUserVolume(VolumeCategory category)
        {
            if (!_volumesLoaded)
            {
                for (var i = 0; i < UserVolumes.Length; i++)
                {
                    var value = PlayerPrefs.GetFloat("HexLive.Audio." + (VolumeCategory)i, 1f);
                    UserVolumes[i] = float.IsNaN(value) ? 1f : Mathf.Clamp01(value);
                }
                _volumesLoaded = true;
            }
            return UserVolumes[(int)category];
        }

        public static void SetUserVolume(VolumeCategory category, float volume)
        {
            GetUserVolume(category);
            volume = float.IsNaN(volume) ? 1f : Mathf.Clamp01(volume);
            UserVolumes[(int)category] = volume;
            PlayerPrefs.SetFloat("HexLive.Audio." + category, volume);
            if (_voicesGroup.hasHandle()) _voicesGroup.setVolume(GetUserVolume(VolumeCategory.Voices));
            if (_environmentGroup.hasHandle()) _environmentGroup.setVolume(GetUserVolume(VolumeCategory.Environment));
            if (_musicGroupReady) _musicGroup.setVolume(GetUserVolume(VolumeCategory.Music));
            PruneMixedEvents();
            foreach (var item in MixedEvents)
                item.Instance.setVolume(item.Gain * GetUserVolume(item.Category));
        }

        private static VolumeCategory CategoryFor(string id) =>
            id.StartsWith("voice_", System.StringComparison.Ordinal) || id == Sfx.HurtF || id == Sfx.DeathF
                ? VolumeCategory.Voices : VolumeCategory.Environment;

        private static FMOD.ChannelGroup GroupFor(string id) =>
            CategoryFor(id) == VolumeCategory.Voices ? _voicesGroup : _environmentGroup;

        private static void PruneMixedEvents()
        {
            for (var i = MixedEvents.Count - 1; i >= 0; i--)
                if (!MixedEvents[i].Instance.isValid())
                    MixedEvents.RemoveAt(i);
        }

        private static void MixEvent(FMOD.Studio.EventInstance instance, string id, float gain)
        {
            PruneMixedEvents();
            for (var i = MixedEvents.Count - 1; i >= 0; i--)
                if (MixedEvents[i].Instance.handle == instance.handle) MixedEvents.RemoveAt(i);
            var category = CategoryFor(id);
            instance.setVolume(gain * GetUserVolume(category));
            MixedEvents.Add((instance, category, gain));
        }

        // Editor "no domain reload" play mode: FMOD's system is torn down each
        // exit-play, our cached handles die with it — start clean every run.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _volumesLoaded = false;
            _voicesGroup = default;
            _environmentGroup = default;
            MixedEvents.Clear();
            EventPaths.Clear();
            Sounds.Clear();
            Paths.Clear();
            VisemePaths.Clear();
            LoadedDefs.Clear();
            _ready = false;
            _failed = false;
            _loading = false;
            _rng = new System.Random(9257);

            // §70: музыкальный стрим и его группа умирают вместе с системой FMOD.
            MusicPaths.Clear();
            _musicIds = System.Array.Empty<string>();
            _musicScanned = false;
            _musicOpen = false;
            _musicGroupReady = false;
            _musicChannel = default;
            _musicSound = default;
            _musicGroup = default;
            MusicTracksChanged = null;
        }

        /// <summary>Load every currently registered sound up front (§41.4:
        /// a lazy load mid-combat once cost a 2.4 s freeze).</summary>
        public static void Prewarm()
        {
            if (_ready || _failed || _loading)
            {
                return;
            }

            var root = Path.Combine(Application.streamingAssetsPath, "HexLive", "Sfx");
            if (!Directory.Exists(root))
            {
                Debug.LogError($"[FmodSfx] Player audio directory is missing: {root}");
                _ready = true;
                return;
            }

            _loading = true;
            var groups = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(IsAudioFile)
                         .Where(path => !IsVoiceFile(path)))
            {
                var group = GroupId(path);
                if (!groups.TryGetValue(group, out var paths))
                {
                    paths = new List<string>();
                    groups[group] = paths;
                }
                paths.Add(path);
            }
            OpenSounds(groups);
        }

        private static bool IsAudioFile(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".wav", System.StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ogg", System.StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp3", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVoiceFile(string path) => path.Replace('\\', '/').Contains(
            "/Voices/", System.StringComparison.Ordinal);

        private static string GroupId(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var split = stem.LastIndexOf('_');
            return split >= 0 && int.TryParse(stem[(split + 1)..], out _) ? stem[..split] : stem;
        }

        private static void OpenSounds(Dictionary<string, List<string>> groups)
        {
            try
            {
                var core = FMODUnity.RuntimeManager.CoreSystem;
                core.getMasterChannelGroup(out _master);
                if (core.createChannelGroup("HexLiveVoices", out _voicesGroup) != FMOD.RESULT.OK ||
                    core.createChannelGroup("HexLiveEnvironment", out _environmentGroup) != FMOD.RESULT.OK)
                    throw new System.InvalidOperationException("Cannot create audio category groups");
                _master.addGroup(_voicesGroup);
                _master.addGroup(_environmentGroup);
                _voicesGroup.setVolume(GetUserVolume(VolumeCategory.Voices));
                _environmentGroup.setVolume(GetUserVolume(VolumeCategory.Environment));
                // Доплер выключен (RTS-камера), метрика дистанций 1:1 в wu.
                core.set3DSettings(0f, 1f, 1f);

                LoadedDefs.Clear();
                foreach (var (id, def) in Defs)
                {
                    LoadedDefs[id] = def;
                }

                foreach (var (id, def) in LoadedDefs)
                {
                    var files = groups.TryGetValue(id, out var groupFiles)
                        ? groupFiles.ToArray()
                        : System.Array.Empty<string>();
                    OpenGroup(core, id, def, files);
                }

                _ready = true;
                _loading = false;
            }
            catch (System.Exception e)
            {
                // Без FMOD игра живёт молча — не спамим, помечаем и выходим.
                _failed = true;
                _loading = false;
                Debug.LogError($"[FmodSfx] init failed, sound disabled: {e.Message}");
            }
        }

        private static bool OpenGroup(FMOD.System core, string id, Def def, string[] files)
        {
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
                var viseme = Path.ChangeExtension(file, ".vis");
                if (File.Exists(viseme))
                {
                    VisemePaths[file] = viseme;
                }
            }

            if (list.Count == 0)
            {
                Debug.LogWarning($"[FmodSfx] no Player audio files for '{id}'");
                return false;
            }

            Sounds[id] = list.ToArray();
            Paths[id] = pathList.ToArray();
            return true;
        }

        private static bool EnsureVoiceGroup(string id)
        {
            if (Sounds.ContainsKey(id))
            {
                return true;
            }
            if (!_ready || !id.StartsWith("voice_", System.StringComparison.Ordinal))
            {
                return false;
            }

            var root = Path.Combine(Application.streamingAssetsPath, "HexLive", "Sfx", "Voices");
            if (!Directory.Exists(root))
            {
                return false;
            }
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsAudioFile)
                .Where(path => string.Equals(GroupId(path), id, System.StringComparison.Ordinal))
                .ToArray();
            if (files.Length == 0)
            {
                return false;
            }

            LoadedDefs[id] = VoiceDef;
            return OpenGroup(FMODUnity.RuntimeManager.CoreSystem, id, VoiceDef, files);
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
        public static bool HasSound(string id) => _ready &&
            (Sounds.ContainsKey(id) || EnsureVoiceGroup(id));

        // ---- §67.12: маршрут через события FMOD Studio --------------------
        // Если Studio-событие доступно, громкость/эффекты/дистанции берутся из
        // него. Иначе тот же Player-owned файл играет через FMOD Core; этот
        // fallback всегда доступен без сети и без content registry.
        // §67.12 r2: играть ли ЛУПЫ (эмбиент, костёр) событиями Studio.
        // Пока false — см. комментарий в StartLoop: у сгенерированных событий
        // нет луп-региона, поэтому они одноразовые. Ставится в true сразу
        // после того, как fix_ambience_loops.js добавит регионы и банк будет
        // пересобран. One-shot'ы событиями идут всегда.
        private const bool LoopsUseStudioEvents = false;

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
                MixEvent(inst, id, volumeGain);

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
            if (!_ready || (!Sounds.TryGetValue(id, out var variants) &&
                            (!EnsureVoiceGroup(id) || !Sounds.TryGetValue(id, out variants))))
            {
                return default;
            }

            var def = LoadedDefs[id];
            var pick = _rng.Next(variants.Length);
            var sound = variants[pick];
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.playSound(sound, GroupFor(id), true, out var channel) != FMOD.RESULT.OK)
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
                VisemeFile = Paths.TryGetValue(id, out var selectedPaths) &&
                             VisemePaths.TryGetValue(selectedPaths[pick], out var visemePath)
                    ? visemePath
                    : null,
            };
        }

        /// <summary>§159: positional dynamic PCM/WAV reply, still owned by FMOD Core.</summary>
        public static Loop PlayFileTracked(
            string wavPath, string visemePath, Vector3 position, float volumeGain = 1f,
            bool listenerRelative = false)
        {
            if (!_ready || string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
            {
                return default;
            }

            var core = FMODUnity.RuntimeManager.CoreSystem;
            var mode = FMOD.MODE.CREATESAMPLE | FMOD.MODE.LOOP_OFF |
                       (listenerRelative
                           ? FMOD.MODE._2D
                           : FMOD.MODE._3D | FMOD.MODE._3D_LINEARSQUAREROLLOFF);
            if (core.createSound(wavPath, mode, out var sound) != FMOD.RESULT.OK)
            {
                return default;
            }
            if (!listenerRelative)
                sound.set3DMinMaxDistance(VoiceDef.MinDist, VoiceDef.MaxDist);
            if (core.playSound(sound, _voicesGroup, true, out var channel) != FMOD.RESULT.OK)
            {
                sound.release();
                return default;
            }

            if (!listenerRelative)
            {
                var pos = ToFmod(position);
                var vel = default(FMOD.VECTOR);
                channel.set3DAttributes(ref pos, ref vel);
            }
            channel.setVolume(VoiceDef.Volume * Mathf.Clamp(volumeGain, 0f, 4f));
            channel.setPaused(false);
            return new Loop
            {
                Channel = channel,
                OwnedSound = sound,
                OwnsSound = true,
                Valid = true,
                File = wavPath,
                VisemeFile = visemePath
            };
        }

        /// <summary>Позиция воспроизведения хэндла в мс (-1 = не играет) —
        /// §67.7: часы липсинка (по ним сэмплируется .vis-таймлайн).</summary>
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
                if (handle.OwnsSound) handle.OwnedSound.release();
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

            // §67.12 r2: ЛУПЫ ПОКА НЕ ЧЕРЕЗ СОБЫТИЯ. У сгенерированных событий
            // эмбиента стоит `looping` на инструменте, но НЕТ луп-региона на
            // таймлайне — FMOD считает такое событие одноразовым
            // (`isOneshot()==true`): оно отыгрывает свои 23 с и умирает, а
            // стартуя на нулевой громкости (кроссфейд день/ночь) вдобавок
            // виртуализируется и глохнет сразу. Из-за этого в билде пропали
            // прибой и птицы. Файловый путь ниже зацикливает честно
            // (MODE.LOOP_NORMAL), поэтому лупы возвращены на него.
            // Вернуть события можно, когда в Studio-проекте у пяти событий
            // Ambience появится Loop Region (FMODStudio/Scripts/fix_ambience_loops.js)
            // и банк будет пересобран — тогда флаг ниже ставится в true.
            if (LoopsUseStudioEvents && EventPathFor(id) is { } eventPath)
            {
                var inst = FMODUnity.RuntimeManager.CreateInstance(eventPath);
                inst.set3DAttributes(FMODUnity.RuntimeUtils.To3DAttributes(position));
                MixEvent(inst, id, Mathf.Clamp01(volumeGain));
                inst.start();
                return new Loop { Event = inst, IsEvent = true, Valid = true };
            }

            var def = LoadedDefs[id];
            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (core.playSound(variants[0], GroupFor(id), true, out var channel) != FMOD.RESULT.OK)
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
                MixEvent(loop.Event, id, Mathf.Clamp01(volumeGain));
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
            if (loop.OwnsSound) loop.OwnedSound.release();
            loop = default;
        }

        // ==== §70: музыка ==================================================
        // Отдельная ветка от SFX по трём причинам:
        //  1) трек длинный (5+ минут) — только СТРИМ, сэмплом он развернулся бы
        //     в десятки мегабайт PCM;
        //  2) он нужен уже в ГЛАВНОМ МЕНЮ, то есть до того, как появится мир,
        //     а вместе с ним и Prewarm — поэтому у музыки свой ленивый init;
        //  3) одновременно звучит ровно один трек, так что и хэндл один.
        // Каждый трек лежит в Player StreamingAssets/HexLive/Music.
        private static readonly Dictionary<string, string> MusicPaths = new();
        private static string[] _musicIds = System.Array.Empty<string>();
        private static bool _musicScanned;
        private static FMOD.Sound _musicSound;
        private static FMOD.Channel _musicChannel;
        private static bool _musicOpen;
        private static FMOD.ChannelGroup _musicGroup;
        private static bool _musicGroupReady;

        public static event System.Action? MusicTracksChanged;

        /// <summary>Ид-ы найденных треков (имя файла без расширения), по алфавиту.</summary>
        public static string[] MusicTracks
        {
            get
            {
                ScanMusic();
                return _musicIds;
            }
        }

        private static void ScanMusic()
        {
            if (_musicScanned)
            {
                return;
            }

            _musicScanned = true;
            var root = Path.Combine(Application.streamingAssetsPath, "HexLive", "Music");
            if (!Directory.Exists(root))
            {
                Debug.LogWarning($"[FmodSfx] Player music directory is missing: {root}");
                return;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsAudioFile))
            {
                MusicPaths[Path.GetFileNameWithoutExtension(path)] = path;
            }
            _musicIds = MusicPaths.Keys
                .OrderBy(value => value, System.StringComparer.Ordinal).ToArray();
            MusicTracksChanged?.Invoke();
        }

        // Своя core-группа, подвешенная под мастер-ШИНУ Studio (а не под
        // мастер-группу Core): тогда музыка слушается фейдера Studio и кнопки
        // Mute Audio в Game view — иначе она вела бы себя как голоса (§67.6),
        // которые при «мьюте» продолжают играть и выглядят как баг.
        private static bool EnsureMusicGroup(FMOD.System core)
        {
            if (_musicGroupReady)
            {
                return true;
            }

            if (core.createChannelGroup("HexLiveMusic", out _musicGroup) != FMOD.RESULT.OK)
            {
                return false;
            }

            var studio = FMODUnity.RuntimeManager.StudioSystem;
            if (studio.getBus("bus:/", out var bus) == FMOD.RESULT.OK &&
                bus.lockChannelGroup() == FMOD.RESULT.OK)
            {
                studio.flushCommands(); // группа шины создаётся не сразу
                if (bus.getChannelGroup(out var busGroup) == FMOD.RESULT.OK)
                {
                    busGroup.addGroup(_musicGroup);
                }
            }

            _musicGroup.setVolume(GetUserVolume(VolumeCategory.Music));
            _musicGroupReady = true;
            return true;
        }

        /// <summary>Запустить трек с нуля (предыдущий глушится). volume 0 —
        /// нормально: директор музыки въезжает фейдом с тишины.</summary>
        public static bool PlayMusic(string id, float volume)
        {
            ScanMusic();
            StopMusic();
            if (!MusicPaths.TryGetValue(id, out var path))
            {
                return false;
            }

            try
            {
                var core = FMODUnity.RuntimeManager.CoreSystem;
                if (!EnsureMusicGroup(core))
                {
                    return false;
                }

                var mode = FMOD.MODE.CREATESTREAM | FMOD.MODE.LOOP_OFF | FMOD.MODE._2D;
                // Blob names are pure SHA-256 and deliberately have no file
                // extension. Tell FMOD the codec explicitly instead of making
                // it guess from the cache path (the macOS runtime otherwise
                // probes OGG/MOD/etc. and rejects a valid MP3 payload).
                var exInfo = new FMOD.CREATESOUNDEXINFO
                {
                    cbsize = System.Runtime.InteropServices.Marshal.SizeOf<FMOD.CREATESOUNDEXINFO>(),
                    suggestedsoundtype = FMOD.SOUND_TYPE.MPEG,
                };
                if (core.createStream(path, mode, ref exInfo, out var sound) != FMOD.RESULT.OK)
                {
                    Debug.LogWarning($"[FmodSfx] music '{id}' failed to open: {path}");
                    return false;
                }

                if (core.playSound(sound, _musicGroup, true, out var channel) != FMOD.RESULT.OK)
                {
                    sound.release();
                    return false;
                }

                channel.setVolume(Mathf.Clamp01(volume));
                channel.setPaused(false);
                _musicSound = sound;
                _musicChannel = channel;
                _musicOpen = true;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[FmodSfx] music init failed: {e.Message}");
                return false;
            }
        }

        /// <summary>Играет ли что-то сейчас (доигравший трек — уже нет).</summary>
        public static bool IsMusicPlaying =>
            _musicOpen &&
            _musicChannel.isPlaying(out var playing) == FMOD.RESULT.OK &&
            playing;

        public static void SetMusicVolume(float volume)
        {
            if (_musicOpen)
            {
                _musicChannel.setVolume(Mathf.Clamp01(volume));
            }
        }

        /// <summary>Позиция и длина текущего трека в мс (-1 = нет трека) —
        /// по ним директор гасит хвост, если трек обрывается не сам.</summary>
        public static int MusicPositionMs =>
            _musicOpen && _musicChannel.getPosition(out var ms, FMOD.TIMEUNIT.MS) == FMOD.RESULT.OK
                ? (int)ms
                : -1;

        public static int MusicLengthMs =>
            _musicOpen && _musicSound.getLength(out var ms, FMOD.TIMEUNIT.MS) == FMOD.RESULT.OK
                ? (int)ms
                : -1;

        /// <summary>Стоп + освобождение стрима. Безопасно звать когда угодно,
        /// в том числе после того, как трек доиграл сам.</summary>
        public static void StopMusic()
        {
            if (!_musicOpen)
            {
                return;
            }

            // Clear the shared state before touching native FMOD. During a
            // no-domain-reload Play Mode exit RuntimeManager can already be
            // tearing its system down while MusicDirector.OnDestroy runs. A
            // stale native handle must never be retried by another director or
            // by the next Play session.
            var channel = _musicChannel;
            var sound = _musicSound;
            _musicOpen = false;
            _musicChannel = default;
            _musicSound = default;

            if (!FMODUnity.RuntimeManager.IsInitialized)
            {
                return;
            }

            if (channel.hasHandle() &&
                channel.isPlaying(out var playing) == FMOD.RESULT.OK && playing)
            {
                channel.stop();
            }

            if (sound.hasHandle())
            {
                sound.release(); // стрим держит открытый файл — отпускаем
            }
        }

        private static FMOD.VECTOR ToFmod(Vector3 v) =>
            new() { x = v.x, y = v.y, z = v.z };
    }
}
