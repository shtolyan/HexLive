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
    private readonly System.Collections.Generic.List<WorldObjectState> _regrown = new();

    public string Name => nameof(FruitProductionSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.PerChunk;

    private readonly System.Collections.Generic.List<WorldObjectState> _producers = new();
    private readonly System.Collections.Generic.List<WorldObjectState> _tickable = new();

    public void Run(WorldState world)
    {
        // Spec 31C.1: unclaimed fruit rots after 2400 ticks — drops on
        // unreachable junctions no longer litter the world forever.
        _rotted.Clear();
        // §156: порог якорный (Tick - SpawnTick), поэтому проспавший плод
        // сгниёт обычным кодом на первом бодром такте — обход по бодрым чанкам
        // это вся правка.
        ChunkMath.CollectTickable(world, _tickable);
        foreach (var candidate in _tickable)
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
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "ProduceRotted", $"Obj={rottedId.Value}");

            }
        }

        // Bug #315 (вердикт игрока): из пня через WorldBalance.StumpRegrowTicks
        // (20 игровых дней) вырастает новая пальма — сразу ОБЫЧНАЯ tree.palm.
        // Промежуточная «молодая» модель (tree.palm_small) выпилена из игры:
        // она была единственным её потребителем, а мир от неё необратимо мельчал
        // (два бревна вместо трёх навсегда). Пень с сидящей на нём не трогаем —
        // вырастет тиком позже.
        _regrown.Clear();
        foreach (var candidate in _tickable)
        {
            if (candidate.DefinitionId == ContentIds.PalmStump &&
                candidate.SpawnTick > 0 && !candidate.IsOccupied &&
                world.Tick - candidate.SpawnTick > WorldBalance.StumpRegrowTicks)
            {
                _regrown.Add(candidate);
            }
        }

        foreach (var stump in _regrown)
        {
            var anchor = stump.Junctions.Count > 0
                ? stump.Junctions[0]
                : default;
            if (!world.Junctions.Items.ContainsKey(anchor))
            {
                continue;
            }

            var tile = stump.Tile;
            var fragment = stump.Fragment;
            WorldObjectMutations.DespawnObject(world, stump.Id);
            var palm = WorldObjectMutations.SpawnObject(
                world, ContentIds.Palm, fragment, tile, anchor);
            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "PalmRegrown",
                    $"Obj={palm.Id.Value} Tile={tile.Q},{tile.R} from stump");
            }
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
        // §156.4: проспавшая пальма выложит ОДИН плод и заведёт интервал
        // заново — так уже устроен код ниже, и это честнее, чем выдумывать
        // историю урожая: лишние плоды всё равно упёрлись бы в MaxConcurrent
        // и сгнили бы по своему якорю раньше, чем кто-то пришёл.
        //
        // Пересобираем список: выше могли исчезнуть сгнившие плоды и пни.
        ChunkMath.CollectTickable(world, _tickable);
        _producers.Clear();
        foreach (var obj in _tickable)
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
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "ProduceSkipped",
                        $"Obj={producer.Id.Value} Def={producer.DefinitionId} CapReached " +
                        $"({producer.ProducedItems.Count}/{produce.MaxConcurrent})");
                }
                continue;
            }

            var (dropTile, dropJunction) = FindDropSpot(world, producer);
            if (dropJunction is null)
            {
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "ProduceSkipped",
                        $"Obj={producer.Id.Value} Def={producer.DefinitionId} NoFreeSpot " +
                        $"(retry at tick {producer.NextProductionTick})");
                }
                continue;
            }

            var spawned = WorldObjectMutations.SpawnObject(
                world, produce.ProducedDefinitionId, producer.Fragment, dropTile, dropJunction.Value);
            producer.ProducedItems.Add(spawned.Id);

            if (SimTrace.Enabled)
            {
                Trace.DebugSystem(world, "ProduceDropped",
                    $"Producer={producer.Id.Value} Spawned={spawned.Id.Value} Def={produce.ProducedDefinitionId} " +
                    $"Tile={dropTile.Q},{dropTile.R} Junction={dropJunction.Value.Value} " +
                    $"Concurrent={producer.ProducedItems.Count}/{produce.MaxConcurrent}");
            }
        }
    }

    // §26.6A r5: a nut is dropped by the palm, and the palm is the one obstacle
    // guaranteed to be next to it — so the old first-fit routinely wedged the
    // fruit against the trunk. That was survivable while the hand could reach
    // THROUGH the trunk; once it cannot, such a nut is simply food that rots.
    // MEASURED before this: 12 seeds × 10 days, nuts dropped unchanged
    // (1270 vs 1282) but picked up 1772 → 1595 and rotted 625 → 651.
    //
    // So the drop is now scored, not first-fit: prefer the spot with the most
    // legal approach cells — exactly the rim the forager will later be held to,
    // asked through the same predicate, so producer and planner cannot disagree.
    // Determinism is preserved: the same fixed tile/slot order, strictly-greater
    // comparison (ties keep the earlier candidate), and the search stops as soon
    // as a spot is open enough, so a cramped grove still gets its drop.
    private const int ApproachesGoodEnough = 4;

    // Deterministic: producer tile first, then hex neighbors in fixed direction
    // order; within a tile, junctions in slot order (spec 29A.2, v1 distance 1).
    private static (TileCoord, JunctionId?) FindDropSpot(WorldState world, WorldObjectState producer)
    {
        var candidateTiles = new System.Collections.Generic.List<TileCoord> { producer.Tile };
        foreach (var neighbor in SpatialQueries.GetNeighbors(world, producer.Tile))
        {
            candidateTiles.Add(neighbor);
        }

        // §29A r2: the trunk blocks one ObstacleRadius of junctions around
        // itself, and the drop keeps a further clearance ring on top — a nut
        // wedged against the blocked ring would have half its approaches shut.
        // Strict: no spot outside the clearance means no drop this interval
        // (the producer retries), never a drop inside it.
        var clearanceSq = 0f;
        var anchor = default(Float2);
        if (world.Content.ObjectDefinitions.TryGetValue(producer.DefinitionId, out var producerDef) &&
            producerDef.ObstacleRadius > 0f &&
            producer.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(producer.Junctions[0], out var anchorJunction))
        {
            var clearance = producerDef.ObstacleRadius * WorldBalance.FruitDropClearanceFactor;
            clearanceSq = clearance * clearance;
            anchor = anchorJunction.WorldPosition;
        }

        TileCoord bestTile = default;
        JunctionId? best = null;
        var bestApproaches = -1;

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

                if (clearanceSq > 0f &&
                    world.Junctions.Items.TryGetValue(junctionId, out var junction))
                {
                    var dx = junction.WorldPosition.X - anchor.X;
                    var dy = junction.WorldPosition.Y - anchor.Y;
                    if (dx * dx + dy * dy < clearanceSq)
                    {
                        continue;
                    }
                }

                var approaches = InteractionReach.CountApproaches(world, junctionId);
                if (approaches > bestApproaches)
                {
                    bestApproaches = approaches;
                    bestTile = tileCoord;
                    best = junctionId;
                    if (approaches >= ApproachesGoodEnough)
                    {
                        return (bestTile, best); // open enough; stop looking
                    }
                }
            }
        }

        // Every candidate walled in (or none at all): fall back to the best seen
        // rather than skipping the drop — a hard-to-reach nut still beats none.
        return best is null ? (producer.Tile, null) : (bestTile, best);
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
