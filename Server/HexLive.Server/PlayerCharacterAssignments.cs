using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Runtime;

namespace HexLive.Server
{

/// <summary>
/// §149: постоянное назначение игровых персонажей. Это НЕ активный лиз
/// §145.4: разрыв соединения возвращает NPC под ИИ, но её id остаётся за тем
/// же playerId до смерти или смены мира.
/// </summary>
public sealed class PlayerCharacterAssignments
{
    public const int CurrentVersion = 1;
    public const int DefaultCharacterLimit = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<PlayerRecord> _players;
    private int _worldGeneration;

    private PlayerCharacterAssignments(string path, List<PlayerRecord> players)
    {
        _path = Path.GetFullPath(path);
        _players = players;
    }

    /// <summary>
    /// Существующий сейв продолжает свой реестр. Свежий мир начинает с
    /// пустого — даже если рядом остался sidecar удалённого старого сейва.
    /// </summary>
    public static PlayerCharacterAssignments Load(string path, bool continueExistingWorld)
    {
        var fullPath = Path.GetFullPath(path);
        if (!continueExistingWorld || !File.Exists(fullPath))
        {
            var fresh = new PlayerCharacterAssignments(fullPath, new List<PlayerRecord>());
            if (!continueExistingWorld && File.Exists(fullPath))
            {
                fresh.Save();
            }

            return fresh;
        }

        PlayerStore? store;
        try
        {
            store = JsonSerializer.Deserialize<PlayerStore>(
                File.ReadAllText(fullPath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidDataException(
                $"Player assignment file '{fullPath}' cannot be read safely.", ex);
        }

        if (store is null || store.Version != CurrentVersion || store.Players is null)
        {
            throw new InvalidDataException(
                $"Player assignment file '{fullPath}' has an unsupported schema.");
        }

        var normalized = Normalize(store.Players);
        var result = new PlayerCharacterAssignments(fullPath, normalized);
        if (!SameRecords(store.Players, normalized))
        {
            result.Save();
        }

        return result;
    }

    /// <summary>
    /// Атомарно сохраняет живые старые назначения и добирает первые свободные
    /// id. Списковый результат — намеренный шов будущего отряда; продуктовый
    /// лимит сейчас равен <see cref="DefaultCharacterLimit"/>.
    /// </summary>
    public IReadOnlyList<int> Reconcile(
        string playerId,
        IEnumerable<int> retainableNpcIds,
        IEnumerable<int> assignableNpcIds,
        int characterLimit = DefaultCharacterLimit)
    {
        if (!TryNormalizePlayerId(playerId, out var canonicalPlayerId))
        {
            throw new ArgumentException(
                "Player id must be a canonical 32-character GUID.", nameof(playerId));
        }

        if (characterLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterLimit));
        }

        var retainable = new HashSet<int>(retainableNpcIds.Where(id => id > 0));
        var assignable = assignableNpcIds
            .Where(id => id > 0 && retainable.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        lock (_gate)
        {
            var record = FindOrAdd(canonicalPlayerId);
            var usedByOthers = new HashSet<int>();
            for (var i = 0; i < _players.Count; i++)
            {
                if (ReferenceEquals(_players[i], record))
                {
                    continue;
                }

                foreach (var npcId in _players[i].NpcIds)
                {
                    usedByOthers.Add(npcId);
                }
            }

            var next = record.NpcIds
                .Where(id => retainable.Contains(id) && !usedByOthers.Contains(id))
                .Distinct()
                .OrderBy(id => id)
                .Take(characterLimit)
                .ToList();

            var occupied = new HashSet<int>(usedByOthers);
            occupied.UnionWith(next);
            for (var i = 0; i < assignable.Length && next.Count < characterLimit; i++)
            {
                if (occupied.Add(assignable[i]))
                {
                    next.Add(assignable[i]);
                }
            }

            next.Sort();
            if (!record.NpcIds.SequenceEqual(next))
            {
                record.NpcIds = next;
                Save();
            }

            return next.ToArray();
        }
    }

    /// <summary>
    /// Снимает ростер под замком мира одним чтением: умирающая §105 всё ещё
    /// сохраняет старое назначение, но новому игроку выдаётся только здоровый
    /// кандидат. Настоящая смерть переносит NPC из Npcs в Corpses.
    /// </summary>
    public IReadOnlyList<int> Reconcile(
        WorldHost host, string playerId, int characterLimit = DefaultCharacterLimit,
        CancellationToken worldLifetime = default, int worldGeneration = 0)
    {
        var roster = host.Read(world =>
        {
            var retainable = new List<int>();
            var assignable = new List<int>();
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Faction != Faction.Colony)
                {
                    continue;
                }

                retainable.Add(npc.Id.Value);
                if (npc.Health > 0f && !npc.IsDying)
                {
                    assignable.Add(npc.Id.Value);
                }
            }

            retainable.Sort();
            assignable.Sort();
            return (retainable, assignable);
        });

        if (worldLifetime.IsCancellationRequested)
        {
            return Array.Empty<int>();
        }

        // The assignment lock serializes both the final cancellation check and
        // the generation transition. A late old-world viewer cannot roll the
        // registry back after a new-world viewer has already assigned NPCs.
        lock (_gate)
        {
            if (worldLifetime.IsCancellationRequested)
            {
                return Array.Empty<int>();
            }

            if (worldGeneration < _worldGeneration)
            {
                return Array.Empty<int>();
            }

            if (worldGeneration > _worldGeneration)
            {
                _worldGeneration = worldGeneration;
                _players.Clear();
            }

            return Reconcile(playerId, roster.retainable, roster.assignable, characterLimit);
        }
    }

    /// <summary>
    /// Новый мир: старые id обозначают уже других людей. Идемпотентность
    /// сохраняет назначения, которые новый viewer успел сделать до позднего
    /// уведомления <c>WorldSwapped</c>.
    /// </summary>
    public void SwitchWorld(int worldGeneration)
    {
        lock (_gate)
        {
            if (worldGeneration <= _worldGeneration)
            {
                return;
            }

            _worldGeneration = worldGeneration;
            if (_players.Count == 0 && !File.Exists(_path))
            {
                return;
            }

            _players.Clear();
            Save();
        }
    }

    public static bool TryNormalizePlayerId(string? value, out string canonical)
    {
        if (Guid.TryParseExact(value, "N", out var id))
        {
            canonical = id.ToString("N");
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    private PlayerRecord FindOrAdd(string playerId)
    {
        for (var i = 0; i < _players.Count; i++)
        {
            if (string.Equals(_players[i].PlayerId, playerId, StringComparison.Ordinal))
            {
                return _players[i];
            }
        }

        var record = new PlayerRecord { PlayerId = playerId };
        _players.Add(record);
        return record;
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var store = new PlayerStore
        {
            Version = CurrentVersion,
            Players = _players,
        };
        var json = JsonSerializer.Serialize(store, JsonOptions) + Environment.NewLine;
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
            TryRestrictToOwner(_path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<PlayerRecord> Normalize(IEnumerable<PlayerRecord> records)
    {
        var normalized = new List<PlayerRecord>();
        var players = new HashSet<string>(StringComparer.Ordinal);
        var npcs = new HashSet<int>();
        foreach (var source in records)
        {
            if (!TryNormalizePlayerId(source.PlayerId, out var playerId) || !players.Add(playerId))
            {
                continue;
            }

            var ids = (source.NpcIds ?? new List<int>())
                .Where(id => id > 0 && npcs.Add(id))
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            normalized.Add(new PlayerRecord { PlayerId = playerId, NpcIds = ids });
        }

        return normalized;
    }

    private static bool SameRecords(IReadOnlyList<PlayerRecord> left, IReadOnlyList<PlayerRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].PlayerId, right[i].PlayerId, StringComparison.Ordinal) ||
                !(left[i].NpcIds ?? new List<int>()).SequenceEqual(right[i].NpcIds))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryRestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Assignment ids are not credentials; failure to chmod must not
            // take the living world down after a successful durable write.
        }
    }

    private sealed class PlayerStore
    {
        public int Version { get; set; } = CurrentVersion;
        public List<PlayerRecord> Players { get; set; } = new();
    }

    private sealed class PlayerRecord
    {
        public string PlayerId { get; set; } = string.Empty;
        public List<int> NpcIds { get; set; } = new();
    }
}

/// <summary>§149.3: чистая серверная граница «все акторы команды назначены
/// этому игроку». Вынесена из WebSocket-цикла, чтобы групповая атомарность
/// проверялась обычным headless-тестом.</summary>
public static class PlayerCommandAssignment
{
    public static bool Allows(ISimulationCommand command, ISet<int> assignedNpcIds)
    {
        switch (command)
        {
            case SetGroupManualControlCommand groupManual:
                foreach (var groupActor in groupManual.Actors)
                {
                    if (!assignedNpcIds.Contains(groupActor.Value))
                    {
                        return false;
                    }
                }

                return true;

            case IGroupSimulationCommand group:
                foreach (var groupActor in group.Actors)
                {
                    if (!assignedNpcIds.Contains(groupActor.Value))
                    {
                        return false;
                    }
                }

                return true;

            default:
                return command.TargetEntity is not { } targetActor ||
                       assignedNpcIds.Contains(targetActor.Value);
        }
    }
}

}
