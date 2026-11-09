using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// §54.15: the water collector's vessel slot, shared by decision, planning and
// execution. A PARKED flask or plastic bottle is an ordinary vessel world object
// anchored on the collector's own junction — same idiom as garments hung on
// the drying rack — so no new persisted state is needed: Owner remembers who
// parked it, ResourceAmount holds fill progress 0..1.
public static class WaterCollectorMath
{
    public const string CollectorId = "station.water_collector";

    // Legacy default for callers creating a plastic vessel; real transfers retain the actual item id.
    public const string VesselId = ContentIds.Bottle;

    /// Is this ground bottle PARKED in a collector's vessel slot? A parked
    /// bottle is working furniture, not a dropped tool — GatherTools must not
    /// scoop it back out (probe: park → re-pick churn, 11 parks in 4 000
    /// ticks, fill forever 0). Retrieval goes through TakeVessel only.
    public static bool IsParked(WorldState world, WorldObjectState obj)
    {
        if (!WaterVesselCatalog.IsPortable(obj.DefinitionId) || obj.Junctions.Count == 0 ||
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
                WaterVesselCatalog.IsPortable(obj.DefinitionId) &&
                obj.Junctions.Contains(slot))
            {
                return obj;
            }
        }

        return null;
    }

    /// Exact physical quantity. Rounding belongs only to drink availability.
    public static float AmountIn(WorldObjectState vessel) =>
        vessel is null || float.IsNaN(vessel.ResourceAmount) || float.IsInfinity(vessel.ResourceAmount)
            ? 0f : System.Math.Clamp(vessel.ResourceAmount, 0f, 1f) * WaterVesselCatalog.Capacity(vessel.DefinitionId);

    /// Whole available gulps; never use this value for a physical transfer.
    public static int ChargesIn(WorldObjectState vessel) =>
        (int)System.Math.Floor(AmountIn(vessel) + 1e-4f);

    /// Can this NPC draw from the parked vessel? Pouring into her OWN empty
    /// bottle is open to everyone (the parked bottle stays and keeps
    /// collecting); walking off with the bottle itself is reserved for its
    /// owner — or anyone once the owner is dead — so a housemate can't strand
    /// the placer bottleless.
    public static bool CanTake(WorldState world, NPCState npc, WorldObjectState vessel)
    {
        if (ChargesIn(vessel) < 1)
        {
            return false;
        }

        if (BottleInventoryMath.FirstEmpty(npc) is not null)
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
