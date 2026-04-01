using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Debug
{

public static class WorldSnapshotExporter
{
    public static WorldSnapshot Export(WorldState world)
    {
        var snapshot = new WorldSnapshot
        {
            Tick = world.Tick,
            Temperature = world.Environment.GlobalTemperature
        };

        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            snapshot.Tiles.Add(new TileSnapshot
            {
                Coord = tile.Coord,
                Walkable = tile.Flags.HasFlag(TileFlags.Walkable),
                Blocked = tile.Flags.HasFlag(TileFlags.Blocked),
                Indoor = tile.Flags.HasFlag(TileFlags.Indoor)
            });
        }

        foreach (var pair in world.Junctions.Items)
        {
            var junction = pair.Value;
            var js = new JunctionSnapshot
            {
                Id = junction.Id,
                WorldPosition = junction.WorldPosition,
                Blocked = junction.Blocked,
                Occupied = world.Occupancy.JunctionOwner.TryGetValue(junction.Id, out var owner) && owner is not null,
                Reserved = world.Reservations.Junctions.ContainsKey(junction.Id)
            };

            foreach (var tileCoord in junction.Tiles)
            {
                js.Tiles.Add(tileCoord);
            }

            foreach (var neighborId in junction.Neighbors)
            {
                js.Neighbors.Add(neighborId);
            }

            snapshot.Junctions.Add(js);
        }

        foreach (var pair in world.Entities.Objects)
        {
            var obj = pair.Value;
            var exported = new ObjectSnapshot
            {
                Id = obj.Id,
                DefinitionId = obj.DefinitionId,
                Tile = obj.Tile
            };

            foreach (var junctionId in obj.Junctions)
            {
                exported.Junctions.Add(junctionId);
            }

            snapshot.Objects.Add(exported);
        }

        foreach (var pair in world.Entities.Npcs)
        {
            var npc = pair.Value;
            var npcSnapshot = new NpcSnapshot
            {
                Id = npc.Id,
                Tile = npc.Tile,
                Position = npc.Position,
                RotationDegrees = npc.RotationDegrees,
                Hunger = npc.Needs.Hunger,
                Energy = npc.Needs.Energy,
                Comfort = npc.Needs.Comfort,
                ThermalDiscomfort = npc.Needs.ThermalDiscomfort,
                CurrentGoal = npc.Mind.CurrentGoal.ToString(),
                PlanStatus = npc.Plan.Status.ToString(),
                MovementStatus = npc.Movement.Status.ToString(),
                ExecutionStatus = npc.Execution.Status.ToString(),
                CurrentInteraction = npc.Execution.CurrentInteraction?.ToString() ?? "-",
                TargetTile = npc.Plan.TargetTile
            };

            foreach (var junctionId in npc.Movement.JunctionPath)
            {
                npcSnapshot.Path.Add(junctionId);
            }

            foreach (var goalScore in npc.Mind.LastScores)
            {
                npcSnapshot.GoalScores.Add(new GoalScoreSnapshot
                {
                    Goal = goalScore.Goal.ToString(),
                    FinalScore = goalScore.FinalScore
                });
            }

            snapshot.Npcs.Add(npcSnapshot);
        }

        foreach (var trace in world.Events.Items)
        {
            snapshot.TraceEvents.Add(new TraceEventSnapshot
            {
                Tick = trace.Tick,
                EntityId = trace.EntityId,
                Type = trace.Type,
                Message = trace.Message
            });
        }

        return snapshot;
    }
}

}
