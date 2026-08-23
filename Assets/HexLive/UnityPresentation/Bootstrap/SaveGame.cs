using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Bootstrap
{
    // Spec 41.2 v2: the save IS the model. v1 stored {seed, tick} and
    // replayed the deterministic history; that dies the moment player
    // commands or LLM decisions add non-determinism, and replay time grew
    // with total played ticks. Now the whole mutable simulation state is
    // serialized (WorldSaveSerializer); loading rebuilds static topology
    // from the seed and applies the blob, then winds offline time forward
    // (spec 41.3) from the LOADED state.
    [Serializable]
    public sealed class SaveGameData
    {
        public int version = 3;
        public int seed;
        public int tick;
        public long unixSeconds;
        public float speed = 1f;

        // §146.2: GameMode ordinal. Худер v2 писался до режимов — читается
        // как Feud (0). Живёт во ВНЕШНЕМ заголовке, потому что режим нужен
        // ДО worldgen'а: блоб применяется на уже построенный остров.
        public int mode;

        // §41.8: directory key of the local world this header came from. It is
        // catalog metadata, not part of the HXLV file and never reaches the sim.
        [NonSerialized] public string worldId;
    }

    [Serializable]
    internal sealed class SaveWorldMetadata
    {
        public string id;
        public long createdUnixSeconds;
        public long lastSavedUnixSeconds;
        public int seed;
        public int mode;
    }

    /// <summary>Cheap main-menu projection of one local world slot (§41.8).</summary>
    public sealed class SaveWorldInfo
    {
        public string Id { get; internal set; }
        public SaveGameData Header { get; internal set; }
        public long CreatedUnixSeconds { get; internal set; }
        public long LastSavedUnixSeconds { get; internal set; }
        public string PreviewPath { get; internal set; }

        public bool HasPreview => !string.IsNullOrEmpty(PreviewPath) && File.Exists(PreviewPath);
    }

    public static class SaveGame
    {
        // Spec 41.3: real seconds -> game ticks at 1x (tick = 0.25 s).
        public const float TicksPerRealSecond = 4f;

        // Spec 41.3: потолка офлайна БОЛЬШЕ НЕТ — по решению игрока мир живёт
        // ровно столько, сколько его не запускали. Прежние 7200 тиков (3 цикла
        // событий = 30 реальных минут) означали, что час отсутствия и неделя
        // дают одинаковый результат: 0.3 визуального дня.
        //
        // Единственный оставшийся ограничитель — арифметический: `tick +
        // offline` обязан остаться в int, иначе целевой тик уйдёт в минус и
        // намотка не начнётся вовсе. Реальная остановка — не число, а условие:
        // намотка прекращается, когда колония вымерла (см. LoadingScreen).
        public static int OfflineTicksCap => int.MaxValue / 2;

        private const int Magic = 0x48584C56; // "HXLV"
        // v3 (§146.2): GameMode ordinal after speed. v2 reads as Feud.
        private const int Version = 3;
        private const string SaveFileName = "world.dat";
        private const string MetadataFileName = "world.json";
        private const string PreviewFileName = "preview.jpg";
        private const string ActiveWorldPref = "HexLive.LocalWorldId";
        private const int PreviewWidth = 640;
        private const int PreviewHeight = 360;

        private static string _activeWorldId;
        // True only inside this process between CreateWorld and its first
        // successful world.dat swap. It prevents Bootstrap's header probe from
        // falling back to an older slot while the fresh one is still empty.
        private static bool _activeWorldPending;
        private static int _pendingSeed;
        private static int _pendingMode;
        private static bool _legacyMigrationChecked;

        private static string SavesRoot =>
            Path.Combine(Application.persistentDataPath, "saves");

        private static string LegacyFilePath =>
            Path.Combine(Application.persistentDataPath, "hexlive_save.dat");

        private static string NewSeedPath =>
            Path.Combine(Application.persistentDataPath, "hexlive_new_seed.txt");

        // v1 (replay-based JSON) — deleted on sight, never read.
        private static string LegacyJsonPath =>
            Path.Combine(Application.persistentDataPath, "hexlive_save.json");

        /// <summary>The selected local world, or null before the menu chooses one.</summary>
        public static string ActiveWorldId => _activeWorldId;

        private static string FilePath =>
            IsSafeWorldId(_activeWorldId)
                ? Path.Combine(SavesRoot, _activeWorldId, SaveFileName)
                : null;

        public static void Write(WorldState world, float speed)
        {
            EnsureActiveWorld(world.Seed, world.Mode);
            var filePath = FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                Debug.LogWarning("[HexLive] Save skipped: no active local world slot.");
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            var tempPath = filePath + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = File.Create(tempPath))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(world.Seed);
                    writer.Write(world.Tick);
                    writer.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    writer.Write(speed);
                    writer.Write((int)world.Mode); // §146.2 (v3)
                    WorldSaveSerializer.Write(world, writer);
                }

                ReplaceWholeFile(tempPath, filePath);
                _activeWorldPending = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save failed: {e.Message}");
                DeleteFile(tempPath);
                return;
            }

            // The model save is authoritative. Catalog decoration must never
            // turn a successful world.dat swap into a reported save failure.
            try
            {
                WriteMetadata(_activeWorldId, world.Seed, world.Mode);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save metadata failed: {e.Message}");
            }

            CapturePreview(directory);
        }

        /// <summary>
        /// Creates and selects an empty slot before a fresh world is built.
        /// Nothing is removed: New Game and Restart both preserve every older
        /// local world (§41.8).
        /// </summary>
        public static string CreateWorld(int seed, GameMode mode)
        {
            EnsureLegacyMigrated();
            Directory.CreateDirectory(SavesRoot);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var seedToken = unchecked((uint)seed).ToString("x8", CultureInfo.InvariantCulture);
            var baseId = stamp + "-" + seedToken;
            var id = baseId;
            for (var suffix = 2; Directory.Exists(Path.Combine(SavesRoot, id)); suffix++)
            {
                id = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            Directory.CreateDirectory(Path.Combine(SavesRoot, id));
            SelectWorldInternal(id);
            _activeWorldPending = true;
            _pendingSeed = seed;
            _pendingMode = (int)mode;
            try
            {
                WriteMetadata(id, seed, mode);
            }
            catch (Exception e)
            {
                // The first model save can recreate metadata. A decorative
                // JSON failure must not stop a fresh game from starting.
                Debug.LogWarning($"[HexLive] New world metadata failed: {e.Message}");
            }
            return id;
        }

        /// <summary>Selects one catalog entry for header read, restore and autosave.</summary>
        public static bool SelectWorld(string worldId)
        {
            EnsureLegacyMigrated();
            if (!IsSafeWorldId(worldId))
            {
                return false;
            }

            var path = Path.Combine(SavesRoot, worldId, SaveFileName);
            if (TryReadHeaderAt(path, worldId, logFailure: false) == null)
            {
                return false;
            }

            SelectWorldInternal(worldId);
            return true;
        }

        /// <summary>
        /// All readable local worlds, newest save first. Invalid/corrupt folders
        /// are left on disk for recovery, but are not offered as playable slots.
        /// </summary>
        public static IReadOnlyList<SaveWorldInfo> ListWorlds()
        {
            EnsureLegacyMigrated();
            var result = new List<SaveWorldInfo>();
            if (!Directory.Exists(SavesRoot))
            {
                return result;
            }

            try
            {
                foreach (var directory in Directory.GetDirectories(SavesRoot))
                {
                    var id = Path.GetFileName(directory);
                    if (!IsSafeWorldId(id))
                    {
                        continue;
                    }

                    var path = Path.Combine(directory, SaveFileName);
                    var header = TryReadHeaderAt(path, id, logFailure: false);
                    if (header == null)
                    {
                        continue;
                    }

                    var metadata = ReadMetadata(directory);
                    var fallbackSaved = ToUnixSeconds(File.GetLastWriteTimeUtc(path));
                    var actualSaved = Math.Max(header.unixSeconds, fallbackSaved);
                    result.Add(new SaveWorldInfo
                    {
                        Id = id,
                        Header = header,
                        CreatedUnixSeconds = metadata?.createdUnixSeconds > 0
                            ? metadata.createdUnixSeconds
                            : fallbackSaved,
                        LastSavedUnixSeconds = Math.Max(
                            metadata?.lastSavedUnixSeconds ?? 0, actualSaved),
                        PreviewPath = Path.Combine(directory, PreviewFileName)
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save catalog unreadable: {e.Message}");
            }

            result.Sort((a, b) => b.LastSavedUnixSeconds.CompareTo(a.LastSavedUnixSeconds));
            RestoreRememberedSelection(result);
            return result;
        }

        // Menu/offline math only — cheap, does not touch the world blob.
        public static SaveGameData TryReadHeader()
        {
            EnsureLegacyMigrated();
            EnsureRememberedActiveWorld();
            var path = FilePath;
            return string.IsNullOrEmpty(path)
                ? null
                : TryReadHeaderAt(path, _activeWorldId, logFailure: true);
        }

        // Spec 41.2 v2: apply the saved model onto a world freshly built from
        // the same seed. False means the world is unusable (partially
        // mutated) — the caller must bootstrap a fresh one.
        public static bool TryRestore(WorldState world)
        {
            var filePath = FilePath;
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                using var reader = new BinaryReader(stream);
                if (reader.ReadInt32() != Magic)
                {
                    return false;
                }

                var version = reader.ReadInt32();
                if (version != 2 && version != Version)
                {
                    return false;
                }

                reader.ReadInt32(); // seed (validated inside the blob)
                reader.ReadInt32(); // tick
                reader.ReadInt64(); // unixSeconds
                reader.ReadSingle(); // speed
                if (version >= 3)
                {
                    reader.ReadInt32(); // mode (validated inside the blob, v51)
                }
                WorldSaveSerializer.Read(world, reader);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save restore failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Compatibility helper for tools that explicitly discard the selected
        /// slot. Normal New Game/Restart paths never call this (§41.8).
        /// </summary>
        public static void Delete()
        {
            var filePath = FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                DeleteFile(filePath);
                DeleteFile(filePath + ".tmp");
            }
            DeleteFile(LegacyJsonPath);
        }

        public static bool TryConsumeNewGameSeed(out int seed)
        {
            seed = 0;
            try
            {
                if (!File.Exists(NewSeedPath))
                {
                    return false;
                }

                var text = File.ReadAllText(NewSeedPath).Trim();
                DeleteFile(NewSeedPath);
                return int.TryParse(text, out seed);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] New-game seed override unreadable: {e.Message}");
                return false;
            }
        }

        // Spec 41.3: how many ticks the world lived while the game was closed.
        public static int OfflineTicks(SaveGameData data)
        {
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - data.unixSeconds;
            if (elapsed <= 0)
            {
                return 0;
            }

            // Потолка нет — но целевой тик у вызывающего это `data.tick +
            // результат`, и он обязан остаться положительным int, иначе намотка
            // не начнётся вообще. Так что вычитаем уже прожитое.
            var ticks = (long)(elapsed * TicksPerRealSecond);
            var headroom = Math.Max(0, OfflineTicksCap - Math.Max(0, data.tick));
            return (int)Math.Min(ticks, headroom);
        }

        private static SaveGameData TryReadHeaderAt(string path, string worldId, bool logFailure)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (reader.ReadInt32() != Magic)
                {
                    return null;
                }

                var version = reader.ReadInt32();
                if (version != 2 && version != Version)
                {
                    return null;
                }

                var data = new SaveGameData
                {
                    version = version,
                    seed = reader.ReadInt32(),
                    tick = reader.ReadInt32(),
                    unixSeconds = reader.ReadInt64(),
                    speed = reader.ReadSingle(),
                    mode = 0,
                    worldId = worldId
                };
                if (version >= 3)
                {
                    data.mode = reader.ReadInt32();
                }
                return data.tick >= 0 ? data : null;
            }
            catch (Exception e)
            {
                if (logFailure)
                {
                    Debug.LogWarning($"[HexLive] Save unreadable: {e.Message}");
                }
                return null;
            }
        }

        private static void EnsureActiveWorld(int seed, GameMode mode)
        {
            EnsureLegacyMigrated();
            if (IsSafeWorldId(_activeWorldId))
            {
                if (_activeWorldPending &&
                    _pendingSeed == seed && _pendingMode == (int)mode)
                {
                    return;
                }

                var filePath = FilePath;
                var header = TryReadHeaderAt(filePath, _activeWorldId, logFailure: false);
                if (header != null && header.seed == seed && header.mode == (int)mode)
                {
                    return;
                }

                // A just-created slot has metadata but no world.dat until the
                // first autosave. It is safe only for the exact world identity.
                if (!File.Exists(filePath))
                {
                    var metadata = ReadMetadata(Path.GetDirectoryName(filePath));
                    if (metadata != null && metadata.seed == seed && metadata.mode == (int)mode)
                    {
                        return;
                    }
                }
            }

            // A direct fresh-world bootstrap (or a safety fallback) must not
            // overwrite whichever older slot happened to be remembered.
            CreateWorld(seed, mode);
        }

        private static void EnsureRememberedActiveWorld()
        {
            if (IsSafeWorldId(_activeWorldId) && _activeWorldPending)
            {
                return;
            }

            if (IsSafeWorldId(_activeWorldId) && File.Exists(FilePath))
            {
                return;
            }

            var remembered = PlayerPrefs.GetString(ActiveWorldPref, string.Empty);
            if (IsSafeWorldId(remembered) &&
                File.Exists(Path.Combine(SavesRoot, remembered, SaveFileName)))
            {
                _activeWorldId = remembered;
                return;
            }

            var worlds = ListWorlds();
            if (worlds.Count > 0)
            {
                SelectWorldInternal(worlds[0].Id);
            }
        }

        private static void RestoreRememberedSelection(IReadOnlyList<SaveWorldInfo> worlds)
        {
            if (IsSafeWorldId(_activeWorldId))
            {
                return;
            }

            var remembered = PlayerPrefs.GetString(ActiveWorldPref, string.Empty);
            for (var i = 0; i < worlds.Count; i++)
            {
                if (worlds[i].Id == remembered)
                {
                    _activeWorldId = remembered;
                    return;
                }
            }

            if (worlds.Count > 0)
            {
                SelectWorldInternal(worlds[0].Id);
            }
        }

        private static void SelectWorldInternal(string worldId)
        {
            _activeWorldId = worldId;
            _activeWorldPending = false;
            PlayerPrefs.SetString(ActiveWorldPref, worldId);
            PlayerPrefs.Save();
        }

        private static void EnsureLegacyMigrated()
        {
            if (_legacyMigrationChecked)
            {
                return;
            }

            _legacyMigrationChecked = true;
            if (!File.Exists(LegacyFilePath))
            {
                return;
            }

            var header = TryReadHeaderAt(LegacyFilePath, null, logFailure: true);
            if (header == null)
            {
                // Never destroy an unreadable legacy file. It remains beside
                // the catalog for manual recovery.
                return;
            }

            try
            {
                Directory.CreateDirectory(SavesRoot);
                var seedToken = unchecked((uint)header.seed).ToString("x8", CultureInfo.InvariantCulture);
                var baseId = "legacy-" + seedToken;
                var id = baseId;
                for (var suffix = 2; Directory.Exists(Path.Combine(SavesRoot, id)); suffix++)
                {
                    id = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                }

                var directory = Path.Combine(SavesRoot, id);
                Directory.CreateDirectory(directory);
                File.Move(LegacyFilePath, Path.Combine(directory, SaveFileName));
                SelectWorldInternal(id);
                WriteMetadata(id, header.seed, (GameMode)header.mode,
                    createdUnixSeconds: header.unixSeconds,
                    lastSavedUnixSeconds: header.unixSeconds);
                Debug.Log($"[HexLive] Migrated legacy local save into world slot '{id}'.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Legacy save migration failed: {e.Message}");
            }
        }

        private static void WriteMetadata(
            string id, int seed, GameMode mode,
            long createdUnixSeconds = 0, long lastSavedUnixSeconds = 0)
        {
            if (!IsSafeWorldId(id))
            {
                return;
            }

            var directory = Path.Combine(SavesRoot, id);
            Directory.CreateDirectory(directory);
            var existing = ReadMetadata(directory);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var metadata = new SaveWorldMetadata
            {
                id = id,
                createdUnixSeconds = existing?.createdUnixSeconds > 0
                    ? existing.createdUnixSeconds
                    : createdUnixSeconds > 0 ? createdUnixSeconds : now,
                lastSavedUnixSeconds = lastSavedUnixSeconds > 0 ? lastSavedUnixSeconds : now,
                seed = seed,
                mode = (int)mode
            };

            var path = Path.Combine(directory, MetadataFileName);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonUtility.ToJson(metadata, true));
            ReplaceWholeFile(tempPath, path);
        }

        private static SaveWorldMetadata ReadMetadata(string directory)
        {
            try
            {
                var path = Path.Combine(directory, MetadataFileName);
                return File.Exists(path)
                    ? JsonUtility.FromJson<SaveWorldMetadata>(File.ReadAllText(path))
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CapturePreview(string directory)
        {
            var camera = Camera.main;
            if (camera == null || !camera.isActiveAndEnabled)
            {
                return;
            }

            RenderTexture target = null;
            Texture2D image = null;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                target = RenderTexture.GetTemporary(
                    PreviewWidth, PreviewHeight, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;

                image = new Texture2D(PreviewWidth, PreviewHeight, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, PreviewWidth, PreviewHeight), 0, 0);
                image.Apply(false, false);

                var path = Path.Combine(directory, PreviewFileName);
                var tempPath = path + ".tmp";
                File.WriteAllBytes(tempPath, image.EncodeToJPG(84));
                ReplaceWholeFile(tempPath, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save preview failed: {e.Message}");
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (target != null)
                {
                    RenderTexture.ReleaseTemporary(target);
                }
                if (image != null)
                {
                    UnityEngine.Object.Destroy(image);
                }
            }
        }

        private static void ReplaceWholeFile(string tempPath, string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                    // Some Unity targets do not implement File.Replace.
                }
                catch (IOException)
                {
                    // Fall through to the portable rename path.
                }

                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private static bool IsSafeWorldId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 80)
            {
                return false;
            }

            for (var i = 0; i < id.Length; i++)
            {
                var c = id[i];
                if ((c < 'a' || c > 'z') &&
                    (c < '0' || c > '9') &&
                    c != '-' && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static long ToUnixSeconds(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
            {
                utc = utc.ToUniversalTime();
            }
            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        private static void DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] Save delete failed: {e.Message}");
            }
        }
    }
}
