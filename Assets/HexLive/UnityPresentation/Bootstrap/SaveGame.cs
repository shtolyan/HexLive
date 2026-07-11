using System;
using System.IO;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
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
        public int version = 2;
        public int seed;
        public int tick;
        public long unixSeconds;
        public float speed = 1f;
    }

    public static class SaveGame
    {
        // Spec 41.3: real seconds -> game ticks at 1x (tick = 0.25 s).
        public const float TicksPerRealSecond = 4f;

        // Spec 41.3: offline progression cap — 3 game days (day = 2400 ticks),
        // so a week away neither starves the colony nor stalls the load.
        public const int OfflineTicksCap = 3 * 2400;

        private const int Magic = 0x48584C56; // "HXLV"
        private const int Version = 2;

        private static string FilePath =>
            Path.Combine(Application.persistentDataPath, "hexlive_save.dat");

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
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                {
                    return null;
                }

                var data = new SaveGameData
                {
                    seed = reader.ReadInt32(),
                    tick = reader.ReadInt32(),
                    unixSeconds = reader.ReadInt64(),
                    speed = reader.ReadSingle()
                };
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
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                {
                    return false;
                }

                reader.ReadInt32(); // seed (validated inside the blob)
                reader.ReadInt32(); // tick
                reader.ReadInt64(); // unixSeconds
                reader.ReadSingle(); // speed
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

            var ticks = (long)(elapsed * TicksPerRealSecond);
            return (int)Math.Min(ticks, OfflineTicksCap);
        }
    }
}
