using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
// Ephemeral invitation, not ownership or a forced social scene. Never persisted.
internal static class GiftSessionMath
{
    private static bool Emergency(WorldState world, NPCState npc) =>
        npc.Health <= 0f || npc.IsDying || npc.IsUnconscious(world.Tick) || npc.IsFighting ||
        npc.IsBeingCarried || npc.IsCarryingPerson || npc.Mind.CurrentGoal == GoalType.Flee ||
        npc.Memory.Dangers.Count > 0 || npc.Needs.Hunger >= SimBalance.StarvingEnterThreshold ||
        npc.Needs.Thirst >= SimBalance.StarvingEnterThreshold;

    internal static bool TryStart(WorldState world, NPCState giver, EntityId recipientId,
        out NPCState recipient, out JunctionId? approach, out string reason)
    {
        recipient = null; approach = null; reason = "TargetUnavailable";
        if (Emergency(world, giver) ||
            !PlayerLootTargets.TryResolve(world, giver, recipientId, InventoryTransferDirection.Give, out recipient, out _) ||
            !world.Entities.Npcs.ContainsKey(recipientId) || Emergency(world, recipient)) return false;
        var standing = PlayerLootTargets.IsStandingRecipient(world, recipient);
        if (standing && (ManualControlMath.IsManual(recipient) || recipient.Mind.GiftRecipientId.HasValue ||
            recipient.Mind.PendingGiftFrom is { } incoming && incoming != giver.Id ||
            recipient.Mind.PendingAidFrom.HasValue || recipient.Mind.PendingTalkFrom.HasValue)) return false;
        var close = InteractionReach.CheckPersonStart(world, giver, recipient,
            standing ? recipient.Position : LyingStations.Point(recipient, LyingStations.SlotFor(world, giver, recipient)),
            standing ? InteractionReach.Aid : LyingStations.Reach(LyingStations.SlotFor(world, giver, recipient)), "Gift invitation");
        if (!close)
        {
            if (!KenshiRescueMath.TryFindApproach(world, giver, recipient, out var found))
            { reason = "Unreachable"; return false; }
            approach = found;
        }
        if (standing && (recipient.Plan.Status == PlanStatus.Active || recipient.Movement.IsMoving ||
            recipient.Execution.Status == ExecutionStatus.InProgress))
        {
            if (!PlanInterruption.TryAbort(world, recipient, InterruptionCause.Auction,
                "Accepted gift invitation, waiting for giver")) return false;
            recipient.Mind.CurrentGoal = GoalType.None;
        }
        End(world, giver, giver.Mind.GiftRecipientId, cancelApproach: false);
        giver.Mind.GiftRecipientId = recipientId;
        giver.Mind.GiftUntilTick = world.Tick + AiBalance.TalkWaitTimeoutTicks;
        giver.Mind.GiftCloseRequested = false;
        giver.Mind.GiftManualAtStart = giver.Mind.ManualControl;
        if (standing) recipient.Mind.PendingGiftFrom = giver.Id;
        reason = string.Empty;
        return true;
    }

    internal static void CancelForNewOrder(WorldState world, NPCState npc)
    {
        End(world, npc, npc.Mind.GiftRecipientId, cancelApproach: false);
        if (npc.Mind.PendingGiftFrom is { } id && world.Entities.Npcs.TryGetValue(id, out var giver))
            End(world, giver, npc.Id);
    }

    internal static bool HoldsDecision(NPCState npc) =>
        npc.Mind.GiftRecipientId.HasValue || npc.Mind.PendingGiftFrom.HasValue;

    internal static void Update(WorldState world)
    {
        foreach (var giver in world.Entities.Npcs.Values)
        {
            if (giver.Mind.GiftRecipientId is not { } id) continue;
            var otherOrder = giver.Plan.Status == PlanStatus.Active &&
                (giver.Plan.TargetAgentId != id || giver.Plan.Goal is not GoalType.PlayerOrder and not GoalType.PlayerInventory);
            if (!world.Entities.Npcs.TryGetValue(id, out var recipient) ||
                !PlayerAuthority.IsPlayerOwned(world, giver) || giver.Mind.ManualControl != giver.Mind.GiftManualAtStart ||
                world.Tick > giver.Mind.GiftUntilTick ||
                Emergency(world, giver) || Emergency(world, recipient) || otherOrder ||
                giver.Movement.Status == Navigation.MovementStatus.Blocked ||
                recipient.Mind.PendingGiftFrom == giver.Id && ManualControlMath.IsManual(recipient))
                End(world, giver, id);
        }
        foreach (var recipient in world.Entities.Npcs.Values)
            if (recipient.Mind.PendingGiftFrom is { } id &&
                (!world.Entities.Npcs.TryGetValue(id, out var giver) || giver.Mind.GiftRecipientId != recipient.Id))
                recipient.Mind.PendingGiftFrom = null;
    }

    internal static void RequestEnd(WorldState world, NPCState giver, EntityId id)
    {
        if (giver.Mind.GiftRecipientId != id) return;
        if (giver.Plan.Status == PlanStatus.Active && giver.Plan.Goal == GoalType.PlayerInventory && giver.Plan.TargetAgentId == id)
        { giver.Mind.GiftCloseRequested = true; return; }
        End(world, giver, id);
    }

    internal static void End(WorldState world, NPCState giver, EntityId? expected, bool cancelApproach = true)
    {
        if (expected is not { } id || giver.Mind.GiftRecipientId != id) return;
        if (world.Entities.Npcs.TryGetValue(id, out var recipient) && recipient.Mind.PendingGiftFrom == giver.Id)
            recipient.Mind.PendingGiftFrom = null;
        giver.Mind.GiftRecipientId = null; giver.Mind.GiftUntilTick = 0; giver.Mind.GiftCloseRequested = false;
        if (cancelApproach && giver.Plan.Goal == GoalType.PlayerOrder && giver.Plan.TargetAgentId == id)
        {
            PlanInterruption.TryAbort(world, giver, InterruptionCause.PlayerCommand, "Gift invitation ended");
            giver.Mind.CurrentGoal = GoalType.None;
        }
    }
}
}
