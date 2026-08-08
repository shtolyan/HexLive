using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

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

    public static int Delivered(WorldObjectState site, string materialId)
    {
        var n = 0;
        foreach (var item in site.Contents)
        {
            if (item.DefinitionId == materialId)
            {
                n++;
            }
        }

        return n;
    }

    // §54.12: ordered build STAGES — a site demands (and accepts) materials one
    // construction stage at a time, not the whole bill at once. The bed's
    // stages mirror the staged piece groups of its assembled prefab
    // (bed_leaf_final children "1".."4"): frame sticks → slat sticks → rope
    // lashing → leaf mattress. Every consumer (gather goals, hauls, deposits,
    // rope crafting) keys off Needs/Remaining, which only expose the CURRENT
    // stage's shortfall — so the build visibly proceeds stage by stage.
    // KEEP IN SYNC with the prefab's groups AND the SimBalance.BedLeafBill*
    // totals (= the sums per material across stages).
    private static readonly (string Material, int Count)[] BedLeafStages =
    {
        (MaterialSticks, 4),  // stage 1: the frame
        (MaterialSticks, 4),  // stage 2: the slats
        (MaterialRope, 8),    // stage 3: the lashing
        (MaterialLeaves, 46)  // stage 4: the mattress
    };

    // bed_basic_final prefab groups "1".."4".
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

    private sealed class MaterialStage
    {
        public readonly (string Material, int Count)[] Requirements;

        public MaterialStage(params (string Material, int Count)[] requirements)
        {
            Requirements = requirements;
        }
    }

    // Architectural stages may require several resources in parallel. This is
    // intentionally separate from the single-material furniture sequence: old
    // beds/stations retain their exact delivery order, while a hut can accept
    // both kinds of wood needed by one visible construction stage.
    private static readonly MaterialStage[] Hut1HexStages =
    {
        new(
            (MaterialSticks, BuildingRules.FrameSticks),
            (MaterialBoards, BuildingRules.FrameBoards)),
        new(
            (MaterialSticks, BuildingRules.EnclosureSticks),
            (MaterialBoards, BuildingRules.EnclosureBoards),
            (MaterialRope, BuildingRules.EnclosureRope)),
        new((MaterialLeaves, BuildingRules.RoofLeaves))
    };

    private static MaterialStage[] GroupedStagesFor(WorldObjectState site) =>
        site.BuildProduct == ContentIds.Hut1Hex ? Hut1HexStages : null;

    private static (string Material, int Count)[] StagesFor(WorldObjectState site) => site.BuildProduct switch
    {
        "bed.leaf" => BedLeafStages,
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
        var groupedStages = GroupedStagesFor(site);
        if (groupedStages is not null)
        {
            return RemainingInGroupedStages(site, materialId, groupedStages);
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

    private static int RemainingInGroupedStages(
        WorldObjectState site, string materialId, MaterialStage[] stages)
    {
        System.Span<int> pool = stackalloc int[AllMaterials.Length];
        for (var i = 0; i < AllMaterials.Length; i++)
        {
            pool[i] = Delivered(site, AllMaterials[i]);
        }

        foreach (var stage in stages)
        {
            var complete = true;
            foreach (var (material, count) in stage.Requirements)
            {
                var index = System.Array.IndexOf(AllMaterials, material);
                if (System.Math.Min(count, pool[index]) < count)
                {
                    complete = false;
                }
            }

            if (!complete)
            {
                foreach (var (material, count) in stage.Requirements)
                {
                    if (material != materialId) continue;
                    var index = System.Array.IndexOf(AllMaterials, material);
                    return System.Math.Max(0, count - pool[index]);
                }

                return 0;
            }

            foreach (var (material, count) in stage.Requirements)
            {
                var index = System.Array.IndexOf(AllMaterials, material);
                pool[index] -= count;
            }
        }

        return 0;
    }

    public static bool Needs(WorldObjectState site, string materialId) =>
        Remaining(site, materialId) > 0;

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
        obj != null && !string.IsNullOrEmpty(obj.BuildProduct);

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
