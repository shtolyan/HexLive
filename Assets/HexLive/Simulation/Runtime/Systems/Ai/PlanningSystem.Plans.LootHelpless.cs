using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §111: дорога до лежащего. Форма взята у §81 — жертва не объект мира, и общий
// путь планировщика (перебор Perception.Objects) ей не годится, — но короче на
// целую ветку: беспомощная не ходит, поэтому преследования здесь нет вовсе.
public sealed partial class PlanningSystem
{
    private static void BuildLootHelplessPlan(WorldState world, NPCState npc)
    {
        var mark = ResolveHelplessMark(world, npc);
        if (mark is null)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonLootHelpless(world, npc, "NoMark");
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed", "Goal=LootHelpless NoMark");

            }
            return;
        }

        if (mark.CurrentJunction is not { } markJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonLootHelpless(world, npc, "MarkOffGrid",
                Spec111.LootHelplessCooldownTicks);
            return;
        }

        // §102 r2: уже вплотную — обыск начинается прямо здесь, на СВОЁМ узле.
        // Гнать его на её узел незачем, а «прибытие» в исполнении сверяется
        // именно с этим.
        if (InteractionReach.CanStrike(world, npc, mark) &&
            npc.CurrentJunction is { } hereJunction &&
            InteractionReach.CanTouchPersonAcross(
                world, hereJunction, markJunction, LyingSpot.InteractionStationReach))
        {
            var here = npc.CurrentJunction ?? markJunction;
            npc.Plan.TargetAgentId = mark.Id;
            npc.Plan.TargetJunctionId = here;
            npc.Plan.TargetTile = mark.Tile;
            ClaimHelplessMark(npc, mark);
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetJunction = here,
                Interaction = InteractionType.Loot
            });
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.Status = PlanStatus.Active;
            return;
        }

        // Loot and every kind of aid share the same lying-body approach: walk
        // to the junction nearest her feet, then execution pins the exact root
        // pose there. This replaces the arbitrary first free neighbour.
        var approach = TryReserveArmsLengthApproach(world, npc, mark, markJunction);
        if (approach is not { } approachJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            AbandonLootHelpless(world, npc, "NoApproach",
                Spec111.LootHelplessCooldownTicks);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "PlanFailed",
                    $"Goal=LootHelpless Mark={mark.Id.Value} NoApproach");
            }
            return;
        }

        npc.Plan.TargetAgentId = mark.Id;
        npc.Plan.TargetJunctionId = approachJunction;
        npc.Plan.TargetTile = mark.Tile;
        ClaimHelplessMark(npc, mark);

        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approachJunction
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetJunction = approachJunction,
            Interaction = InteractionType.Loot
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlanBuilt",
                $"Goal=LootHelpless Mark=NPC{mark.Id.Value} " +
                $"Items={mark.Inventory.Items.Count}");
        }
    }

    // Держимся ОДНОГО тела, пока оно годится: перевыбор на каждом ребилде
    // заставлял бы его метаться между двумя лежащими, не дойдя ни до кого
    // (ровно то, чем болеет §56 Prey).
    private static NPCState ResolveHelplessMark(WorldState world, NPCState npc)
    {
        if (npc.Mind.LootHelplessTargetNpcId is { } chosen &&
            world.Entities.Npcs.TryGetValue(chosen, out var current) &&
            LootHelplessMath.IsLootableBy(world, npc, current))
        {
            return current;
        }

        var mark = LootHelplessMath.BestMark(world, npc);
        if (mark is not null)
        {
            npc.Mind.LootHelplessTargetNpcId = mark.Id;
            npc.Mind.LootHelplessTakenCount = 0;
        }

        return mark;
    }

    private static void ClaimHelplessMark(NPCState npc, NPCState mark)
    {
        // Кьюшки над лежащей молча гасятся (§60), поэтому сигналов тут нет —
        // только сама заявка. Некому её показывать: она без сознания.
        mark.Mind.PendingLootedBy = npc.Id;
    }

    // Снять заявку ОБЯЗАТЕЛЬНО во ВСЕХ выходах: забытая метка делает тело
    // «занятым навсегда», и больше его не тронет никто (урок §81).
    internal static void AbandonLootHelpless(
        WorldState world, NPCState npc, string reason, int cooldownTicks = -1)
    {
        if (npc.Mind.LootHelplessTargetNpcId is { } markId &&
            world.Entities.Npcs.TryGetValue(markId, out var mark) &&
            mark.Mind.PendingLootedBy is { } claimed && claimed.Equals(npc.Id))
        {
            mark.Mind.PendingLootedBy = null;
        }

        npc.Mind.LootHelplessTargetNpcId = null;
        npc.Mind.LootHelplessTakenCount = 0;
        // §89: срыв — не попытка. Полный кулдаун вешает только сыгранная сцена,
        // здесь короткая передышка, чтобы он не молотил планами каждый тик.
        npc.Mind.LootHelplessCooldownUntilTick = world.Tick +
            (cooldownTicks >= 0 ? cooldownTicks : Spec111.LootHelplessRetryTicks);
        if (npc.Mind.CurrentGoal == GoalType.LootHelpless)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "LootHelplessAbandoned", $"Reason={reason}");

        }
    }
}

}
