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

    // Spec §50: a per-object variant tag the renderer reads (e.g. which limb a
    // "body.limb_severed" object is, so it bakes the matching bone chain).
    public string Variant { get; set; } = string.Empty;

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

    // Spec §52: build-site payload. What this site becomes once finished
    // (e.g. "bed.basic") and the material bill it must accumulate in Contents
    // before a hammer can raise it. Zero bill / empty product ⇒ not a site.
    public string BuildProduct { get; set; } = string.Empty;

    public int BillLogs { get; set; }

    public int BillStones { get; set; }

    public int BillLeaves { get; set; }

    // Spec §54.2: beds are lashed from sticks (rails/slats) and, for the premium
    // bedroll, a rope binding — so the furniture bill needs these two extra
    // material channels beyond the original log/stone/leaf trio.
    public int BillSticks { get; set; }

    public int BillRope { get; set; }

    // Spec §52: a garment is a container. When it is taken off (or torn), the
    // pocket items it carried ride down with it and live here on the ground
    // object — the NPC remembers (via perception) that its bottle is "in those
    // panties over there" and can fetch it without dressing. Also used as a
    // build-site's delivered-materials store and a fireside stockpile bin.
    public List<Agents.ItemInstance> Contents { get; } = new();
}

}
