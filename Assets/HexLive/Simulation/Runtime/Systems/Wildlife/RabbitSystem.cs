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

// Spec 29F.1/29F.2: rabbits graze, hop, and flee; kill attempts resolve
// automatically when a spear-carrying NPC gets adjacent.
public sealed class RabbitSystem : ISimulationSystem
{
    public string Name => nameof(RabbitSystem);

    public TickLayer Layer => TickLayer.Medium;

    private const int MaxRabbits = 4;
    private const int RespawnCheckTicks = 2400; // rabbits breed fast

    private const int SpawnMinDistanceFromNpc = 3;
    private const int FleeRadiusTiles = 2;
    private const float HopChance = 0.2f; // crabs scuttle, not sprint (spec 31C.1)
    private const float KillChance = 0.5f;
    private const int SpookTicks = 150;

    private readonly System.Collections.Generic.List<Wildlife.RabbitState> _deadRabbits = new();
    private readonly System.Collections.Generic.List<JunctionId> _spawnCandidates = new();

    public void Run(WorldState world)
    {
        // Spec 41.2 v2: the timer lives in WorldState so it survives a save.
        if (world.Tick >= world.NextRabbitSpawnCheckTick)
        {
            world.NextRabbitSpawnCheckTick = world.Tick + RespawnCheckTicks;
            while (world.Rabbits.Count < MaxRabbits && TrySpawnRabbit(world))
            {
            }
        }

        _deadRabbits.Clear();
        foreach (var rabbit in world.Rabbits)
        {
            RunRabbit(world, rabbit);
        }

        foreach (var dead in _deadRabbits)
        {
            world.Rabbits.Remove(dead);
            // Spec §54: the kill leaves a carcass to be butchered (no instant loot).
            ExecutionSystem.SpawnCarcass(world, dead.Tile, dead.Junction, "rabbit");
        }
    }

    private void RunRabbit(WorldState world, Wildlife.RabbitState rabbit)
    {
        // Movement: flee from the nearest close NPC, otherwise hop around.
        NPCState? nearest = null;
        var nearestDistance = int.MaxValue;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var distance = HexSpatialMath.HexDistance(rabbit.Tile, npc.Tile);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = npc;
            }
        }

        if (nearest is not null && nearestDistance <= FleeRadiusTiles)
        {
            FleeHop(world, rabbit, nearest);
        }
        else if (MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id, 217) < HopChance)
        {
            RandomHop(world, rabbit);
        }

        // Hunt resolution (spec 29F.2).
        if (world.Tick < rabbit.SpookedUntilTick)
        {
            return;
        }

        // Spec 35.6: bow first — a hunter with an arrow shoots from <= 3
        // tiles, no adjacency chase needed.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Mind.CurrentGoal != GoalType.Hunt ||
                !npc.Body.CanUseToolsOrWeapons ||
                !npc.Inventory.Items.Contains("tool.bow") ||
                !npc.Inventory.Items.Contains("resource.arrow") ||
                HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile) > 3)
            {
                continue;
            }

            npc.Inventory.Items.Remove("resource.arrow");
            var hitRoll = MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 173 + npc.Id.Value, 806);
            Trace.Emit(world, npc.Id, "BowShot",
                $"Rabbit={rabbit.Id} Dist={HexSpatialMath.HexDistance(npc.Tile, rabbit.Tile)} Roll={hitRoll:F2}");
            if (hitRoll < 0.6f)
            {
                // Spec §54: no instant loot — the kill drops a carcass to butcher.
                if (MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 211 + npc.Id.Value, 807) < 0.4f)
                {
                    ExecutionSystem.GiveOrDrop(world, npc, "resource.arrow");
                    Trace.Emit(world, npc.Id, "ArrowRecovered", $"From rabbit {rabbit.Id}");
                }

                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(bow, Roll={hitRoll:F2}) -> carcass");
            }
            else
            {
                rabbit.SpookedUntilTick = world.Tick + SpookTicks;
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Hunt);
                Trace.Emit(world, npc.Id, "HuntMissed",
                    $"Rabbit={rabbit.Id} arrow lost in the grass (Roll={hitRoll:F2})");
            }

            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Body.CanUseToolsOrWeapons ||
                !npc.Inventory.Items.Contains("tool.spear") ||
                npc.CurrentJunction is not { } npcJunction)
            {
                continue;
            }

            var adjacent = npcJunction.Equals(rabbit.Junction) ||
                (world.Junctions.Items.TryGetValue(rabbit.Junction, out var rabbitJunction) &&
                 rabbitJunction.Neighbors.Contains(npcJunction));
            if (!adjacent)
            {
                continue;
            }

            var roll = MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id * 131 + npc.Id.Value, 605);
            if (roll < KillChance)
            {
                // Spec §54: no instant loot — the kill drops a carcass to butcher.
                _deadRabbits.Add(rabbit);
                Trace.Emit(world, npc.Id, "CrabKilled",
                    $"Crab={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R} " +
                    $"(Roll={roll:F2}) -> carcass");
            }
            else
            {
                rabbit.SpookedUntilTick = world.Tick + SpookTicks;
                PlanningSystem.SetGoalCooldown(world, npc, GoalType.Hunt);
                Trace.Emit(world, npc.Id, "HuntMissed",
                    $"Rabbit={rabbit.Id} escaped (Roll={roll:F2})");
            }

            break;
        }
    }

    private static void FleeHop(WorldState world, Wildlife.RabbitState rabbit, NPCState threat)
    {
        if (!world.Junctions.Items.TryGetValue(rabbit.Junction, out var junction))
        {
            return;
        }

        Junction? best = null;
        var bestDistance = -1f;
        foreach (var neighborId in junction.Neighbors)
        {
            if (!world.Junctions.Items.TryGetValue(neighborId, out var neighbor) ||
                neighbor.Blocked || neighbor.Door || IsIndoor(world, neighbor) ||
                SpatialQueries.IsAllWaterJunction(world, neighborId))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(neighbor.WorldPosition, threat.Position);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = neighbor;
            }
        }

        if (best is not null)
        {
            MoveRabbitTo(rabbit, best);
        }
    }

    private static void RandomHop(WorldState world, Wildlife.RabbitState rabbit)
    {
        if (!world.Junctions.Items.TryGetValue(rabbit.Junction, out var junction) ||
            junction.Neighbors.Count == 0)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, rabbit.Id, 419) * junction.Neighbors.Count);
        pick = System.Math.Min(pick, junction.Neighbors.Count - 1);
        if (world.Junctions.Items.TryGetValue(junction.Neighbors[pick], out var next) &&
            !next.Blocked && !next.Door && !IsIndoor(world, next) &&
            !SpatialQueries.IsAllWaterJunction(world, next.Id))
        {
            MoveRabbitTo(rabbit, next);
        }
    }

    private static bool IsIndoor(WorldState world, Junction junction)
    {
        return junction.Tiles.Count > 0 &&
            world.Tiles.Items.TryGetValue(junction.Tiles[0], out var tile) &&
            tile.Flags.HasFlag(TileFlags.Indoor);
    }

    private static void MoveRabbitTo(Wildlife.RabbitState rabbit, Junction next)
    {
        rabbit.Junction = next.Id;
        rabbit.Position = next.WorldPosition;
        if (next.Tiles.Count > 0)
        {
            rabbit.Tile = next.Tiles[0];
        }
    }

    private readonly System.Collections.Generic.List<TileCoord> _waterTiles = new();

    private bool TrySpawnRabbit(WorldState world)
    {
        _spawnCandidates.Clear();
        _waterTiles.Clear();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Flags.HasFlag(TileFlags.Water))
            {
                _waterTiles.Add(tile.Coord);
            }
        }

        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 || IsIndoor(world, junction) ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                continue;
            }

            // Spec 31C.1: crabs live on the river bank — within 2 tiles of water.
            var nearWater = false;
            foreach (var waterCoord in _waterTiles)
            {
                if (HexSpatialMath.HexDistance(junction.Tiles[0], waterCoord) <= 2)
                {
                    nearWater = true;
                    break;
                }
            }

            if (!nearWater)
            {
                continue;
            }

            var farEnough = true;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (HexSpatialMath.HexDistance(junction.Tiles[0], npc.Tile) < SpawnMinDistanceFromNpc)
                {
                    farEnough = false;
                    break;
                }
            }

            if (farEnough)
            {
                _spawnCandidates.Add(junction.Id);
            }
        }

        if (_spawnCandidates.Count == 0)
        {
            return false;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick, world.NextRabbitId, 947) * _spawnCandidates.Count);
        pick = System.Math.Min(pick, _spawnCandidates.Count - 1);
        var spawnJunction = world.Junctions.Items[_spawnCandidates[pick]];

        var rabbit = new Wildlife.RabbitState
        {
            Id = world.NextRabbitId++,
            Junction = spawnJunction.Id,
            Position = spawnJunction.WorldPosition,
            Tile = spawnJunction.Tiles[0]
        };
        world.Rabbits.Add(rabbit);
        Trace.EmitSystem(world, "CrabSpawned",
            $"Rabbit={rabbit.Id} at Tile={rabbit.Tile.Q},{rabbit.Tile.R}");
        return true;
    }
}

}
