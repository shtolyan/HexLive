using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Server.Mcp;

/// <summary>
/// §171.25: what a colonist knows about the island's camps (common knowledge)
/// and about the people she has met (personal experience). Reads only; never
/// creates an acquaintance, never reveals residents of a camp or whether a
/// remembered person is still alive.
/// </summary>
internal static class McpSocialObservations
{
    internal const int MaxKnownPeople = 12;

    // Sector centres counter-clockwise from +X (east); +Y is north (§171.25).
    private static readonly string[] Directions = ["E", "NE", "N", "NW", "W", "SW", "S", "SE"];

    public static List<object> Camps(WorldState world, NPCState npc)
    {
        var rows = new List<object>();
        foreach (var (camp, home) in world.FactionHomes.Where(p => FactionRelations.IsGirlCamp(p.Key)).OrderBy(p => (int)p.Key))
        {
            var own = camp == npc.Faction;
            var here = ColonyQueries.InCamp(world, npc.Tile, camp);
            rows.Add(new Dictionary<string, object?>
            {
                ["camp"] = camp.ToString(),
                ["number"] = camp == Faction.Colony ? 1 : (int)camp,
                ["own"] = own,
                ["here"] = here,
                ["direction"] = here ? string.Empty : Direction(npc.Tile, home),
                ["distanceTiles"] = HexSpatialMath.HexDistance(npc.Tile, home),
                ["stance"] = own ? "own" : FactionRelations.AreHostile(world, npc.Faction, camp) ? "hostile" : "neutral",
                ["visited"] = own || here || npc.Memory.SurveyedTiles.ContainsKey(home),
            });
        }
        return rows;
    }

    internal static string Direction(TileCoord from, TileCoord to)
    {
        if (from.Equals(to)) return string.Empty;
        var a = HexSpatialMath.TileToWorld(from);
        var b = HexSpatialMath.TileToWorld(to);
        var degrees = MathF.Atan2(b.Y - a.Y, b.X - a.X) * (180f / MathF.PI);
        var sector = (int)MathF.Round(degrees / 45f);
        return Directions[((sector % 8) + 8) % 8];
    }

    public static (List<object> Rows, int Total) KnownPeople(WorldState world, NPCState npc, IEnumerable<int> visibleIds)
    {
        var visible = visibleIds.ToHashSet();
        var ids = npc.Social.Relationships.Keys.Concat(npc.Memory.KnownAgents.Keys)
            .Where(id => id != npc.Id).Distinct().ToList();
        var hour = Math.Max(1, Spec136.HourTicks);
        var people = new List<(int Id, float Familiarity, float Affinity, int Recent, Dictionary<string, object?> Row)>();
        foreach (var id in ids)
        {
            // A name needs a body: the living roster or the corpse table. Which
            // one is NOT reported — death is known only by sight (§171.25).
            if (!world.Entities.Npcs.TryGetValue(id, out var body) && !world.Entities.Corpses.TryGetValue(id, out body))
                continue;
            npc.Social.Relationships.TryGetValue(id, out var relation);
            npc.Memory.KnownAgents.TryGetValue(id, out var met);
            var camp = met?.Faction ?? body.Faction;
            var seenTick = met?.LastSeenTick;
            var talkTick = relation is { LastInteractionTick: > 0 } ? relation.LastInteractionTick : (int?)null;
            var nameId = body.DisplayName ?? string.Empty;
            people.Add((id.Value, relation?.Familiarity ?? 0f, relation?.Affinity ?? 0f,
                Math.Max(seenTick ?? int.MinValue, talkTick ?? int.MinValue), new Dictionary<string, object?>
            {
                ["npcId"] = id.Value,
                ["nameId"] = nameId,
                ["names"] = McpNpcObservations.LocalizedNames(nameId),
                ["sex"] = McpNpcObservations.SexOf(body),
                ["camp"] = camp.ToString(),
                ["sameCamp"] = camp == npc.Faction,
                ["relationship"] = relation == null ? null : new
                {
                    trust = relation.Trust, familiarity = relation.Familiarity,
                    affinity = relation.Affinity, authority = relation.Authority,
                },
                ["hoursSinceSeen"] = seenTick is { } seen ? Math.Max(0, world.Tick - seen) / hour : null,
                ["hoursSinceTalk"] = talkTick is { } talk ? Math.Max(0, world.Tick - talk) / hour : null,
                ["visibleNow"] = visible.Contains(id.Value),
            }));
        }
        var rows = people
            .OrderByDescending(p => p.Familiarity)
            .ThenByDescending(p => MathF.Abs(p.Affinity))
            .ThenByDescending(p => p.Recent)
            .ThenBy(p => p.Id)
            .Take(MaxKnownPeople)
            .Select(p => (object)p.Row)
            .ToList();
        return (rows, people.Count);
    }
}
