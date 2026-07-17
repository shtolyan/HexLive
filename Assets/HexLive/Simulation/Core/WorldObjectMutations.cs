using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Core
{

// Runtime object lifecycle (spec 29A.3). Spawn and despawn must stay symmetric:
// every structure touched here is the full set of places an object lives in.
public static class WorldObjectMutations
{
    public static WorldObjectState SpawnObject(
        WorldState world,
        string definitionId,
        FragmentId fragment,
        TileCoord tile,
        JunctionId anchorJunction)
    {
        var worldObject = new WorldObjectState
        {
            Id = new ObjectId(world.NextRuntimeObjectId++),
            DefinitionId = definitionId,
            Fragment = fragment,
            Tile = tile,
            // §59-склад: начальный запас — из декларации объекта (вода в
            // дырявом кокосе); без склада — легаси-единица.
            ResourceAmount = world.Content.ObjectDefinitions.TryGetValue(definitionId, out var def) &&
                def.StoredAmount(Content.StoredKind.Water) > 0f
                    ? def.StoredAmount(Content.StoredKind.Water)
                    : 1f,
            SpawnTick = world.Tick
        };
        worldObject.Junctions.Add(anchorJunction);

        world.Entities.Objects[worldObject.Id] = worldObject;

        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objects))
        {
            objects = new List<ObjectId>();
            world.Caches.ObjectsByTile[tile] = objects;
        }

        objects.Add(worldObject.Id);

        SetObstacleBlocking(world, worldObject, blocked: true);

        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = null,
            Type = "ObjectSpawned",
            Message = $"Obj={worldObject.Id.Value} Def={definitionId} " +
                $"Tile={tile.Q},{tile.R} Junction={anchorJunction.Value}"
        });

        return worldObject;
    }

    public static bool DespawnObject(WorldState world, ObjectId objectId)
    {
        if (!world.Entities.Objects.TryGetValue(objectId, out var worldObject))
        {
            return false;
        }

        world.Entities.Objects.Remove(objectId);

        if (world.Caches.ObjectsByTile.TryGetValue(worldObject.Tile, out var objects))
        {
            objects.Remove(objectId);
        }

        SetObstacleBlocking(world, worldObject, blocked: false);

        // Junction reservations are owned by NPC plans, not by objects —
        // they are released by plan invalidation/interrupt cleanup (spec 23.17).

        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = null,
            Type = "ObjectDespawned",
            Message = $"Obj={objectId.Value} Def={worldObject.DefinitionId} " +
                $"Tile={worldObject.Tile.Q},{worldObject.Tile.R}"
        });

        return true;
    }

    // Spec 31C.1: Obstacle-tagged objects (trunks, boulders) are solid —
    // their anchor junctions block while the object stands. Symmetric on
    // spawn/despawn; felling a tree reopens the path via the same door.
    internal static void SetObstacleBlocking(WorldState world, WorldObjectState worldObject, bool blocked)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition) ||
            !definition.Tags.Contains("Obstacle"))
        {
            return;
        }

        var changed = false;
        if (blocked)
        {
            worldObject.BlockedJunctions.Clear();
            foreach (var junctionId in CollectObstacleJunctions(world, worldObject, definition.ObstacleRadius))
            {
                if (world.Junctions.Items.TryGetValue(junctionId, out var junction) && !junction.Blocked)
                {
                    junction.Blocked = true;
                    worldObject.BlockedJunctions.Add(junctionId);
                    changed = true;

                    // §45 r5: nudge anyone standing where the obstacle lands —
                    // same as the hut-wall builder. An NPC left standing ON a
                    // newly blocked junction keeps the (still valid) key, so
                    // the perception re-anchor never fires, pathfinding can't
                    // start, and EVERYTHING reads unreachable: on 25-day soaks
                    // 3 of 6 deaths were girls starving pinned at the campfire
                    // after a bed/rack spawned under their feet.
                    foreach (var bystander in world.Entities.Npcs.Values)
                    {
                        if (bystander.CurrentJunction is { } cj && cj.Equals(junctionId))
                        {
                            bystander.CurrentJunction = null;
                        }
                    }
                }
            }
        }
        else
        {
            // Spec 31C.7: unblock exactly what this object blocked —
            // overlapping obstacles and walls stay intact.
            foreach (var junctionId in worldObject.BlockedJunctions)
            {
                if (world.Junctions.Items.TryGetValue(junctionId, out var junction) && junction.Blocked)
                {
                    junction.Blocked = false;
                    changed = true;
                }
            }

            worldObject.BlockedJunctions.Clear();
        }

        if (changed)
        {
            world.TopologyVersion++;
        }
    }

    // Anchor junctions plus, for solid furniture, every junction of the
    // anchor tile and its neighbors within ObstacleRadius of the anchor.
    private static System.Collections.Generic.List<JunctionId> CollectObstacleJunctions(
        WorldState world, WorldObjectState worldObject, float radius)
    {
        var result = new System.Collections.Generic.List<JunctionId>(worldObject.Junctions);

        if (radius <= 0f || worldObject.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchor))
        {
            return result;
        }

        var center = anchor.WorldPosition;
        var tiles = new System.Collections.Generic.List<TileCoord> { worldObject.Tile };
        foreach (var direction in HexDirection.All)
        {
            tiles.Add(new TileCoord(worldObject.Tile.Q + direction.DQ, worldObject.Tile.R + direction.DR));
        }

        var radiusSq = radius * radius;
        foreach (var coord in tiles)
        {
            if (!world.Tiles.Items.TryGetValue(coord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction))
                {
                    continue;
                }

                var dx = junction.WorldPosition.X - center.X;
                var dy = junction.WorldPosition.Y - center.Y;
                if (dx * dx + dy * dy <= radiusSq && !result.Contains(junctionId))
                {
                    result.Add(junctionId);
                }
            }
        }

        return result;
    }
}

}
