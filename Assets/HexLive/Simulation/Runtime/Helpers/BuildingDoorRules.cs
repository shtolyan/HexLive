using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>Saved door state and navigation only; approach and intent belong to callers.</summary>
public static class BuildingDoorRules
{
    public static bool IsDoor(WorldObjectState piece) => DoorTopology.IsDoorPiece(piece);

    public static bool IsOpen(WorldObjectState piece) => IsDoor(piece) && piece.IsDoorOpen;

    /// <summary>§129: кто вправе открыть эту дверь — см. DoorTopology.OwnerFaction.</summary>
    public static Agents.Faction OwnerFaction(WorldState world, WorldObjectState door) =>
        DoorTopology.OwnerFaction(world, door);

    public static bool TryOpen(WorldState world, ObjectId doorId) => TrySetOpen(world, doorId, true);

    public static bool TryClose(WorldState world, ObjectId doorId) => TrySetOpen(world, doorId, false);

    // §129: a swing never touches Junction.Blocked and never bumps
    // TopologyVersion — the portal stays route-passable for everyone, and only
    // the door caches (keyed on DoorStateVersion) see the change. Who may NOT
    // walk it is the router's business (DoorTopology.ForbiddenFor); that hands
    // may not reach through a closed leaf is SpatialQueries.IsBarrierFor's.
    public static bool TrySetOpen(WorldState world, ObjectId doorId, bool open)
    {
        if (world == null || !world.Entities.Objects.TryGetValue(doorId, out var door) ||
            !IsDoor(door) || door.Junctions.Count != 1)
            return false;

        var portalId = door.Junctions[0];
        if (!world.Junctions.Items.TryGetValue(portalId, out var portal)) return false;
        if (door.IsDoorOpen == open && !portal.Blocked && portal.Door) return true;
        // Закрыть можно только пустой проём: ни резервации (IsJunctionFree),
        // ни стоящего в нём тела — CurrentJunction это НЕ резервация, поэтому
        // проверяется отдельно (§129: дверь не захлопывается на человеке).
        if (!open)
        {
            if (!SpatialQueries.IsJunctionFree(world, portalId)) return false;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.CurrentJunction is { } standing && standing.Equals(portalId))
                    return false;
            }
        }

        door.IsDoorOpen = open;
        portal.Door = true;
        portal.Blocked = false;
        door.BlockedJunctions.Remove(portalId);
        world.DoorStateVersion++;
        return true;
    }
}

}
