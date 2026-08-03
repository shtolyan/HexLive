using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec 29C.3: dogs — roam, aggro, chase, bite. Combat is mutual and
// reactive: the bitten NPC is held in place and strikes back automatically.
internal static class CombatHelpSystem
{
    public static void CallForHelpFromDog(WorldState world, NPCState victim, int dogId, int attackers)
    {
        CallForHelp(world, victim, dogId, null, $"Dog={dogId}", attackers);
    }

    public static void CallForHelpFromNpc(WorldState world, NPCState victim, EntityId attackerId, int attackers)
    {
        CallForHelp(world, victim, null, attackerId, $"Attacker=NPC{attackerId.Value}", attackers);
    }

    private static void CallForHelp(
        WorldState world,
        NPCState victim,
        int? dogId,
        EntityId? attackerId,
        string attackerLabel,
        int attackers)
    {
        if (!Spec57.HelpCryEnabled ||
            world.Tick - victim.Mind.LastHelpCryTick < Spec57.HelpCryCooldownTicks)
        {
            return;
        }

        victim.Mind.LastHelpCryTick = world.Tick;
        // Суффикс «:кто напал» — вид рисовал собаку на ЛЮБОЙ крик о помощи, в
        // том числе когда резал человек. Пир — АТАКУЮЩИЙ, а не сама жертва:
        // прежний victim.Id всплывал её же лицом над её же головой.
        SocialCueSignals.Stamp(world, victim, dogId.HasValue ? "HelpCry:dog" : "HelpCry:npc", attackerId);
        Trace.Emit(world, victim.Id, "HelpCry",
            $"{attackerLabel} Radius={Spec57.HelpCryRadiusTiles} " +
            $"Health={victim.Health:F2} Attackers={attackers}");

        var responders = 0;
        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Id == victim.Id ||
                helper.Health <= 0f ||
                // §72: only her own side answers. Without this the outsider who
                // just cut her hears the cry, rolls compassion, takes the Defend
                // goal against HIMSELF and stands frozen for the goal-lock —
                // which reads as a pathing bug, not a faction bug.
                !FactionRelations.AreAllies(helper, victim) ||
                (attackerId is { } attackerEntity && helper.Id.Equals(attackerEntity)) ||
                // §60: asleep or out cold — the cry does not register at all:
                // no waking into Defend, no "ignored" cue, not even an emoji.
                helper.IsUnconscious(world.Tick) ||
                helper.Execution.CurrentInteraction == InteractionType.Sleep ||
                HexSpatialMath.HexDistance(helper.Tile, victim.Tile) > Spec57.HelpCryRadiusTiles)
            {
                continue;
            }

            var relationship = helper.Social.GetOrCreate(victim.Id);
            var affinity01 = (relationship.Affinity + 1f) * 0.5f;
            var compassionPressure = 1f - helper.Needs.Compassion;
            var score = helper.CompassionTrait * 0.55f +
                affinity01 * 0.35f +
                compassionPressure * 0.10f;
            var roll = MathUtil.Hash01(world.Seed, world.Tick, victim.Id.Value, helper.Id.Value);
            var canHelp = helper.Health >= Spec57.HelpCryHealthGate &&
                helper.Needs.Blood >= Spec57.HelpCryHealthGate &&
                !helper.Mind.IsStarving &&
                !helper.Mind.IsDehydrated &&
                !helper.IsFighting &&
                helper.Mind.CurrentGoal != GoalType.Flee &&
                helper.Mind.CurrentGoal != GoalType.Defend &&
                helper.Mind.CurrentGoal != GoalType.GroupHunt && // §108: она уже идёт бить

                (dogId.HasValue || attackerId.HasValue);

            if (!canHelp || score < Spec57.HelpCryDecisionThreshold || roll > score)
            {
                SocialCueSignals.Stamp(world, helper, "HelpCryIgnored", victim.Id);
                Trace.Emit(world, helper.Id, "HelpCryIgnored",
                    $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                    $"Score={score:F2} Roll={roll:F2} CanHelp={canHelp}");
                continue;
            }

            if (helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, helper,
                    $"Answering help cry from NPC{victim.Id.Value}");
            }

            helper.Mind.CurrentGoal = GoalType.Defend;
            helper.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Defend,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec57.HelpCryCooldownTicks
            };
            helper.Mind.CombatAssistDogId = dogId;
            helper.Mind.CombatAssistAttackerNpcId = attackerId;
            helper.Mind.PendingTalkFrom = null;
            helper.Mind.PendingAidFrom = null;
            SocialCueSignals.Stamp(world, helper, "HelpCryAnswer", victim.Id);
            SocialCueSignals.Stamp(world, victim, "HelpCryAnswered", helper.Id);
            Trace.Emit(world, helper.Id, "HelpCryAnswered",
                $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                $"Score={score:F2} Roll={roll:F2} Affinity={relationship.Affinity:F2} " +
                $"CompassionTrait={helper.CompassionTrait:F2}");

            responders++;
            if (responders >= Spec57.MaxHelpCryResponders)
            {
                break;
            }
        }
    }

    // 29C.4B friend-guard: runs every medium fight pass, BEFORE any cry.
    // A friend (affinity >= FriendGuardAffinity) within FriendGuardRadiusTiles
    // of the victim needs no cry and no compassion roll — she aborts whatever
    // she is doing and takes the Defend goal at the aggressor. The cry
    // (score + roll + health gate) stays as the wider-radius fallback for
    // everyone who is not that close a friend.
    public static void RallyFriends(
        WorldState world,
        NPCState victim,
        int? dogId,
        EntityId? attackerId,
        string attackerLabel)
    {
        if (!Spec57.FriendGuardEnabled || (!dogId.HasValue && !attackerId.HasValue))
        {
            return;
        }

        foreach (var helper in world.Entities.Npcs.Values)
        {
            if (helper.Id == victim.Id ||
                helper.Health <= 0f ||
                helper.Body.IsProne ||
                helper.IsUnconscious(world.Tick) || // §60: out cold — no rushing anywhere
                helper.Execution.CurrentInteraction == InteractionType.Sleep || // §60: sleepers ignore cries too
                helper.IsFighting ||
                helper.Mind.CurrentGoal == GoalType.Defend ||
                helper.Mind.CurrentGoal == GoalType.Flee ||
                // §108: она уже идёт бить его — своей волей и вместе с двумя
                // подругами. Перекинуть её в Defend значило бы разменять
                // расправу на конвой и развалить группу на полпути.
                helper.Mind.CurrentGoal == GoalType.GroupHunt ||
                !FactionRelations.AreAllies(helper, victim) || // §72: her side only
                (attackerId is { } aId && helper.Id.Equals(aId)) ||
                HexSpatialMath.HexDistance(helper.Tile, victim.Tile) > Spec57.FriendGuardRadiusTiles)
            {
                continue;
            }

            // §72: an OUTSIDER attacking one of ours rallies the whole camp, with
            // no affinity gate. The 0.25 friendship threshold is right for a
            // domestic scrap, but the girls have not built that affinity up in
            // the opening days — and "they fight back as one" has to be true
            // exactly then, when a lone stranger is picking them off one by one.
            var attackerIsOutsider = attackerId is { } outsiderId &&
                world.Entities.Npcs.TryGetValue(outsiderId, out var attackerNpc) &&
                FactionRelations.AreHostile(attackerNpc, victim);
            var skipAffinityGate = attackerIsOutsider && Spec72.RallyIgnoresAffinityVsOutsider;

            var relationship = helper.Social.GetOrCreate(victim.Id);
            if (!skipAffinityGate && relationship.Affinity < Spec57.FriendGuardAffinity)
            {
                continue;
            }

            // §109: вписаться — РЕШЕНИЕ, а не рефлекс. Раньше порог дружбы
            // тащил в драку любую соседку независимо от её шансов и состояния.
            // Теперь она взвешивает: дорога ли ей та, кого бьют; потянет ли
            // она ЭТОГО противника; и жива ли сама. Умирающая и разбитая не
            // лезет никогда.
            if (helper.Health < Spec57.FriendGuardHealthGate ||
                helper.IsDying ||
                !helper.Body.CanUseToolsOrWeapons)
            {
                continue;
            }

            // Шансы: размен Force против человека; против зверя — об константу
            // (у собак нет оружия и брони, их Force не считается).
            var myForce = AbuseMath.Force(world, helper);
            var foeForce = attackerId is { } foeId &&
                world.Entities.Npcs.TryGetValue(foeId, out var foe)
                    ? AbuseMath.Force(world, foe)
                    : Spec57.FriendGuardDogForce;
            var edge = MathUtil.Clamp01(
                myForce / System.Math.Max(foeForce, 0.0001f));

            var condition = MathUtil.Clamp01(
                System.Math.Min(helper.Health, MobSystem.WorstPartHealth(helper)));
            var affinity01 = MathUtil.Clamp01((relationship.Affinity + 1f) * 0.5f);
            var hatred = attackerId is { } hatedId
                ? MathUtil.Clamp01(-helper.Social.GetOrCreate(hatedId).Affinity)
                : 0f;

            var score = affinity01 * Spec57.FriendGuardAffinityWeight +
                edge * Spec57.FriendGuardEdgeWeight +
                condition * Spec57.FriendGuardConditionWeight +
                hatred * Spec57.FriendGuardHatredBonus;

            // Бросок на ОКНО боя, не на тик: RallyFriends зовут каждый средний
            // проход, и по-тиковый переброс превратил бы любой порог в
            // «рано или поздно да».
            var window = world.Tick / System.Math.Max(1, Spec57.HelpCryCooldownTicks);
            var roll = MathUtil.Hash01(
                world.Seed, window, victim.Id.Value, helper.Id.Value);
            if (score < Spec57.FriendGuardDecisionFloor || roll > score)
            {
                if (world.Tick % 64 == 0)
                {
                    Trace.Emit(world, helper.Id, "FriendGuardDeclined",
                        $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                        $"Score={score:F2} Roll={roll:F2} Edge={edge:F2} " +
                        $"Cond={condition:F2} Aff={relationship.Affinity:F2}");
                }
                continue;
            }

            if (helper.Plan.Status == PlanStatus.Active ||
                helper.Execution.Status == ExecutionStatus.InProgress)
            {
                PlanInterruption.Abort(world, helper,
                    $"Rushing to defend friend NPC{victim.Id.Value}");
            }

            helper.Mind.CurrentGoal = GoalType.Defend;
            helper.Mind.GoalLock = new GoalLock
            {
                Goal = GoalType.Defend,
                StartTick = world.Tick,
                EndTick = world.Tick + Spec57.HelpCryCooldownTicks
            };
            helper.Mind.CombatAssistDogId = dogId;
            helper.Mind.CombatAssistAttackerNpcId = attackerId;
            helper.Mind.PendingTalkFrom = null;
            helper.Mind.PendingAidFrom = null;
            SocialCueSignals.Stamp(world, helper, "HelpCryAnswer", victim.Id);
            SocialCueSignals.Stamp(world, victim, "HelpCryAnswered", helper.Id);
            Trace.Emit(world, helper.Id, "FriendGuard",
                $"Victim=NPC{victim.Id.Value} {attackerLabel} " +
                $"Score={score:F2} Roll={roll:F2} Edge={edge:F2} " +
                $"Affinity={relationship.Affinity:F2} " +
                $"Dist={HexSpatialMath.HexDistance(helper.Tile, victim.Tile)}");
        }
    }

    public static void ClearAssist(NPCState npc)
    {
        npc.Mind.CombatAssistDogId = null;
        npc.Mind.CombatAssistAttackerNpcId = null;
        npc.Mind.AssistHoldSinceTick = 0;
        if (npc.Mind.CurrentGoal == GoalType.Defend)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }
    }
}

}
