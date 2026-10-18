using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §64.10: where her own bed and her camp's beds stand is knowledge she has by
/// right, like the hearth seeded at birth (§27.18A r2) — not a sighting that
/// the 256-record memory cap may evict.
///
/// Измерено на сейве Сингапура (сид 629209070, тик 2 202 349): кровать,
/// поднятая из площадки, — НОВЫЙ объект, и постоянная запись о площадке на
/// неё не переезжала. Через сутки кровать вытеснялась из памяти обычным
/// порядком, планировщик сна не видел ни одной кровати и уводил Машу спать
/// на ПОЛ того самого дома, где стояла её свободная кровать: пол под крышей
/// выигрывает −100 у любого гекса (§29C.4A).
/// </summary>
internal static class HomeBedKnowledge
{
    /// <summary>A finished bed that is hers, or stands in her camp.</summary>
    internal static bool IsHomeBed(WorldState world, NPCState npc, WorldObjectState bed) =>
        ContentIds.IsBed(bed.DefinitionId) &&
        string.IsNullOrEmpty(bed.BuildProduct) &&
        (bed.Owner is { } owner && owner.Equals(npc.Id) ||
         ColonyQueries.InCamp(world, bed.Tile, npc.Faction));

    /// <summary>
    /// The site → piece handoff of <see cref="ExecutionSystem.RaiseFurnitureSite"/>:
    /// whoever knew the site knows the piece, exactly as firmly; the owner of a
    /// personal bed always does.
    /// </summary>
    internal static void HandOver(WorldState world, ObjectId siteId, WorldObjectState raised)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var knewSite = npc.Memory.KnownObjects.TryGetValue(siteId, out var site);
            if (knewSite)
            {
                npc.Memory.KnownObjects.Remove(siteId);
            }

            var permanent = knewSite && site.IsPermanent ||
                raised.Owner is { } owner && owner.Equals(npc.Id);
            if (!knewSite && !permanent)
            {
                continue;
            }

            npc.Memory.KnownObjects[raised.Id] = Record(world, raised, permanent);
            npc.Memory.Version++; // §22.7: кэш вида памяти обязан увидеть замену
        }
    }

    /// <summary>A live sighting of a home bed makes the record permanent — the
    /// self-healing path for beds whose site handoff predates §64.10.</summary>
    internal static void NoteSighting(
        WorldState world, NPCState npc, WorldObjectState obj, ObjectMemory record)
    {
        if (!record.IsPermanent && IsHomeBed(world, npc, obj))
        {
            record.IsPermanent = true;
        }
    }

    /// <summary>
    /// The sleep planner found no bed among what she perceives and remembers.
    /// Before settling for the ground she recalls the home beds the memory cap
    /// had already evicted (older saves): each becomes permanent memory again
    /// and joins this tick's perception as a remembered entry, so the ordinary
    /// candidate rules — usable, reachable, free approach — still decide.
    /// Returns true when anything was recalled.
    /// </summary>
    internal static bool RecallHomeBeds(WorldState world, NPCState npc)
    {
        List<WorldObjectState> recalled = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!IsHomeBed(world, npc, obj) || npc.Memory.KnownObjects.ContainsKey(obj.Id))
            {
                continue;
            }

            (recalled ??= new List<WorldObjectState>()).Add(obj);
        }

        if (recalled is null)
        {
            return false;
        }

        recalled.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        var from = npc.CurrentJunction;
        foreach (var bed in recalled)
        {
            npc.Memory.KnownObjects[bed.Id] = Record(world, bed, permanent: true);
            npc.Memory.Version++;
            if (!world.Content.ObjectDefinitions.TryGetValue(bed.DefinitionId, out var definition))
            {
                continue;
            }

            if (!npc.Perception.MemoryObjectPool.TryGetValue(bed.Id, out var remembered))
            {
                remembered = new PerceivedObject();
                npc.Perception.MemoryObjectPool[bed.Id] = remembered;
            }

            var anchor = bed.Junctions.Count > 0 ? bed.Junctions[0] : (JunctionId?)null;
            remembered.Id = bed.Id;
            remembered.DefinitionId = bed.DefinitionId;
            remembered.FromMemory = true;
            remembered.Tile = bed.Tile;
            remembered.Distance = HexSpatialMath.Distance(
                npc.Position, HexSpatialMath.TileToWorld(bed.Tile));
            remembered.IsReachable = from.HasValue && anchor.HasValue &&
                Connectivity.ReachableBeside(world, from.Value, anchor.Value, npc.Body.CanJump, bed);
            remembered.IsOccupied = false; // assumed free until seen (spec 27.18A)
            remembered.OccupiedBy = null;
            remembered.AvailableInteractions.Clear();
            foreach (var interaction in definition.Interactions)
            {
                remembered.AvailableInteractions.Add(interaction.Type);
            }

            npc.Perception.Objects.Add(remembered);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "HomeBedRecalled",
                    $"Obj={bed.Id.Value} Tile={bed.Tile.Q},{bed.Tile.R} Own={bed.Owner?.Equals(npc.Id) == true} " +
                    $"Reachable={remembered.IsReachable}");
            }
        }

        return true;
    }

    private static ObjectMemory Record(WorldState world, WorldObjectState obj, bool permanent) => new()
    {
        Id = obj.Id,
        DefinitionId = obj.DefinitionId,
        Tile = obj.Tile,
        Junction = obj.Junctions.Count > 0 ? obj.Junctions[0] : null,
        IsPermanent = permanent,
        LastSeenTick = world.Tick
    };
}

}
