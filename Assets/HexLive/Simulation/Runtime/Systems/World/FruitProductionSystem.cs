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

// Spec 29A: producers (e.g. apple trees) periodically drop their produce
// on a free junction of a nearby walkable tile.
public sealed class FruitProductionSystem : ISimulationSystem
{
    private readonly System.Collections.Generic.List<ObjectId> _rotted = new();

    public string Name => nameof(FruitProductionSystem);

    public TickLayer Layer => TickLayer.Slow;

    private readonly System.Collections.Generic.List<WorldObjectState> _producers = new();

    public void Run(WorldState world)
    {
        // Spec 31C.1: unclaimed fruit rots after 2400 ticks — drops on
        // unreachable junctions no longer litter the world forever.
        _rotted.Clear();
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if ((candidate.DefinitionId == ContentIds.Coconut ||
                 candidate.DefinitionId == ContentIds.CoconutPierced ||
                 candidate.DefinitionId == ContentIds.CoconutOpen) &&
                candidate.SpawnTick > 0 && world.Tick - candidate.SpawnTick > WorldBalance.FruitRotTicks &&
                !candidate.IsOccupied)
            {
                _rotted.Add(candidate.Id);
            }
        }

        foreach (var rottedId in _rotted)
        {
            WorldObjectMutations.DespawnObject(world, rottedId);
            Trace.EmitSystem(world, "ProduceRotted", $"Obj={rottedId.Value}");
        }

        // Spec 29A.2/19.7A: production only in the "daylight" half. Timers are
        // left untouched overnight, so overdue producers fire at dawn.
        // §19.7B: the half is measured on the EVENT CYCLE, not the stretched
        // visual day. Coconuts are the island's only water, and on the visual
        // clock the dead window grew 1200 -> 12000 ticks: the grove could not
        // replenish for 50 real minutes while the colony kept drinking, and a
        // 72000-tick soak went from 0 deaths to 3, every one of them
        // "thirst reached the death threshold". Keeping the gate on the cycle
        // preserves both the real-time yield AND the dawn-burst rhythm.
        var cyclePhase = (world.Tick % EnvironmentSystem.EventCycleTicks) /
            (float)EnvironmentSystem.EventCycleTicks;
        if (cyclePhase >= 0.5f)
        {
            return;
        }

        // Snapshot producers first: spawning mutates Entities.Objects mid-iteration.
        _producers.Clear();
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                definition.Produce != null)
            {
                _producers.Add(obj);
            }
        }

        foreach (var producer in _producers)
        {
            var produce = world.Content.ObjectDefinitions[producer.DefinitionId].Produce;
            if (produce is null || world.Tick < producer.NextProductionTick)
            {
                continue;
            }

            // A failed drop also waits the full interval (spec 29A.2).
            producer.NextProductionTick = world.Tick + produce.IntervalTicks;

            producer.ProducedItems.RemoveAll(id => !world.Entities.Objects.ContainsKey(id));
            if (producer.ProducedItems.Count >= produce.MaxConcurrent)
            {
                Trace.EmitSystem(world, "ProduceSkipped",
                    $"Obj={producer.Id.Value} Def={producer.DefinitionId} CapReached " +
                    $"({producer.ProducedItems.Count}/{produce.MaxConcurrent})");
                continue;
            }

            var (dropTile, dropJunction) = FindDropSpot(world, producer);
            if (dropJunction is null)
            {
                Trace.EmitSystem(world, "ProduceSkipped",
                    $"Obj={producer.Id.Value} Def={producer.DefinitionId} NoFreeSpot " +
                    $"(retry at tick {producer.NextProductionTick})");
                continue;
            }

            var spawned = WorldObjectMutations.SpawnObject(
                world, produce.ProducedDefinitionId, producer.Fragment, dropTile, dropJunction.Value);
            producer.ProducedItems.Add(spawned.Id);

            Trace.EmitSystem(world, "ProduceDropped",
                $"Producer={producer.Id.Value} Spawned={spawned.Id.Value} Def={produce.ProducedDefinitionId} " +
                $"Tile={dropTile.Q},{dropTile.R} Junction={dropJunction.Value.Value} " +
                $"Concurrent={producer.ProducedItems.Count}/{produce.MaxConcurrent}");
        }
    }

    // Deterministic: producer tile first, then hex neighbors in fixed direction
    // order; within a tile, junctions in slot order (spec 29A.2, v1 distance 1).
    private static (TileCoord, JunctionId?) FindDropSpot(WorldState world, WorldObjectState producer)
    {
        var candidateTiles = new System.Collections.Generic.List<TileCoord> { producer.Tile };
        foreach (var neighbor in SpatialQueries.GetNeighbors(world, producer.Tile))
        {
            candidateTiles.Add(neighbor);
        }

        foreach (var tileCoord in candidateTiles)
        {
            if (!SpatialQueries.IsTileWalkable(world, tileCoord) ||
                !world.Tiles.Items.TryGetValue(tileCoord, out var tile))
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!SpatialQueries.IsJunctionPassable(world, junctionId) ||
                    !SpatialQueries.IsJunctionFree(world, junctionId) ||
                    IsObjectAnchor(world, tileCoord, junctionId))
                {
                    continue;
                }

                return (tileCoord, junctionId);
            }
        }

        return (producer.Tile, null);
    }

    private static bool IsObjectAnchor(WorldState world, TileCoord tile, JunctionId junctionId)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var objectIds))
        {
            return false;
        }

        foreach (var objectId in objectIds)
        {
            if (world.Entities.Objects.TryGetValue(objectId, out var obj) &&
                obj.Junctions.Contains(junctionId))
            {
                return true;
            }
        }

        return false;
    }
}

}
