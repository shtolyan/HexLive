using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    /// <summary>
    /// §133: подобрать забытую вещь и отнести домой. Две ноги в одном шаге, как
    /// у стирки (§40.6 r4): пока вещи нет в руке — идём за ней; как только
    /// подняли — перенацеливаемся на гардероб и идём туда.
    /// </summary>
    private static void RunStowCarriedGarment(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Movement.IsMoving || npc.CurrentJunction is not { } current ||
            npc.Plan.TargetJunctionId is not { } target)
        {
            return;
        }

        // ── Нога 1: дойти до вещи и взять её в руку ──────────────────────────
        if (npc.Execution.HeldGarment is null)
        {
            if (!current.Equals(target))
            {
                if (npc.Movement.Status == MovementStatus.Blocked)
                {
                    PlanInterruption.Abort(world, npc, "StowClothes: route to the garment blocked");
                    npc.Mind.CurrentGoal = GoalType.None;
                }

                return;
            }

            if (step.TargetObject is not { } garmentId ||
                !TryPickGarmentIntoHand(world, npc, garmentId))
            {
                // Кто-то успел раньше — это не беда и не провал.
                PlanInterruption.Abort(world, npc, "StowClothes: garment gone");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            if (StowMath.FindUndressSpot(world, npc) is not { } spot)
            {
                // Дома не стало (или до него не дойти): вещь в руке, и класть её
                // обратно посреди поля бессмысленно — прерывание положит её под
                // ноги штатным путём.
                PlanInterruption.Abort(world, npc, "StowClothes: no home spot");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }

            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            npc.Plan.TargetObjectId = spot.StowObject;
            npc.Plan.TargetJunctionId = spot.Stand;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.PathIndex = 0;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "StowClothesCarrying",
                    $"{npc.Execution.HeldGarment.DefinitionId} -> Stand={spot.Stand.Value} " +
                    $"Stow={(spot.StowObject is { } s ? s.Value.ToString() : "homeGround")}");
            }

            return;
        }

        // ── Нога 2: донести до дома и положить ───────────────────────────────
        if (!current.Equals(target))
        {
            if (npc.Movement.Status == MovementStatus.Blocked)
            {
                PlanInterruption.Abort(world, npc, "StowClothes: route home blocked");
                npc.Mind.CurrentGoal = GoalType.None;
            }

            return;
        }

        var held = npc.Execution.HeldGarment;
        npc.Execution.HeldGarment = null;
        var laid = StowGarmentWithContents(world, npc, held, npc.Plan.TargetObjectId);
        if (laid != null && npc.Execution.HeldGarmentContents.Count > 0)
        {
            laid.Contents.AddRange(npc.Execution.HeldGarmentContents);
        }

        npc.Execution.HeldGarmentContents.Clear();
        Trace.Emit(world, npc.Id, "ClothesStowed",
            $"{held.DefinitionId} brought home Obj={(laid is null ? "-" : laid.Id.Value.ToString())}");

        SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.Cooldowns.RemoveAll(c => c.Goal == GoalType.StowClothes);
        npc.Mind.Cooldowns.Add(new GoalCooldown
        {
            Goal = GoalType.StowClothes,
            EndTick = world.Tick + AiBalance.FailureCooldownTicks
        });
    }
}

}
