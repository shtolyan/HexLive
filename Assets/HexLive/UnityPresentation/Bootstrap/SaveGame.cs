using System;
using System.IO;
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

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, "hexlive_save.dat");

        private static string NewSeedPath =>
            Path.Combine(Application.persistentDataPath, "hexlive_new_seed.txt");

        // v1 (replay-based JSON) — deleted on sight, never read.
        private static string LegacyJsonPath =>
            Path.Combine(Application.persistentDataPath, "hexlive_save.json");

        public static void Write(WorldState world, float speed)
        {
            var tempPath = FilePath + ".tmp";
            try
            {
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

                // Swap in whole — a crash mid-write must never eat the old save.
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                File.Move(tempPath, FilePath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[HexLive] Save failed: {e.Message}");
            }
        }

        // Menu/offline math only — cheap, does not touch the world blob.
        public static SaveGameData TryReadHeader()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                using var stream = File.OpenRead(FilePath);
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
                    mode = 0
                };
                if (version >= 3)
                {
                    data.mode = reader.ReadInt32();
                }
                return data.tick >= 0 ? data : null;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[HexLive] Save unreadable, starting fresh: {e.Message}");
                return null;
            }
        }

        // Spec 41.2 v2: apply the saved model onto a world freshly built from
        // the same seed. False means the world is unusable (partially
        // mutated) — the caller must bootstrap a fresh one.
        public static bool TryRestore(WorldState world)
        {
            try
            {
                using var stream = File.OpenRead(FilePath);
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
                UnityEngine.Debug.LogWarning($"[HexLive] Save restore failed: {e.Message}");
                return false;
            }
        }

        public static void Delete()
        {
            DeleteFile(FilePath);
            DeleteFile(FilePath + ".tmp");
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
                UnityEngine.Debug.LogWarning($"[HexLive] New-game seed override unreadable: {e.Message}");
                return false;
            }
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
                UnityEngine.Debug.LogWarning($"[HexLive] Save delete failed: {e.Message}");
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
    }
}
