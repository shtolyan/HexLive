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

public sealed partial class DecisionSystem
{
    // Spec 35.3: materials bill for the pending hut piece.
    internal readonly struct BuildPiece
    {
        public BuildPiece(int logs, int stones, int leaves, int edge, string kind)
        {
            Logs = logs;
            Stones = stones;
            Leaves = leaves;
            Edge = edge;
            Kind = kind;
        }

        public int Logs { get; }

        public int Stones { get; }

        public int Leaves { get; }

        public int Edge { get; }

        public string Kind { get; }
    }

    internal static BuildPiece? NextBuildPiece(WorldState world)
    {
        var project = world.Project;
        if (project is null || project.Completed)
        {
            return null;
        }

        if (!project.FloorDone)
        {
            return new BuildPiece(1, 0, 2, -1, "Floor");
        }

        for (var i = 0; i < 6; i++)
        {
            if (i != project.DoorEdge && !project.EdgeDone[i])
            {
                return new BuildPiece(1, 1, 0, i, "Wall");
            }
        }

        if (!project.EdgeDone[project.DoorEdge])
        {
            return new BuildPiece(2, 0, 0, project.DoorEdge, "Door");
        }

        return null;
    }

    // Spec §52: the nearest reachable, unfinished furniture build-site the NPC
    // can see (live object, so its Contents/Bill are readable).
    internal static WorldObjectState FindBuildSite(NPCState npc, WorldState world)
    {
        // Spec §54: the hearth build-site wins over any other site — it only
        // exists during cold start (until the campfire is raised), and it must
        // be built first (fire gates warmth, cooking and all crafting).
        //
        // Behavior audit (Jul 2026): after the hearth, the queue is ORDERED,
        // not first-perceived — the live campfire's open upgrade bill (12
        // sticks + 18 stones + 2 rope) used to hijack the single buildSite
        // slot for the whole run, so the bed's rope stage was never "the
        // site's need" and CraftRope fired 0 times in 250 soak-days. Sleep
        // furniture (bed, drying rack) finishes first; the stone ring and
        // other upgrades take the surplus afterwards.
        // §64.9: the colony SPLITS its builders instead of queueing them. The
        // single buildSite slot is what every bed feeder reads (dreamPull and
        // bedLeaf/Stick/Rope/LogPull all require "the slot IS the bed"), so
        // whatever holds the slot is the only thing that colonist can build.
        // Measured on the 6-seed 10-day soak before this change: seed 31337 held
        // campfire.spot@upgrade for 78 984 npc-ticks and the staked bed.leaf for
        // 244 — the bed's stage-1 sticks never got a single pull, and 0 beds were
        // raised on ALL six seeds (30-day baseline: 2 beds for 24 colonists).
        //
        // A plain "dream first" rank fixes the beds and KILLS the colony: the
        // hearth's stone ring and the water collector sat behind a bed queue that
        // lasts the whole run, and the 30-day soak went 4/4 alive → whole-colony
        // wipes on seeds 777/999 (thirst 1.00, thermal −0.5…−0.79). Strict
        // priority in either direction starves whatever is behind it.
        //
        // So: dream builders take the bed, the rest keep the §63 r2 order
        // (hearth upgrade > furniture > the rest) untouched. Labour is split, no
        // project is starved, and who builds beds is a stable per-girl trait.
        var buildsTheDream = SpecDream.Enabled &&
            world.ActiveDream == DreamType.OwnBed &&
            IsDreamBuilder(npc, world);
        WorldObjectState firstSite = null;
        WorldObjectState dreamSite = null;
        WorldObjectState furnitureSite = null;
        WorldObjectState hearthUpgrade = null;
        foreach (var obj in npc.Perception.Objects)
        {
            if (!obj.IsReachable ||
                !world.Entities.Objects.TryGetValue(obj.Id, out var site) ||
                !BuildSiteMath.IsSite(site))
            {
                continue;
            }

            // §54.14: only the BARE hearth site (no fire raised yet) gets the
            // cold-start priority. A live campfire mid-upgrade (stone ring /
            // spit outstanding) queues like any other furniture site.
            if (site.BuildProduct == "campfire.spot" && site.DefinitionId == "build.site")
            {
                return site;
            }

            if (site.DefinitionId == "campfire.spot")
            {
                hearthUpgrade ??= site;
            }
            else if (site.DefinitionId == "build.site" &&
                site.BuildProduct is "bed.leaf" or "bed.basic")
            {
                if (buildsTheDream)
                {
                    dreamSite ??= site;
                }
                else
                {
                    furnitureSite ??= site;
                }
            }
            else if (site.DefinitionId == "build.site" &&
                site.BuildProduct is "station.drying_rack" or "station.water_collector")
            {
                furnitureSite ??= site;
            }

            firstSite ??= site;
        }

        // A dream builder goes to the bed if she can see one; everyone else (and
        // she, once the beds are done) keeps the §63 r2 queue exactly as it was:
        // bare hearth > hearth upgrade > furniture/stations > the rest.
        return dreamSite ?? hearthUpgrade ?? furnitureSite ?? firstSite;
    }

    // §64.9: is this colonist one of the colony's bed builders? Her own staked
    // site always is (nobody else owes her a bed), plus a stable share of the
    // others so the bed is worked by a crew, not by one girl between chores.
    // Deterministic per girl-per-world (Hash01 over seed+id), so the split
    // survives reloads and reads as character rather than as flicker.
    private static bool IsDreamBuilder(NPCState npc, WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.BuildProduct is "bed.leaf" or "bed.basic" &&
                obj.Owner is { } owner && owner.Equals(npc.Id))
            {
                return true;
            }
        }

        return MathUtil.Hash01(world.Seed, npc.Id.Value, 64, 6409) <
            SpecDream.DreamBuilderShare;
    }

    internal static bool CarriesSiteMaterial(NPCState npc, WorldObjectState site)
    {
        foreach (var mat in BuildSiteMath.AllMaterials)
        {
            if (BuildSiteMath.Needs(site, mat) && npc.Inventory.Items.Contains(mat))
            {
                return true;
            }
        }

        return false;
    }
}

}
