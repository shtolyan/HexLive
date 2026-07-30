using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// §54.15: the water collector's vessel slot, shared by decision, planning and
// execution. The PARKED bottle is an ordinary world object ("tool.bottle")
// anchored on the collector's own junction — same idiom as garments hung on
// the drying rack — so no new persisted state is needed: Owner remembers who
// parked it, ResourceAmount holds fill progress 0..1.
internal static class WaterCollectorMath
{
    public const string CollectorId = "station.water_collector";

    public const string VesselId = "tool.bottle";

    /// Is this ground bottle PARKED in a collector's vessel slot? A parked
    /// bottle is working furniture, not a dropped tool — GatherTools must not
    /// scoop it back out (probe: park → re-pick churn, 11 parks in 4 000
    /// ticks, fill forever 0). Retrieval goes through TakeVessel only.
    public static bool IsParked(WorldState world, WorldObjectState obj)
    {
        if (obj.DefinitionId != VesselId || obj.Junctions.Count == 0 ||
            !world.Caches.ObjectsByTile.TryGetValue(obj.Tile, out var ids))
        {
            return false;
        }

        var slot = obj.Junctions[0];
        foreach (var id in ids)
        {
            if (world.Entities.Objects.TryGetValue(id, out var other) &&
                other.DefinitionId == CollectorId &&
                other.Junctions.Contains(slot))
            {
                return true;
            }
        }

        return false;
    }

    /// The bottle parked in this collector's slot, or null.
    public static WorldObjectState FindVessel(WorldState world, WorldObjectState collector)
    {
        if (collector.Junctions.Count == 0 ||
            !world.Caches.ObjectsByTile.TryGetValue(collector.Tile, out var ids))
        {
            return null;
        }

        var slot = collector.Junctions[0];
        foreach (var id in ids)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == VesselId &&
                obj.Junctions.Contains(slot))
            {
                return obj;
            }
        }

        return null;
    }

    /// Whole gulps the vessel's collected rain converts to on TakeVessel.
    public static int ChargesIn(WorldObjectState vessel) =>
        (int)System.Math.Floor(vessel.ResourceAmount * SimBalance.BottleCapacity + 1e-4f);

    /// Can this NPC draw from the parked vessel? Pouring into her OWN empty
    /// bottle is open to everyone (the parked bottle stays and keeps
    /// collecting); walking off with the bottle itself is reserved for its
    /// owner — or anyone once the owner is dead — so a housemate can't strand
    /// the placer bottleless.
    public static bool CanTake(WorldState world, NPCState npc, WorldObjectState vessel)
    {
        if (npc.BottleWater != WaterKind.None || ChargesIn(vessel) < 1)
        {
            return false;
        }

        if (npc.Inventory.Items.Exists(i => i.DefinitionId == VesselId))
        {
            return true; // pours over — the parked bottle is untouched
        }

        if (vessel.Owner is not { } owner || owner == npc.Id)
        {
            return true; // hers (or ownerless) — take it back
        }

        return !world.Entities.Npcs.TryGetValue(owner, out var placer) || placer.Health <= 0f;
    }
}

}
