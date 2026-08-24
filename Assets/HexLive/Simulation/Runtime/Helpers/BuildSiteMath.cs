using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;
using HexLive.Simulation.Runtime.Blueprints;

namespace HexLive.Simulation.Runtime
{

// Spec §52: material accounting for a furniture build-site. Materials hauled
// in live in the object's Contents; the bill lives in Bill* fields; a hammer
// raises it once the bill is met. Product/bill are per-instance so one def
// ("build.site") covers every furniture type.
internal static class BuildSiteMath
{
    public const string MaterialLogs = "resource.log"; // spec §54: builds are log-framed
    public const string MaterialStones = "resource.stone";
    public const string MaterialLeaves = "resource.palm_leaf";
    public const string MaterialSticks = "resource.stick"; // spec §54.2: bed rails/slats
    public const string MaterialRope = "resource.rope";    // spec §54.2: bedroll binding
    public const string MaterialBoards = ContentIds.Board;  // §119: workbench top + diagonal braces

    // Every material a furniture site can bill for — iterate this instead of a
    // hardcoded trio so deposit/read-back cover sticks and rope too.
    public static readonly string[] AllMaterials =
    {
        MaterialLogs, MaterialStones, MaterialLeaves, MaterialSticks, MaterialRope, MaterialBoards
    };

    /// <summary>
    /// §120: a site whose product is raised as INDEPENDENT MODULES (the
    /// BuildingRules grammar) rather than as ordered furniture stages. The
    /// canonical hut and any committed player plan are both such products.
    /// </summary>
    public static bool IsArchitecturalBuilding(string buildProduct) =>
        buildProduct == ContentIds.Hut1Hex || buildProduct == ContentIds.HutPlan;

    public static bool IsFreeArchitectureSite(WorldObjectState site) =>
        FreeArchitectureRules.IsFreePiece(site) && !string.IsNullOrEmpty(site.BuildProduct);

    /// <summary>Authored construction method shared by bidding and execution.
    /// A missing/unknown product is conservative and still requires a hammer.</summary>
    public static bool NeedsHammer(WorldState world, WorldObjectState site) =>
        string.IsNullOrEmpty(site.BuildProduct) ||
        !world.Content.ObjectDefinitions.TryGetValue(
            site.BuildProduct, out var definition) ||
        !definition.HasTag(ObjectTags.HandBuilt);

    public static int Delivered(WorldObjectState site, string materialId)
    {
        var n = 0;
        foreach (var item in site.Contents)
        {
            // §151: палка в трёхслотовом запасе костра — топливо, а не
            // доставленная стойка вертела. Маркер живёт на экземпляре и
            // переживает save/load вместе с остальными полями ItemInstance.
            if (item.DefinitionId == materialId &&
                !ContainerLootMath.IsQueuedCampfireFuel(site, item))
            {
                n++;
            }
        }

        return n;
    }

    // The one canonical bed is built in four visible stages.
    private static readonly (string Material, int Count)[] BedBasicStages =
    {
        (MaterialLogs, 4),    // stage 1: the side rails
        (MaterialSticks, 5),  // stage 2: the slats
        (MaterialRope, 10),   // stage 3: the lashing
        (MaterialLeaves, 50)  // stage 4: the mattress
    };

    // §35.5B: drying_rack_final prefab groups "1".."3" — two uprights planted
    // in the ground, two rails across them, four rope lashings at the joints.
    private static readonly (string Material, int Count)[] DryingRackStages =
    {
        (MaterialSticks, 2),  // stage 1: the planted uprights
        (MaterialSticks, 2),  // stage 2: the rails
        (MaterialRope, 4)     // stage 3: the lashings
    };

    // §54.14: campfire_final prefab groups "1".."5" — the stick pile is a
    // WORKING fire on its own (the site raises into a cold campfire the moment
    // stage 1 lands, see ApplyFurnitureSite); the spit and the ring are
    // upgrades delivered to the live fire and finished in place.
    // §54.17 (r3): the SPIT comes before the stone ring. The old order put the
    // 18-stone ring at stage 2, and since Remaining() only exposes the current
    // stage's shortfall, cooking waited on a ring that soaks showed almost
    // never finishes (§63.4: not once in ~500 seed-days) — so MeatRoasted
    // could not happen at all. Now the spit costs a working fire + 3 sticks +
    // 2 rope (days, not weeks) and the ring stays the long-tail fuel-economy
    // upgrade it thematically is.
    public const int CampfireStage1Sticks = 9;
    private static readonly (string Material, int Count)[] CampfireStages =
    {
        (MaterialSticks, CampfireStage1Sticks), // stage 1: the stick pile (usable fire)
        (MaterialSticks, 2),  // stage 2: the two planted forked posts
        (MaterialSticks, 1),  // stage 3: the crossbar
        (MaterialRope, 2),    // stage 4: the lashings
        (MaterialStones, 18)  // stage 5: the dense stone ring
    };

    // §54.15: water_collector_final prefab groups "1".."5" — the frame is
    // planted first, the stand goes in under it, then the rim closes the
    // frame, the corners are lashed, and the leaf funnel is laid in last
    // (the funnel is what actually catches rain, so it finishes the piece).
    private static readonly (string Material, int Count)[] WaterCollectorStages =
    {
        (MaterialSticks, 4),  // stage 1: the four planted uprights
        (MaterialStones, 5),  // stage 2: the stone stand for the vessel
        (MaterialSticks, 4),  // stage 3: the top rim
        (MaterialRope, 8),    // stage 4: the corner lashings
        (MaterialLeaves, 11)  // stage 5: the funnel
    };

    // §119: station.workbench.fbx groups "01".."05". Each logical child is
    // one delivered resource; Unity reveals those children in this exact order.
    // The Blender file has no Animator/Action/NLA staging data.
    private static readonly (string Material, int Count)[] WorkbenchStages =
    {
        (MaterialSticks, 4), // 01: four legs
        (MaterialSticks, 2), // 02: two lower horizontal rails
        (MaterialBoards, 2), // 03: left/right diagonal braces
        (MaterialRope, 2),   // 04: paired lashings (one object per rope)
        (MaterialBoards, 4)  // 05: four tabletop planks
    };

    private static (string Material, int Count)[] StagesFor(WorldObjectState site) => site.BuildProduct switch
    {
        "bed.basic" => BedBasicStages,
        "station.drying_rack" => DryingRackStages,
        "campfire.spot" => CampfireStages,
        "station.water_collector" => WaterCollectorStages,
        ContentIds.Workbench => WorkbenchStages,
        _ => null
    };

    // The whole bill's shortfall, stage-blind (stocked checks, debug readouts).
    public static int TotalRemaining(WorldObjectState site, string materialId) => materialId switch
    {
        MaterialLogs => System.Math.Max(0, site.BillLogs - Delivered(site, materialId)),
        MaterialStones => System.Math.Max(0, site.BillStones - Delivered(site, materialId)),
        MaterialLeaves => System.Math.Max(0, site.BillLeaves - Delivered(site, materialId)),
        MaterialSticks => System.Math.Max(0, site.BillSticks - Delivered(site, materialId)),
        MaterialRope => System.Math.Max(0, site.BillRope - Delivered(site, materialId)),
        MaterialBoards => System.Math.Max(0, site.BillBoards - Delivered(site, materialId)),
        _ => 0
    };

    // The CURRENT stage's shortfall of a material — 0 for anything the active
    // stage doesn't call for (staged sites) or the plain bill shortfall
    // (unstaged sites: campfire, hut pieces).
    public static int Remaining(WorldObjectState site, string materialId)
    {
        if (IsFreeArchitectureSite(site) && !site.ArchitectureElements[0].Buildable)
        {
            // A roof may be planned immediately, but it is not a work target
            // and requests no materials until its exact support edge is up.
            return 0;
        }

        if (IsArchitecturalBuilding(site.BuildProduct))
        {
            // §120 modular grammar: wall/floor/support cubes are independent,
            // so all of their materials may be hauled in parallel. Roof leaves
            // become demand only after half of this roof patch's support
            // vertices are actually complete (3/6 for hut_1hex, 4/7 for the
            // player's committed plan — the count is the PLAN's, not a constant).
            if (materialId == MaterialLeaves && !BuildingRules.RoofUnlocked(
                    site.Id.Value,
                    Delivered(site, MaterialSticks),
                    Delivered(site, MaterialBoards),
                    Delivered(site, MaterialRope),
                    site.BuildProduct))
                return 0;
            return TotalRemaining(site, materialId);
        }

        var stages = StagesFor(site);
        if (stages is null)
        {
            return TotalRemaining(site, materialId);
        }

        // Attribute the delivered pool to stages in order; the first stage
        // left short is the current one.
        System.Span<int> pool = stackalloc int[AllMaterials.Length];
        for (var i = 0; i < AllMaterials.Length; i++)
        {
            pool[i] = Delivered(site, AllMaterials[i]);
        }

        foreach (var (material, count) in stages)
        {
            var index = System.Array.IndexOf(AllMaterials, material);
            var used = System.Math.Min(count, pool[index]);
            pool[index] -= used;
            if (used < count)
            {
                return material == materialId ? count - used : 0;
            }
        }

        return 0; // every stage complete
    }

    public static bool Needs(WorldObjectState site, string materialId) =>
        Remaining(site, materialId) > 0;

    /// <summary>May this site store the carried material on this visit?
    /// Collector visuals remain stage ordered, but its future-stage bundles
    /// may be pre-stocked in the frame. Otherwise prepared sticks/rope sit in
    /// a pack until their stage opens and are consumed as fire fuel in the
    /// meantime, defeating the whole-bill gathering contract.</summary>
    public static bool AcceptsDelivery(WorldObjectState site, string materialId) =>
        site.BuildProduct == ContentIds.WaterCollector
            ? TotalRemaining(site, materialId) > 0
            : Needs(site, materialId);

    public static bool IsStocked(WorldObjectState site)
    {
        foreach (var mat in AllMaterials)
        {
            if (TotalRemaining(site, mat) > 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSite(WorldObjectState obj) =>
        obj != null && !string.IsNullOrEmpty(obj.BuildProduct) &&
        (!IsFreeArchitectureSite(obj) || obj.ArchitectureElements[0].Buildable);

    // §54.14 (r2): FUNCTIONAL stage checks on a live campfire. Delivered
    // materials stay in Contents after the bill closes, so these read the same
    // whether the upgrade bill is still open or finished. A legacy fire spawned
    // without the staged contents (old saves, dev scenes) reads as bare stage 1.
    public static bool CampfireRingComplete(WorldObjectState fire) =>
        fire != null && Delivered(fire, MaterialStones) >= SimBalance.CampfireBillStones;

    // The spit = stages 2-4 (posts, crossbar, lashings) — complete when the
    // full stick and rope bills are in. The stone ring (stage 5, §54.17 r3)
    // may or may not exist yet; it is a fuel-economy upgrade, not a cooking
    // prerequisite.
    public static bool CampfireSpitComplete(WorldObjectState fire) =>
        fire != null &&
        Delivered(fire, MaterialSticks) >= SimBalance.CampfireBillSticks &&
        Delivered(fire, MaterialRope) >= SimBalance.CampfireBillRope;

    // §54.14 (r2): meat hanging on the spit (raw still roasting, or cooked
    // waiting to be taken). Hanging items live in the fire's Contents next to
    // the delivered build materials; food.* never collides with resource.*.
    public static int HangingMeat(WorldObjectState fire, string definitionId)
    {
        var n = 0;
        foreach (var item in fire.Contents)
        {
            if (item.DefinitionId == definitionId)
            {
                n++;
            }
        }

        return n;
    }

    // The tag a Gather* goal filters on to fetch what a site still needs.
    public static string TagForMaterial(string materialId) => materialId switch
    {
        MaterialLogs => "Log",
        MaterialStones => "Stone",
        MaterialLeaves => "PalmLeaf",
        MaterialSticks => "Stick",
        MaterialRope => "Rope",
        MaterialBoards => "Wood",
        _ => null
    };
}

}
