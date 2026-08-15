using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §121: живая половина ручного управления. Приказ ПРИНИМАЕТ
// ManualCommandExecutor (один раз, в момент клика), а держит его эта система —
// каждый средний проход: доведён ли приказ до конца, жива ли цель атаки, не
// ушла ли она с того узла, к которому проложен подход.
//
// ⭐ ПОЧЕМУ ПОСЛЕ RaidSystem. MobSystem гасит IsFighting У ВСЕХ в начале
// среднего прохода, и владеющая боем система защёлкивает флаг заново. Значит
// у боевого латча ровно один владелец, и он обязан быть ПОСЛЕДНИМ словом за
// проход. Для приказа-атаки этот владелец — здесь. Поставить систему раньше
// (или защёлкнуть латч один раз при получении приказа) — значит получить NPC,
// который бьёт воздух: ровно баг §109.12.
public sealed class ManualOrderSystem : ISimulationSystem
{
    public string Name => nameof(ManualOrderSystem);

    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!Spec121.ManualControlEnabled)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!npc.Mind.ManualControl || npc.Health <= 0f)
            {
                continue;
            }

            switch (npc.Mind.CurrentGoal)
            {
                case GoalType.PlayerOrder:
                    SweepFinishedOrder(world, npc);
                    break;
                case GoalType.PlayerAttack:
                    KeepAttacking(world, npc);
                    break;
            }

            // §121.7: приказ завершён (цель None, сцепки нет) и окно внимания
            // игрока истекло — возврат под ИИ. Авто-цель §121.6 (Eat/Drink)
            // держит CurrentGoal != None и потому отсрочивает релиз до своего
            // завершения; sweep выше в этом же проходе уже мог снять цель.
            if (npc.Mind.CurrentGoal == GoalType.None &&
                npc.Mind.ManualAttackNpcId is null &&
                npc.Mind.ManualAttackMobId is null &&
                !ManualControlMath.HasActiveOrder(npc) &&
                world.Tick - npc.Mind.LastManualInputTick >= Spec121.ManualIdleReleaseTicks)
            {
                ManualCommandExecutor.ReleaseToAi(
                    world, npc, "Таймаут ручного управления", expired: true);
            }
        }
    }

    // Приказ доигран (дошла, сделала, или план сорвался) — цель снимается, и
    // она просто стоит. Именно СТОИТ: аукцион для неё закрыт, следующий шаг
    // будет за игроком. Заодно это возвращает её в «без дела» для §109 —
    // то есть под удары она снова начнёт отвечать.
    private static void SweepFinishedOrder(WorldState world, NPCState npc)
    {
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            // §123 formation endpoints remain owned until arrival. Ordinary
            // single move orders still carry no reservation; renew only a
            // claim that this NPC already owns, so old behavior is unchanged.
            if (npc.Plan.TargetObjectId is null &&
                npc.Plan.TargetJunctionId is { } destination &&
                world.Reservations.Junctions.TryGetValue(destination, out var reservation) &&
                reservation.Owner.Equals(npc.Id))
            {
                SpatialMutations.TryReserveJunction(
                    world, destination, npc.Id, world.Tick, Spec121.ManualReserveTicks);
            }
            return;
        }

        var outcome = npc.Plan.Status == PlanStatus.Completed ? "Completed" : "Failed";
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.RunRequested = false;
        // §121.7: завершение приказа перезапускает окно — поход длиной больше
        // таймаута не должен «истечь» в момент прибытия.
        npc.Mind.LastManualInputTick = world.Tick;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderFinished", $"Order=PlayerOrder Outcome={outcome}");

        }
    }

    private static void KeepAttacking(WorldState world, NPCState npc)
    {
        if (npc.Mind.ManualAttackNpcId is { } targetId)
        {
            KeepAttackingNpc(world, npc, targetId);
            return;
        }

        if (npc.Mind.ManualAttackMobId is { } mobId)
        {
            KeepAttackingMob(world, npc, mobId);
            return;
        }

        // Цель испарилась вместе с полями — приказа больше нет.
        npc.Mind.CurrentGoal = GoalType.None;
    }

    private static void KeepAttackingNpc(WorldState world, NPCState npc, EntityId targetId)
    {
        // Лежащего не добивают: приказ «бить» исполнен, когда противник больше
        // не противник. Хочет обобрать — §111 это отдельное действие, и
        // отдавать его должен игрок отдельным приказом.
        if (!world.Entities.Npcs.TryGetValue(targetId, out var target) ||
            target.Health <= 0f ||
            target.IsUnconscious(world.Tick))
        {
            EndAttack(world, npc, target is null ? "TargetGone" : "TargetDown");
            return;
        }

        if (InteractionReach.CanStrike(world, npc, target))
        {
            // Дошла — стоять и бить. План при этом гасится: держать активный
            // план «дойти» в упор значило бы, что она отвернётся и пойдёт на
            // свою зарезервированную клетку посреди размена.
            if (npc.Plan.Status == PlanStatus.Active)
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Дошла до цели приказа");
                npc.Mind.CurrentGoal = GoalType.PlayerAttack;
            }

            var fresh = npc.Mind.CombatOpponentNpcId is not { } previous ||
                !previous.Equals(target.Id);
            npc.IsFighting = true;
            npc.Mind.CombatOpponentNpcId = target.Id;
            if (fresh)
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ManualAttackEngaged", $"Target=NPC{target.Id.Value}");

                }
            }

            return;
        }

        // Не дошла — вести погоню. Цель перечитывается КАЖДЫЙ проход, как у
        // §108: план на клетку, где противник стоял в момент приказа, привёл бы
        // её в пустое место, разминувшись с ним по дороге.
        if (target.CurrentJunction is not { } targetJunction)
        {
            return;
        }

        var stale = !(npc.Plan.Status == PlanStatus.Active &&
            npc.Plan.TargetJunctionId is { } current &&
            (current.Equals(targetJunction) ||
             PlanningSystem.IsAdjacentJunction(world, current, targetJunction)));
        if (!stale || npc.Movement.HopTimer > 0f)
        {
            return;
        }

        if (npc.Plan.Status == PlanStatus.Active)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Цель приказа сместилась");
            npc.Mind.CurrentGoal = GoalType.PlayerAttack;
        }

        if (PlanningSystem.PickApproachJunction(world, npc, targetJunction) is not { } approach)
        {
            // Все подходы заняты — подождать проход, приказ не отменяется.
            return;
        }

        npc.Plan.Goal = GoalType.PlayerAttack;
        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualChaseRepath",
                $"Target=NPC{target.Id.Value} ApproachJunction={approach.Value}");
        }
    }

    private static void KeepAttackingMob(WorldState world, NPCState npc, int mobId)
    {
        if (!ManualControlMath.TryGetMob(world, mobId, out var mob))
        {
            EndAttack(world, npc, "TargetGone");
            return;
        }

        // Удары по зверю раздаёт AnimalCombatSystem всем, у кого стоит
        // CombatAssistDogId, — приказу достаточно держать метку и подводить.
        npc.Mind.CombatAssistDogId = mobId;

        if (HexSpatialMath.HexDistance(npc.Tile, mob.Tile) <= 1)
        {
            if (npc.Plan.Status == PlanStatus.Active)
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Дошла до зверя");
                npc.Mind.CurrentGoal = GoalType.PlayerAttack;
                npc.Mind.CombatAssistDogId = mobId;
            }

            return;
        }

        var stale = !(npc.Plan.Status == PlanStatus.Active &&
            npc.Plan.TargetJunctionId is { } current &&
            (current.Equals(mob.Junction) ||
             PlanningSystem.IsAdjacentJunction(world, current, mob.Junction)));
        if (!stale || npc.Movement.HopTimer > 0f)
        {
            return;
        }

        if (npc.Plan.Status == PlanStatus.Active)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Зверь сместился");
            npc.Mind.CurrentGoal = GoalType.PlayerAttack;
            npc.Mind.CombatAssistDogId = mobId;
        }

        if (PlanningSystem.PickApproachJunction(world, npc, mob.Junction) is not { } approach)
        {
            return;
        }

        npc.Plan.Goal = GoalType.PlayerAttack;
        npc.Plan.TargetJunctionId = approach;
        npc.Plan.TargetTile = mob.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualChaseRepath",
                $"Target=Dog{mobId} ApproachJunction={approach.Value}");
        }
    }

    private static void EndAttack(WorldState world, NPCState npc, string reason)
    {
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, $"Приказ атаки окончен: {reason}");
        }

        ManualCommandExecutor.ClearAttackOrder(world, npc);
        npc.IsFighting = false;
        npc.Mind.CurrentGoal = GoalType.None;
        // §121.7: конец сцепки = завершение приказа — окно перезапускается.
        npc.Mind.LastManualInputTick = world.Tick;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderFinished", $"Order=PlayerAttack Outcome={reason}");

        }
    }
}

}
