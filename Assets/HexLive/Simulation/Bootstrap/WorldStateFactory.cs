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
    private int _nextPointValue = 1;
    private int _nextConnectionGroupValue = 1;
    private readonly Dictionary<string, ConnectionGroup> _connectionGroupsByPositionKey = new Dictionary<string, ConnectionGroup>();

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

        GeneratePoints(world, fragmentId, fragment);
    }

    private void GeneratePoints(WorldState world, FragmentId fragmentId, Fragment fragment)
    {
        foreach (var pair in fragment.Tiles)
        {
            var tile = pair.Value;
            foreach (var template in HexPointLayout.GetInteriorTemplates())
            {
                var point = new Point
                {
                    Id = new PointId(_nextPointValue++),
                    Fragment = fragmentId,
                    AnchorTile = tile.Coord,
                    Role = template.Role,
                    LocalOffset = template.Offset,
                    Kind = PointKind.Interior
                };
                point.Tiles.Add(tile.Coord);
                world.Points.Items[point.Id] = point;
                world.Occupancy.PointOwner[point.Id] = null;
                tile.Points.Add(point.Id);
            }

            foreach (var template in HexPointLayout.GetConnectionTemplates())
            {
                var point = new Point
                {
                    Id = new PointId(_nextPointValue++),
                    Fragment = fragmentId,
                    AnchorTile = tile.Coord,
                    Role = template.Role,
                    LocalOffset = template.Offset,
                    Kind = PointKind.Connection
                };
                point.Tiles.Add(tile.Coord);

                var worldPosition = HexSpatialMath.PointToWorld(tile.Coord, point.LocalOffset);
                var key = GetConnectionPositionKey(worldPosition);
                if (!_connectionGroupsByPositionKey.TryGetValue(key, out var connectionGroup))
                {
                    connectionGroup = new ConnectionGroup
                    {
                        Id = new ConnectionGroupId(_nextConnectionGroupValue++)
                    };
                    _connectionGroupsByPositionKey[key] = connectionGroup;
                    world.ConnectionGroups.Items[connectionGroup.Id] = connectionGroup;
                }

                point.ConnectionGroupId = connectionGroup.Id;
                connectionGroup.Points.Add(point.Id);
                if (!connectionGroup.Tiles.Contains(tile.Coord))
                {
                    connectionGroup.Tiles.Add(tile.Coord);
                }

                world.Points.Items[point.Id] = point;
                world.Occupancy.PointOwner[point.Id] = null;
                tile.Points.Add(point.Id);
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

        foreach (var slot in bootstrap.PointSlots)
        {
            if (slot < 0 || slot >= tile.Points.Count)
            {
                throw new InvalidOperationException(
                    $"Object {bootstrap.Id} references invalid point slot {slot} on tile {tileCoord}. " +
                    $"Available slot range: 0..{tile.Points.Count - 1}.");
            }

            worldObject.Points.Add(tile.Points[slot]);
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

    private static string GetConnectionPositionKey(Float2 position)
    {
        var x = (int)Math.Round(position.X * 10000f);
        var y = (int)Math.Round(position.Y * 10000f);
        return string.Format("{0}:{1}", x, y);
    }
}

}
