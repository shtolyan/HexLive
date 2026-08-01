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
            if (obj.DefinitionId == "build.site" && string.IsNullOrEmpty(obj.BuildProduct))
            {
                (orphanSites ??= new System.Collections.Generic.List<ObjectId>()).Add(obj.Id);
            }

            // §66: the tent is retired (CraftTent is disabled in DecisionSystem)
            // — sweep any lean-to already standing in a loaded world so the camp
            // is rid of it, not just spared new ones.
            if (obj.DefinitionId == "shelter.tent")
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

            Trace.EmitSystem(world, "OrphanSitesCleared",
                $"removed {orphanSites.Count} orphaned object(s) (product-less build.site / retired shelter.tent)");
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
        var basicBeds = 0;
        var bedSitesInProgress = 0;
        var racks = 0;
        var rackSitesInProgress = 0;
        var collectors = 0; // §54.15: communal water collector
        var collectorSitesInProgress = 0;
        WorldObjectState hearth = null;
        // §64: per-colonist bed ownership (personal beds). Who already owns a bed
        // (any / a premium one), who has one under construction, and any finished
        // bed left ownerless (a reclaimed bed, or the hut's free bed.basic) that a
        // bedless colonist can simply claim instead of building anew.
        var ownedAnyBed = new System.Collections.Generic.HashSet<EntityId>();
        var ownedBasicBed = new System.Collections.Generic.HashSet<EntityId>();
        var siteOwners = new System.Collections.Generic.HashSet<EntityId>();
        var siteBasicOwners = new System.Collections.Generic.HashSet<EntityId>();
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
            if (obj.DefinitionId == "build.site" && BuildSiteMath.IsSite(obj))
            {
                if (obj.BuildProduct is "bed.leaf" or "bed.basic")
                {
                    bedSitesInProgress++;
                    if (obj.Owner is { } siteOwner)
                    {
                        siteOwners.Add(siteOwner);
                        if (obj.BuildProduct == "bed.basic")
                        {
                            siteBasicOwners.Add(siteOwner);
                        }
                    }
                }
                else if (obj.BuildProduct == "station.drying_rack")
                {
                    rackSitesInProgress++;
                }
                else if (obj.BuildProduct == "station.water_collector")
                {
                    collectorSitesInProgress++;
                }

                continue;
            }

            if (obj.DefinitionId == "station.drying_rack")
            {
                racks++;
            }

            if (obj.DefinitionId == "station.water_collector")
            {
                collectors++;
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

                if (obj.Owner is { } bedOwner)
                {
                    ownedAnyBed.Add(bedOwner);
                    if (obj.DefinitionId == "bed.basic")
                    {
                        ownedBasicBed.Add(bedOwner);
                    }
                }
                else if (ownerlessBed is null)
                {
                    ownerlessBed = obj;
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
            var rackSpot = FindFiresideHex(world, hearth);
            if (rackSpot is { } rackPlacement)
            {
                var rackSite = WorldObjectMutations.SpawnObject(
                    world, "build.site", new FragmentId(1), rackPlacement.Tile, rackPlacement.Junction);
                rackSite.BuildProduct = "station.drying_rack";
                // §66: the rack has no sleeper to warm — it simply faces the
                // flames, so the hung garments dry turned toward the heat.
                rackSite.RotationDegrees = rackPlacement.FacingYaw;
                WorldObjectMutations.SetObstacleBlocking(world, rackSite, blocked: true);
                rackSite.BillSticks = SimBalance.RackBillSticks;
                rackSite.BillRope = SimBalance.RackBillRope;
                RememberSiteForColony(world, faction, rackSite);
                Trace.EmitSystem(world, "RackSitePlaced",
                    "station.drying_rack site staked by the hearth");
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
                    world, "build.site", new FragmentId(1),
                    collectorPlacement.Tile, collectorPlacement.Junction);
                collectorSite.BuildProduct = "station.water_collector";
                collectorSite.RotationDegrees = collectorPlacement.FacingYaw;
                WorldObjectMutations.SetObstacleBlocking(world, collectorSite, blocked: true);
                collectorSite.BillSticks = SimBalance.WaterCollectorBillSticks;
                collectorSite.BillStones = SimBalance.WaterCollectorBillStones;
                collectorSite.BillRope = SimBalance.WaterCollectorBillRope;
                collectorSite.BillLeaves = SimBalance.WaterCollectorBillLeaves;
                RememberSiteForColony(world, faction, collectorSite);
                Trace.EmitSystem(world, "CollectorSitePlaced",
                    "station.water_collector site staked by the hearth");
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
            // §64.8: a girl whose dream is the premium bedroll passes on a free
            // leaf mat — claiming it would close her bed dream with the wrong
            // bed. A free bed.basic suits everyone.
            if (ownerlessBed is not null)
            {
                var claimant = FirstLiving(world, faction,
                    id => !ownedAnyBed.Contains(id) && !siteOwners.Contains(id) &&
                        (ownerlessBed.DefinitionId == "bed.basic" || !WantsPremiumBed(world, id)));
                if (claimant is not null)
                {
                    ownerlessBed.Owner = claimant.Id;
                    Trace.EmitSystem(world, "BedClaimed",
                        $"{ownerlessBed.DefinitionId} {ownerlessBed.Id.Value} claimed by colonist {claimant.Id.Value}");
                    return;
                }
            }

            if (hearth is null || bedSitesInProgress > 0)
            {
                return;
            }

            // First tier: a leaf mat for anyone with no bed at all — unless her
            // dream is the premium bedroll (§64.8, PremiumBedChance): then her
            // first and only bed is staked as bed.basic and the leaf tier is
            // skipped. Second tier (§54.12): once everyone owns a bed, premium
            // bedrolls (bed.basic) for those without one — same
            // owner-per-colonist rule (a premium dreamer already owns hers).
            var owner = FirstLiving(world, faction,
                id => !ownedAnyBed.Contains(id) && !siteOwners.Contains(id));
            var dreamProduct = "bed.leaf";
            if (owner is not null && WantsPremiumBed(world, owner.Id))
            {
                dreamProduct = "bed.basic";
            }
            else if (owner is null && SimBalance.BedBasicEnabled)
            {
                owner = FirstLiving(world, faction,
                    id => !ownedBasicBed.Contains(id) && !siteBasicOwners.Contains(id));
                dreamProduct = "bed.basic";
            }

            if (owner is null)
            {
                return; // everyone has their own bed — the dream is fulfilled
            }

            StakeBed(world, faction, hearth, dreamProduct, owner.Id);
            return;
        }

        // --- Pre-§64 baseline (SpecDream disabled): aggregate count path.
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
            world, "build.site", new FragmentId(1), placement.Tile, placement.Junction);
        site.BuildProduct = product;
        site.Owner = owner;
        // §66: a bed is laid SIDE-ON to the hearth — the sleeper warms her flank,
        // never her head or her feet. The yaw rides onto the raised bed, and the
        // body pinned to the bed's sleep point turns with it.
        site.RotationDegrees = placement.SideOnYaw;
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

        RememberSiteForColony(world, faction, site);
        Trace.EmitSystem(world, "BedSitePlaced",
            $"{product} site staked by the hearth" +
            (owner is { } o ? $" for colonist {o.Value}" : string.Empty));
    }

    // §64.8: does this girl dream of the PREMIUM bedroll instead of the plain
    // leaf mat? A stable per-girl-per-world trait: Hash01 over (seed, id) makes
    // the wish deterministic across ticks and reloads without touching saves.
    // Gated on BedBasicEnabled so disabling the premium tier falls back to leaf.
    private static bool WantsPremiumBed(WorldState world, EntityId id) =>
        SimBalance.BedBasicEnabled &&
        MathUtil.Hash01(world.Seed, id.Value, 64, 6408) < SpecDream.PremiumBedChance;

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
