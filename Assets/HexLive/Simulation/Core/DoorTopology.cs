using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Core
{

// §129: derived door caches. A closed door is BEHAVIOUR, not topology — the
// portal junction is never Blocked, so none of the connectivity caches care
// about a swing. What does care:
//
//   - MovementSystem: "is my next step a closed portal, and whose door is it";
//   - the router: "which portals may THIS faction never step on" (hardAvoid);
//   - SpatialQueries.IsBarrierFor: "may hands reach through this junction".
//
// All three read the caches below. DoorByPortal changes only with build/
// demolition (TopologyVersion); the closed set and the per-faction bans also
// change on every swing (DoorStateVersion) — hence the two-key pair.
public static class DoorTopology
{
    public const string DoorDefinitionId = "architecture.door.wood";

    public static bool IsDoorPiece(WorldObjectState piece) => piece != null &&
        piece.DefinitionId == DoorDefinitionId && piece.IsArchitectureElement;

    // §129: who may open this door. Nothing but the colony can produce a door
    // today (hut build §120 and the bootstrap hut are both Colony), so this is
    // a constant — and the SINGLE place to swap in a persisted field when
    // outsider construction lands. All hostility decisions downstream go
    // through FactionRelations, never a hand-rolled comparison.
    public static Faction OwnerFaction(WorldState world, WorldObjectState door) =>
        Faction.Colony;

    /// <summary>Портал закрытой двери? (пустой кэш ⇒ всегда false)</summary>
    public static bool IsClosedDoorPortal(WorldState world, JunctionId junctionId)
    {
        EnsureDoorCaches(world);
        return world.Caches.ClosedDoorPortals.Contains(junctionId);
    }

    /// <summary>Дверь, чей портал стоит на этом узле (независимо от створки).</summary>
    public static bool TryGetDoorAt(
        WorldState world, JunctionId junctionId, out WorldObjectState door)
    {
        EnsureDoorCaches(world);
        door = null;
        return world.Caches.DoorByPortal.TryGetValue(junctionId, out var doorId) &&
               world.Entities.Objects.TryGetValue(doorId, out door);
    }

    /// <summary>
    /// §129: узлы, на которые фракция F не имеет права ступать — закрытые
    /// порталы враждебных ей дверей. NULL при пустом наборе: колония в мире
    /// без враждебных дверей не платит ни аллокацией, ни лишней веткой в
    /// роутере (путь остаётся бит-в-бит как до §129).
    /// </summary>
    public static HashSet<JunctionId> ForbiddenFor(WorldState world, Faction faction)
    {
        EnsureDoorCaches(world);
        return world.Caches.FactionForbiddenJunctions.TryGetValue(faction, out var set) &&
               set.Count > 0
            ? set
            : null;
    }

    private static readonly Faction[] AllFactions =
        {
            Faction.Colony, Faction.Outsiders, Faction.Colony2, Faction.Colony3,
            Faction.Colony4, Faction.Colony5, Faction.Colony6, Faction.Castaway
        };

    private static void EnsureDoorCaches(WorldState world)
    {
        var caches = world.Caches;
        if (caches.DoorByPortalBuiltVersion != world.TopologyVersion)
        {
            caches.DoorByPortal.Clear();
            foreach (var worldObject in world.Entities.Objects.Values)
            {
                // One door = one navigation throat (BuildingDoorRules keeps the
                // same invariant); a malformed multi-portal door is not indexed.
                if (IsDoorPiece(worldObject) && worldObject.Junctions.Count == 1)
                {
                    caches.DoorByPortal[worldObject.Junctions[0]] = worldObject.Id;
                }
            }

            caches.DoorByPortalBuiltVersion = world.TopologyVersion;
            // The state layer below derives from this map — force its rebuild.
            caches.DoorStateBuiltTopologyVersion = 0;
        }

        if (caches.DoorStateBuiltTopologyVersion == world.TopologyVersion &&
            caches.DoorStateBuiltDoorVersion == world.DoorStateVersion)
        {
            return;
        }

        caches.ClosedDoorPortals.Clear();
        caches.FactionForbiddenJunctions.Clear();
        foreach (var pair in caches.DoorByPortal)
        {
            if (!world.Entities.Objects.TryGetValue(pair.Value, out var door) ||
                door.IsDoorOpen)
            {
                continue;
            }

            caches.ClosedDoorPortals.Add(pair.Key);
            var owner = OwnerFaction(world, door);
            foreach (var faction in AllFactions)
            {
                if (!FactionRelations.AreHostile(world, faction, owner))
                {
                    continue;
                }

                if (!caches.FactionForbiddenJunctions.TryGetValue(faction, out var set))
                {
                    set = new HashSet<JunctionId>();
                    caches.FactionForbiddenJunctions[faction] = set;
                }

                set.Add(pair.Key);
            }
        }

        caches.DoorStateBuiltTopologyVersion = world.TopologyVersion;
        caches.DoorStateBuiltDoorVersion = world.DoorStateVersion;
    }
}

}
