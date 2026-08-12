using HexLive.Simulation.Agents;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    // §133: сцена короткая — подошла, спросила, услышала ответ. Разговором это
    // не делается: у Talk своя цель, свои темы и своя ссора на выходе.
    private const int AskWearPermissionTicks = 16;

    /// <summary>Сколько живёт полученное «да» (одна вещь, один раз).</summary>
    private const int WearGrantTtlTicks = 600;

    /// <summary>Сколько не переспрашивают после «нет».</summary>
    private const int WearDenyCooldownTicks = 2000;

    private static void RunAskWearPermission(WorldState world, NPCState npc, PlanStep step)
    {
        if (npc.Plan.TargetAgentId is not { } ownerId ||
            !world.Entities.Npcs.TryGetValue(ownerId, out var owner) ||
            step.TargetObject is not { } garmentId ||
            !world.Entities.Objects.TryGetValue(garmentId, out var garment))
        {
            AbortAsk(world, npc, owner: null, "WearPermission: target vanished");
            return;
        }

        if (npc.Movement.IsMoving)
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None &&
            npc.Movement.Status == MovementStatus.Blocked)
        {
            AbortAsk(world, npc, owner, "WearPermission: approach blocked");
            return;
        }

        // Прибытие буквальное — стоя на зарезервированной точке подхода (§26.3 r2).
        if (npc.Execution.Status == ExecutionStatus.None &&
            step.TargetJunction is { } want &&
            (npc.CurrentJunction is not { } at || !at.Equals(want)))
        {
            return;
        }

        if (npc.Execution.Status == ExecutionStatus.None)
        {
            if (!InteractionReach.CheckStart(world, npc, owner.Position,
                    InteractionReach.Talk, $"AskWear NPC{ownerId.Value}"))
            {
                AbortAsk(world, npc, owner, $"WearPermission: NPC{ownerId.Value} out of range");
                return;
            }

            // Хозяйка без сознания не отвечает — а молчание согласием тут не
            // считается: обобрать лежачую подругу это уже совсем другая сцена.
            if (owner.IsUnconscious(world.Tick))
            {
                AbortAsk(world, npc, owner, $"WearPermission: NPC{ownerId.Value} unconscious");
                return;
            }

            var faceDelta = new Float2(
                owner.Position.X - npc.Position.X, owner.Position.Y - npc.Position.Y);
            npc.RotationDegrees = HexSpatialMath.AngleDegrees(HexSpatialMath.Normalize(faceDelta));
            npc.Execution.Status = ExecutionStatus.InProgress;
            npc.Execution.CurrentInteraction = InteractionType.Talk;
            npc.Execution.StartTick = world.Tick;
            npc.Execution.EndTick = world.Tick + AskWearPermissionTicks;
            return;
        }

        if (world.Tick < npc.Execution.EndTick)
        {
            return;
        }

        var verdict = WearPermissionMath.Decide(world, owner, npc, garment);
        if (verdict == WearPermissionMath.Verdict.Grant)
        {
            npc.Mind.WearGrants.RemoveAll(g => g.Item.Equals(garmentId));
            npc.Mind.WearGrants.Add(new WearGrant
            {
                Item = garmentId,
                Owner = ownerId,
                ExpiresTick = world.Tick + WearGrantTtlTicks
            });
            SocialCueSignals.Stamp(world, npc, "TalkRequest", ownerId);
            SocialCueSignals.Stamp(world, owner, "TalkIncoming", npc.Id);
            Trace.Emit(world, npc.Id, "WearPermissionGranted",
                $"NPC{ownerId.Value}->NPC{npc.Id.Value} lends {garment.DefinitionId} " +
                $"Obj={garmentId.Value}");
        }
        else
        {
            npc.Mind.WearDenials.RemoveAll(d => d.Item.Equals(garmentId));
            npc.Mind.WearDenials.Add(new WearDenial
            {
                Item = garmentId,
                UntilTick = world.Tick + WearDenyCooldownTicks
            });

            // Отказ «самой нужна» обиды не несёт — как «извини, занята» у §28.10.
            // Личный отказ стоит отношений, и только он.
            if (verdict == WearPermissionMath.Verdict.RefuseDislike)
            {
                var rel = npc.Social.GetOrCreate(ownerId);
                rel.Affinity = MathUtil.Clamp(
                    rel.Affinity - SocialBalance.RejectionAffinityPenalty, -1f, 1f);
                Trace.Emit(world, npc.Id, "RelationshipChanged",
                    $"NPC{npc.Id.Value}->NPC{ownerId.Value} Aff={rel.Affinity:F2} " +
                    $"(-{SocialBalance.RejectionAffinityPenalty:F2}) after clothes refusal");
            }

            SocialCueSignals.Stamp(world, npc, "TalkRejected", ownerId);
            SocialCueSignals.Stamp(world, owner, "TalkRefused", npc.Id);
            Trace.Emit(world, npc.Id, "WearPermissionRefused",
                $"NPC{ownerId.Value} keeps {garment.DefinitionId} Obj={garmentId.Value} " +
                $"Reason={verdict}");
        }

        if (owner.Mind.PendingTalkFrom is { } claim && claim.Equals(npc.Id))
        {
            owner.Mind.PendingTalkFrom = null;
        }

        // Разговор кончился удачей или отказом — но точка подхода была
        // ЗАБРОНИРОВАНА планом, и не отпустить её значит оставить дырку в
        // проходе (храповик TeardownLint именно об этом и напоминает).
        if (step.TargetJunction is { } approach)
        {
            SpatialMutations.ReleaseJunctionReservation(world, approach, npc.Id);
        }

        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetAgentId = null;
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.Status = PlanStatus.Completed;
        npc.Mind.CurrentGoal = GoalType.None;
    }

    private static void AbortAsk(WorldState world, NPCState npc, NPCState owner, string reason)
    {
        if (owner?.Mind.PendingTalkFrom is { } claim && claim.Equals(npc.Id))
        {
            owner.Mind.PendingTalkFrom = null;
        }

        PlanningSystem.SetGoalCooldown(world, npc, GoalType.Dress);
        PlanInterruption.Abort(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
    }
}

}
