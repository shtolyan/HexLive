using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{

public sealed class WorldStateFactory
{
    private int _nextJunctionValue = 1;
    private readonly Dictionary<(int, int), JunctionId> _junctionsByKey = new();

    public WorldState Create(WorldBootstrapDefinition bootstrap)
    {
        var world = new WorldState
        {
            Tick = 0,
            TickDeltaTime = bootstrap.Simulation.TickDeltaTime,
            Seed = bootstrap.Simulation.Seed
        };

        foreach (var pair in PrototypeContentCatalog.CreateDefaults())
        {
            world.Content.ObjectDefinitions[pair.Key] = pair.Value;
        }

        // Per-object assets (WorldObjectConfig → WorldObjectLibrary): merge
        // asset-declared actions/tags over the defaults, add new object types.
        WorldObjectLibrary.ApplyTo(world.Content.ObjectDefinitions);

        world.Environment.GlobalTemperature = bootstrap.Environment.GlobalTemperature;

        foreach (var fragmentBootstrap in bootstrap.Fragments)
        {
            AddFragment(world, fragmentBootstrap);
        }

        BuildAdjacency(world);
        BlockEdgeJunctions(world);
        BlockCliffAndSeaJunctions(world);
        OpenSwimRing(world);

        foreach (var objectBootstrap in bootstrap.Objects)
        {
            AddObject(world, objectBootstrap);
        }

        foreach (var npcBootstrap in bootstrap.Npcs)
        {
            AddNpc(world, npcBootstrap);
        }

        // §54.10: the communal hut (spec 35.3) is retired — it was never seen in
        // play, pulled logs/stones/time away from the things that matter, and its
        // only reward was a bed.basic that the progressive bed build-site now
        // supplies. No world.Project ⇒ NextBuildPiece stays null ⇒ GoalType.Build
        // never fires. (CreateBuildProject is left defined but unused.)
        CreateCampfireSite(world); // §54 cold start: the hearth is built, not given
        // §54.2: beds are woven at the campfire (CraftBed tiers) — the §52 bed
        // build-site is retired, so it's no longer seeded here.
        SeedHomeKnowledge(world);

        // Spec 29E.3: campfires start cold (ResourceAmount is fuel ticks).
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Tags.Contains("Campfire"))
            {
                obj.ResourceAmount = 0f;
            }
        }

        // Spec 31A.5B + 42: everyone starts dressed FOR THE WEATHER — random
        // (deterministic per seed+NPC) underwear beneath a random outer set.
        // At ~10 °C ambient the [16,22] comfort band demands ~+0.5 warmth
        // (top+pants+boots ≈ 0.5 → ~15 °C effective, mild chill that the
        // campfire covers); a seeded coin-flip jacket (+0.4) makes some girls
        // genuinely comfortable and leaves others chasing the fire — texture,
        // not a death sentence. Worn items are NOT world objects, so this
        // provisions the cold WITHOUT perturbing routes/placement.
        // Spec 42 (WarmUp era): castaways wash ashore in almost nothing —
        // random (deterministic per seed+NPC) underwear, MAYBE shorts, MAYBE
        // a top missing entirely. Clothing barely warms; the designed way
        // through a cold night is the campfire, not the wardrobe.
        // Custom-print underwear/tops (fal.ai textures, spec 42) join the
        // seeded rotation so castaways can wash ashore in the leopard/star
        // panties or the tie-dye/tropic tee.
        // The 2026-07 new-wear drop (spec §31B.4) joins the rotation too: the
        // extracted panties/bra/swimsuit read as castaway beachwear. The
        // dresses and sweater stay out — nobody washes ashore in a fur dress
        // (they're registered, dressable and tunable in the WardrobeTest
        // scene).
        string[] startBottoms = { "Panty_11571", "Bikini Bottom", "underwear.panty_leo", "underwear.panty_stars", "underwear.panty_flair", "underwear.panty_basic", "underwear.swim_bottom", "underwear.panty_dots", "underwear.panty_stripe", "underwear.panty_cherry" };
        string[] startTops = { "Bikini top", "Top_11927", "CowTop", "clothing.top_tiedye", "clothing.top_tropic", "underwear.bra_basic", "underwear.swim_top", "underwear.bra_dots", "underwear.bra_stripe", "underwear.bra_cherry" };
        string[] startShorts = { "Shorts Green", "Shorts short", "Shorts 1389", "clothing.shorts_red", "clothing.shorts_olive", "clothing.shorts_cherry" };
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var id = npc.Id.Value;
            npc.WornItems.Add(startBottoms[(int)(MathUtil.Hash01(world.Seed, id, 11, 4201) * startBottoms.Length)]);
            if (MathUtil.Hash01(world.Seed, id, 12, 4202) < 0.8f)
            {
                npc.WornItems.Add(startTops[(int)(MathUtil.Hash01(world.Seed, id, 13, 4203) * startTops.Length)]);
            }

            if (MathUtil.Hash01(world.Seed, id, 14, 4204) < 0.5f)
            {
                npc.WornItems.Add(startShorts[(int)(MathUtil.Hash01(world.Seed, id, 15, 4205) * startShorts.Length)]);
            }

            Runtime.EquipmentMath.Recalculate(world, npc);
        }

        return world;
    }

    // Spec 35.3: choose the communal hut site (seeded) — walkable, dry,
    // 5-7 tiles from home, all six neighbors present and walkable; the
    // door edge faces home. A construction.site object anchors the work.
    private static void CreateBuildProject(WorldState world)
    {
        var home = new TileCoord(0, 2);
        var candidates = new List<TileCoord>();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Blocked) ||
                tile.Flags.HasFlag(TileFlags.Indoor) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(pair.Key, home);
            if (distance is < 5 or > 7)
            {
                continue;
            }

            var allNeighborsOk = true;
            foreach (var direction in HexDirection.All)
            {
                var neighbor = new TileCoord(pair.Key.Q + direction.DQ, pair.Key.R + direction.DR);
                if (!world.Tiles.Items.TryGetValue(neighbor, out var neighborTile) ||
                    !neighborTile.Flags.HasFlag(TileFlags.Walkable) ||
                    neighborTile.Flags.HasFlag(TileFlags.Water))
                {
                    allNeighborsOk = false;
                    break;
                }
            }

            if (allNeighborsOk)
            {
                candidates.Add(pair.Key);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        candidates.Sort((x, y) => (x.Q * 1000 + x.R).CompareTo(y.Q * 1000 + y.R));
        var pick = (int)(MathUtil.Hash01(world.Seed, 35, 3, 1901) * candidates.Count);
        pick = Math.Min(pick, candidates.Count - 1);
        var site = candidates[pick];

        var doorEdge = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < HexDirection.All.Length; i++)
        {
            var direction = HexDirection.All[i];
            var neighbor = new TileCoord(site.Q + direction.DQ, site.R + direction.DR);
            var distance = HexSpatialMath.HexDistance(neighbor, home);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                doorEdge = i;
            }
        }

        world.Project = new BuildProject { Tile = site, DoorEdge = doorEdge };

        var siteTile = world.Tiles.Items[site];
        WorldObjectMutations.SpawnObject(world, "construction.site",
            new FragmentId(1), site, siteTile.Junctions[0]);
    }

    // Spec §52: stake the communal bed as a build-site next to the campfire —
    // an intent point every NPC knows from the start (SeedHomeKnowledge runs
    // right after). Bill = 3 logs + 2 stones; a hammer raises it once stocked.
    // Placed on a free junction of a tile neighbouring the hearth so it sits in
    // the yard, not on top of the fire.
    // §54.14 (r2) cold start: the yard's chosen hearth spot holds only the
    // MARK — a bare campfire build-site (BuildSiteMath.CampfireStages). The
    // colony piles the stage-1 sticks itself, the site raises into a cold
    // campfire, and TendFire lights it (lighter or friction). Nothing is
    // pre-built or handed out; the generator-chosen good location is kept.
    private static void CreateCampfireSite(WorldState world)
    {
        var hearth = new TileCoord(0, 4);
        if (!world.Tiles.Items.TryGetValue(hearth, out var tile))
        {
            return;
        }

        JunctionId? junction = null;
        foreach (var jid in tile.Junctions)
        {
            if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
            {
                junction = jid;
                break;
            }
        }

        if (junction is not { } j)
        {
            return;
        }

        // §54.14 (r2): the hearth starts as a BARE marked build-site — nothing
        // is pre-built or pre-delivered. The spot is chosen (the colony knows
        // where the fire belongs, SeedHomeKnowledge), but the stick pile itself
        // must be hauled in: the moment stage 1 (9 sticks) lands the site raises
        // into a real cold campfire (ApplyFurnitureSite), and the upgrade stages
        // (stone ring, roasting spit) keep growing in place. GOAP treats this
        // site as survival-critical (hearthUrgent) — warmth, comfort and cooking
        // all gate on it.
        var site = WorldObjectMutations.SpawnObject(world, "build.site", new FragmentId(1), hearth, j);
        site.BuildProduct = "campfire.spot";
        site.BillSticks = SimBalance.CampfireBillSticks;
        site.BillStones = SimBalance.CampfireBillStones;
        site.BillRope = SimBalance.CampfireBillRope;

        // §54.9A: the site claims the footprint of the campfire it will become
        // (BuildProduct was empty at SpawnObject time — re-invoke now).
        WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);

        // §54.14 (r2): starter sticks scattered around the marked spot — the
        // colony still hauls and piles them itself, but the material is at
        // hand (see SimBalance.CampfireStarterSticks for why this must exist).
        var scattered = 0;
        for (var ring = 1; ring <= 2 && scattered < SimBalance.CampfireStarterSticks; ring++)
        {
            foreach (var dir in HexDirection.All)
            {
                if (scattered >= SimBalance.CampfireStarterSticks)
                {
                    break;
                }

                var coord = new TileCoord(hearth.Q + dir.DQ * ring, hearth.R + dir.DR * ring);
                if (!world.Tiles.Items.TryGetValue(coord, out var around) ||
                    around.Flags.HasFlag(TileFlags.Water))
                {
                    continue;
                }

                foreach (var jid in around.Junctions)
                {
                    if (scattered >= SimBalance.CampfireStarterSticks)
                    {
                        break;
                    }

                    if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
                    {
                        WorldObjectMutations.SpawnObject(world, "resource.stick", new FragmentId(1), coord, jid);
                        scattered++;
                    }
                }
            }
        }
    }

    private static void CreateBedSite(WorldState world)
    {
        // §54: anchor the bed-site by the hearth — a finished campfire if one
        // exists, otherwise the campfire build-site (cold start).
        var campfire = FindObject(world, "campfire.spot") ?? FindHearthSite(world);
        if (campfire is null)
        {
            return;
        }

        // Collect free junctions on dry tiles neighbouring the hearth, so both
        // the bed-site and the hammers land on real, walkable spots.
        var spots = new System.Collections.Generic.List<(TileCoord Tile, JunctionId Junction)>();
        foreach (var dir in HexDirection.All)
        {
            var coord = new TileCoord(campfire.Tile.Q + dir.DQ, campfire.Tile.R + dir.DR);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile) ||
                tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            foreach (var jid in tile.Junctions)
            {
                if (world.Junctions.Items.TryGetValue(jid, out var jn) && !jn.Blocked)
                {
                    spots.Add((coord, jid));
                    break;
                }
            }
        }

        if (spots.Count == 0)
        {
            return;
        }

        var site = WorldObjectMutations.SpawnObject(
            world, "build.site", new FragmentId(1), spots[0].Tile, spots[0].Junction);
        site.BuildProduct = "bed.basic";
        // §54.2: the premium bedroll bill = the assembled bed_basic_final prefab's
        // real pieces (4 log rails + stick slats + rope lashings + leaf mattress),
        // so the progressive site reveals piece-per-delivery into a whole bed.
        // ⚠ Logs are heavily contested (the hearth eats them first), so a log-billed
        // starter bed can stall — if it never finishes in soak, zero BillLogs here
        // (fall back to the old all-stone frame) rather than desyncing bill/model.
        site.BillLogs = SimBalance.BedBasicBillLogs;
        site.BillSticks = SimBalance.BedBasicBillSticks;
        site.BillRope = SimBalance.BedBasicBillRope;
        site.BillLeaves = SimBalance.BedBasicBillLeaves;

        // §52: two builder's hammers near the hearth — a second girl can build
        // while the first carries one off. Placed on the remaining free spots.
        for (var i = 1; i < spots.Count && i <= 2; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "tool.hammer", new FragmentId(1), spots[i].Tile, spots[i].Junction);
        }
    }

    private static WorldObjectState FindObject(WorldState world, string definitionId)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == definitionId)
            {
                return obj;
            }
        }

        return null;
    }

    // §54 cold start: the campfire build-site (the hearth before it's raised).
    private static WorldObjectState FindHearthSite(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.BuildProduct == "campfire.spot")
            {
                return obj;
            }
        }

        return null;
    }

    // Spec 27.18A: NPCs know their home layout at start — every bootstrap
    // object becomes a permanent memory record for every NPC.
    private static void SeedHomeKnowledge(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var obj in world.Entities.Objects.Values)
            {
                npc.Memory.KnownObjects[obj.Id] = new Memory.ObjectMemory
                {
                    Id = obj.Id,
                    DefinitionId = obj.DefinitionId,
                    Tile = obj.Tile,
                    Junction = obj.Junctions.Count > 0 ? obj.Junctions[0] : null,
                    IsPermanent = true,
                    LastSeenTick = 0
                };
            }
        }
    }

    private void AddFragment(WorldState world, FragmentBootstrap bootstrap)
    {
        var fragmentId = new FragmentId(bootstrap.Id);
        var fragment = new Fragment { Id = fragmentId };
        world.Fragments.Items[fragmentId] = fragment;

        foreach (var tileBootstrap in bootstrap.Tiles)
        {
            var coord = new TileCoord(tileBootstrap.Q, tileBootstrap.R);
            var tile = new Tile
            {
                Coord = coord,
                Flags = GetTileFlags(tileBootstrap),
                Elevation = tileBootstrap.Elevation
            };

            fragment.Tiles[coord] = tile;
            world.Tiles.Items[coord] = tile;

            world.Occupancy.EntitiesInTile[coord] = new List<EntityId>();
        }

        GenerateJunctions(world, fragmentId, fragment);

        foreach (var tileBootstrap in bootstrap.Tiles)
        {
            if (tileBootstrap.BlockedSlots.Count == 0)
            {
                continue;
            }

            var coord = new TileCoord(tileBootstrap.Q, tileBootstrap.R);
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var slot in tileBootstrap.BlockedSlots)
            {
                if (slot >= 0 && slot < tile.Junctions.Count)
                {
                    var junctionId = tile.Junctions[slot];
                    if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                    {
                        junction.Blocked = true;
                    }
                }
            }
        }
    }

    private void GenerateJunctions(WorldState world, FragmentId fragmentId, Fragment fragment)
    {
        foreach (var pair in fragment.Tiles)
        {
            var tile = pair.Value;

            foreach (var template in HexPointLayout.GetInteriorTemplates())
            {
                var key = HexPointLayout.GetJunctionKeyPair(tile.Coord, template.SubAxial);
                var junction = CreateJunction(world, fragmentId, tile, template, key);
                tile.Junctions.Add(junction.Id);
            }

            foreach (var template in HexPointLayout.GetBoundaryTemplates())
            {
                var key = HexPointLayout.GetJunctionKeyPair(tile.Coord, template.SubAxial);

                if (_junctionsByKey.TryGetValue(key, out var existingId))
                {
                    var existing = world.Junctions.Items[existingId];
                    if (!existing.Tiles.Contains(tile.Coord))
                    {
                        existing.Tiles.Add(tile.Coord);
                    }

                    tile.Junctions.Add(existingId);
                }
                else
                {
                    var junction = CreateJunction(world, fragmentId, tile, template, key);
                    tile.Junctions.Add(junction.Id);
                }
            }
        }
    }

    private Junction CreateJunction(WorldState world, FragmentId fragmentId, Tile tile, JunctionTemplate template, (int, int) key)
    {
        var junction = new Junction
        {
            Id = new JunctionId(_nextJunctionValue++),
            Fragment = fragmentId,
            WorldPosition = HexSpatialMath.TileToWorld(tile.Coord) + template.Offset
        };
        junction.Tiles.Add(tile.Coord);

        world.Junctions.Items[junction.Id] = junction;
        world.Occupancy.JunctionOwner[junction.Id] = null;
        _junctionsByKey[key] = junction.Id;

        return junction;
    }

    private void BuildAdjacency(WorldState world)
    {
        foreach (var pair in _junctionsByKey)
        {
            var key = pair.Key;
            var junctionId = pair.Value;
            var junction = world.Junctions.Items[junctionId];

            foreach (var offset in HexPointLayout.NeighborKeyOffsets)
            {
                var neighborKey = (key.Item1 + offset.dx, key.Item2 + offset.dy);
                if (_junctionsByKey.TryGetValue(neighborKey, out var neighborId))
                {
                    if (!junction.Neighbors.Contains(neighborId))
                    {
                        junction.Neighbors.Add(neighborId);
                    }
                }
            }
        }
    }

    // Spec 20.16: cliffs are junction blocks — a boundary junction whose
    // owning LAND tiles differ by more than one level is impassable, and
    // junctions living entirely on unwalkable sea are closed outright.
    private static void BlockCliffAndSeaJunctions(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0)
            {
                continue;
            }

            var anyWalkable = false;
            var minElevation = int.MaxValue;
            var maxElevation = int.MinValue;
            foreach (var coord in junction.Tiles)
            {
                if (!world.Tiles.Items.TryGetValue(coord, out var tile))
                {
                    continue;
                }

                if (tile.Flags.HasFlag(TileFlags.Walkable))
                {
                    anyWalkable = true;
                }

                minElevation = System.Math.Min(minElevation, tile.Elevation);
                maxElevation = System.Math.Max(maxElevation, tile.Elevation);
            }

            if (!anyWalkable)
            {
                junction.Blocked = true; // open sea
                continue;
            }

            if (junction.Tiles.Count > 1 && maxElevation - minElevation > 1)
            {
                junction.Blocked = true; // cliff face
            }
            else if (junction.Tiles.Count > 1 && maxElevation - minElevation == 1)
            {
                // Spec 40.17: a walkable junction straddling a single step is a
                // climb seam — crossable, but the pathfinder charges 2x.
                world.ClimbSeams.Add(junction.Id);
            }
        }
    }

    // Spec 40.18: open a one-deep swimmable ring — sea junctions that touch
    // walkable land become crossable (unblocked + tagged SwimJunctions), so the
    // pathfinder can enter the water at a steep cost. Deeper sea stays blocked,
    // so the ring is a dead-end until a second land mass gives it a far shore.
    private static void OpenSwimRing(WorldState world)
    {
        var opened = new System.Collections.Generic.List<Common.JunctionId>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (!junction.Blocked || !SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            foreach (var neighborId in junction.Neighbors)
            {
                if (world.Junctions.Items.TryGetValue(neighborId, out var neighbor) &&
                    !neighbor.Blocked && !SpatialQueries.IsAllWaterJunction(world, neighborId))
                {
                    opened.Add(junction.Id);
                    break;
                }
            }
        }

        foreach (var id in opened)
        {
            world.Junctions.Items[id].Blocked = false;
            world.SwimJunctions.Add(id);
        }

        OpenStraitCorridor(world);
    }

    // Spec 40.18: flood the SE strait box so a connected swim path bridges the
    // peninsula to the second island (the one-deep ring alone can't cross a full
    // water tile). Bounded to the SE corner the home colony never routes into.
    private static void OpenStraitCorridor(WorldState world)
    {
        var opened = new System.Collections.Generic.List<Common.JunctionId>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (!junction.Blocked || !SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            var inStrait = junction.Tiles.Count > 0;
            foreach (var coord in junction.Tiles)
            {
                if (coord.Q < 7 || coord.Q > 10 || coord.R < 2 || coord.R > 6)
                {
                    inStrait = false;
                    break;
                }
            }

            if (inStrait)
            {
                opened.Add(junction.Id);
            }
        }

        foreach (var id in opened)
        {
            world.Junctions.Items[id].Blocked = false;
            world.SwimJunctions.Add(id);
            world.StraitJunctions.Add(id); // spec 40.18 step 4: cheap crossing
        }
    }

    private static void BlockEdgeJunctions(WorldState world)
    {
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Tiles.Count == 1 && junction.Neighbors.Count < 6)
            {
                junction.Blocked = true;
            }
        }
    }

    private void AddObject(WorldState world, ObjectBootstrap bootstrap)
    {
        var tileCoord = new TileCoord(bootstrap.TileQ, bootstrap.TileR);
        if (!world.Tiles.Items.TryGetValue(tileCoord, out var tile))
        {
            throw new InvalidOperationException($"Object {bootstrap.Id} references missing tile {tileCoord}.");
        }

        var worldObject = new WorldObjectState
        {
            Id = new ObjectId(bootstrap.Id),
            DefinitionId = bootstrap.DefinitionId,
            Fragment = new FragmentId(bootstrap.FragmentId),
            Tile = tileCoord,
            ResourceAmount = 1f
        };

        foreach (var slot in bootstrap.JunctionSlots)
        {
            if (slot < 0 || slot >= tile.Junctions.Count)
            {
                throw new InvalidOperationException(
                    $"Object {bootstrap.Id} references invalid junction slot {slot} on tile {tileCoord}. " +
                    $"Available slot range: 0..{tile.Junctions.Count - 1}.");
            }

            worldObject.Junctions.Add(tile.Junctions[slot]);
        }

        world.Entities.Objects[worldObject.Id] = worldObject;
        if (!world.Caches.ObjectsByTile.TryGetValue(tileCoord, out var objects))
        {
            objects = new List<ObjectId>();
            world.Caches.ObjectsByTile[tileCoord] = objects;
        }

        objects.Add(worldObject.Id);

        // Spec 31C.1/31C.7: obstacles block their anchor (and footprint).
        WorldObjectMutations.SetObstacleBlocking(world, worldObject, blocked: true);

        if (worldObject.Id.Value >= world.NextRuntimeObjectId)
        {
            world.NextRuntimeObjectId = worldObject.Id.Value + 1;
        }
    }

    private void AddNpc(WorldState world, NpcBootstrap bootstrap)
    {
        var coord = new TileCoord(bootstrap.TileQ, bootstrap.TileR);
        var npc = new NPCState
        {
            Id = new EntityId(bootstrap.Id),
            DisplayName = bootstrap.DisplayName,
            ActorMesh = bootstrap.ActorMesh,
            Fragment = new FragmentId(bootstrap.FragmentId),
            Tile = coord,
            Position = HexSpatialMath.TileToWorld(coord)
        };

        npc.Needs.Hunger = bootstrap.Hunger;
        npc.Needs.Thirst = bootstrap.Thirst;
        npc.Needs.Energy = bootstrap.Energy;
        npc.Needs.Comfort = bootstrap.Comfort;
        npc.Needs.Social = bootstrap.Social;
        npc.Needs.ThermalDiscomfort = bootstrap.ThermalDiscomfort;

        // Spec §53: personality compassion weight, drawn once and fixed for life.
        // Spreads the colony from reserved (helps only when idle) to deeply
        // caring (breaks off her own chores to tend the hurt). Deterministic on
        // the world seed + npc id so a replay is identical.
        npc.CompassionTrait = HexLive.Simulation.Runtime.Spec53.TraitMin +
            MathUtil.Hash01(world.Seed, bootstrap.Id, 53, 5301) *
            (HexLive.Simulation.Runtime.Spec53.TraitMax - HexLive.Simulation.Runtime.Spec53.TraitMin);

        // Spec 29H: everyone carries a personal water bottle (starts empty) — the
        // only starting kit. §54 cold start: the spear is no longer handed out,
        // it must be crafted (1 stick at the fire), like every other tool.
        npc.Inventory.Items.Add(new Agents.ItemInstance("tool.bottle"));
        // Spec 40.3: two bandages start in the med pouch (Needs.Bandages),
        // not the general pack.

        world.Entities.Npcs[npc.Id] = npc;
        world.Occupancy.EntitiesInTile[coord].Add(npc.Id);

        if (!world.Caches.EntitiesByTile.TryGetValue(coord, out var tileEntities))
        {
            tileEntities = new List<EntityId>();
            world.Caches.EntitiesByTile[coord] = tileEntities;
        }

        tileEntities.Add(npc.Id);

        if (!world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var fragmentEntities))
        {
            fragmentEntities = new List<EntityId>();
            world.Caches.EntitiesByFragment[npc.Fragment] = fragmentEntities;
        }

        fragmentEntities.Add(npc.Id);
    }

    private static TileFlags GetTileFlags(TileBootstrap bootstrap)
    {
        var flags = TileFlags.None;
        if (bootstrap.Walkable)
        {
            flags |= TileFlags.Walkable;
        }

        if (bootstrap.Blocked)
        {
            flags |= TileFlags.Blocked;
        }

        if (bootstrap.Indoor)
        {
            flags |= TileFlags.Indoor;
        }

        if (bootstrap.Water)
        {
            flags |= TileFlags.Water;
        }

        return flags;
    }
}

}
