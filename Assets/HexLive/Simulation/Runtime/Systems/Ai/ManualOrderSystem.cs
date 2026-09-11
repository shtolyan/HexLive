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

    public ChunkPolicy ChunkPolicy => ChunkPolicy.NpcDriven;

    public void Run(WorldState world)
    {
        if (!Spec121.ManualControlEnabled)
        {
            return;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            AgentCommandLedger.Observe(world, npc);
            if (!npc.Mind.ManualControl || npc.Health <= 0f)
            {
                continue;
            }

            // §121.10: исход приказа считывается ДО свипа — тот сбрасывает
            // Plan.Status в None, и «дошла и подобрала» стало бы неотличимо
            // от «план сорвался». Очередь «собрать всё» живёт только на
            // успехах: сорванный подход её закрывает.
            var orderCompleted = npc.Plan.Status == PlanStatus.Completed &&
                npc.Execution.Status != ExecutionStatus.InProgress;

            switch (npc.Mind.CurrentGoal)
            {
                case GoalType.PlayerOrder:
                // §124.1: приказ «уложить в кровать» держит цель Rescue —
                // успешную укладку завершает CompleteCarrier, а вот СНЕСЁННЫЙ
                // план без этого свипа оставлял бы ручную с вечной целью
                // Rescue: ни авто-нужд, ни таймаута (оба ждут None).
                case GoalType.Rescue:
                // §121.9: социальные и само-приказы носят РОДНУЮ цель (как
                // крафт §138). Доигранный или сорванный план обязан вернуть
                // цель в None тем же свипом — иначе ручная застревает с вечной
                // Socialize/Aid: ни авто-нужд, ни таймаута (оба ждут None), а
                // планировщик для ручной выключен и цель не починит.
                case GoalType.Socialize:
                case GoalType.Romance:
                case GoalType.Aid:
                case GoalType.Splint:
                case GoalType.FitProsthetic:
                case GoalType.TreatWounds:
                case GoalType.Sleep:
                case GoalType.Sit:
                case GoalType.Bathe:
                case GoalType.WashClothes:
                case GoalType.Explore:
                    SweepFinishedOrder(world, npc);
                    break;
                case GoalType.PlayerAttack:
                    KeepAttacking(world, npc);
                    break;
                // §121.9 (тёмная фаза): погоня §56 — как приказ атаки, жертву
                // держит тот же якорь ManualAttackNpcId; удары ведёт
                // PredationSystem, пока цель Prey и жертва смежна.
                case GoalType.Prey:
                    KeepPreying(world, npc);
                    break;
                // Сцена §81 доиграна или сорвана — снять клеймо жертвы тем же
                // AbandonAbuse, что у автономного (иначе PendingAbuseFrom
                // остаётся навсегда), и вернуть её в «стоит и ждёт приказа».
                case GoalType.Abuse:
                    if (npc.Plan.Status != PlanStatus.Active &&
                        npc.Execution.Status != ExecutionStatus.InProgress)
                    {
                        PlanningSystem.AbandonAbuse(
                            world, npc, "ManualOrderFinished", 0);
                        npc.Mind.CurrentGoal = GoalType.None;
                        ManualControlMath.RenewInactivityLease(world, npc);
                    }

                    break;
            }

            // §121.10 (баг #270): «собрать всё на гексе» — это ОЧЕРЕДЬ обычных
            // ручных приказов, а не пакетный сбор. Следующий предмет берётся
            // только когда предыдущий доигран (цель уже вернулась в None) —
            // так гекс разбирается по одному листу за раз, и любой новый
            // приказ игрока обрывает очередь сам собой (ClearForNewOrder).
            if (npc.Mind.CurrentGoal == GoalType.None &&
                ManualGatherTargets.IsActive(npc.Mind))
            {
                ManualCommandExecutor.ContinueGatherAll(world, npc, orderCompleted);
            }

            // §121.7: приказ завершён (цель None, сцепки нет) и реальный lease
            // игрока истёк — возврат под ИИ. Авто-цель §121.6 (Eat/Drink)
            // держит CurrentGoal != None и потому отсрочивает релиз до своего
            // завершения; sweep выше в этом же проходе уже мог снять цель.
            if (npc.Mind.CurrentGoal == GoalType.None &&
                npc.Mind.ManualAttackNpcId is null &&
                npc.Mind.ManualAttackMobId is null &&
                !ManualControlMath.HasActiveOrder(npc) &&
                ManualControlMath.InactivityLeaseExpired(world, npc))
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

        var outcome = npc.Plan.Status switch
        {
            PlanStatus.Completed => "Completed",
            PlanStatus.Invalid => "Invalid",
            _ => "Failed"
        };
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.RunRequested = false;
        npc.Plan.RequestedTalkTopic = null;
        // §121.7: завершение приказа продлевает lease — поход длиной больше
        // таймаута не должен «истечь» в момент прибытия.
        ManualControlMath.RenewInactivityLease(world, npc);
        // §160: the controller needs this result after Plan.Status is swept to None.
        Trace.Emit(world, npc.Id, "ManualOrderFinished", $"Order=PlayerOrder Outcome={outcome}");
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
        // Бессознательного не добивают; сознательное ползание и обычный сон
        // не завершают приказ. Хочет обобрать — §111 это отдельное действие, и
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
                ClearAttackApproach(world, npc, "Дошла до цели приказа");
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
            ClearAttackApproach(world, npc, "Цель приказа сместилась");
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
                ClearAttackApproach(world, npc, "Дошла до зверя");
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
            ClearAttackApproach(world, npc, "Зверь сместился");
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

    // §121.9: погоня каннибализма. Форма украдена у KeepAttackingNpc — цель
    // перечитывается каждый средний проход, план перекладывается на её узел.
    // Разница одна: рядом с жертвой цель ОСТАЁТСЯ Prey (удары выдаёт
    // PredationSystem по смежности), латч IsFighting ставит она же.
    private static void KeepPreying(WorldState world, NPCState npc)
    {
        if (npc.Mind.ManualAttackNpcId is not { } targetId)
        {
            // Якоря нет — приказ доигран или снят; обычный sweep.
            SweepFinishedOrder(world, npc);
            return;
        }

        world.Entities.Npcs.TryGetValue(targetId, out var target);
        if (!Spec121.ManualDarkOrdersEnabled || target is null || target.Health <= 0f)
        {
            EndAttack(world, npc, target is null ? "TargetGone" : "TargetDown");
            return;
        }

        if (target.CurrentJunction is not { } targetJunction ||
            npc.CurrentJunction is not { } here)
        {
            return;
        }

        var adjacent = here.Equals(targetJunction) ||
            (world.Junctions.Items.TryGetValue(targetJunction, out var tj) &&
             tj.Neighbors.Contains(here));
        if (adjacent)
        {
            // Дошла — стоять; смежность и удары решает PredationSystem.
            if (npc.Plan.Status == PlanStatus.Active)
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Дошла до жертвы §56");
                npc.Mind.CurrentGoal = GoalType.Prey;
            }

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
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "Жертва §56 сместилась");
            npc.Mind.CurrentGoal = GoalType.Prey;
        }

        npc.Plan.Goal = GoalType.Prey;
        npc.Plan.TargetJunctionId = targetJunction;
        npc.Plan.TargetTile = target.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = targetJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualChaseRepath",
                $"Target=NPC{target.Id.Value} PreyJunction={targetJunction.Value}");
        }
    }

    // Replacing an approach is internal work of the same attack order. The
    // ordinary abort still releases route state, but must not fail its receipt.
    private static void ClearAttackApproach(WorldState world, NPCState npc, string reason)
    {
        world.AgentCommands.TryGetValue(npc.Id.Value, out var ledger);
        var sequence = ledger?.ActiveSequence ?? 0;
        if (ledger != null) ledger.ActiveSequence = 0;
        try { PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, reason); }
        finally
        {
            if (ledger != null && sequence != 0 && ledger.ActiveSequence == 0 &&
                ledger.Receipts.Find(r => r.Sequence == sequence)?.Outcome == "accepted")
                ledger.ActiveSequence = sequence;
        }
    }

    private static void EndAttack(WorldState world, NPCState npc, string reason)
    {
        AgentCommandLedger.Finish(world, npc, reason == "TargetDown" ? "completed" : "failed", reason);
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, $"Приказ атаки окончен: {reason}");
        }

        ManualCommandExecutor.ClearAttackOrder(world, npc);
        npc.IsFighting = false;
        npc.Mind.CurrentGoal = GoalType.None;
        // §121.7: конец сцепки = завершение приказа — lease продлевается.
        ManualControlMath.RenewInactivityLease(world, npc);
        Trace.Emit(world, npc.Id, "ManualOrderFinished", $"Order=PlayerAttack Outcome={reason}");
    }
}

}
