using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Server.Mcp;

/// <summary>§144: bounded retrieval of this body's own last-known objects.</summary>
internal static class McpKnownObjectObservations
{
    internal const int DefaultLimit = 16;
    internal const int MaximumLimit = 64;

    internal sealed record Query(string DefinitionPrefix, InteractionType? Interaction, int Limit);
    internal sealed record Tile(int q, int r);
    internal sealed record Position(float x, float y);
    internal sealed record Row(int objectId, string definitionId, Tile lastKnownTile,
        Position lastKnownTileCenter, int? lastKnownJunction, int lastSeenTick, long ageTicks,
        string provenance, int distanceTiles, string[] catalogInteractions);
    internal sealed record View(int npcId, string worldId, int tick, string source, IReadOnlyList<Row> objects,
        int totalMatches, bool truncated);

    public static View Read(WorldState world, NPCState npc, Query query, string worldId = "")
    {
        if (query.Limit is < 1 or > MaximumLimit) throw new ArgumentOutOfRangeException(nameof(query));
        // Request-owned O(limit) storage. No persistent cache, full-memory copy,
        // world-object lookup or mutation of the existing TTL/cap owner.
        var selected = new ObjectMemory[query.Limit];
        var distances = new int[query.Limit];
        var count = 0;
        var matched = 0;
        foreach (var remembered in npc.Memory.KnownObjects.Values)
        {
            var age = Math.Max(0L, (long)world.Tick - remembered.LastSeenTick);
            if (!remembered.IsPermanent && age > AiBalance.MemoryTtlTicks) continue;
            if (!remembered.DefinitionId.StartsWith(query.DefinitionPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!world.Content.ObjectDefinitions.TryGetValue(remembered.DefinitionId, out var definition)) continue;
            if (query.Interaction.HasValue)
            {
                var hasInteraction = false;
                foreach (var interaction in definition.Interactions)
                    if (interaction.Type == query.Interaction.Value) { hasInteraction = true; break; }
                if (!hasInteraction) continue;
            }
            matched++;
            var distance = HexSpatialMath.HexDistance(npc.Tile, remembered.Tile);
            var insert = count;
            while (insert > 0 && (distances[insert - 1] > distance ||
                   distances[insert - 1] == distance && selected[insert - 1].Id.Value > remembered.Id.Value)) insert--;
            if (insert >= query.Limit) continue;
            var last = Math.Min(count, query.Limit - 1);
            for (var move = last; move > insert; move--)
            { selected[move] = selected[move - 1]; distances[move] = distances[move - 1]; }
            selected[insert] = remembered;
            distances[insert] = distance;
            if (count < query.Limit) count++;
        }
        var rows = new List<Row>(count);
        for (var index = 0; index < count; index++)
        {
            var remembered = selected[index];
            var definition = world.Content.ObjectDefinitions[remembered.DefinitionId];
            var verbs = new List<string>();
            foreach (var interaction in definition.Interactions)
            {
                var name = interaction.Type.ToString();
                if (!verbs.Contains(name)) verbs.Add(name);
            }
            var position = HexSpatialMath.TileToWorld(remembered.Tile);
            rows.Add(new Row(remembered.Id.Value, remembered.DefinitionId,
                new Tile(remembered.Tile.Q, remembered.Tile.R), new Position(position.X, position.Y),
                remembered.Junction?.Value, remembered.LastSeenTick,
                Math.Max(0L, (long)world.Tick - remembered.LastSeenTick),
                remembered.IsPermanent ? "homeKnowledge" : "observedMemory", distances[index], verbs.ToArray()));
        }
        return new View(npc.Id.Value, worldId, world.Tick, "personalMemory", rows, matched, matched > count);
    }
}
