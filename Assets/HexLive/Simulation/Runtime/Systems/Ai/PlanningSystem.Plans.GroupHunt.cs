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

// §108: половина «дойти» у групповой охоты. Удары — в GroupHuntSystem, как у
// налёта; здесь только «идти к нему и не растянуться в цепочку».
//
// План ДВИЖЕНИЯ, без TargetAgentId. Это не мелочь: исполнитель отправляет
// любой план с TargetAgentId в RunTalk, и охотница подошла бы к чужаку
// РАЗГОВАРИВАТЬ.
public sealed partial class PlanningSystem
{
    private void BuildGroupHuntPlan(WorldState world, NPCState npc)
    {
        if (npc.Mind.GroupHuntTargetNpcId is not { } targetId ||
            !world.Entities.Npcs.TryGetValue(targetId, out var quarry) ||
            quarry.Health <= 0f)
        {
            GroupHuntSystem.EndHunt(world, npc, "TargetGone");
            npc.Plan.Status = PlanStatus.Failed;
            return;
        }

        // Бюджет охоты — тот же замок, что поставил сговор. Истёк — расходятся.
        if (npc.Mind.GoalLock is not { } huntLock ||
            huntLock.Goal != GoalType.GroupHunt ||
            world.Tick >= huntLock.EndTick)
        {
            GroupHuntSystem.EndHunt(world, npc, "Timeout");
            npc.Plan.Status = PlanStatus.Failed;
            return;
        }

        // §106: нырнул — вода укрывает и его. Не ждать на берегу.
        if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, quarry))
        {
            GroupHuntSystem.EndHunt(world, npc, "TargetSwimming");
            npc.Plan.Status = PlanStatus.Failed;
            return;
        }

        if (GroupHuntMath.PartyAlive(world, targetId) < Spec108.GroupHuntMinRemaining)
        {
            GroupHuntSystem.EndHunt(world, npc, "PartyCollapsed");
            npc.Plan.Status = PlanStatus.Failed;
            return;
        }

        if (quarry.CurrentJunction is not { } quarryJunction)
        {
            npc.Plan.Status = PlanStatus.Failed;
            Trace.Emit(world, npc.Id, "PlanFailed", "Goal=GroupHunt TargetOffGrid");
            return;
        }

        // Цель перечитывается КАЖДУЮ пересборку — отсюда живая погоня, ровно
        // как у Defend за движущимся зверем. Никакого отдельного «режима
        // преследования» не нужно. Пересборка при этом идёт каждый средний
        // проход, пока он движется: сторож устаревшего плана в PlanningSystem.
        // Run роняет активный план, как только тот перестаёт вести К НЕМУ.
        //
        // «Достала» — это дистанция удара, а не соседство узлов: иначе она,
        // стоя в шаге от него, продолжала бы добегать до зарезервированной
        // клетки, и это читалось как «прошла мимо и не заметила».
        var onStation = InteractionReach.CanStrike(world, npc, quarry) ||
            (npc.CurrentJunction is { } current &&
             IsAdjacentJunction(world, current, quarryJunction));
        if (onStation)
        {
            // Стоять и бить. План из одного шага «на свой же узел» здесь
            // завершался бы мгновенно и пересобирался каждый проход (грабли
            // §29C.4B), а удары всё равно раздаёт GroupHuntSystem по смежности.
            npc.Plan.Status = PlanStatus.Completed;
            return;
        }

        // ⭐ ДЕРЖАТЬСЯ ВМЕСТЕ. Сравнение идёт с САМОЙ ОТСТАВШЕЙ, и только строго:
        // хвост группы не ждёт никого и всегда идёт, поэтому взаимное ожидание
        // («после вас») невозможно по построению, а не по счастливой ручке.
        var mine = HexSpatialMath.HexDistance(npc.Tile, quarry.Tile);
        var rear = GroupHuntMath.RearGuardDistance(world, targetId, quarry.Tile);
        if (mine < rear - Spec108.GroupHuntSpreadTiles)
        {
            npc.Plan.Status = PlanStatus.Completed;
            if (npc.Mind.AssistHoldSinceTick == 0)
            {
                npc.Mind.AssistHoldSinceTick = world.Tick;
                Trace.Emit(world, npc.Id, "GroupHuntHolding",
                    $"Target=NPC{targetId.Value} Mine={mine} Rear={rear} — ждёт своих");
            }

            return;
        }

        npc.Mind.AssistHoldSinceTick = 0;

        // Свободный соседний узел, зарезервированный за ней: трое не могут
        // претендовать на одну клетку — ровно тот случай, который комментарий
        // у PickApproachJunction называл «a raiding party».
        if (PickApproachJunction(world, npc, quarryJunction) is not { } approach)
        {
            // Подхода нет — не бросать охоту (соседние узлы освободятся, когда
            // подруги встанут), просто подождать этот проход.
            npc.Plan.Status = PlanStatus.Completed;
            Trace.Emit(world, npc.Id, "GroupHuntHolding",
                $"Target=NPC{targetId.Value} NoFreeApproachJunction");
            return;
        }

        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = quarry.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "PlanBuilt",
            $"Goal=GroupHunt Target=NPC{targetId.Value} " +
            $"ApproachJunction={approach.Value} Dist={mine} Rear={rear} " +
            "Steps=[MoveToJunction]");
    }
}

}
