#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.History
{
    public readonly struct GameHistoryRecord
    {
        public GameHistoryRecord(int tick, int? entityId, string type, string message)
        {
            Tick = tick;
            EntityId = entityId;
            Type = type ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public int Tick { get; }
        public int? EntityId { get; }
        public string Type { get; }
        public string Message { get; }
        public bool IsValid => !string.IsNullOrEmpty(Type);

        public static GameHistoryRecord FromEvent(SimulationEvent simulationEvent) =>
            new(simulationEvent.Tick, simulationEvent.EntityId,
                simulationEvent.Type, simulationEvent.Message);

        public string Serialize()
        {
            var entity = EntityId.HasValue
                ? EntityId.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
            var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(Message));
            return string.Join("\t",
                Tick.ToString(CultureInfo.InvariantCulture),
                entity,
                Type,
                message);
        }

        public static bool TryParse(string line, out GameHistoryRecord record)
        {
            record = default;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var parts = line.Split('\t');
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tick))
            {
                return false;
            }

            int? entityId = null;
            if (parts[1] != "-" &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEntity))
            {
                entityId = parsedEntity;
            }

            string message;
            try
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            }
            catch
            {
                message = string.Empty;
            }

            record = new GameHistoryRecord(tick, entityId, parts[2], message);
            return record.IsValid;
        }
    }

    /// <summary>
    /// Player-facing history sink. The simulation trace buffer stays bounded and
    /// technical; this file-backed log keeps only important story events and the
    /// UI reads small tails/pages from disk on demand.
    /// </summary>
    public sealed class GameHistoryLog : IDisposable
    {
        public const int DefaultTailEntries = 120;

        private const int FlushEveryRecords = 16;
        private const float FlushEverySeconds = 3f;
        private const int InitialTailBytes = 16 * 1024;

        private static readonly HashSet<string> HistoryEventTypes = new()
        {
            "Aided",
            "AidStarted",
            "AidRequested",
            "AidWaitTimeout",
            "Bandaged",
            "BandageCrafted",
            "BedCrafted",
            "BledOut",
            "BottleFilled",
            "BoulderBroken",
            "BuildProgress",
            "Buried",
            "Butchered",
            "CoconutDrank",
            "CoconutEaten",
            "CoconutProcessed",
            "CraftedArrows",
            "CraftedAxe",
            "CraftedBow",
            "CraftedCloth",
            "CraftedKnife",
            "CraftedLeather",
            "CraftedPickaxe",
            "CraftedRope",
            "CraftedSpear",
            "CrownChopped",
            "DireStraits",
            "DogAggro",
            "DogFight",
            "DogKilled",
            "DogShot",
            "Collapsed",
            "DrankBottle",
            "EmergencyUnload",
            "Fainted",
            "FireFueled",
            "FireLit",
            "FireOut",
            "FoodShared",
            "FoodStolen",
            "FriendGuard",
            "FurnitureBuilt",
            "Grieving",
            "HelpCry",
            "HelpCryAnswered",
            "HelpCryAssistArrived",
            "HelpCryAssistLost",
            "HelpCryAssistStarted",
            "HelpCryDefended",
            "HelpCryIgnored",
            "HutCompleted",
            "InteractionBlocked",
            "InteractionRejected",
            "LimbSevered",
            "LogSplit",
            "MeatCooked",
            "Medicated",
            "Mourned",
            "Murdered",
            "NightRaid",
            "NpcDied",
            "PredatorKilled",
            "PreyFled",
            "PreyFoughtBack",
            "Preyed",
            "RackCrafted",
            "RaftLaunched",
            "RaftProgress",
            "RainStarted",
            "RainStopped",
            "RelationshipChanged",
            "SharkBite",
            "StatusDehydrated",
            "StatusOverheated",
            "StatusStarving",
            "StarvedToDeath",
            "StormSurge",
            "Sunburn",
            "TalkCompleted",
            "TalkQuarreled",
            "TalkRequested",
            "TalkStarted",
            "TalkWaitTimeout",
            "TentCrafted",
            "TreeChopped",
            "VisitedGrave",
            "VitalPartDestroyed",
            "WokeUp"
        };

        private StreamWriter? _writer;
        private string _path = string.Empty;
        private int _pendingFlushes;
        private float _lastFlushTime;

        public string CurrentPath => _path;

        public static bool IsGameHistoryEvent(SimulationEvent simulationEvent) =>
            HistoryEventTypes.Contains(simulationEvent.Type) ||
            simulationEvent.Type.StartsWith("Crafted", StringComparison.Ordinal);

        public static string PathForSeed(int seed) =>
            Path.Combine(Application.persistentDataPath,
                $"hexlive_history_{seed.ToString(CultureInfo.InvariantCulture)}.tsv");

        public void OpenForWorld(int seed, bool preserveExisting)
        {
            Dispose();
            _path = PathForSeed(seed);
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!preserveExisting && File.Exists(_path))
                {
                    File.Delete(_path);
                }

                var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
                _pendingFlushes = 0;
                _lastFlushTime = Time.unscaledTime;
            }
            catch (Exception e)
            {
                _writer = null;
                Debug.LogWarning($"[HexLive] History open failed: {e.Message}");
            }
        }

        public bool Record(SimulationEvent simulationEvent)
        {
            if (_writer == null || !IsGameHistoryEvent(simulationEvent))
            {
                return false;
            }

            _writer.WriteLine(GameHistoryRecord.FromEvent(simulationEvent).Serialize());
            _pendingFlushes++;
            if (_pendingFlushes >= FlushEveryRecords)
            {
                Flush();
            }

            return true;
        }

        public void Tick()
        {
            if (_writer == null || _pendingFlushes <= 0)
            {
                return;
            }

            if (Time.unscaledTime - _lastFlushTime >= FlushEverySeconds)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_writer == null)
            {
                return;
            }

            _writer.Flush();
            _pendingFlushes = 0;
            _lastFlushTime = Time.unscaledTime;
        }

        public void Dispose()
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                Flush();
            }
            finally
            {
                _writer.Dispose();
                _writer = null;
            }
        }

        public static List<GameHistoryRecord> ReadTail(int seed, int maxRecords)
        {
            var path = PathForSeed(seed);
            if (!File.Exists(path) || maxRecords <= 0)
            {
                return new List<GameHistoryRecord>();
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = stream.Length;
                var window = Math.Min(length, InitialTailBytes);
                string text;

                while (true)
                {
                    var start = Math.Max(0L, length - window);
                    stream.Seek(start, SeekOrigin.Begin);
                    var bytes = new byte[(int)(length - start)];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    text = Encoding.UTF8.GetString(bytes, 0, read);
                    var lineCount = CountLines(text);
                    if (start == 0 || lineCount >= maxRecords + 1)
                    {
                        if (start > 0)
                        {
                            var firstBreak = text.IndexOf('\n');
                            text = firstBreak >= 0 ? text.Substring(firstBreak + 1) : string.Empty;
                        }

                        break;
                    }

                    window = Math.Min(length, window * 2);
                }

                var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var first = Math.Max(0, lines.Length - maxRecords);
                var records = new List<GameHistoryRecord>(Math.Min(maxRecords, lines.Length));
                for (var i = first; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (GameHistoryRecord.TryParse(line, out var record))
                    {
                        records.Add(record);
                    }
                }

                return records;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HexLive] History read failed: {e.Message}");
                return new List<GameHistoryRecord>();
            }
        }

        private static int CountLines(string text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }
    }
}
