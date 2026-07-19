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

// Spec §54.2: beds are raised at a PROGRESSIVE build-site — hauled leaf/stick
// pieces accrete into the mat (rendered growing via BedFactory), then a hammer
// finishes it. This system is the site PLACER: once the colony has a lit hearth
// but fewer beds than living girls, and no bed is currently under construction,
// it stakes ONE bed.leaf site by the fire. One at a time, so a build reads as a
// clear "this bed, now" project; the finished bed bumps the count and the next
// site follows. Placement is a colony intent (like the bootstrap campfire site)
// — deliberately OUTSIDE the per-NPC auction so the fragile survival balance is
// untouched; the hauling/raising is the existing BuildFurniture chain.
public sealed class BedSiteSystem : ISimulationSystem
{
    public string Name => nameof(BedSiteSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        var livingGirls = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f)
            {
                livingGirls++;
            }
        }

        if (livingGirls == 0)
        {
            return;
        }

        // Count finished beds + beds already under construction; find the hearth.
        var beds = 0;
        var basicBeds = 0;
        var bedSitesInProgress = 0;
        var racks = 0;
        var rackSitesInProgress = 0;
        WorldObjectState hearth = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            // §54.14: an in-place upgrading piece (the stage-1+ campfire) keeps
            // an open bill, so IsSite() is true for it — but it is a REAL
            // hearth, not a pending site. Only literal build.site objects count
            // as in-progress here; otherwise the colony can never stake a bed
            // until the fire's stone ring and spit are fully finished (40-day
            // soaks: zero beds, chronic energy pit).
            if (obj.DefinitionId == "build.site" && BuildSiteMath.IsSite(obj))
            {
                if (obj.BuildProduct is "bed.leaf" or "bed.basic")
                {
                    bedSitesInProgress++;
                }
                else if (obj.BuildProduct == "station.drying_rack")
                {
                    rackSitesInProgress++;
                }

                continue;
            }

            if (obj.DefinitionId == "station.drying_rack")
            {
                racks++;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            if (def.Tags.Contains("Bed"))
            {
                beds++;
                if (obj.DefinitionId == "bed.basic")
                {
                    basicBeds++;
                }
            }
            else if (hearth is null && def.Tags.Contains("Campfire"))
            {
                hearth = obj;
            }
        }

        // §35.5B: one communal drying rack, staked by the hearth like a bed —
        // the same staged haul/raise chain builds it (2 uprights → 2 rails →
        // 4 lashings). Cheap, so it goes up first; beds follow next tick.
        if (hearth is not null && racks == 0 && rackSitesInProgress == 0)
        {
            var rackSpot = FindFiresideSpot(world, hearth, "station.drying_rack");
            if (rackSpot is { } rackPlacement)
            {
                var rackSite = WorldObjectMutations.SpawnObject(
                    world, "build.site", new FragmentId(1), rackPlacement.Tile, rackPlacement.Junction);
                rackSite.BuildProduct = "station.drying_rack";
                WorldObjectMutations.SetObstacleBlocking(world, rackSite, blocked: true);
                rackSite.BillSticks = SimBalance.RackBillSticks;
                rackSite.BillRope = SimBalance.RackBillRope;
                RememberSiteForColony(world, rackSite);
                Trace.EmitSystem(world, "RackSitePlaced",
                    "station.drying_rack site staked by the hearth");
                return;
            }
        }

        // Fire first (a lit hearth, not the cold pit-site). One bed at a time.
        // Cap at one bed per living girl. §54.12: once every girl sleeps on
        // SOMETHING, the colony moves to the second bed tier — premium
        // bedrolls (bed.basic), each built FROM SCRATCH at its own fireside
        // site (hammer-raised), until each girl has one. Not an upgrade: the
        // leaf mats stay.
        var product = beds < livingGirls
            ? "bed.leaf"
            : SimBalance.BedBasicEnabled && basicBeds < livingGirls
                ? "bed.basic"
                : null;
        if (hearth is null || bedSitesInProgress > 0 || product is null)
        {
            return;
        }

        var spot = FindFiresideSpot(world, hearth, product);
        if (spot is not { } placement)
        {
            return;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, "build.site", new FragmentId(1), placement.Tile, placement.Junction);
        site.BuildProduct = product;
        // §54.9A: the site now knows what it will become — claim the finished
        // bed's physical footprint so nothing else is placed across the frame.
        WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);
        if (product == "bed.leaf")
        {
            site.BillLeaves = SimBalance.BedLeafBillLeaves;
            site.BillSticks = SimBalance.BedLeafBillSticks;
            site.BillRope = SimBalance.BedLeafBillRope;
        }
        else
        {
            site.BillLogs = SimBalance.BedBasicBillLogs;
            site.BillSticks = SimBalance.BedBasicBillSticks;
            site.BillRope = SimBalance.BedBasicBillRope;
            site.BillLeaves = SimBalance.BedBasicBillLeaves;
        }

        RememberSiteForColony(world, site);
        Trace.EmitSystem(world, "BedSitePlaced",
            $"{product} site staked by the hearth ({beds}/{livingGirls} beds, {basicBeds} premium)");
    }

    private static void RememberSiteForColony(WorldState world, WorldObjectState site)
    {
        var junction = site.Junctions.Count > 0 ? site.Junctions[0] : (JunctionId?)null;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Memory.KnownObjects[site.Id] = new ObjectMemory
            {
                Id = site.Id,
                DefinitionId = site.DefinitionId,
                Tile = site.Tile,
                Junction = junction,
                IsPermanent = true,
                LastSeenTick = world.Tick
            };
        }
    }

    // §54.9A: the spot where the bed PHYSICALLY fits. A junction is only a
    // candidate when every point under the finished bed's footprint (the
    // product's ObstacleRadius, measured off the real prefab) is clear of
    // obstacle-blocked junctions (boulders, palms, the fire's ember ring,
    // other beds/sites) and water. Fireside ring first; when the near tiles
    // are too cluttered the search widens one ring so the colony still gets
    // its bed — closest valid spot to the flames wins.
    private static (TileCoord Tile, JunctionId Junction)? FindFiresideSpot(
        WorldState world, WorldObjectState hearth, string product)
    {
        var footprint = world.Content.ObjectDefinitions.TryGetValue(product, out var productDef)
            ? productDef.ObstacleRadius
            : 0f;
        if (hearth.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(hearth.Junctions[0], out var hearthAnchor))
        {
            return null;
        }

        (TileCoord Tile, JunctionId Junction)? best = null;
        var bestDist = float.MaxValue;
        for (var dq = -2; dq <= 2; dq++)
        {
            for (var dr = -2; dr <= 2; dr++)
            {
                var coord = new TileCoord(hearth.Tile.Q + dq, hearth.Tile.R + dr);
                var ring = HexSpatialMath.HexDistance(coord, hearth.Tile);
                if (ring is 0 or > 2 ||
                    !world.Tiles.Items.TryGetValue(coord, out var tile) ||
                    tile.Flags.HasFlag(TileFlags.Water))
                {
                    continue;
                }

                // §54.12: one STRUCTURE per fireside tile. IsJunctionFree only
                // sees NPC reservations, so the premium-bed site used to get
                // staked ON TOP of the finished leaf mat. Loose pickup-able
                // items don't claim a tile — furniture does.
                if (TileHoldsStructure(world, coord))
                {
                    continue;
                }

                foreach (var jid in tile.Junctions)
                {
                    if (!world.Junctions.Items.TryGetValue(jid, out var jn) || jn.Blocked ||
                        !SpatialQueries.IsJunctionFree(world, jid) ||
                        !SpatialQueries.FootprintClear(world, jn, footprint))
                    {
                        continue;
                    }

                    // Ring 1 always beats ring 2 — the bed stays fireside.
                    var dist = HexSpatialMath.Distance(jn.WorldPosition, hearthAnchor.WorldPosition) +
                        (ring == 2 ? 1000f : 0f);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = (coord, jid);
                    }
                }
            }
        }

        return best;
    }

    // A non-portable object (no PickUp interaction: a bed, a build-site, the
    // rack, a grave…) parked on the tile — the tile is spoken for.
    private static bool TileHoldsStructure(WorldState world, TileCoord coord)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var ids))
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (!world.Entities.Objects.TryGetValue(id, out var obj))
            {
                continue;
            }

            if (BuildSiteMath.IsSite(obj))
            {
                return true;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            var portable = false;
            foreach (var interaction in def.Interactions)
            {
                if (interaction.Type == InteractionType.PickUp)
                {
                    portable = true;
                    break;
                }
            }

            if (!portable)
            {
                return true;
            }
        }

        return false;
    }
}

}
