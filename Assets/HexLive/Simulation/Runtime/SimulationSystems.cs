using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

public interface ISimulationSystem
{
    string Name { get; }

    TickLayer Layer { get; }

    void Run(WorldState world);
}

public enum TickLayer
{
    Fast,
    Medium,
    Slow
}

public interface IDecisionModel
{
}

public interface IPlanner
{
}

public interface IPathfinder
{
}

public interface IInteractionResolver
{
}

public sealed class PerceptionSystem : ISimulationSystem
{
    public string Name => nameof(PerceptionSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Perception.Objects.Clear();
            npc.Perception.Agents.Clear();
            npc.Perception.Self.Hunger = npc.Needs.Hunger;
            npc.Perception.Self.Energy = npc.Needs.Energy;
            npc.Perception.Self.Comfort = npc.Needs.Comfort;
            npc.Perception.Self.Social = npc.Needs.Social;
            npc.Perception.Self.ThermalDiscomfort = npc.Needs.ThermalDiscomfort;
            npc.Perception.Self.Tile = npc.Tile;
            npc.Perception.Self.Fragment = npc.Fragment;
            npc.Perception.Environment.Temperature = world.Environment.GlobalTemperature;
            npc.Perception.Environment.NearbyAgentsCount = world.Entities.Npcs.Count - 1;
            npc.Perception.Environment.IsCrowded = world.Entities.Npcs.Count > 2;
            npc.Perception.Environment.IsPrivate = world.Entities.Npcs.Count <= 1;
            npc.Perception.LastUpdatedTick = world.Tick;

            var npcJunction = ResolveCurrentJunction(world, npc);

            foreach (var obj in world.Entities.Objects.Values)
            {
                if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition))
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(npc.Position, HexSpatialMath.TileToWorld(obj.Tile));
                var objJunction = obj.Junctions.Count > 0 ? obj.Junctions[0] : (JunctionId?)null;
                var isReachable = npcJunction.HasValue && objJunction.HasValue &&
                    HexPathfinder.FindPath(world, npcJunction.Value, objJunction.Value).Count > 0;

                var perceived = new PerceivedObject
                {
                    Id = obj.Id,
                    Tile = obj.Tile,
                    Distance = distance,
                    IsReachable = isReachable,
                    IsOccupied = obj.IsOccupied
                };

                foreach (var interaction in definition.Interactions)
                {
                    perceived.AvailableInteractions.Add(interaction.Type);
                }

                npc.Perception.Objects.Add(perceived);
            }

            Trace.Emit(world, npc.Id, "PerceptionUpdated", $"Objects={npc.Perception.Objects.Count}");
        }
    }

    private static JunctionId? ResolveCurrentJunction(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction.HasValue && world.Junctions.Items.ContainsKey(npc.CurrentJunction.Value))
        {
            return npc.CurrentJunction;
        }

        var nearest = SpatialQueries.FindNearestJunction(world, npc.Position);
        npc.CurrentJunction = nearest;
        return nearest;
    }
}

public sealed class DecisionSystem : ISimulationSystem
{
    public string Name => nameof(DecisionSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Mind.LastScores.Clear();

            AddGoalScore(npc, GoalType.Eat, npc.Needs.Hunger, HasInteraction(npc, InteractionType.Eat));
            AddGoalScore(npc, GoalType.Sleep, 1f - npc.Needs.Energy, HasInteraction(npc, InteractionType.Sleep));
            AddGoalScore(npc, GoalType.Sit, 1f - npc.Needs.Comfort, HasInteraction(npc, InteractionType.Sit));
            AddGoalScore(npc, GoalType.Dress, npc.Needs.ThermalDiscomfort, HasInteraction(npc, InteractionType.Dress));
            AddGoalScore(npc, GoalType.Idle, 0.05f, true);

            GoalScore? best = null;
            foreach (var score in npc.Mind.LastScores)
            {
                if (best is null || score.FinalScore > best.FinalScore)
                {
                    best = score;
                }
            }

            if (best is null)
            {
                continue;
            }

            npc.Mind.CurrentGoal = best.Goal;
            npc.Mind.LastDecision = new DecisionResult
            {
                SelectedGoal = best.Goal,
                Reason = $"Selected {best.Goal} at tick {world.Tick}"
            };

            foreach (var score in npc.Mind.LastScores)
            {
                npc.Mind.LastDecision.Scores.Add(score);
            }

            Trace.Emit(world, npc.Id, "GoalSelected", best.Goal.ToString());
        }
    }

    private static void AddGoalScore(NPCState npc, GoalType goal, float needValue, bool isAvailable)
    {
        var score = new GoalScore
        {
            Goal = goal,
            BaseScore = goal == GoalType.Idle ? 0.01f : 0.1f,
            NeedModifier = needValue,
            FinalScore = isAvailable ? 0.1f + needValue : 0f
        };

        npc.Mind.LastScores.Add(score);
    }

    private static bool HasInteraction(NPCState npc, InteractionType interactionType)
    {
        foreach (var obj in npc.Perception.Objects)
        {
            if (obj.IsReachable && obj.AvailableInteractions.Contains(interactionType))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class PlanningSystem : ISimulationSystem
{
    public string Name => nameof(PlanningSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status == PlanStatus.Active && npc.Plan.Goal == npc.Mind.CurrentGoal)
            {
                continue;
            }

            npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.Goal = npc.Mind.CurrentGoal;

            var interactionType = GoalToInteraction(npc.Mind.CurrentGoal);
            if (interactionType is null)
            {
                npc.Plan.Status = PlanStatus.Completed;
                continue;
            }

            PerceivedObject? selected = null;
            foreach (var perceived in npc.Perception.Objects)
            {
                if (!perceived.IsReachable || !perceived.AvailableInteractions.Contains(interactionType.Value))
                {
                    continue;
                }

                if (selected is null || perceived.Distance < selected.Distance)
                {
                    selected = perceived;
                }
            }

            if (selected is null || !world.Entities.Objects.TryGetValue(selected.Id, out var worldObject))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "PlanFailed", $"Goal={npc.Mind.CurrentGoal}");
                continue;
            }

            var targetJunction = worldObject.Junctions.Count > 0 ? worldObject.Junctions[0] : (JunctionId?)null;
            npc.Plan.TargetObjectId = worldObject.Id;
            npc.Plan.TargetTile = worldObject.Tile;
            npc.Plan.TargetJunctionId = targetJunction;

            if (targetJunction is { } jId &&
                !SpatialMutations.TryReserveJunction(world, jId, npc.Id, world.Tick, 48))
            {
                npc.Plan.Status = PlanStatus.Failed;
                Trace.Emit(world, npc.Id, "ReservationFailed", $"Junction={jId.Value}");
                continue;
            }

            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = targetJunction,
                TargetObject = worldObject.Id
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = worldObject.Id,
                TargetJunction = targetJunction
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            Trace.Emit(world, npc.Id, "PlanBuilt", $"Goal={npc.Plan.Goal};Target={worldObject.DefinitionId};Tile={worldObject.Tile.Q},{worldObject.Tile.R}");
        }
    }

    private static InteractionType? GoalToInteraction(GoalType goal)
    {
        return goal switch
        {
            GoalType.Eat => InteractionType.Eat,
            GoalType.Sleep => InteractionType.Sleep,
            GoalType.Sit => InteractionType.Sit,
            GoalType.Dress => InteractionType.Dress,
            _ => null
        };
    }
}

public sealed class PathfindingSystem : ISimulationSystem
{
    public string Name => nameof(PathfindingSystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active || npc.Plan.TargetJunctionId is null)
            {
                continue;
            }

            if (npc.Movement.IsMoving && npc.Movement.JunctionPath.Count > 0)
            {
                continue;
            }

            if (npc.CurrentJunction.HasValue && npc.CurrentJunction.Value.Equals(npc.Plan.TargetJunctionId.Value))
            {
                continue;
            }

            var startJunction = npc.CurrentJunction ?? SpatialQueries.FindNearestJunction(world, npc.Position);
            if (startJunction is null)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No current junction";
                continue;
            }

            var path = HexPathfinder.FindPath(world, startJunction.Value, npc.Plan.TargetJunctionId.Value);
            if (path.Count == 0)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No path";
                Trace.Emit(world, npc.Id, "PathFailed", $"To junction {npc.Plan.TargetJunctionId.Value.Value}");
                continue;
            }

            npc.Movement.JunctionPath.Clear();
            foreach (var step in path)
            {
                npc.Movement.JunctionPath.Add(step);
            }

            npc.Movement.PathIndex = 1;
            npc.Movement.IsMoving = path.Count > 1;
            npc.Movement.Status = npc.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived;
            npc.Movement.StopReason = string.Empty;
            Trace.Emit(world, npc.Id, "PathBuilt", $"Length={path.Count}");
        }
    }
}

public sealed class MovementSystem : ISimulationSystem
{
    public string Name => nameof(MovementSystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Movement.IsMoving || npc.Movement.JunctionPath.Count == 0)
            {
                continue;
            }

            var targetIndex = npc.Movement.PathIndex;
            if (targetIndex >= npc.Movement.JunctionPath.Count)
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Arrived;
                continue;
            }

            var targetJunctionId = npc.Movement.JunctionPath[targetIndex];
            if (!world.Junctions.Items.TryGetValue(targetJunctionId, out var targetJunction))
            {
                npc.Movement.IsMoving = false;
                npc.Movement.Status = MovementStatus.Invalid;
                continue;
            }

            var target = targetJunction.WorldPosition;
            var delta = new Float2(target.X - npc.Position.X, target.Y - npc.Position.Y);
            var direction = HexSpatialMath.Normalize(delta);
            npc.Movement.DesiredDirection = direction;
            npc.Movement.DesiredRotationDegrees = HexSpatialMath.AngleDegrees(direction);

            // Rotate toward target (progressive, not instant)
            var turnPerTick = npc.TurnSpeed * world.TickDeltaTime;
            npc.RotationDegrees = MathUtil.RotateTowards(
                npc.RotationDegrees,
                npc.Movement.DesiredRotationDegrees,
                turnPerTick);

            // Only move when facing roughly the right direction
            var facingError = MathUtil.Abs(MathUtil.DeltaAngle(npc.RotationDegrees, npc.Movement.DesiredRotationDegrees));
            const float alignmentThreshold = 30f;

            if (facingError > alignmentThreshold)
            {
                // Still rotating — don't move yet
                npc.Movement.Status = MovementStatus.Rotating;
                continue;
            }

            // Scale speed by alignment: full speed when aligned, slower when turning
            var alignmentFactor = 1f - (facingError / alignmentThreshold) * 0.5f;
            var movementPerTick = npc.MoveSpeed * alignmentFactor * world.TickDeltaTime;
            var distance = HexSpatialMath.Distance(npc.Position, target);

            if (distance <= movementPerTick)
            {
                npc.Position = target;
                npc.CurrentJunction = targetJunctionId;

                var previousTile = npc.Tile;
                if (targetJunction.Tiles.Count > 0)
                {
                    var newTile = targetJunction.Tiles[0];
                    if (newTile != previousTile)
                    {
                        npc.Tile = newTile;
                        SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
                        Trace.Emit(world, npc.Id, "EnteredTile", $"{npc.Tile.Q},{npc.Tile.R}");
                    }
                }

                npc.Movement.PathIndex++;

                if (npc.Movement.PathIndex >= npc.Movement.JunctionPath.Count)
                {
                    npc.Movement.IsMoving = false;
                    npc.Movement.Status = MovementStatus.Arrived;
                    Trace.Emit(world, npc.Id, "MovementCompleted", $"Junction={targetJunctionId.Value}");
                }
            }
            else
            {
                npc.Position += direction * movementPerTick;
                npc.Movement.Status = MovementStatus.Moving;
                npc.RotationDegrees = MathUtil.RotateTowards(
                    npc.RotationDegrees,
                    npc.Movement.DesiredRotationDegrees,
                    turnPerTick);
            }
        }
    }
}

public sealed class ExecutionSystem : ISimulationSystem
{
    public string Name => nameof(ExecutionSystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active || npc.Plan.TargetObjectId is null)
            {
                continue;
            }

            if (!world.Entities.Objects.TryGetValue(npc.Plan.TargetObjectId.Value, out var worldObject))
            {
                npc.Plan.Status = PlanStatus.Failed;
                continue;
            }

            if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
            {
                npc.Plan.Status = PlanStatus.Failed;
                continue;
            }

            if (npc.Movement.IsMoving)
            {
                continue;
            }

            if (npc.Movement.Status != MovementStatus.Arrived && npc.Movement.JunctionPath.Count > 0)
            {
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.None)
            {
                var interaction = definition.Interactions[0];
                npc.Execution.Status = ExecutionStatus.InProgress;
                npc.Execution.CurrentInteraction = interaction.Type;
                npc.Execution.TargetObject = worldObject.Id;
                npc.Execution.StartTick = world.Tick;
                npc.Execution.EndTick = world.Tick + interaction.DurationTicks;
                worldObject.IsOccupied = true;
                worldObject.CurrentUser = npc.Id;
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.OccupyJunction(world, jId, npc.Id);
                }

                Trace.Emit(world, npc.Id, "InteractionStarted", $"{interaction.Type} -> {worldObject.DefinitionId}");
                continue;
            }

            if (npc.Execution.Status == ExecutionStatus.InProgress && world.Tick >= npc.Execution.EndTick)
            {
                ApplyEffects(npc, definition.Interactions[0].Effects);
                npc.Execution.Status = ExecutionStatus.Completed;
                npc.Execution.LastCompletedTick = world.Tick;
                worldObject.IsOccupied = false;
                worldObject.CurrentUser = null;
                if (npc.Plan.TargetJunctionId is { } jId)
                {
                    SpatialMutations.FreeJunction(world, jId, npc.Id);
                    SpatialMutations.ReleaseJunctionReservation(world, jId, npc.Id);
                }

                npc.Plan.Status = PlanStatus.Completed;
                npc.Plan.Steps.Clear();
                npc.Plan.TargetObjectId = null;
                npc.Plan.TargetJunctionId = null;
                npc.Plan.TargetTile = null;
                npc.Mind.CurrentGoal = GoalType.None;
                npc.Execution.CurrentInteraction = null;
                npc.Execution.TargetObject = null;
                npc.Execution.StartTick = 0;
                npc.Execution.EndTick = 0;
                npc.Movement.JunctionPath.Clear();
                npc.Movement.PathIndex = 0;
                Trace.Emit(world, npc.Id, "InteractionCompleted", definition.Interactions[0].Type.ToString());
            }
        }
    }

    private static void ApplyEffects(NPCState npc, InteractionEffects effects)
    {
        npc.Needs.Hunger = MathUtil.Clamp01(npc.Needs.Hunger + effects.HungerDelta);
        npc.Needs.Energy = MathUtil.Clamp01(npc.Needs.Energy + effects.EnergyDelta);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort + effects.ComfortDelta);
        npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(npc.Needs.ThermalDiscomfort + effects.ThermalDelta);
        npc.EquippedWarmth = MathUtil.Clamp01(npc.EquippedWarmth + effects.WarmthDelta);
    }
}

public sealed class TemperatureSystem : ISimulationSystem
{
    public string Name => nameof(TemperatureSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var ambientPressure = world.Environment.GlobalTemperature < 12f ? 0.06f : -0.03f;
            npc.Needs.ThermalDiscomfort = MathUtil.Clamp01(
                npc.Needs.ThermalDiscomfort + ambientPressure - npc.EquippedWarmth * 0.05f);
        }
    }
}

internal static class Trace
{
    public static void Emit(WorldState world, EntityId entityId, string type, string message)
    {
        world.Events.Add(new SimulationEvent
        {
            Tick = world.Tick,
            EntityId = entityId.Value,
            Type = type,
            Message = message
        });
    }

    public static string FormatTile(TileCoord? tile) => tile is null ? "-" : $"{tile.Value.Q},{tile.Value.R}";
}

}
