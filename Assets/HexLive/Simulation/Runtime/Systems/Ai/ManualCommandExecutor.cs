using System.Collections.Generic;
using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// §121: приказы игрока становятся планами. Единственное место, где команда
// превращается в состояние NPC, — дальше всё делают ШТАТНЫЕ системы:
// PathfindingSystem прокладывает путь, MovementSystem ведёт, ExecutionSystem
// исполняет взаимодействие. Ручной режим не заводит второй симуляции, он
// только отбирает у аукциона право ставить цель.
//
// Три правила, которые здесь нельзя нарушить:
//
// 1. ⭐ КАЖДЫЙ приказ начинается с PlanInterruption.Abort. Игрок кликает
//    быстрее, чем идёт тик, и без этого каждый второй клик оставлял бы за
//    собой зарезервированный узел и занятый объект — мир бы медленно
//    зарастал «занято навсегда».
// 2. Цели берутся ИЗ МИРА, а не из восприятия NPC. Игрок видит остров целиком;
//    приказ на кокос, которого она ещё не заметила, обязан работать. Правду на
//    месте всё равно проверит ExecutionSystem, когда она дойдёт.
// 3. Отказ — это ТРАССА, а не тишина: ManualOrderRejected с Reason=. Молчащий
//    приказ читается игроком как «игра сломалась».
internal static class ManualCommandExecutor
{
    public static ManualCommandAdmission Apply(WorldState world, ISimulationCommand command)
    {
        var admission = new AdmissionTracker(command.TargetEntity, OrderName(command));
        if (!Spec121.ManualControlEnabled)
        {
            admission.Reject("FeatureDisabled");
            return FinishAdmission(world, admission);
        }

        switch (command)
        {
            case SetManualControlCommand setManual:
                ApplySetManual(world, setManual, admission);
                break;
            case MoveToCommand moveTo:
                ApplyMoveTo(world, moveTo, admission);
                break;
            case InteractCommand interact:
                ApplyInteract(world, interact, admission);
                break;
            case AttackNpcCommand attackNpc:
                ApplyAttackNpc(world, attackNpc, admission);
                break;
            case CarryPersonCommand carryPerson:
                ApplyCarryPerson(world, carryPerson, admission);
                break;
            case PutDownPersonCommand putDownPerson:
                ApplyPutDownPerson(world, putDownPerson, admission);
                break;
            case AttackMobCommand attackMob:
                ApplyAttackMob(world, attackMob, admission);
                break;
            case StopCommand stop:
                ApplyStop(world, stop, admission);
                break;
            case GroupMoveCommand groupMove:
                ApplyGroupMove(world, groupMove);
                break;
            case GroupStopCommand groupStop:
                ApplyGroupStop(world, groupStop);
                break;
            case GroupAttackNpcCommand groupAttackNpc:
                ApplyGroupAttackNpc(world, groupAttackNpc);
                break;
            case GroupAttackMobCommand groupAttackMob:
                ApplyGroupAttackMob(world, groupAttackMob);
                break;
            case SetGroupManualControlCommand setGroupManual:
                ApplySetGroupManual(world, setGroupManual);
                break;
            case ManageInventoryCommand inventory:
                ApplyManageInventory(world, inventory, admission);
                break;
            case TransferInventoryCommand transfer:
                ApplyTransferInventory(world, transfer, admission);
                break;
            default:
                admission.Reject("UnsupportedCommand");
                break;
        }

        return FinishAdmission(world, admission);
    }

    private sealed class AdmissionTracker
    {
        public AdmissionTracker(EntityId? actor, string order)
        {
            Actor = actor;
            Order = order;
        }

        public EntityId? Actor { get; }
        public string Order { get; }
        public string Reason { get; private set; } = string.Empty;
        public bool Accepted => Reason.Length == 0;

        public void Reject(string reason)
        {
            if (Reason.Length == 0) Reason = reason;
        }

        public ManualCommandAdmission Result => new(
            Accepted
                ? ManualCommandAdmissionStatus.Accepted
                : ManualCommandAdmissionStatus.Rejected,
            Actor, Order, Reason);
    }

    private static string OrderName(ISimulationCommand command) => command switch
    {
        SetManualControlCommand => "SetManual",
        MoveToCommand => "MoveTo",
        InteractCommand => "Interact",
        AttackNpcCommand => "AttackNpc",
        CarryPersonCommand => "CarryPerson",
        PutDownPersonCommand => "PutDownPerson",
        AttackMobCommand => "AttackMob",
        StopCommand => "Stop",
        GroupMoveCommand => "GroupMove",
        GroupStopCommand => "GroupStop",
        GroupAttackNpcCommand => "GroupAttackNpc",
        GroupAttackMobCommand => "GroupAttackMob",
        SetGroupManualControlCommand => "SetManual",
        ManageInventoryCommand => "Inventory",
        TransferInventoryCommand => "TransferInventory",
        _ => command.GetType().Name
    };

    private static ManualCommandAdmission FinishAdmission(
        WorldState world, AdmissionTracker admission)
    {
        var result = admission.Result;
        if (SimTrace.Enabled)
        {
            var message = $"Order={result.Order} Status={result.Status} " +
                $"Reason={(result.Reason.Length == 0 ? "-" : result.Reason)}";
            if (result.Actor is { } actor)
            {
                Trace.Debug(world, actor, "ManualCommandAdmission", message);
            }
            else
            {
                Trace.DebugSystem(world, "ManualCommandAdmission", message);
            }
        }

        return result;
    }

    /// <summary>Приказ отклонён и почему. Тип НЕ в GameEventTypes намеренно:
    /// это сигнал игроку в момент клика, а не строка в летописи колонии.</summary>
    private static void Reject(
        WorldState world, EntityId npc, string verb, string reason,
        AdmissionTracker admission)
    {
        admission.Reject(reason);
        Trace.Emit(world, npc, "ManualOrderRejected", $"Order={verb} Reason={reason}");
    }

    // Общий вход: NPC существует, жив и (кроме тумблера) действительно ручной.
    private static bool TryTakeOrder(
        WorldState world, EntityId id, string verb, bool requireManual,
        AdmissionTracker admission, out NPCState npc)
    {
        if (!world.Entities.Npcs.TryGetValue(id, out npc) || npc.Health <= 0f)
        {
            Reject(world, id, verb, "NoSuchNpc", admission);
            return false;
        }

        if (npc.Faction != Faction.Colony)
        {
            Reject(world, id, verb, "NotOwned", admission);
            return false;
        }

        if (requireManual && !npc.Mind.ManualControl)
        {
            // Клик, отправленный до того, как игрок вернул её ИИ, — приказ
            // молча применять нельзя: он бы перебил только что выбранную цель.
            Reject(world, id, verb, "NotManual", admission);
            return false;
        }

        return true;
    }

    // Тело не в состоянии слушаться: кома, умирание, обморок, притворство,
    // рыдания. Тумблер и «отставить» проходят — они меняют не действие, а
    // режим, и должны работать над лежащей.
    private static bool Incapacitated(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) ||
        world.Tick < npc.Mind.CryingUntilTick ||
        world.Tick < npc.Mind.PlayDeadUntilTick;

    // ⭐ Общее начало любого действия: снять с себя всё, что держал прошлый
    // приказ. Без этого спам кликов течёт резервациями (см. правило 1).
    private static void ClearForNewOrder(
        WorldState world, NPCState npc, string reason, bool keepCarriedPerson = false)
    {
        // Bug #95 / spec 41.5: a manual order may wake a sleeper, but it must
        // not make the sim translate the body while GetUp is still playing.
        // Capture this before Abort clears CurrentInteraction, then retain the
        // replacement order behind the same grace as a completed sleep.
        var interruptedSleep = npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Sleep;

        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress ||
            npc.IsCarryingPerson || npc.Mind.InterruptedRescuePatientId is not null)
        {
            if (keepCarriedPerson && npc.IsCarryingPerson)
            {
                PlanInterruption.TryAbortKeepingCarriedPerson(world, npc, InterruptionCause.PlayerCommand, reason);
            }
            else
            {
                PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, reason);
            }
        }

        if (interruptedSleep)
        {
            npc.Mind.WakeGraceUntilTick = System.Math.Max(
                npc.Mind.WakeGraceUntilTick, world.Tick + AiBalance.WakeGraceTicks);
        }

        npc.Plan.Steps.Clear();
        npc.Plan.RunRequested = false;
        npc.Mind.GoalLock = null;
        // §121.7: любая принятая команда продлевает lease внимания игрока
        // (сюда приходят только принятые — TryTakeOrder уже отработал).
        ManualControlMath.RenewInactivityLease(world, npc);
    }

    private static void ApplySetManual(
        WorldState world, SetManualControlCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "SetManual", requireManual: false,
                admission, out var npc))
        {
            return;
        }

        if (npc.Mind.ManualControl == command.Enabled)
        {
            return;
        }

        if (!command.Enabled)
        {
            ReleaseToAi(world, npc, "Игрок вернул управление ИИ", expired: false);
            return;
        }

        ClearForNewOrder(world, npc, "Игрок взял управление");

        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.ManualAttackNpcId = null;
        npc.Mind.ManualAttackMobId = null;

        // Приглашения снимаются вместе с автономией: ждать разговора или
        // помощи она больше не станет, и оставленная заявка подвесила бы
        // ЗВАВШУЮ — та стоит и ждёт ответа, которого уже не будет.
        npc.Mind.PendingTalkFrom = null;
        npc.Mind.PendingAidFrom = null;

        npc.Mind.ManualControl = true;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualControlChanged", "Enabled=1");
        }
    }

    // §121.7: единственный владелец перехода 🎮→🧠 — и тумблер игрока, и
    // таймаут бездействия идут через него, чтобы «вернуть под ИИ» всегда
    // значило одно и то же. expired=true добавляет player-visible событие:
    // молчаливое «она вдруг зажила своей жизнью» читалось бы как поломка.
    internal static void ReleaseToAi(
        WorldState world, NPCState npc, string reason, bool expired)
    {
        // keepCarriedPerson НЕ ставим: как и прежний тумблер off, возврат под
        // ИИ безопасно кладёт ношу — дальше RescueSystem сам решит поднять.
        ClearForNewOrder(world, npc, reason);
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.ManualAttackNpcId = null;
        npc.Mind.ManualAttackMobId = null;
        npc.Mind.ManualControl = false;
        ManualControlMath.ClearInactivityLease(npc);
        if (expired)
        {
            Trace.Emit(world, npc.Id, "ManualControlExpired",
                $"IdleSeconds={Spec121.ManualIdleReleaseSeconds}");
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualControlChanged", "Enabled=0");
        }
    }

    private static void ApplyStop(
        WorldState world, StopCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "Stop", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        ClearForNewOrder(world, npc, "Приказ отставить", keepCarriedPerson: true);
        npc.Mind.CurrentGoal = GoalType.None;
        ClearAttackOrder(world, npc);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderStopped", "Order=Stop");

        }
    }

    private static void ApplyMoveTo(
        WorldState world, MoveToCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "MoveTo", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "MoveTo", "Incapacitated", admission);
            return;
        }

        if (SpatialQueries.FindNearestJunction(world, command.WorldPosition) is not { } destination ||
            !world.Junctions.Items.TryGetValue(destination, out var junction) ||
            junction.Blocked)
        {
            Reject(world, npc.Id, "MoveTo", "Unreachable", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, destination, npc.Body.CanJump))
        {
            Reject(world, npc.Id, "MoveTo", "Unreachable", admission);
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        InstallMovePlan(world, npc, destination, junction, command.Run);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=MoveTo Junction={destination.Value} " +
                $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
                $"Pace={(command.Run ? "Run" : "Walk")}");
        }
    }

    private static void InstallMovePlan(
        WorldState world, NPCState npc, JunctionId destination, Junction junction,
        bool run)
    {
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetJunctionId = destination;
        npc.Plan.TargetTile = junction.Tiles.Count > 0 ? junction.Tiles[0] : null;
        npc.Plan.RunRequested = run;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
    }

    private static void ApplyCarryPerson(
        WorldState world, CarryPersonCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "CarryPerson", requireManual: true,
                admission, out var carrier))
        {
            return;
        }

        if (Incapacitated(world, carrier))
        {
            Reject(world, carrier.Id, "CarryPerson", "Incapacitated", admission);
            return;
        }

        if (carrier.IsCarryingPerson)
        {
            Reject(world, carrier.Id, "CarryPerson", "HandsOccupied", admission);
            return;
        }

        // Приказ игрока тоже не поднимает ползущую на ноги.
        if (carrier.Body.IsCrawling)
        {
            Reject(world, carrier.Id, "CarryPerson", "Crawling", admission);
            return;
        }

        if (!KenshiRescueMath.TryGetPerson(
                world, command.Target, out var person, out var dead) ||
            person.Id.Equals(carrier.Id))
        {
            Reject(world, carrier.Id, "CarryPerson", "NoSuchPerson", admission);
            return;
        }

        if (person.IsBeingCarried || (!dead && !person.IsLyingDown(world.Tick)))
        {
            Reject(world, carrier.Id, "CarryPerson", "PersonNotAvailable", admission);
            return;
        }

        ClearForNewOrder(world, carrier, "Ручной приказ поднять человека");
        ClearAttackOrder(world, carrier);
        if (!KenshiRescueMath.TryFindApproach(world, carrier, person, out var approach))
        {
            carrier.Mind.CurrentGoal = GoalType.None;
            Reject(world, carrier.Id, "CarryPerson", "Unreachable", admission);
            return;
        }

        carrier.Plan.Goal = GoalType.PlayerOrder;
        carrier.Plan.TargetAgentId = person.Id;
        carrier.Plan.TargetJunctionId = approach;
        carrier.Plan.TargetTile = person.Tile;
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = approach
        });
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PickUpPerson,
            TargetJunction = approach,
            Interaction = InteractionType.PickUpPerson
        });
        carrier.Plan.CurrentStepIndex = 0;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Mind.CurrentGoal = GoalType.PlayerOrder;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "ManualOrderAccepted",
                $"Order=CarryPerson Target=NPC{person.Id.Value} Dead={(dead ? 1 : 0)} " +
                $"Junction={approach.Value}");
        }
    }

    private static void ApplyPutDownPerson(
        WorldState world, PutDownPersonCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "PutDownPerson", requireManual: true,
                admission, out var carrier))
        {
            return;
        }

        if (!carrier.IsCarryingPerson)
        {
            Reject(world, carrier.Id, "PutDownPerson", "HandsEmpty", admission);
            return;
        }

        var carried = carrier.CarriedNpcId;
        PlanInterruption.TryAbort(world, carrier, InterruptionCause.PlayerCommand, "Игрок положил переносимого человека");
        carrier.Mind.CurrentGoal = GoalType.None;
        // §121.7: PutDown идёт мимо ClearForNewOrder — lease продлевается здесь.
        ManualControlMath.RenewInactivityLease(world, carrier);
        ClearAttackOrder(world, carrier);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "ManualOrderAccepted",
                $"Order=PutDownPerson Target=NPC{carried?.Value ?? 0}");
        }
    }

    // §123: group commands deliberately have one aggregate trace. The UI can
    // explain partial success without racing a burst of per-NPC toast events.
    private static void GroupResult(
        WorldState world, string order, int selected, int manual, int accepted,
        int noPath, int incapacitated, int ai)
    {
        Trace.EmitSystem(world, "GroupOrderResult",
            $"Order={order} Selected={selected} Manual={manual} Accepted={accepted} " +
            $"NoPath={noPath} Incapacitated={incapacitated} AI={ai}");
    }

    private static List<NPCState> GroupActors(
        WorldState world, IReadOnlyList<EntityId> ids, bool rejectIncapacitated,
        out int manual, out int incapacitated, out int ai)
    {
        var eligible = new List<NPCState>();
        manual = 0;
        incapacitated = 0;
        ai = 0;
        foreach (var id in ids)
        {
            if (!PlayerAuthority.CanControl(world, id, out var npc) || npc.Health <= 0f)
            {
                continue;
            }

            if (!npc.Mind.ManualControl)
            {
                ai++;
                continue;
            }

            manual++;
            if (rejectIncapacitated && Incapacitated(world, npc))
            {
                incapacitated++;
                continue;
            }

            eligible.Add(npc);
        }

        eligible.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return eligible;
    }

    private static void ApplyGroupMove(WorldState world, GroupMoveCommand command)
    {
        var actors = GroupActors(world, command.Actors, rejectIncapacitated: true,
            out var manual, out var incapacitated, out var ai);
        var formation = GroupFormationPlanner.Plan(world, actors, command.WorldPosition);

        // A participant's old reservation is releasable only when that same
        // participant received a new assignment. Unmatched actors keep both
        // their action and their claims.
        var reassigned = new HashSet<EntityId>();
        foreach (var assignment in formation.Assignments) reassigned.Add(assignment.Npc.Id);
        foreach (var assignment in formation.Assignments)
        {
            if (world.Reservations.Junctions.TryGetValue(
                    assignment.Destination, out var oldReservation) &&
                reassigned.Contains(oldReservation.Owner))
            {
                SpatialMutations.ReleaseJunctionReservation(
                    world, assignment.Destination, oldReservation.Owner);
            }
        }

        var accepted = 0;
        foreach (var assignment in formation.Assignments)
        {
            if (!world.Junctions.Items.TryGetValue(assignment.Destination, out var destination) ||
                !SpatialMutations.TryReserveJunction(
                    world, assignment.Destination, assignment.Npc.Id,
                    world.Tick, Spec121.ManualReserveTicks))
            {
                continue;
            }

            ClearForNewOrder(world, assignment.Npc, "Новый групповой приказ игрока",
                keepCarriedPerson: true);
            ClearAttackOrder(world, assignment.Npc);
            // If the deterministic assignment reused this actor's former
            // endpoint, Abort just released the provisional owner-guarded
            // claim above. Renew after interruption as the transaction's
            // final write; no other system can race us inside command drain.
            SpatialMutations.TryReserveJunction(
                world, assignment.Destination, assignment.Npc.Id,
                world.Tick, Spec121.ManualReserveTicks);
            InstallMovePlan(
                world, assignment.Npc, assignment.Destination, destination, command.Run);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, assignment.Npc.Id, "ManualOrderAccepted",
                    $"Order=GroupMove Junction={assignment.Destination.Value} " +
                    $"Pace={(command.Run ? "Run" : "Walk")}");
            }
            accepted++;
        }

        GroupResult(world, "MoveTo", command.Actors.Count, manual, accepted,
            actors.Count - accepted, incapacitated, ai);
    }

    private static void ApplyGroupStop(WorldState world, GroupStopCommand command)
    {
        var actors = GroupActors(world, command.Actors, rejectIncapacitated: false,
            out var manual, out var ignored, out var ai);
        foreach (var npc in actors)
        {
            ClearForNewOrder(world, npc, "Групповой приказ отставить",
                keepCarriedPerson: true);
            npc.Mind.CurrentGoal = GoalType.None;
            ClearAttackOrder(world, npc);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualOrderStopped", "Order=GroupStop");

            }
        }

        GroupResult(world, "Stop", command.Actors.Count, manual, actors.Count, 0, 0, ai);
    }

    private static void ApplyGroupAttackNpc(WorldState world, GroupAttackNpcCommand command)
    {
        var actors = GroupActors(world, command.Actors, rejectIncapacitated: true,
            out var manual, out var incapacitated, out var ai);
        var accepted = 0;
        if (world.Entities.Npcs.TryGetValue(command.Target, out var target) && target.Health > 0f)
        {
            foreach (var npc in actors)
            {
                if (npc.Id.Equals(target.Id)) continue;
                ClearForNewOrder(world, npc, "Групповой приказ атаковать");
                ClearAttackOrder(world, npc);
                npc.Mind.CurrentGoal = GoalType.PlayerAttack;
                npc.Mind.ManualAttackNpcId = target.Id;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                        $"Order=GroupAttackNpc Target=NPC{target.Id.Value}");
                }
                accepted++;
            }
        }

        GroupResult(world, "AttackNpc", command.Actors.Count, manual, accepted,
            actors.Count - accepted, incapacitated, ai);
    }

    private static void ApplyGroupAttackMob(WorldState world, GroupAttackMobCommand command)
    {
        var actors = GroupActors(world, command.Actors, rejectIncapacitated: true,
            out var manual, out var incapacitated, out var ai);
        var accepted = 0;
        if (ManualControlMath.TryGetMob(world, command.MobId, out _))
        {
            foreach (var npc in actors)
            {
                ClearForNewOrder(world, npc, "Групповой приказ атаковать зверя");
                ClearAttackOrder(world, npc);
                npc.Mind.CurrentGoal = GoalType.PlayerAttack;
                npc.Mind.ManualAttackMobId = command.MobId;
                npc.Mind.CombatAssistDogId = command.MobId;
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                        $"Order=GroupAttackMob Target=Dog{command.MobId}");
                }
                accepted++;
            }
        }

        GroupResult(world, "AttackMob", command.Actors.Count, manual, accepted,
            actors.Count - accepted, incapacitated, ai);
    }

    private static void ApplySetGroupManual(
        WorldState world, SetGroupManualControlCommand command)
    {
        var accepted = 0;
        foreach (var id in command.Actors)
        {
            if (!PlayerAuthority.CanControl(world, id, out var npc) || npc.Health <= 0f)
            {
                continue;
            }

            accepted++;
            if (npc.Mind.ManualControl == command.Enabled) continue;
            ClearForNewOrder(world, npc, command.Enabled
                ? "Игрок взял групповое управление"
                : "Игрок вернул группу ИИ");
            npc.Mind.CurrentGoal = GoalType.None;
            ClearAttackOrder(world, npc);
            npc.Mind.ManualControl = command.Enabled;
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, npc.Id, "ManualControlChanged",
                    $"Enabled={(command.Enabled ? 1 : 0)}");
            }
        }

        GroupResult(world, "SetManual", command.Actors.Count,
            command.Enabled ? accepted : 0, accepted, 0, 0,
            command.Enabled ? 0 : accepted);
    }

    private static void ApplyManageInventory(
        WorldState world, ManageInventoryCommand command, AdmissionTracker admission)
    {
        if (!PlayerAuthority.CanMutateInventory(world, command.Npc, out var npc) ||
            npc.Health <= 0f)
        {
            Reject(world, command.Npc, "Inventory", "NotOwned", admission);
            return;
        }

        var source = command.Item.Source == InventoryItemSource.Carried
            ? npc.Inventory.Items
            : npc.WornItems;
        if (command.Item.Index < 0 || command.Item.Index >= source.Count ||
            source[command.Item.Index].DefinitionId != command.Item.ExpectedDefinitionId)
        {
            Reject(world, npc.Id, "Inventory", "StaleItem", admission);
            return;
        }

        var item = source[command.Item.Index];
        var stepType = PlanStepType.PlayerDropCarried;
        switch (command.Action)
        {
            case InventoryAction.Wear:
                if (command.Item.Source != InventoryItemSource.Carried ||
                    !world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var wearDef) ||
                    wearDef.Layer is null)
                {
                    Reject(world, npc.Id, "Inventory", "InvalidAction", admission);
                    return;
                }
                stepType = PlanStepType.PlayerWearInventory;
                break;
            case InventoryAction.Stow:
                if (command.Item.Source != InventoryItemSource.Worn)
                {
                    Reject(world, npc.Id, "Inventory", "InvalidAction", admission);
                    return;
                }
                stepType = PlanStepType.PlayerStowWorn;
                break;
            case InventoryAction.Drop:
                stepType = command.Item.Source == InventoryItemSource.Worn
                    ? PlanStepType.PlayerDropWorn
                    : PlanStepType.PlayerDropCarried;
                break;
            default:
                Reject(world, npc.Id, "Inventory", "InvalidAction", admission);
                return;
        }

        if (!PlayerInventoryMath.FitsAfter(world, npc, command.Item, command.Action))
        {
            Reject(world, npc.Id, "Inventory", "InsufficientSpace", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Ручное изменение инвентаря");
        ClearAttackOrder(world, npc);
        npc.Plan.Goal = GoalType.PlayerInventory;
        npc.Plan.TargetItemDefinitionId = item.DefinitionId;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = stepType,
            // Source index already exists in the serialized step shape. It is
            // not a timeout for these append-only inventory step types.
            TimeoutEndTick = command.Item.Index
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerInventory;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Inventory Action={command.Action} Source={command.Item.Source} " +
                $"Index={command.Item.Index} Def={item.DefinitionId}");
        }
    }

    private static void ApplyTransferInventory(
        WorldState world, TransferInventoryCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Looter, "TransferInventory",
                requireManual: true, admission, out var looter))
        {
            return;
        }

        if (Incapacitated(world, looter))
        {
            Reject(world, looter.Id, "TransferInventory", "Incapacitated", admission);
            return;
        }

        if (command.Direction is not InventoryTransferDirection.Take and
            not InventoryTransferDirection.Give)
        {
            Reject(world, looter.Id, "TransferInventory", "InvalidDirection", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Other, out var other) ||
            other.Id.Equals(looter.Id) || other.Health <= 0f ||
            !other.IsUnconscious(world.Tick) || other.IsBeingCarried ||
            CombatMedium.IsNpcSwimming(world, other))
        {
            Reject(world, looter.Id, "TransferInventory", "PersonNotAvailable", admission);
            return;
        }

        var source = command.Direction == InventoryTransferDirection.Take ? other : looter;
        var destination = command.Direction == InventoryTransferDirection.Take ? looter : other;
        if (!PlayerInventoryTransferMath.FitsAfter(
                world, source, destination, command.Item, command.Count))
        {
            Reject(world, looter.Id, "TransferInventory", "StaleOrNoSpace", admission);
            return;
        }

        ClearForNewOrder(world, looter, "Ручной обмен с лежащим человеком");
        ClearAttackOrder(world, looter);

        looter.Plan.Goal = GoalType.PlayerInventory;
        looter.Plan.TargetAgentId = other.Id;
        looter.Plan.TargetItemDefinitionId = command.Item.ExpectedDefinitionId;
        looter.Plan.TargetTile = other.Tile;

        // §111.13: приказ игрока идёт мимо планировщика, поэтому станцию он
        // занимает прямо здесь — иначе ручной обмен остался бы единственным
        // путём, который по-прежнему делит точку у ног с чужой сценой.
        var orderSlot = LyingStations.TryClaim(world, looter, other, out var claimed)
            ? claimed
            : LyingStations.SlotFor(world, looter, other);
        var closeEnough = InteractionReach.CheckPersonStart(
            world, looter, other, LyingStations.Point(other, orderSlot),
            LyingStations.Reach(orderSlot),
            $"Player inventory transfer with NPC{other.Id.Value}");
        if (!closeEnough)
        {
            if (!KenshiRescueMath.TryFindApproach(world, looter, other, out var approach))
            {
                looter.Mind.CurrentGoal = GoalType.None;
                looter.Plan.Goal = GoalType.None;
                looter.Plan.Status = PlanStatus.Failed;
                looter.Plan.CurrentStepIndex = 0;
                looter.Plan.Steps.Clear();
                looter.Plan.TargetAgentId = null;
                looter.Plan.TargetItemDefinitionId = null;
                looter.Plan.TargetJunctionId = null;
                looter.Plan.TargetTile = null;
                Reject(world, looter.Id, "TransferInventory", "Unreachable", admission);
                return;
            }

            looter.Plan.TargetJunctionId = approach;
            looter.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = approach
            });
        }

        var stepType = (command.Direction, command.Item.Source) switch
        {
            (InventoryTransferDirection.Take, InventoryItemSource.Carried) =>
                PlanStepType.PlayerTakeCarried,
            (InventoryTransferDirection.Take, InventoryItemSource.Worn) =>
                PlanStepType.PlayerTakeWorn,
            (InventoryTransferDirection.Give, InventoryItemSource.Carried) =>
                PlanStepType.PlayerGiveCarried,
            _ => PlanStepType.PlayerGiveWorn
        };
        looter.Plan.Steps.Add(new PlanStep
        {
            Type = stepType,
            TimeoutEndTick = PlayerInventoryTransferMath.PackCursor(
                command.Item.Index, command.Count)
        });
        looter.Plan.CurrentStepIndex = 0;
        looter.Plan.Status = PlanStatus.Active;
        looter.Mind.CurrentGoal = GoalType.PlayerInventory;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, looter.Id, "ManualOrderAccepted",
                $"Order=TransferInventory Direction={command.Direction} " +
                $"Other=NPC{other.Id.Value} Source={command.Item.Source} " +
                $"Index={command.Item.Index} Count={command.Count} " +
                $"Def={command.Item.ExpectedDefinitionId}");
        }
    }

    private static void ApplyInteract(
        WorldState world, InteractCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "Interact", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "Interact", "Incapacitated", admission);
            return;
        }

        // Правило 2: объект берётся из МИРА, а не из npc.Perception.
        if (!world.Entities.Objects.TryGetValue(command.Target, out var worldObject))
        {
            Reject(world, npc.Id, "Interact", "TargetGone", admission);
            return;
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            Reject(world, npc.Id, "Interact", "TargetGone", admission);
            return;
        }

        InteractionDefinition? interaction = null;
        foreach (var candidate in definition.Interactions)
        {
            if (candidate.Type == command.Interaction)
            {
                interaction = candidate;
                break;
            }
        }

        if (interaction is null)
        {
            Reject(world, npc.Id, "Interact", "NoSuchAction", admission);
            return;
        }

        // Тот же ANY-OF гейт, что стоит на входе в ExecutionSystem. Здесь он —
        // ВТОРАЯ проверка (первая посерила пункт в меню), и обе нужны: меню
        // могло быть открыто до того, как она выронила нож.
        if (interaction.RequiredCapabilities.Count > 0 &&
            !DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities))
        {
            Reject(world, npc.Id, "Interact", "MissingTool", admission);
            return;
        }

        if (worldObject.IsOccupied && worldObject.CurrentUser is { } user && !user.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Interact", "Occupied", admission);
            return;
        }

        if (worldObject.Junctions.Count == 0)
        {
            Reject(world, npc.Id, "Interact", "Unreachable", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока");
        ClearAttackOrder(world, npc);

        var anchor = worldObject.Junctions[0];
        // Manual and autonomous object orders share the exact same approach
        // predicate.  In particular Sit/Sleep always reserve the nearest free
        // rim junction; their anchor is a pose marker, never a standing spot.
        var standBeside = PlanningSystem.RequiresBesideApproach(
            world, anchor, command.Interaction);

        JunctionId target;
        if (standBeside)
        {
            // К стволу вплотную не встать — планировщик решает это выбором
            // клетки на ободе, и приказ пользуется ТЕМ ЖЕ кодом: разойдись эти
            // две дороги, ручная колонистка вставала бы к пальме иначе, чем
            // автоматическая, и «подойти» значило бы разное.
            if (!PlanningSystem.TryReserveBesideJunction(
                    world, npc, anchor, Spec121.ManualReserveTicks, out target,
                    SpatialQueries.BesideReach(definition.ObstacleRadius), worldObject))
            {
                Reject(world, npc.Id, "Interact", "Unreachable", admission);
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }
        }
        else
        {
            target = anchor;
            if (!SpatialMutations.TryReserveJunction(
                    world, target, npc.Id, world.Tick, Spec121.ManualReserveTicks))
            {
                Reject(world, npc.Id, "Interact", "Occupied", admission);
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }
        }

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, target, npc.Body.CanJump))
        {
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            Reject(world, npc.Id, "Interact", "Unreachable", admission);
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Двухшаговый план — байт в байт тот, что строит планировщик для любой
        // работы с объектом. ExecutionSystem не знает и не должна знать, что
        // этот план пришёл от игрока.
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetObjectId = worldObject.Id;
        npc.Plan.TargetTile = worldObject.Tile;
        npc.Plan.TargetJunctionId = target;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = target,
            TargetObject = worldObject.Id
        });
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.Interact,
            TargetObject = worldObject.Id,
            TargetJunction = target,
            Interaction = command.Interaction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Interact Obj={worldObject.Id.Value} Def={worldObject.DefinitionId} " +
                $"Action={command.Interaction} Junction={target.Value}");
        }
    }

    private static void ApplyAttackNpc(
        WorldState world, AttackNpcCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "AttackNpc", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "AttackNpc", "Incapacitated", admission);
            return;
        }

        if (command.Target.Equals(npc.Id) ||
            !world.Entities.Npcs.TryGetValue(command.Target, out var target) ||
            target.Health <= 0f)
        {
            Reject(world, npc.Id, "AttackNpc", "TargetGone", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ атаковать");
        ClearAttackOrder(world, npc);

        npc.Mind.CurrentGoal = GoalType.PlayerAttack;
        npc.Mind.ManualAttackNpcId = target.Id;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=AttackNpc Target=NPC{target.Id.Value}");
        }
    }

    private static void ApplyAttackMob(
        WorldState world, AttackMobCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "AttackMob", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "AttackMob", "Incapacitated", admission);
            return;
        }

        if (!ManualControlMath.TryGetMob(world, command.MobId, out _))
        {
            Reject(world, npc.Id, "AttackMob", "TargetGone", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ атаковать зверя");
        ClearAttackOrder(world, npc);

        npc.Mind.CurrentGoal = GoalType.PlayerAttack;
        npc.Mind.ManualAttackMobId = command.MobId;
        // Сцепку со зверем держит §57: AnimalCombatSystem бьёт любого, у кого
        // стоит CombatAssistDogId, — приказ просто становится в тот же строй.
        npc.Mind.CombatAssistDogId = command.MobId;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=AttackMob Target=Dog{command.MobId}");
        }
    }

    // Снять сцепку прошлого приказа-атаки. Отдельно от Abort: пара живёт не в
    // плане, а в боевых полях, и Abort про них не знает.
    internal static void ClearAttackOrder(WorldState world, NPCState npc)
    {
        if (npc.Mind.ManualAttackNpcId is { } previous &&
            npc.Mind.CombatOpponentNpcId is { } opponent &&
            opponent.Equals(previous))
        {
            npc.Mind.CombatOpponentNpcId = null;
            FightScene.ReleaseSwingSlot(npc);
            if (world.Entities.Npcs.TryGetValue(previous, out var other) &&
                other.Mind.CombatOpponentNpcId is { } theirs && theirs.Equals(npc.Id))
            {
                other.Mind.CombatOpponentNpcId = null;
            }
        }

        if (npc.Mind.ManualAttackMobId is { } mobId && npc.Mind.CombatAssistDogId == mobId)
        {
            npc.Mind.CombatAssistDogId = null;
        }

        npc.Mind.ManualAttackNpcId = null;
        npc.Mind.ManualAttackMobId = null;
    }
}

}
