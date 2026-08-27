using System.Collections.Generic;
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
    private static void RunPlayerInventoryTransfer(WorldState world, NPCState looter)
    {
        // Like manual person pickup, the final action owns the whole plan while
        // an optional MoveToJunction remains at index zero.
        if (looter.Plan.Steps.Count == 0 || looter.Plan.TargetAgentId is null)
        {
            FailPlayerInventoryTransfer(world, looter, "PersonNotAvailable");
            return;
        }

        var step = looter.Plan.Steps[^1];
        PlayerInventoryTransferMath.UnpackCursor(
            step.TimeoutEndTick ?? 0, out var index, out var count);
        var take = step.Type is PlanStepType.PlayerTakeCarried or
            PlanStepType.PlayerTakeWorn;
        var itemSource = step.Type is PlanStepType.PlayerTakeWorn or
            PlanStepType.PlayerGiveWorn
                ? InventoryItemSource.Worn
                : InventoryItemSource.Carried;

        // §128 r2 (#164): тот же предикат, что принял приказ, — иначе «разрешили
        // спящих» дошло бы только до половины пути. §153.1: направление шага и
        // есть направление приказа, поэтому подарок проверяется тем же кодом.
        if (looter.Plan.TargetAgentId is not { } otherId ||
            !PlayerLootTargets.TryResolve(
                world, looter, otherId,
                take ? InventoryTransferDirection.Take : InventoryTransferDirection.Give,
                out var other, out var carriedBySelf))
        {
            FailPlayerInventoryTransfer(world, looter, "PersonNotAvailable");
            return;
        }

        if (looter.Movement.IsMoving ||
            (looter.Movement.Status != MovementStatus.Arrived &&
             looter.Movement.JunctionPath.Count > 0))
        {
            return;
        }

        // §128: тело в руках — дистанция ноль по построению, станция у ног
        // неопределима (у несомого нет CurrentJunction).
        if (!carriedBySelf)
        {
            // §153.1: у стоящей получательницы станции у ног нет — та же
            // развилка, что на приёме приказа, и тот же радиус помощи.
            var standing = PlayerLootTargets.IsStandingRecipient(world, other);
            var transferSlot = standing
                ? 0
                : LyingStations.SlotFor(world, looter, other);
            var anchor = standing ? other.Position : LyingStations.Point(other, transferSlot);
            var reach = standing ? InteractionReach.Aid : LyingStations.Reach(transferSlot);
            if (looter.Movement.Status == MovementStatus.Blocked ||
                !InteractionReach.CheckPersonStart(
                    world, looter, other, anchor, reach,
                    $"Player inventory transfer with NPC{other.Id.Value}"))
            {
                FailPlayerInventoryTransfer(world, looter, "OutOfReach");
                return;
            }
        }

        var source = take ? other : looter;
        var destination = take ? looter : other;

        if (take && world.Entities.Npcs.ContainsKey(other.Id) &&
            looter.Faction != other.Faction &&
            !CampDiplomacyMath.CanLoot(world, looter, other))
        {
            FailPlayerInventoryTransfer(world, looter, "NoLootMotive");
            return;
        }

        var itemRef = new InventoryItemRef(
            itemSource, index, looter.Plan.TargetItemDefinitionId ?? string.Empty);

        if (!PlayerInventoryTransferMath.TryResolveTransfer(
                world, source, itemRef, count, out var moving, out var contents))
        {
            FailPlayerInventoryTransfer(world, looter, "StaleItem");
            return;
        }

        if (!PlayerInventoryTransferMath.FitsAfter(
                world, source, destination, itemRef, count))
        {
            FailPlayerInventoryTransfer(world, looter, "InsufficientSpace");
            return;
        }

        PlayerInventoryTransferMath.MoveResolved(
            world, source, destination, itemRef, moving, contents);

        if (take && world.Entities.Npcs.ContainsKey(other.Id) &&
            looter.Faction != other.Faction)
        {
            CombatHelpSystem.RallyLootWitnesses(world, other, looter.Id);
            SocialCueSignals.StampItem(
                world, looter, "LootHelplessTook", itemRef.ExpectedDefinitionId);
        }

        if (!take)
        {
            ReactToGift(world, looter, other, itemRef.ExpectedDefinitionId, moving);
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, looter.Id, "PlayerInventoryTransferred",
                $"Direction={(take ? "Take" : "Give")} Other=NPC{other.Id.Value} " +
                $"Source={itemSource} Index={index} Count={moving.Count} " +
                $"Contents={contents.Count} " +
                $"Def={itemRef.ExpectedDefinitionId}");
        }

        FinishPlayerInventoryTransfer(world, looter, PlanStatus.Completed);
    }

    /// <summary>
    /// §153.3: подарок принят — и получательница на него ОТВЕЧАЕТ. Отношения
    /// двигает только осознанный приём: положить вещь в карман спящей или
    /// мёртвой можно (§128 это разрешал и разрешает), но благодарности за это
    /// не бывает — иначе «подарок» превратился бы в способ качать симпатию,
    /// пока цель без сознания.
    /// </summary>
    private static void ReactToGift(
        WorldState world, NPCState giver, NPCState receiver,
        string definitionId, List<ItemInstance> moving)
    {
        if (!PlayerLootTargets.CanReactToGift(world, receiver) || moving.Count == 0)
        {
            return;
        }

        var verdict = GiftAppraisal.Evaluate(
            receiver, definitionId, moving.Count, moving[0].ResourceAmount);

        // Знакомство растёт у ОБЕИХ: они постояли рядом и что-то друг о друге
        // узнали. Симпатия — только у получательницы к дарительнице: подарок
        // это её оценка чужого поступка, а не сделка.
        var toGiver = receiver.Social.GetOrCreate(giver.Id);
        var toReceiver = giver.Social.GetOrCreate(receiver.Id);
        toGiver.Familiarity = MathUtil.Clamp01(
            toGiver.Familiarity + SocialBalance.TalkRelationshipGain);
        toReceiver.Familiarity = MathUtil.Clamp01(
            toReceiver.Familiarity + SocialBalance.TalkRelationshipGain);
        toGiver.Affinity = MathUtil.Clamp(
            toGiver.Affinity + verdict.AffinityDelta, -1f, 1f);
        receiver.Social.MarkInteraction(giver.Id, world.Tick);
        giver.Social.MarkInteraction(receiver.Id, world.Tick);

        // §28.15E: тот же канал, что у беседы, — над головой всплывает знакомый
        // «+/−». Второго способа показать сдвиг отношений в игре нет, и заводить
        // его значило бы, что подарок читается иначе, чем ссора.
        receiver.Execution.LastTalkResultTick = world.Tick;
        receiver.Execution.LastTalkAffinityDelta = verdict.AffinityDelta;
        SocialCueSignals.StampItem(
            world, receiver, $"GiftReceived:{verdict.Reaction}", definitionId);

        Trace.Emit(world, giver.Id, "GiftGiven",
            $"->NPC{receiver.Id.Value} Item={definitionId} Count={moving.Count} " +
            $"Reaction={verdict.Reaction} Driver={verdict.Driver} " +
            $"Score={verdict.Score:F2}");
        Trace.Emit(world, receiver.Id, "RelationshipChanged",
            $"NPC{receiver.Id.Value}->NPC{giver.Id.Value} " +
            $"Fam={toGiver.Familiarity:F2} (+{SocialBalance.TalkRelationshipGain:F2}) " +
            $"Aff={toGiver.Affinity:F2} ({verdict.AffinityDelta:+0.00;-0.00})");
    }

    private static void FailPlayerInventoryTransfer(
        WorldState world, NPCState looter, string reason)
    {
        Trace.Emit(world, looter.Id, "ManualOrderRejected",
            $"Order=TransferInventory Reason={reason}");
        FinishPlayerInventoryTransfer(world, looter, PlanStatus.Failed);
    }

    private static void FinishPlayerInventoryTransfer(
        WorldState world, NPCState looter, PlanStatus status)
    {
        if (looter.Plan.TargetJunctionId is { } approach)
        {
            SpatialMutations.ReleaseJunctionReservation(world, approach, looter.Id);
        }

        looter.Execution.Status = ExecutionStatus.None;
        looter.Execution.CurrentInteraction = null;
        looter.Execution.TargetObject = null;
        looter.Plan.Status = status;
        looter.Plan.Steps.Clear();
        looter.Plan.TargetAgentId = null;
        looter.Plan.TargetItemDefinitionId = null;
        looter.Plan.TargetJunctionId = null;
        looter.Plan.TargetTile = null;
        looter.Mind.CurrentGoal = GoalType.None;
        looter.Movement.JunctionPath.Clear();
        looter.Movement.PathIndex = 0;
        looter.Movement.IsMoving = false;
        looter.Movement.SetStatus(MovementStatus.Idle);
    }
}

}
