using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Content
{

public sealed class WorldObjectState
{
    public ObjectId Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public FragmentId Fragment { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public List<JunctionId> Junctions { get; } = new();

    public bool IsOccupied { get; set; }

    public EntityId? CurrentUser { get; set; }

    public float ResourceAmount { get; set; }

    // Spec 35.5: ground items get rained on; wetness survives the
    // drop -> pickup -> dress round-trip.
    public float Wetness { get; set; }

    // Spec 35.6: durability survives the same round-trip — otherwise the
    // dress-churn loop would repair clothes for free.
    public float Durability { get; set; } = 1f;

    // Spec 31C.1: runtime spawn moment; produce rots 2400 ticks after it.
    public int SpawnTick { get; set; }

    // Spec 31C.7: exactly the junctions THIS object flipped to Blocked —
    // despawn unblocks only these, so overlapping obstacles/walls survive.
    public System.Collections.Generic.List<HexLive.Simulation.Common.JunctionId> BlockedJunctions { get; } = new();

    // Production runtime data (producers only, spec 29A)
    public int NextProductionTick { get; set; }

    public List<ObjectId> ProducedItems { get; } = new();
}

}
