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
        WorldObjectState firstSite = null;
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
                site.BuildProduct is "bed.leaf" or "bed.basic" or "station.drying_rack")
            {
                furnitureSite ??= site;
            }

            firstSite ??= site;
        }

        // §63 r2 (user pass): the hearth's upgrades (stone ring → spit) come
        // RIGHT AFTER the fire itself — the ring multiplies every fueling, so
        // it outranks furniture. Queue: bare hearth > campfire upgrade >
        // bed/rack sites > the rest. (BedSiteSystem's bed-per-girl backlog
        // used to monopolize the slot and the ring never saw a stone.)
        return hearthUpgrade ?? furnitureSite ?? firstSite;
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
