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
// pieces accrete into the bed (rendered growing via BedAssembly), then a hammer
// finishes it. This system is the site PLACER: once the colony has a lit hearth
// but fewer beds than living girls, and no bed is currently under construction,
// it stakes ONE bed.basic site by the fire. One at a time, so a build reads as a
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
        CraftProjectMath.CancelOrphanedProjects(world);
        // §bed-placement fix: sweep orphaned furniture build-sites. A
        // "build.site" with an EMPTY BuildProduct is degenerate cruft (legacy
        // saves — no current path stakes one without a product). They aren't
        // real sites (IsSite=false), yet TileHoldsStructure still counts them,
        // so they squat on the fireside ring and shove a real bed onto a cramped
        // edge tile whose 1.39-wu footprint seals its own approach (0 deliveries
        // ever). Clearing them frees FindFiresideHex to stake on a good ring-1
        // tile. Runs each slow tick; self-heals and stays a no-op once clean.
        System.Collections.Generic.List<ObjectId> orphanSites = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.BuildSite && string.IsNullOrEmpty(obj.BuildProduct))
            {
                (orphanSites ??= new System.Collections.Generic.List<ObjectId>()).Add(obj.Id);
            }

            // §66: the tent is retired (CraftTent is disabled in DecisionSystem)
            // — sweep any lean-to already standing in a loaded world so the camp
            // is rid of it, not just spared new ones.
            if (obj.DefinitionId == ContentIds.Tent)
            {
                (orphanSites ??= new System.Collections.Generic.List<ObjectId>()).Add(obj.Id);
            }
        }

        if (orphanSites != null)
        {
            foreach (var id in orphanSites)
            {
                WorldObjectMutations.DespawnObject(world, id);
            }

            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "OrphanSitesCleared",
                    $"removed {orphanSites.Count} orphaned object(s) (product-less build.site / retired shelter.tent)");
            }
        }

        // §72: every camp stakes its own furniture. The outsider gets a hearth
        // and a bed by the SAME rules, in his own yard — no separate building
        // code, just the same system run once per camp. With one camp authored
        // (or none, as in the test bootstraps) this is one pass over exactly
        // the pre-§72 set.
        if (world.FactionHomes.Count == 0)
        {
            RunForCamp(world, Faction.Colony);
            return;
        }

        foreach (var faction in _campScratch(world))
        {
            RunForCamp(world, faction);
        }
    }

    // Stable iteration order (enum ordinal), so two camps can never race for
    // the same tile depending on dictionary layout.
    private static readonly System.Collections.Generic.List<Faction> _campOrder = new();

    private static System.Collections.Generic.List<Faction> _campScratch(WorldState world)
    {
        _campOrder.Clear();
        foreach (var faction in world.FactionHomes.Keys)
        {
            _campOrder.Add(faction);
        }

        _campOrder.Sort((a, b) => ((int)a).CompareTo((int)b));
        return _campOrder;
    }

    private static void RunForCamp(WorldState world, Faction faction)
    {
        var livingGirls = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && npc.Faction == faction)
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
        var bedSitesInProgress = 0;
        var racks = 0;
        var rackSitesInProgress = 0;
        var collectors = 0; // §54.15: communal water collector
        var collectorSitesInProgress = 0;
        var workbenches = 0;
        var workbenchSitesInProgress = 0;
        WorldObjectState hearth = null;
        // §64: per-colonist bed ownership (personal beds). Who already owns a bed
        // (any / a premium one), who has one under construction, and any finished
        // bed left ownerless (a reclaimed bed, or the hut's free bed.basic) that a
        // bedless colonist can simply claim instead of building anew.
        var ownedAnyBed = new System.Collections.Generic.HashSet<EntityId>();
        var siteOwners = new System.Collections.Generic.HashSet<EntityId>();
        WorldObjectState ownerlessBed = null;
        foreach (var obj in world.Entities.Objects.Values)
        {
            // §72: only this camp's yard. Otherwise the girls' four beds would
            // read as "the outsider already has one" and vice versa.
            if (!ColonyQueries.InCamp(world, obj.Tile, faction))
            {
                continue;
            }

            // §54.14: an in-place upgrading piece (the stage-1+ campfire) keeps
            // an open bill, so IsSite() is true for it — but it is a REAL
            // hearth, not a pending site. Only literal build.site objects count
            // as in-progress here; otherwise the colony can never stake a bed
            // until the fire's stone ring and spit are fully finished (40-day
            // soaks: zero beds, chronic energy pit).
            if (obj.DefinitionId == ContentIds.BuildSite && BuildSiteMath.IsSite(obj))
            {
                if (obj.BuildProduct == ContentIds.BedBasic)
                {
                    bedSitesInProgress++;
                    if (obj.Owner is { } siteOwner)
                    {
                        siteOwners.Add(siteOwner);
                    }
                }
                else if (obj.BuildProduct == ContentIds.DryingRack)
                {
                    rackSitesInProgress++;
                }
                else if (obj.BuildProduct == ContentIds.WaterCollector)
                {
                    collectorSitesInProgress++;
                }
                else if (obj.BuildProduct == ContentIds.Workbench)
                {
                    workbenchSitesInProgress++;
                }

                continue;
            }

            if (obj.DefinitionId == ContentIds.DryingRack)
            {
                racks++;
            }

            if (obj.DefinitionId == ContentIds.WaterCollector)
            {
                collectors++;
            }

            if (obj.DefinitionId == ContentIds.Workbench)
            {
                workbenches++;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def))
            {
                continue;
            }

            if (def.HasTag("Bed"))
            {
                beds++;
                if (obj.Owner is { } bedOwner)
                {
                    ownedAnyBed.Add(bedOwner);
                }
                else if (ownerlessBed is null)
                {
                    ownerlessBed = obj;
                }
            }
            else if (hearth is null && def.HasTag("Campfire"))
            {
                hearth = obj;
            }
        }

        // §35.5B: one communal drying rack, staked by the hearth like a bed —
        // the same staged haul/raise chain builds it (2 uprights → 2 rails →
        // 4 lashings). Cheap, so it goes up first; beds follow next tick.
        if (hearth is not null && racks == 0 && rackSitesInProgress == 0)
        {
            var rackSpot = FindFiresideHex(world, hearth);
            if (rackSpot is { } rackPlacement)
            {
                var rackSite = WorldObjectMutations.SpawnObject(
                    world, ContentIds.BuildSite, new FragmentId(1), rackPlacement.Tile, rackPlacement.Junction);
                rackSite.BuildProduct = ContentIds.DryingRack;
                // §66: the rack has no sleeper to warm — it simply faces the
                // flames, so the hung garments dry turned toward the heat.
                rackSite.RotationDegrees =
                    StructurePlacement.QuantizeHexYaw(rackPlacement.FacingYaw);
                WorldObjectMutations.SetObstacleBlocking(world, rackSite, blocked: true);
                rackSite.BillSticks = SimBalance.RackBillSticks;
                rackSite.BillRope = SimBalance.RackBillRope;
                RememberSiteForColony(world, faction, rackSite);
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "RackSitePlaced",
                        "station.drying_rack site staked by the hearth");
                }
                return;
            }
        }

        // §54.15: one communal water collector follows the rack — the same
        // staged fireside build (no hammer). The rack is STAKED first (it's
        // cheaper), but the collector doesn't wait for it to finish: water is
        // survival (coconut spawns are halved with it), so both sites stand
        // open and the haul chain feeds whichever material is at hand. 5-day
        // smoke with a finished-rack gate: the rack never completed, so the
        // collector never even staked.
        if (hearth is not null && (racks > 0 || rackSitesInProgress > 0) &&
            collectors == 0 && collectorSitesInProgress == 0)
        {
            var collectorSpot = FindFiresideHex(world, hearth);
            if (collectorSpot is { } collectorPlacement)
            {
                var collectorSite = WorldObjectMutations.SpawnObject(
                    world, ContentIds.BuildSite, new FragmentId(1),
                    collectorPlacement.Tile, collectorPlacement.Junction);
                collectorSite.BuildProduct = ContentIds.WaterCollector;
                collectorSite.RotationDegrees =
                    StructurePlacement.QuantizeHexYaw(collectorPlacement.FacingYaw);
                WorldObjectMutations.SetObstacleBlocking(world, collectorSite, blocked: true);
                collectorSite.BillSticks = SimBalance.WaterCollectorBillSticks;
                collectorSite.BillStones = SimBalance.WaterCollectorBillStones;
                collectorSite.BillRope = SimBalance.WaterCollectorBillRope;
                collectorSite.BillLeaves = SimBalance.WaterCollectorBillLeaves;
                RememberSiteForColony(world, faction, collectorSite);
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "CollectorSitePlaced",
                        "station.water_collector site staked by the hearth");
                }
                return;
            }
        }

        // §119: a stabilized amputee turns the workbench into a durable colony
        // objective. It is one staged model (01..05), fed one visible piece per
        // resource through the existing build-site pipeline.
        if (hearth is not null && NeedsProstheticWorkbench(world, faction) &&
            workbenches == 0 && workbenchSitesInProgress == 0)
        {
            var workbenchSpot = FindFiresideHex(world, hearth);
            if (workbenchSpot is { } placement)
            {
                var site = WorldObjectMutations.SpawnObject(
                    world, ContentIds.BuildSite, new FragmentId(1),
                    placement.Tile, placement.Junction);
                site.BuildProduct = ContentIds.Workbench;
                site.RotationDegrees = StructurePlacement.QuantizeHexYaw(placement.FacingYaw);
                site.BillBoards = Spec119.WorkbenchBillBoards;
                site.BillSticks = Spec119.WorkbenchBillSticks;
                site.BillRope = Spec119.WorkbenchBillRope;
                WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);
                RememberSiteForColony(world, faction, site);
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "WorkbenchSitePlaced",
                        "station.workbench site staked: boards 6, sticks 6, rope 2");
                }
                return;
            }
        }

        // §64: beds are PERSONAL. Each colonist wants her own; the dream drives
        // the staking, one bed at a time, until every living colonist owns one.
        if (SpecDream.Enabled)
        {
            // Beds wait for the colony's first dream — a fire — to be fulfilled.
            // (Today a bed could stake at a cold hearth; the dream holds it until
            // the hearth is actually lit, per SpecDream.CampfireRequiresLit.)
            if (!world.CampfireDreamDone)
            {
                return;
            }

            // Reuse before rebuild: hand any ownerless finished bed (a reclaimed
            // one, or the hut's free bed.basic) to a colonist who has none.
            // Any ownerless canonical bed can satisfy the next owner's dream.
            if (ownerlessBed is not null)
            {
                var claimant = FirstLiving(world, faction,
                    id => !ownedAnyBed.Contains(id) && !siteOwners.Contains(id));
                if (claimant is not null)
                {
                    ownerlessBed.Owner = claimant.Id;
                    if (SimTrace.Enabled)
                    {
                        Trace.DebugSystem(world, "BedClaimed",
                            $"{ownerlessBed.DefinitionId} {ownerlessBed.Id.Value} claimed by colonist {claimant.Id.Value}");
                    }
                    return;
                }
            }

            if (hearth is null || bedSitesInProgress > 0)
            {
                return;
            }

            // One canonical bed per colonist. Future quality changes upgrade
            // this same owned object instead of replacing it with another type.
            var owner = FirstLiving(world, faction,
                id => !ownedAnyBed.Contains(id) && !siteOwners.Contains(id));

            if (owner is null)
            {
                return; // everyone has their own bed — the dream is fulfilled
            }

            StakeBed(world, faction, hearth, ContentIds.BedBasic, owner.Id);
            return;
        }

        // --- Pre-§64 baseline (SpecDream disabled): aggregate count path.
        // Fire first (a lit hearth, not the cold pit-site). One bed at a time.
        // Cap at one canonical bed per living colonist.
        var product = beds < livingGirls ? ContentIds.BedBasic : null;
        if (hearth is null || bedSitesInProgress > 0 || product is null)
        {
            return;
        }

        StakeBed(world, faction, hearth, product, null);
    }

    // §54.9A / §64: stake ONE bed build-site by the hearth, optionally stamped
    // with the colonist it belongs to (null = shared). Owner rides onto the
    // finished bed when it is raised (ExecutionSystem.ApplyFurnitureSite).
    private static void StakeBed(
        WorldState world, Faction faction, WorldObjectState hearth, string product, EntityId? owner)
    {
        var spot = FindFiresideHex(world, hearth);
        if (spot is not { } placement)
        {
            return;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite, new FragmentId(1), placement.Tile, placement.Junction);
        site.BuildProduct = product;
        site.Owner = owner;
        // §66: a bed is laid SIDE-ON to the hearth — the sleeper warms her flank,
        // never her head or her feet. The yaw rides onto the raised bed, and the
        // body pinned to the bed's sleep point turns with it.
        site.RotationDegrees = StructurePlacement.QuantizeHexYaw(placement.SideOnYaw);
        // §54.9A: the site now knows what it will become — claim the finished
        // bed's physical footprint so nothing else is placed across the frame.
        WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);
        site.BillLogs = SimBalance.BedBasicBillLogs;
        site.BillSticks = SimBalance.BedBasicBillSticks;
        site.BillRope = SimBalance.BedBasicBillRope;
        site.BillLeaves = SimBalance.BedBasicBillLeaves;

        RememberSiteForColony(world, faction, site);
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BedSitePlaced",
                $"{product} site staked by the hearth" +
                (owner is { } o ? $" for colonist {o.Value}" : string.Empty));
        }
    }

    private static bool NeedsProstheticWorkbench(WorldState world, Faction faction)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health <= 0f || !FactionRelations.AreAllies(faction, npc.Faction))
            {
                continue;
            }

            foreach (var part in new[] { BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR })
            {
                if (npc.Body.IsSevered(part) &&
                    !KenshiProstheticMath.HasUnstabilizedWound(npc, part) &&
                    npc.Body.Condition(part).Prosthetic is null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // First living colonist matching a predicate on her id (bed-target picking).
    private static NPCState FirstLiving(
        WorldState world, Faction faction, System.Func<EntityId, bool> predicate)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && npc.Faction == faction && predicate(npc.Id))
            {
                return npc;
            }
        }

        return null;
    }

    // §72: only OUR camp learns where we staked it. Without the faction gate
    // the outsider gets a free permanent map of the girls' beds, rack and
    // collector the instant they mark them out.
    private static void RememberSiteForColony(WorldState world, Faction faction, WorldObjectState site)
    {
        var junction = site.Junctions.Count > 0 ? site.Junctions[0] : (JunctionId?)null;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Faction != faction)
            {
                continue;
            }

            npc.Memory.KnownObjects[site.Id] = new ObjectMemory
            {
                Id = site.Id,
                DefinitionId = site.DefinitionId,
                Tile = site.Tile,
                Junction = junction,
                IsPermanent = true,
                LastSeenTick = world.Tick
            };
            npc.Memory.Version++; // §22.7: кэш вида памяти обязан увидеть вставку
        }
    }

    // §66 (was §54.9A): the HEX the piece is raised on. One build per hex, always
    // at the hex centre — so the candidate is a whole fireside tile, never a
    // random junction on it. A tile qualifies when it is dry walkable ground,
    // holds no other structure, and its interior is clear of blocked points
    // (boulders, palms, the fire's ember disc) — see StructurePlacement.
    // Fireside ring first; when ring 1 is full the search widens one ring so the
    // colony still gets its bed. Closest hex to the flames wins, and the yaw that
    // lays the piece side-on / facing the fire comes back with it.
    private static (TileCoord Tile, JunctionId Junction, float SideOnYaw, float FacingYaw)? FindFiresideHex(
        WorldState world, WorldObjectState hearth)
    {
        var hearthPos = hearth.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(hearth.Junctions[0], out var hearthAnchor)
                ? hearthAnchor.WorldPosition
                : HexSpatialMath.TileToWorld(hearth.Tile);

        (TileCoord Tile, JunctionId Junction, float SideOnYaw, float FacingYaw)? best = null;
        var bestDist = float.MaxValue;
        for (var dq = -2; dq <= 2; dq++)
        {
            for (var dr = -2; dr <= 2; dr++)
            {
                var coord = new TileCoord(hearth.Tile.Q + dq, hearth.Tile.R + dr);
                var ring = HexSpatialMath.HexDistance(coord, hearth.Tile);
                if (ring is 0 or > 2 || !StructurePlacement.HexFreeForBuild(world, coord))
                {
                    continue;
                }

                if (StructurePlacement.CenterJunction(world, coord) is not { } center ||
                    !world.Junctions.Items.TryGetValue(center, out var centerJn))
                {
                    continue;
                }

                // Ring 1 always beats ring 2 — the piece stays fireside.
                var dist = HexSpatialMath.Distance(centerJn.WorldPosition, hearthPos) +
                    (ring == 2 ? 1000f : 0f);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = (coord, center,
                        StructurePlacement.SideOnYaw(centerJn.WorldPosition, hearthPos),
                        StructurePlacement.FacingYaw(centerJn.WorldPosition, hearthPos));
                }
            }
        }

        return best;
    }
}

}
