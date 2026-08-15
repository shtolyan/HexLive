using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Repairs a persisted or externally produced split-brain state where an active
/// non-sleep plan has been installed over a live bed-sleep interaction.
/// </summary>
public sealed class SleepPlanConsistencySystem : ISimulationSystem
{
    public string Name => nameof(SleepPlanConsistencySystem);

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Execution.Status != ExecutionStatus.InProgress ||
                npc.Execution.CurrentInteraction != InteractionType.Sleep ||
                npc.Plan.Status != PlanStatus.Active ||
                npc.Plan.Goal == GoalType.Sleep && npc.Mind.CurrentGoal == GoalType.Sleep)
            {
                continue;
            }

            PlanInterruption.TryAbort(
                world,
                npc,
                InterruptionCause.Replan,
                $"Sleep does not belong to active {npc.Plan.Goal} plan " +
                $"(Goal={npc.Mind.CurrentGoal})");
        }
    }
}

}
