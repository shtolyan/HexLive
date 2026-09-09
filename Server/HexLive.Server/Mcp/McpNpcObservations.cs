using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;

namespace HexLive.Server.Mcp;

/// <summary>§144.12: identify a visible person without exposing private world state.</summary>
internal static class McpNpcObservations
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Names = new(LoadNames);

    public static List<object> Visible(WorldState world, NPCState observer)
    {
        var visible = new SortedDictionary<int, (PerceivedAgent Agent, bool Hostile)>();
        foreach (var agent in observer.Perception.Agents)
            if (agent.CanSee) visible[agent.Id.Value] = (agent, false);
        foreach (var agent in observer.Perception.Hostiles)
            if (agent.CanSee) visible[agent.Id.Value] = (agent, true);

        var rows = new List<object>();
        foreach (var (id, entry) in visible)
        {
            if (id == observer.Id.Value ||
                !world.Entities.Npcs.TryGetValue(new EntityId(id), out var body) || body.Health <= 0f)
                continue;
            var agent = entry.Agent;
            var nameId = body.DisplayName ?? string.Empty;
            rows.Add(new Dictionary<string, object?>
            {
                ["npcId"] = id,
                ["nameId"] = nameId,
                ["names"] = LocalizedNames(nameId),
                ["faction"] = agent.Faction.ToString(),
                ["sameCamp"] = agent.Faction == observer.Faction,
                ["hostile"] = entry.Hostile,
                ["distance"] = agent.Distance,
                ["reachable"] = agent.IsReachable,
                ["busy"] = agent.IsBusy,
                ["moving"] = agent.IsMoving,
                ["unconscious"] = agent.IsUnconscious,
                ["dying"] = agent.IsDying,
                ["suffering"] = agent.Suffering,
                ["aidKind"] = agent.AidKind.ToString(),
            });
        }
        return rows;
    }

    private static Dictionary<string, string> LocalizedNames(string nameId) =>
        Names.Value.TryGetValue(nameId, out var names)
            ? names
            : new Dictionary<string, string> { ["en"] = nameId, ["ru"] = nameId };

    private static Dictionary<string, Dictionary<string, string>> LoadNames()
    {
        using var resource = typeof(McpNpcObservations).Assembly.GetManifestResourceStream("HexLive.Mcp.NpcNames.json")
            ?? throw new InvalidDataException("Missing generated I2 NPC names resource.");
        var names = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(resource)
            ?? throw new InvalidDataException("Invalid generated I2 NPC names resource.");
        return new Dictionary<string, Dictionary<string, string>>(names, StringComparer.OrdinalIgnoreCase);
    }
}
