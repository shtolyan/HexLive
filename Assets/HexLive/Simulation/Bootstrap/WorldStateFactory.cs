using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
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

        CreateBuildProject(world);
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

        // Spec 31A.5B: everyone starts in underwear (per-NPC instance).
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.WornItems.Add("underwear.cloth");
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

        // Spec 29H: everyone carries a personal water bottle (starts empty).
        npc.Inventory.Items.Add(new Agents.ItemInstance("tool.bottle"));
        // Spec 29F.4 (iter 32): everyone starts with a spear — a weapon is
        // always to hand (foreshadows the weapon-slot equipment). With
        // coconuts scarcer, this lets hunting actually happen from day one.
        npc.Inventory.Items.Add(new Agents.ItemInstance("tool.spear"));
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
