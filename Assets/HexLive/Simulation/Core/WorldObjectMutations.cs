using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;

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
        if (worldObject.IsArchitectureElement)
        {
            // Each LEGO object owns the exact junctions it changed. Never run
            // the furniture radius algorithm for a wall bay: removing one bay
            // must reopen only that bay, not rebuild or erase the whole hut.
            if (!blocked)
            {
                var architectureChanged = ReleaseOwnedBlocking(world, worldObject);
                if (worldObject.DefinitionId == "architecture.door.wood")
                {
                    foreach (var junctionId in worldObject.Junctions)
                    {
                        if (!world.Junctions.Items.TryGetValue(junctionId, out var junction)) continue;
                        junction.Door = false;
                        architectureChanged = true;
                    }
                }
                if (architectureChanged) world.TopologyVersion++;
            }
            return;
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            return;
        }

        // §54.9A (revised): a build-site used to pre-claim the full physical
        // footprint of the piece it will BECOME. But a large footprint (the
        // 1.39-wu leaf bed) blocks not just the site's tile but its whole
        // approach ring — so ReachableBeside fails and the builder can never
        // walk up to deposit/raise it (0 deliveries; the bed is unbuildable on
        // any tight fireside). A site is still just an intent marker, not a
        // solid object, so while it is ASSEMBLING (DefinitionId "build.site")
        // it does NOT obstacle-block; it stays walkable. Overlap is already
        // prevented without the block: furniture sites stake ONE AT A TIME
        // (bedSitesInProgress/rackSitesInProgress gates), TileHoldsStructure
        // reserves the tile, and the RAISED piece claims the real footprint via
        // its own SpawnObject. Only that finished piece (bed.leaf, campfire…)
        // swaps in the product footprint below.
        if (worldObject.DefinitionId == "build.site")
        {
            return;
        }

        if (!string.IsNullOrEmpty(worldObject.BuildProduct) &&
            world.Content.ObjectDefinitions.TryGetValue(worldObject.BuildProduct, out var productDefinition))
        {
            definition = productDefinition;
        }

        if (!definition.HasTag("Obstacle"))
        {
            return;
        }

        var changed = false;
        if (blocked)
        {
            worldObject.BlockedJunctions.Clear();
            foreach (var junctionId in CollectObstacleJunctions(world, worldObject, definition.ObstacleRadius))
            {
                if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                {
                    if (!worldObject.BlockedJunctions.Contains(junctionId))
                        worldObject.BlockedJunctions.Add(junctionId);
                    if (!junction.Blocked)
                    {
                        junction.Blocked = true;
                        changed = true;
                    }

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
            changed = ReleaseOwnedBlocking(world, worldObject);
        }

        if (changed)
        {
            world.TopologyVersion++;
        }
    }

    /// <summary>
    /// Applies the exact authored junction footprint around the piece's visible
    /// centre. This is the indoor-furniture path: beds, wardrobes and the small
    /// hearth are not radial obstacles, and an integrated bed's route anchor is
    /// deliberately different from its mesh/lying centre (§120, #154).
    /// </summary>
    internal static void SetAuthoredFurnitureBlocking(
        WorldState world,
        WorldObjectState worldObject,
        bool blocked,
        Float2? authoredCenter = null)
    {
        if (world == null || worldObject == null) return;

        var footprintId = worldObject.DefinitionId == ContentIds.Campfire &&
            worldObject.Variant == BuildingRules.HutHearthVariant
                ? "furniture.hearth"
                : worldObject.DefinitionId;
        if (!BlueprintFurnitureFootprints.HasFootprint(footprintId))
        {
            SetObstacleBlocking(world, worldObject, blocked);
            return;
        }

        var changed = ReleaseOwnedBlocking(world, worldObject);
        if (!blocked)
        {
            if (changed) world.TopologyVersion++;
            return;
        }

        if (worldObject.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchor))
        {
            if (changed) world.TopologyVersion++;
            return;
        }

        var yawStep = ((int)System.MathF.Round(worldObject.RotationDegrees / 60f) % 6 + 6) % 6;
        var center = authoredCenter ??
            anchor.WorldPosition + BlueprintFurnitureFootprints.CentroidOffset(footprintId, yawStep);
        const float exactLatticeToleranceSq = 0.001f * 0.001f;
        foreach (var offset in BlueprintFurnitureFootprints.CenteredWorldOffsets(footprintId, yawStep))
        {
            var point = center + offset;
            if (FindExactJunction(world, point, exactLatticeToleranceSq) is not { } junctionId ||
                !world.Junctions.Items.TryGetValue(junctionId, out var junction))
            {
                continue;
            }

            if (junction.Door)
            {
                continue;
            }

            // Record shared ownership too. The approved canonical layout has
            // one bed-corner/wardrobe-end junction in common; removing either
            // piece must not open a point still occupied by the other.
            if (!worldObject.BlockedJunctions.Contains(junctionId))
                worldObject.BlockedJunctions.Add(junctionId);
            if (!junction.Blocked)
            {
                junction.Blocked = true;
                changed = true;
            }

            foreach (var bystander in world.Entities.Npcs.Values)
            {
                if (bystander.CurrentJunction is { } current && current.Equals(junctionId))
                    bystander.CurrentJunction = null;
            }
        }

        if (worldObject.BlockedJunctions.Count > 0) changed = true;
        if (changed) world.TopologyVersion++;
    }

    private static JunctionId? FindExactJunction(
        WorldState world, Float2 point, float toleranceSq)
    {
        foreach (var pair in world.Junctions.Items)
        {
            var delta = pair.Value.WorldPosition - point;
            if (delta.X * delta.X + delta.Y * delta.Y <= toleranceSq)
                return pair.Key;
        }

        return null;
    }

    /// <summary>
    /// Releases only this object's ownership. Kept internal for architecture
    /// repair, which rebuilds wall and furniture topology in separate passes.
    /// The caller increments TopologyVersion once after its whole transaction.
    /// </summary>
    internal static bool ReleaseOwnedBlocking(
        WorldState world, WorldObjectState worldObject)
    {
        var changed = false;
        foreach (var junctionId in worldObject.BlockedJunctions)
        {
            var heldByOther = false;
            foreach (var other in world.Entities.Objects.Values)
            {
                if (other.Id.Equals(worldObject.Id)) continue;
                if (other.BlockedJunctions.Contains(junctionId))
                {
                    heldByOther = true;
                    break;
                }
            }

            if (!heldByOther &&
                world.Junctions.Items.TryGetValue(junctionId, out var junction) &&
                junction.Blocked)
            {
                junction.Blocked = false;
                changed = true;
            }
        }

        worldObject.BlockedJunctions.Clear();
        return changed;
    }

    /// <summary>
    /// A completed door throat outranks every stale obstacle owner. Topology
    /// repair calls this before rebuilding walls/furniture so a rotated or old
    /// save cannot retain a furniture footprint across the corridor (§120).
    /// </summary>
    internal static void ClearBlockingOwnershipAt(WorldState world, JunctionId junctionId)
    {
        foreach (var worldObject in world.Entities.Objects.Values)
            worldObject.BlockedJunctions.Remove(junctionId);
        if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
            junction.Blocked = false;
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
