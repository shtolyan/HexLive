using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static void RunLimbCare(WorldState world, NPCState helper)
    {
        if (helper.Plan.TargetAgentId is not { } patientId ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            !CampDiplomacyMath.CanProvideCare(world, helper, patient))
        {
            AbortLimbCare(world, helper, null, "patient missing or hostile");
            return;
        }

        if (helper.Movement.IsMoving ||
            (helper.Movement.Status != MovementStatus.Arrived &&
             helper.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        if (helper.Movement.Status == MovementStatus.Blocked)
        {
            AbortLimbCare(world, helper, patient, "approach blocked");
            return;
        }

        var splinting = helper.Mind.CurrentGoal == GoalType.Splint;
        BodyPart part;
        string itemId = ContentIds.Splint;
        var repair = false;
        var stillNeeded = splinting
            ? KenshiProstheticMath.TryFindSplintPart(patient, out part) &&
              KenshiProstheticMath.HasItem(helper, ContentIds.Splint)
            : KenshiProstheticMath.TryFindProstheticPart(
                world, helper, patient, out part, out itemId, out repair);
        if (!stillNeeded)
        {
            AbortLimbCare(world, helper, patient, "limb care no longer possible");
            return;
        }

        if (helper.Execution.Status == ExecutionStatus.None)
        {
            if (!InteractionReach.CheckStart(
                    world, helper, patient.Position, InteractionReach.Aid, "limb care"))
            {
                AbortLimbCare(world, helper, patient, "patient out of reach");
                return;
            }

            if (patient.IsLyingDown(world.Tick))
            {
                // §111.13: лечение конечности ходит своим подходом (§118), мимо
                // общей брони станций, — значит берёт её здесь.
                if (!LyingStations.TryClaim(world, helper, patient, out var startSlot))
                {
                    AbortLimbCare(world, helper, patient, "no free station at the patient");
                    return;
                }

                LyingStations.Align(helper, patient, startSlot);
            }

            // §111.13: лечение конечности — единственная сцена у лежащего, которая
            // НИКОГДА не занимала свой узел (освобождала по выходу, не взяв на
            // входе). Одному участнику это сходило с рук; в сцене на несколько
            // человек свободный по документам узел разбирают соседи.
            HoldSceneJunction(world, helper);

            helper.Execution.Status = ExecutionStatus.InProgress;
            helper.Execution.CurrentInteraction = splinting
                ? InteractionType.Splint
                : InteractionType.FitProsthetic;
            helper.Execution.StartTick = world.Tick;
            helper.Execution.EndTick = world.Tick +
                KenshiProstheticMath.TreatmentTicks(helper, helper.Mind.CurrentGoal, itemId);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, helper.Id, splinting ? "SplintStarted" : "ProstheticFitStarted",
                    $"NPC{patient.Id.Value} Part={part} Item={itemId} Repair={repair}");
            }
            return;
        }

        if (world.Tick < helper.Execution.EndTick)
        {
            HoldSceneJunction(world, helper);
            if (patient.IsLyingDown(world.Tick))
            {
                // §111.13: поза удерживается, но не догоняет уехавшее тело.
                var slot = LyingStations.SlotFor(world, helper, patient);
                if (!InteractionReach.CheckPersonStart(world, helper, patient,
                        LyingStations.Point(patient, slot), LyingStations.Reach(slot),
                        "limb care hold"))
                {
                    AbortLimbCare(world, helper, patient, "patient left the care station");
                    return;
                }

                LyingStations.Align(helper, patient, slot);
            }
            return;
        }

        var applied = splinting
            ? KenshiProstheticMath.ApplySplint(world, helper, patient, part)
            : KenshiProstheticMath.FitOrRepair(
                world, helper, patient, part, itemId, repair);
        if (!applied)
        {
            AbortLimbCare(world, helper, patient, "supplies or medical precondition changed");
            return;
        }

        SkillTrace.Award(world, helper,
            splinting ? InteractionType.Splint : InteractionType.FitProsthetic,
            System.Math.Max(1, helper.Execution.EndTick - helper.Execution.StartTick));
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, helper.Id, splinting ? "SplintApplied" : "ProstheticFitted",
                $"NPC{patient.Id.Value} Part={part} Item={itemId} Repair={repair}");
        }
        CompleteLimbCare(world, helper, patient);
    }

    private static void CompleteLimbCare(WorldState world, NPCState helper, NPCState patient)
    {
        patient.Mind.PendingAidFrom = null;
        if (helper.Plan.TargetJunctionId is { } junction)
        {
            SpatialMutations.FreeJunction(world, junction, helper.Id);
            SpatialMutations.ReleaseJunctionReservation(world, junction, helper.Id);
        }

        LyingStations.ReleaseStation(helper);
        helper.Execution.Status = ExecutionStatus.None;
        helper.Execution.CurrentInteraction = null;
        helper.Execution.StartTick = 0;
        helper.Execution.EndTick = 0;
        helper.Plan.Status = PlanStatus.Completed;
        helper.Plan.Steps.Clear();
        helper.Plan.TargetAgentId = null;
        helper.Plan.TargetJunctionId = null;
        helper.Plan.TargetTile = null;
        helper.Mind.CurrentGoal = GoalType.None;
    }

    private static void AbortLimbCare(
        WorldState world, NPCState helper, NPCState patient, string reason)
    {
        if (patient is not null && patient.Mind.PendingAidFrom == helper.Id)
        {
            patient.Mind.PendingAidFrom = null;
        }
        PlanInterruption.TryAbort(world, helper, InterruptionCause.ExecutionFailure, reason);
        helper.Mind.CurrentGoal = GoalType.None;
    }
}

}
