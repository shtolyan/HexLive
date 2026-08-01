using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §81: дорога до жертвы. Форма украдена у §53 — у похода помочь, — потому что
// это буквально она со стрелкой в другую сторону: там идут к тому, кому плохо, и
// ОТДАЮТ из рюкзака, тут идут к тому, у кого есть, и ЗАБИРАЮТ.
public sealed partial class PlanningSystem
{
    private static void BuildAbusePlan(WorldState world, NPCState npc)
    {
        // Заявку на нас уже подали — стоим и ждём, идти некуда.
        if (npc.Mind.PendingAbuseFrom is not null)
        {
            npc.Plan.Status = PlanStatus.Completed;
            return;
        }

        var mark = ResolveAbuseMark(world, npc);
        if (mark is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "NoMark");
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=Abuse NoMark");
            return;
        }

        if (mark.CurrentJunction is not { } markJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "MarkOffGrid");
            return;
        }

        // Уже стоим вплотную — сцена начинается прямо здесь, идти незачем.
        if (npc.CurrentJunction is { } here && here.Equals(markJunction))
        {
            npc.Plan.TargetAgentId = mark.Id;
            npc.Plan.TargetJunctionId = markJunction;
            npc.Plan.TargetTile = mark.Tile;
            ClaimMark(world, npc, mark);
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetJunction = markJunction,
                Interaction = InteractionType.Abuse
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            return;
        }

        var approach = TryReserveArmsLengthApproach(world, npc, mark, markJunction);
        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonAbuse(world, npc, "NoApproach");
            Trace.Emit(world, npc.Id, "PlanFailed", $"Goal=Abuse Mark={mark.Id.Value} NoApproach");
            return;
        }

        npc.Plan.TargetAgentId = mark.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = mark.Tile;
        ClaimMark(world, npc, mark);

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Abuse
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=Abuse Mark=NPC{mark.Id.Value} Loot={npc.Mind.AbuseHasLoot} " +
            $"Ratio={AbuseMath.Ratio(world, npc, mark):F2}");
    }

    // Держимся ОДНОЙ жертвы, пока она годится: пересчёт на каждом тике заставлял
    // бы его метаться между двумя девушками, не дойдя ни до одной.
    private static NPCState ResolveAbuseMark(WorldState world, NPCState npc)
    {
        if (npc.Mind.AbuseTargetNpcId is { } chosen &&
            world.Entities.Npcs.TryGetValue(chosen, out var current) &&
            current.Health > 0f &&
            !current.IsUnconscious(world.Tick) &&
            !(Spec81.AbuseRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, current)) &&
            HexSpatialMath.HexDistance(npc.Tile, current.Tile) <= Spec81.AbuseScanRadiusTiles)
        {
            return current;
        }

        var mark = AbuseMath.BestMark(world, npc, out var hasLoot);
        if (mark is not null)
        {
            npc.Mind.AbuseTargetNpcId = mark.Id;
            npc.Mind.AbuseHasLoot = hasLoot;
            npc.Mind.AbuseBeat = 0;
            npc.Mind.AbuseBlows = 0;
        }

        return mark;
    }

    private static void ClaimMark(WorldState world, NPCState npc, NPCState mark)
    {
        mark.Mind.PendingAbuseFrom = npc.Id;
        SocialCueSignals.Stamp(world, npc, "AbuseDemand", mark.Id);
        SocialCueSignals.Stamp(world, mark, "AbuseThreatened", npc.Id);
    }

    // Снять заявку ОБЯЗАТЕЛЬНО: иначе жертва остаётся помеченной навсегда и её
    // не сможет выбрать никто, включая её собственных собеседников.
    internal static void AbandonAbuse(WorldState world, NPCState npc, string reason)
    {
        if (npc.Mind.AbuseTargetNpcId is { } markId &&
            world.Entities.Npcs.TryGetValue(markId, out var mark) &&
            mark.Mind.PendingAbuseFrom is { } claimed && claimed.Equals(npc.Id))
        {
            mark.Mind.PendingAbuseFrom = null;
        }

        npc.Mind.AbuseTargetNpcId = null;
        npc.Mind.AbuseBeat = 0;
        npc.Mind.AbuseBlows = 0;
        npc.Mind.AbuseHasLoot = false;
        npc.Mind.AbuseCooldownUntilTick = world.Tick + Spec81.AbuseCooldownTicks;
        if (npc.Mind.CurrentGoal == GoalType.Abuse)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }

        Trace.Emit(world, npc.Id, "AbuseAbandoned", $"Reason={reason}");
    }
}

}
