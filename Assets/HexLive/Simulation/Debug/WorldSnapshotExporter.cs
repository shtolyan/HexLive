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
            Temperature = world.Environment.GlobalTemperature,
            Clock = Runtime.EnvironmentSystem.FormatClock(world.Environment.TimeOfDayNormalized),
            DayPhase = world.Environment.Phase.ToString(),
            UvIndex = world.Environment.UvIndex,
            IsRaining = world.Environment.IsRaining,
            RaftProgress = world.RaftProgress,
            RaftTarget = HexLive.Simulation.Core.WorldState.RaftTarget
        };

        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            snapshot.Tiles.Add(new TileSnapshot
            {
                Coord = tile.Coord,
                Walkable = tile.Flags.HasFlag(TileFlags.Walkable),
                Blocked = tile.Flags.HasFlag(TileFlags.Blocked),
                Indoor = tile.Flags.HasFlag(TileFlags.Indoor),
                Water = tile.Flags.HasFlag(TileFlags.Water),
                Elevation = tile.Elevation
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
                Tile = obj.Tile,
                ResourceAmount = obj.ResourceAmount
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
                DisplayName = npc.DisplayName,
                ActorMesh = npc.ActorMesh,
                Tile = npc.Tile,
                Position = npc.Position,
                RotationDegrees = npc.RotationDegrees,
                Health = npc.Health,
                IsFighting = npc.IsFighting,
                Hunger = npc.Needs.Hunger,
                Thirst = npc.Needs.Thirst,
                Energy = npc.Needs.Energy,
                Comfort = npc.Needs.Comfort,
                Social = npc.Needs.Social,
                ThermalDiscomfort = npc.Needs.ThermalDiscomfort,
                ThermalComfort = npc.Needs.ThermalComfort,
                Stamina = npc.Needs.Stamina,
                Hygiene = npc.Needs.Hygiene,
                Blood = npc.Needs.Blood,
                TanLevel = npc.Needs.TanLevel,
                Bandages = npc.Needs.Bandages,
                Pills = npc.Needs.Pills,
                IsFainted = world.Tick < npc.Mind.FaintedUntilTick,
                Stress = npc.Needs.Stress,
                CurrentGoal = npc.Mind.CurrentGoal.ToString(),
                PlanStatus = npc.Plan.Status.ToString(),
                MovementStatus = npc.Movement.Status.ToString(),
                ExecutionStatus = npc.Execution.Status.ToString(),
                CurrentInteraction = npc.Execution.CurrentInteraction?.ToString() ?? "-",
                TargetTile = npc.Plan.TargetTile,
                IsStarving = npc.Mind.IsStarving,
                InventoryCapacity = npc.Inventory.Capacity,
                GoalLockEndTick = npc.Mind.GoalLock is { } goalLock &&
                    goalLock.Goal == npc.Mind.CurrentGoal && goalLock.EndTick > world.Tick
                        ? goalLock.EndTick
                        : null
            };

            foreach (var item in npc.Inventory.Items)
            {
                npcSnapshot.InventoryItems.Add(item);
            }

            foreach (var item in npc.WornItems)
            {
                // Spec 40.11: per-garment durability for the character panel's
                // wear progress bars ("id\tdurability").
                npcSnapshot.WornDurability.Add($"{item.DefinitionId}\t{item.Durability:0.###}");
                npcSnapshot.WornItems.Add(item);
            }

            var worstPartValue = 1f;
            var worstPartName = "-";
            foreach (var part in npc.Body.Parts)
            {
                npcSnapshot.BodyParts.Add($"{part.Key}={part.Value:F2}");
                if (part.Value < worstPartValue)
                {
                    worstPartValue = part.Value;
                    worstPartName = part.Key.ToString();
                }
            }

            npcSnapshot.WorstBodyPart = worstPartValue < 1f
                ? $"{worstPartName} {worstPartValue:F2}"
                : "OK";

            foreach (var cooldown in npc.Mind.Cooldowns)
            {
                if (cooldown.EndTick > world.Tick)
                {
                    npcSnapshot.CooldownGoals.Add($"{cooldown.Goal}:{cooldown.EndTick}");
                }
            }

            foreach (var relation in npc.Social.Relationships)
            {
                var otherName = world.Entities.Npcs.TryGetValue(relation.Key, out var otherNpc)
                    ? otherNpc.DisplayName
                    : $"NPC{relation.Key.Value}";

                npcSnapshot.Relationships.Add(
                    $"NPC{relation.Key.Value}: T={relation.Value.Trust:F2} F={relation.Value.Familiarity:F2} A={relation.Value.Affinity:F2}");

                npcSnapshot.RelationshipDetails.Add(new RelationshipSnapshot
                {
                    OtherId = relation.Key.Value,
                    OtherName = string.IsNullOrEmpty(otherName) ? $"NPC{relation.Key.Value}" : otherName,
                    Trust = relation.Value.Trust,
                    Familiarity = relation.Value.Familiarity,
                    Affinity = relation.Value.Affinity
                });
            }

            npcSnapshot.KnownObjectCount = npc.Memory.KnownObjects.Count;
            foreach (var known in npc.Memory.KnownObjects.Values)
            {
                npcSnapshot.KnownObjects.Add(
                    $"{known.DefinitionId}@{known.Tile.Q},{known.Tile.R}" +
                    (known.IsPermanent ? "" : $" (seen t{known.LastSeenTick})"));
            }

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

        foreach (var crab in world.Rabbits)
        {
            snapshot.Crabs.Add(new CrabSnapshot
            {
                Id = crab.Id,
                Tile = crab.Tile,
                Position = crab.Position
            });
        }

        foreach (var dog in world.Dogs)
        {
            snapshot.Dogs.Add(new DogSnapshot
            {
                Id = dog.Id,
                Tile = dog.Tile,
                Position = dog.Position,
                Health = dog.Health,
                Status = dog.Status.ToString()
            });
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
