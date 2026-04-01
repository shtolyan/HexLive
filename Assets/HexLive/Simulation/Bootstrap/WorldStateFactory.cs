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
            TickDeltaTime = bootstrap.Simulation.TickDeltaTime
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

        foreach (var objectBootstrap in bootstrap.Objects)
        {
            AddObject(world, objectBootstrap);
        }

        foreach (var npcBootstrap in bootstrap.Npcs)
        {
            AddNpc(world, npcBootstrap);
        }

        return world;
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
                Flags = GetTileFlags(tileBootstrap)
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
    }

    private void AddNpc(WorldState world, NpcBootstrap bootstrap)
    {
        var coord = new TileCoord(bootstrap.TileQ, bootstrap.TileR);
        var npc = new NPCState
        {
            Id = new EntityId(bootstrap.Id),
            Fragment = new FragmentId(bootstrap.FragmentId),
            Tile = coord,
            Position = HexSpatialMath.TileToWorld(coord)
        };

        npc.Needs.Hunger = bootstrap.Hunger;
        npc.Needs.Energy = bootstrap.Energy;
        npc.Needs.Comfort = bootstrap.Comfort;
        npc.Needs.Social = bootstrap.Social;
        npc.Needs.ThermalDiscomfort = bootstrap.ThermalDiscomfort;

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

        return flags;
    }
}

}
