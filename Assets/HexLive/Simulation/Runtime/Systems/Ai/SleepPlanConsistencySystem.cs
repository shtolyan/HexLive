using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Reconciles live sleep with its plan or the body recovery state that owns it.
/// </summary>
public sealed class SleepPlanConsistencySystem : ISimulationSystem
{
    public string Name => nameof(SleepPlanConsistencySystem);

    public TickLayer Layer => TickLayer.Fast;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Execution.Status != ExecutionStatus.InProgress ||
                npc.Execution.CurrentInteraction != InteractionType.Sleep)
            {
                continue;
            }

            if (npc.Plan.Status != PlanStatus.Active)
            {
                TryWakePlanlessSleeper(world, npc);
                continue;
            }

            if (npc.Plan.Goal == GoalType.Sleep && npc.Mind.CurrentGoal == GoalType.Sleep ||
                // §121.1: ручной приказ «спать» ДЕРЖИТ обычный PlayerOrder над
                // живой Sleep-интеракцией — это штатное состояние, а не
                // split-brain из bug-128 (там поверх сна стоял ЧУЖОЙ план
                // WashClothes). Рвать его значило будить её через тик после
                // укладки и молча гасить приказ.
                npc.Plan.Goal == GoalType.PlayerOrder &&
                npc.Mind.CurrentGoal == GoalType.PlayerOrder &&
                ManualControlMath.IsManual(npc))
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

    private static void TryWakePlanlessSleeper(WorldState world, NPCState npc)
    {
        // A rescue can finish after the patient has recovered, and manual
        // carry can deliver a conscious person to a bed. Those placements
        // have no plan to run the normal sleep completion block.
        if (npc.Health <= 0f || npc.IsBeingCarried || npc.IsUnconscious(world.Tick) ||
            npc.IsCrying(world.Tick) || npc.IsPlayingDead(world.Tick)) return;

        var reason = ExecutionSystem.GetSleepInterruptReason(world, npc, alreadyAsleep: true);
        if (reason is null && npc.Needs.Energy < Spec49.NightSleepWakeEnergy) return;

        if (BedSleep.TryGetOccupiedBed(world, npc, out var bed) &&
            !LyingSpot.TryStandAfterObjectSleep(world, npc, bed)) return;

        if (PlanInterruption.TryAbort(world, npc, InterruptionCause.Replan,
                $"Planless sleep ended: {reason ?? "Rested"}"))
        {
            Trace.Emit(world, npc.Id, "WokeUp", $"Cause={reason ?? "Rested"} Energy={npc.Needs.Energy:F2}");
        }
    }
}

}
