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

    // Spec §66: the yaw a BUILT piece stands at (degrees CCW from +X — the same
    // sim-angle convention as NPCState.RotationDegrees; the presentation maps it
    // to a Unity yaw). Set when the build-site is staked and carried onto the
    // raised piece, so a bed can lie side-on to the fire. EXACTLY 0 means "no
    // facing assigned" — loose items, natural props, and every object staked
    // before §66, which the view leaves unrotated so old worlds are untouched
    // (StructurePlacement wraps a real facing into (0, 360], never 0).
    public float RotationDegrees { get; set; }

    public bool IsOccupied { get; set; }

    public EntityId? CurrentUser { get; set; }

    // Spec §64: persistent ownership — the colonist a personal bed belongs to
    // (stamped on the bed's build-site, carried onto the finished bed when it is
    // raised). Distinct from the transient CurrentUser/IsOccupied, which only
    // mark who is using it RIGHT NOW. null = shared/unowned. DreamSystem frees
    // this back to null when the owner dies so a survivor can claim the bed.
    public EntityId? Owner { get; set; }

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

    public float Dirtiness { get; set; }

    public float Bloodiness { get; set; }

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

    // §119: boards are a first-class staged construction resource. The
    // workbench's six board_* children are revealed from this exact channel.
    public int BillBoards { get; set; }

    /// <summary>
    /// §54.19: с какого тика ТЕКУЩУЮ стадию этой стройки нечем закрыть —
    /// материала, которого ей не хватает, в мире не нашлось ни у кого.
    /// <para>
    /// Ставится и снимается в <c>DecisionSystem.FindBuildSite</c> и служит
    /// одному: не давать мёртвой стройке держать единственный слот очереди
    /// и морить голодом всё, что стоит за ней. <c>null</c> = «закрыть есть чем
    /// (или ещё не смотрели)».
    /// </para>
    /// <para>
    /// НЕ сохраняется и НЕ ездит по проводу — это наблюдение, а не факт мира:
    /// после загрузки колония просто смотрит заново и за
    /// <c>SimBalance.BuildSiteUnstockableSkipTicks</c> приходит к тому же
    /// выводу. Решения считаются только на сервере, клиенту знать нечего.
    /// </para>
    /// </summary>
    public int? UnstockableSinceTick { get; set; }

    // §120 constructor layer. A real constructor piece is a top-level
    // WorldObject whose ArchitectureOwnerId points at the footprint aggregate
    // and whose ArchitectureElements list contains exactly one component. The
    // list remains for v32-v33 save migration only: those versions incorrectly
    // stored every component on the building owner and consequently made the
    // whole hut one selectable object.
    public ObjectId? ArchitectureOwnerId { get; set; }

    /// <summary>
    /// §120.8: какой чертёж строит эта площадка/здание с продуктом
    /// <c>building.hut_plan</c>. 0 — встроенный committed-план; иначе ключ в
    /// <c>WorldState.PlayerBlueprints</c>. Инстансные данные (сейв v50);
    /// по проводу не едет — клиент рендерит модульные объекты.
    /// </summary>
    public int BlueprintId { get; set; }

    public List<ArchitectureElementState> ArchitectureElements { get; } = new();

    public bool IsArchitectureElement => ArchitectureOwnerId.HasValue &&
        ArchitectureElements.Count == 1;

    // §120.2: persistent state of an architectural door LEGO piece.
    public bool IsDoorOpen { get; set; } = true;

    // §119: one immutable work position chosen when a workbench site is staked.
    // It rides onto the finished station and is the only legal craft approach.
    public JunctionId? CraftJunction { get; set; }

    // §119: an item craft is the output object itself, present in the world at
    // zero progress. Ingredients live in Contents until completion/cancellation.
    public int CraftWorkRequired { get; set; }

    public int CraftWorkDone { get; set; }

    public int CraftBatchCount { get; set; } = 1;

    public ObjectId? CraftStationObjectId { get; set; }

    public bool IsCraftProject => CraftWorkRequired > 0 && CraftWorkDone < CraftWorkRequired;

    // Spec §52: a garment is a container. When it is taken off (or torn), the
    // pocket items it carried ride down with it and live here on the ground
    // object — the NPC remembers (via perception) that its bottle is "in those
    // panties over there" and can fetch it without dressing. Also used as a
    // build-site's delivered-materials store and a fireside stockpile bin.
    public List<Agents.ItemInstance> Contents { get; } = new();
}

}
