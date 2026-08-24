using System.Collections.Generic;
using System.Linq;
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
            case SetOutfitLockCommand setOutfitLock:
                ApplySetOutfitLock(world, setOutfitLock, admission);
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
            case PutPersonInBedCommand putInBed:
                ApplyPutPersonInBed(world, putInBed, admission);
                break;
            case AttackMobCommand attackMob:
                ApplyAttackMob(world, attackMob, admission);
                break;
            case StopCommand stop:
                ApplyStop(world, stop, admission);
                break;
            case CraftItemCommand craft:
                ApplyCraft(world, craft, admission);
                break;
            case TalkToCommand talkTo:
                ApplyTalkTo(world, talkTo, admission);
                break;
            case RomancePersonCommand romance:
                ApplyRomancePerson(world, romance, admission);
                break;
            case MergeCampsCommand mergeCamps:
                ApplyMergeCamps(world, mergeCamps, admission);
                break;
            case AidPersonCommand aidPerson:
                ApplyAidPerson(world, aidPerson, admission);
                break;
            case TreatLimbsCommand treatLimbs:
                ApplyTreatLimbs(world, treatLimbs, admission);
                break;
            case SelfActionCommand selfAction:
                ApplySelfAction(world, selfAction, admission);
                break;
            case PreyPersonCommand prey:
                ApplyPreyPerson(world, prey, admission);
                break;
            case AbusePersonCommand abuse:
                ApplyAbusePerson(world, abuse, admission);
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
            case TransferContainerCommand containerTransfer:
                ApplyTransferContainer(world, containerTransfer, admission);
                break;
            case PlaceBuildingPlanCommand placePlan:
                ApplyPlaceBuildingPlan(world, placePlan, admission);
                break;
            case PlaceFurnitureSiteCommand placeFurniture:
                ApplyPlaceFurnitureSite(world, placeFurniture, admission);
                break;
            case PlaceBuildingBlueprintCommand placeBlueprint:
                ApplyPlaceBuildingBlueprint(world, placeBlueprint, admission);
                break;
            case UpdateBuildingBlueprintCommand updateBlueprint:
                ApplyUpdateBuildingBlueprint(world, updateBlueprint, admission);
                break;
            case ApplyFreeArchitectureCommand freeArchitecture:
                ApplyFreeArchitecture(world, freeArchitecture, admission);
                break;
            case RotateBuildSiteCommand rotateSite:
                ApplyRotateBuildSite(world, rotateSite, admission);
                break;
            case CancelBuildSiteCommand cancelSite:
                ApplyCancelBuildSite(world, cancelSite, admission);
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
        SetOutfitLockCommand => "SetOutfitLock",
        MoveToCommand => "MoveTo",
        InteractCommand => "Interact",
        AttackNpcCommand => "AttackNpc",
        CarryPersonCommand => "CarryPerson",
        PutDownPersonCommand => "PutDownPerson",
        PutPersonInBedCommand => "PutPersonInBed",
        AttackMobCommand => "AttackMob",
        StopCommand => "Stop",
        CraftItemCommand => "Craft",
        TalkToCommand => "TalkTo",
        RomancePersonCommand c => c.Forced ? "ForceRomance" : "Romance",
        MergeCampsCommand => "MergeCamps",
        AidPersonCommand => "Aid",
        TreatLimbsCommand => "TreatLimbs",
        SelfActionCommand => "SelfAction",
        PreyPersonCommand => "Prey",
        AbusePersonCommand => "Abuse",
        GroupMoveCommand => "GroupMove",
        GroupStopCommand => "GroupStop",
        GroupAttackNpcCommand => "GroupAttackNpc",
        GroupAttackMobCommand => "GroupAttackMob",
        SetGroupManualControlCommand => "SetManual",
        ManageInventoryCommand => "Inventory",
        TransferInventoryCommand => "TransferInventory",
        TransferContainerCommand => "TransferContainer",
        PlaceBuildingPlanCommand => "PlaceBuildingPlan",
        PlaceFurnitureSiteCommand => "PlaceFurnitureSite",
        PlaceBuildingBlueprintCommand => "PlaceBuildingBlueprint",
        UpdateBuildingBlueprintCommand => "UpdateBuildingBlueprint",
        ApplyFreeArchitectureCommand => "ApplyFreeArchitecture",
        RotateBuildSiteCommand => "RotateBuildSite",
        CancelBuildSiteCommand => "CancelBuildSite",
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

        // §149.4: право спрашиваем у ЕДИНСТВЕННОГО гейта (§123), а не пишем
        // фракцию второй раз своими словами. Вторая формулировка и разошлась:
        // сервер выдавал девушку соседнего лагеря, а этот `!= Colony` отбивал
        // каждый её приказ «NotOwned» — тумблер не переключался, и выданный
        // персонаж выглядел неуправляемым.
        if (!PlayerAuthority.IsPlayerOwned(world, npc))
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
        // §121.7: принятая РУЧНАЯ команда продлевает lease внимания игрока.
        // Mode-independent inventory transfers §123/§128 also pass here, but
        // must neither switch AI mode nor create a dormant manual lease.
        if (npc.Mind.ManualControl)
        {
            ManualControlMath.RenewInactivityLease(world, npc);
        }
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
            // An idempotent remote SetManual(true) is the viewer heartbeat:
            // renew the simulation's longer inactivity lease together with the
            // server ownership lease. Otherwise the server says the viewer is
            // alive while §121.7 silently returns the selected actor to AI.
            if (command.Enabled)
            {
                ManualControlMath.RenewInactivityLease(world, npc);
            }

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

    private static void ApplySetOutfitLock(
        WorldState world, SetOutfitLockCommand command, AdmissionTracker admission)
    {
        if (!PlayerAuthority.CanMutateInventory(world, command.Npc, out var npc) ||
            npc.Health <= 0f)
        {
            Reject(world, command.Npc, "SetOutfitLock", "NotOwned", admission);
            return;
        }

        OutfitMaintenanceMath.SetLockedOutfit(world, npc, command.Enabled);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "OutfitLockChanged",
                $"Enabled={(command.Enabled ? 1 : 0)} " +
                $"Selected={npc.Mind.DesiredOutfit.Count}");
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

        // §121.5: точка прибытия — СВОБОДНЫЙ узел. Клик в клетку с трупом,
        // лежащей или чужой резервацией отправлял её в занятый узел: 81 тик
        // молчаливого ожидания и тихий провал. Занятое место при приёме
        // заменяется ближайшим свободным соседним (как в формации §123.6);
        // своя клетка — не занята.
        if (!destination.Equals(npc.CurrentJunction) &&
            !SpatialQueries.IsJunctionFree(world, destination))
        {
            JunctionId? fallback = null;
            var bestSq = float.MaxValue;
            foreach (var neighborId in SpatialQueries.GetPassableNeighbors(world, destination))
            {
                if (!SpatialQueries.IsJunctionFree(world, neighborId) ||
                    !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                {
                    continue;
                }

                var dx = neighbor.WorldPosition.X - command.WorldPosition.X;
                var dy = neighbor.WorldPosition.Y - command.WorldPosition.Y;
                var sq = dx * dx + dy * dy;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    fallback = neighborId;
                }
            }

            if (fallback is { } free)
            {
                destination = free;
                junction = world.Junctions.Items[free];
            }
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(
                world, start, destination,
                PlanningSystem.CanUseCriticalTraversal(npc)))
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

        // §118.4 r2 (#166): своих носят всегда, спят они или нет. Чужой на ногах
        // в руки не даётся — это уже не носилки, а захват.
        if (!ManualCarryTargets.CanCarry(world, carrier, person, dead))
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

    // §124.1: донести несомого до кровати и уложить. Занятость решается ДО
    // подхода (Occupied читабельнее, чем Unreachable), а сама укладка — ровно
    // тем же путём, что §105.17 у ИИ: план PutPersonInBed → RunRescue →
    // PutDownAtDestination → BedSleep.TryEnter.
    private static void ApplyPutPersonInBed(
        WorldState world, PutPersonInBedCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "PutPersonInBed", requireManual: true,
                admission, out var carrier))
        {
            return;
        }

        if (Incapacitated(world, carrier))
        {
            Reject(world, carrier.Id, "PutPersonInBed", "Incapacitated", admission);
            return;
        }

        if (!carrier.IsCarryingPerson || carrier.CarriedNpcId is not { } carriedId)
        {
            Reject(world, carrier.Id, "PutPersonInBed", "HandsEmpty", admission);
            return;
        }

        // Только ЖИВОГО: мёртвое тело в кровать не укладывается — BedSleep
        // сделал бы из трупа «спящего».
        if (!world.Entities.Npcs.TryGetValue(carriedId, out var patient) ||
            patient.Health <= 0f)
        {
            Reject(world, carrier.Id, "PutPersonInBed", "PersonNotAvailable", admission);
            return;
        }

        if (!world.Entities.Objects.TryGetValue(command.Bed, out var bed) ||
            !ContentIds.IsBed(bed.DefinitionId) ||
            !string.IsNullOrEmpty(bed.BuildProduct))
        {
            Reject(world, carrier.Id, "PutPersonInBed", "TargetGone", admission);
            return;
        }

        if (bed.IsOccupied && bed.CurrentUser != patient.Id)
        {
            Reject(world, carrier.Id, "PutPersonInBed", "Occupied", admission);
            return;
        }

        // Транзакция плана СОХРАНЯЕТ ношу — как MoveTo/Stop (§124).
        ClearForNewOrder(world, carrier, "Приказ уложить в кровать", keepCarriedPerson: true);
        ClearAttackOrder(world, carrier);

        if (!KenshiRescueMath.TryBeginManualBedPlacement(world, carrier, patient, bed))
        {
            Reject(world, carrier.Id, "PutPersonInBed", "Unreachable", admission);
            carrier.Mind.CurrentGoal = GoalType.None;
            return;
        }

        carrier.Mind.CurrentGoal = GoalType.Rescue;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, carrier.Id, "ManualOrderAccepted",
                $"Order=PutPersonInBed Bed={bed.DefinitionId}#{bed.Id.Value} " +
                $"Patient=NPC{patient.Id.Value}");
        }
    }

    // §121.9: подойти и поговорить. Цель занята, идёт или не в духе — приказ
    // всё равно принимается: отказ по прибытии сыграет штатный RunTalk
    // (видимый cue TalkRejected), ровно как у автономной инициаторки. На
    // приёме отклоняется только физически невозможное.
    private static void ApplyTalkTo(
        WorldState world, TalkToCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "TalkTo", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "TalkTo", "Incapacitated", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "TalkTo", "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var partner) ||
            partner.Health <= 0f)
        {
            Reject(world, npc.Id, "TalkTo", "TargetGone", admission);
            return;
        }

        // С лежащей без сознания или с несомой не разговаривают; спящую или
        // занятую RunTalk вежливо откажет по прибытии, поэтому они проходят.
        if (partner.IsUnconscious(world.Tick) ||
            partner.CarriedByNpcId is not null ||
            partner.CurrentJunction is not { } partnerJunction)
        {
            Reject(world, npc.Id, "TalkTo", "TargetUnavailable", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ поговорить", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);
        if (!PlanningSystem.TryInstallTalkPlan(
                world, npc, partner, partner.Id, partnerJunction, partner.Tile,
                out _))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            Reject(world, npc.Id, "TalkTo", "Unreachable", admission);
            return;
        }

        npc.Plan.Goal = GoalType.Socialize;
        npc.Mind.CurrentGoal = GoalType.Socialize;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=TalkTo Target=NPC{partner.Id.Value}");
        }
    }

    private static void ApplyRomancePerson(
        WorldState world, RomancePersonCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc,
                command.Forced ? "ForceRomance" : "Romance",
                requireManual: true, admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc) || command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Romance", "Incapacitated", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var partner) ||
            partner.CurrentJunction is not { } partnerJunction ||
            partner.Health <= 0f)
        {
            Reject(world, npc.Id, "Romance", "TargetUnavailable", admission);
            return;
        }

        if (command.Forced && !Spec121.ManualDarkOrdersEnabled)
        {
            Reject(world, npc.Id, "Romance", "FeatureDisabled", admission);
            return;
        }

        var allowed = command.Forced
            ? RomanceMath.CanForce(world, npc, partner)
            : RomanceMath.CanConsent(world, npc, partner);
        if (!allowed)
        {
            Reject(world, npc.Id, "Romance",
                command.Forced ? "CannotForce" : "NoMutualConsent", admission);
            return;
        }

        ClearForNewOrder(world, npc,
            command.Forced ? "Приказ принудить" : "Романтическое приглашение",
            keepCarriedPerson: true);
        ClearAttackOrder(world, npc);
        if (!PlanningSystem.TryInstallRomancePlan(world, npc, partner,
                partnerJunction, command.Forced, out _))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            Reject(world, npc.Id, "Romance", "Unreachable", admission);
            return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order={(command.Forced ? "ForceRomance" : "Romance")} " +
                $"Target=NPC{partner.Id.Value}");
        }
    }

    private static void ApplyMergeCamps(
        WorldState world, MergeCampsCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "MergeCamps", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc) || npc.Body.IsProne || npc.IsFighting)
        {
            Reject(world, npc.Id, "MergeCamps", "Incapacitated", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var target) ||
            target.Health <= 0f)
        {
            Reject(world, npc.Id, "MergeCamps", "TargetGone", admission);
            return;
        }

        if (target.IsUnconscious(world.Tick) || target.Body.IsProne ||
            target.IsFighting || target.CarriedByNpcId is not null)
        {
            Reject(world, npc.Id, "MergeCamps", "TargetUnavailable", admission);
            return;
        }

        if (!CampDiplomacyMath.CanMerge(world, npc, target, out var reason))
        {
            Reject(world, npc.Id, "MergeCamps", reason, admission);
            return;
        }

        if (HexSpatialMath.Distance(npc.Position, target.Position) > InteractionReach.Talk)
        {
            Reject(world, npc.Id, "MergeCamps", "TooFarToTalk", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Объединение лагерей", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);
        var choice = command.UseTargetCamp
            ? CampHomeChoice.SecondCamp
            : CampHomeChoice.FirstCamp;
        if (!CampDiplomacyMath.TryMerge(world, npc, target, choice, out reason))
        {
            Reject(world, npc.Id, "MergeCamps", reason, admission);
            return;
        }

        SocialCueSignals.Stamp(world, npc, "TalkSuccess", target.Id);
        SocialCueSignals.Stamp(world, target, "TalkSuccess", npc.Id);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=MergeCamps Target=NPC{target.Id.Value} " +
                $"Home={(command.UseTargetCamp ? "Target" : "Ours")}");
        }
    }

    // §121.9: помочь ЯВНЫМ видом помощи (§53). Вид выбирает игрок; припас
    // проверяется тем же предикатом, что у ИИ (AidSupply.Has). «Нужна ли ей
    // именно эта помощь» на приёме сознательно не проверяется — по прибытии
    // это честно решает штатный RunAid с живой переоценкой.
    private static void ApplyAidPerson(
        WorldState world, AidPersonCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "Aid", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "Aid", "Incapacitated", admission);
            return;
        }

        if (command.Kind == AidKind.None)
        {
            Reject(world, npc.Id, "Aid", "InvalidKind", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Aid", "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var partner) ||
            partner.Health <= 0f)
        {
            Reject(world, npc.Id, "Aid", "TargetGone", admission);
            return;
        }

        if (!CampDiplomacyMath.CanProvideCare(world, npc, partner))
        {
            Reject(world, npc.Id, "Aid", "NotAlly", admission);
            return;
        }

        // Без сознания помогать МОЖНО (стабилизация §53.8); нельзя — несомой
        // на чужих руках и той, у кого нет узла (геометрии подхода не к чему).
        if (partner.CarriedByNpcId is not null ||
            partner.CurrentJunction is not { } partnerJunction)
        {
            Reject(world, npc.Id, "Aid", "TargetUnavailable", admission);
            return;
        }

        if (!AidSupply.Has(world, npc, command.Kind))
        {
            Reject(world, npc.Id, "Aid", "NoSupplies", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ помочь", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);
        AidAssessment.Assess(partner, world.Tick, out var suffering);
        if (!PlanningSystem.TryInstallAidPlan(
                world, npc, partner, partner.Id, command.Kind, partner.Tile,
                suffering, partnerJunction, fromMemory: false, out _))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            Reject(world, npc.Id, "Aid", "Unreachable", admission);
            return;
        }

        npc.Plan.Goal = GoalType.Aid;
        npc.Mind.CurrentGoal = GoalType.Aid;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Aid Kind={command.Kind} Target=NPC{partner.Id.Value}");
        }
    }

    // §121.9: наложить шину или приладить протез. Что именно — решает та же
    // математика, что у реактивного ИИ (§116): сперва шина, затем протез.
    private static void ApplyTreatLimbs(
        WorldState world, TreatLimbsCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "TreatLimbs", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "TreatLimbs", "Incapacitated", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "TreatLimbs", "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var patient) ||
            patient.Health <= 0f)
        {
            Reject(world, npc.Id, "TreatLimbs", "TargetGone", admission);
            return;
        }

        if (!CampDiplomacyMath.CanProvideCare(world, npc, patient))
        {
            Reject(world, npc.Id, "TreatLimbs", "NotAlly", admission);
            return;
        }

        if (patient.IsBeingCarried)
        {
            Reject(world, npc.Id, "TreatLimbs", "PersonNotAvailable", admission);
            return;
        }

        // Тот же порядок выбора, что у TryAssignLimbCare: шина первой.
        var goal = GoalType.None;
        var hasSplintDamage = KenshiProstheticMath.TryFindSplintPart(patient, out _);
        if (Spec118.SplintsEnabled && hasSplintDamage &&
            KenshiProstheticMath.HasItem(npc, ContentIds.Splint))
        {
            goal = GoalType.Splint;
        }
        else if (Spec118.ProstheticsEnabled &&
                 KenshiProstheticMath.TryFindProstheticPart(
                     world, npc, patient, out _, out _, out _))
        {
            goal = GoalType.FitProsthetic;
        }

        if (goal == GoalType.None)
        {
            // Повреждение есть, а сделать нечего → нет припаса (или пациентка
            // лежит не в кровати для протеза); повреждения нет → нечего лечить.
            var reason = hasSplintDamage || KenshiProstheticMath.HasSeveredLimb(patient)
                ? "NoSupplies"
                : "NoLimbDamage";
            Reject(world, npc.Id, "TreatLimbs", reason, admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ заняться конечностями");
        ClearAttackOrder(world, npc);
        if (!RescueSystem.TryInstallLimbCarePlan(world, npc, patient, goal))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            // Провал внутри — либо нет подхода, либо станции лежачей заняты.
            var stationBusy = false;
            if (patient.IsLyingDown(world.Tick))
            {
                stationBusy = true;
                for (var slot = 0; slot < LyingStations.Count; slot++)
                {
                    if (LyingStations.IsFree(world, patient, slot, npc) &&
                        LyingStations.IsUsable(world, patient, slot))
                    {
                        stationBusy = false;
                        break;
                    }
                }
            }

            Reject(world, npc.Id, "TreatLimbs",
                stationBusy ? "StationBusy" : "Unreachable", admission);
            return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=TreatLimbs Goal={goal} Target=NPC{patient.Id.Value}");
        }
    }

    // §121.9: планы самодействий ставят ТЕ ЖЕ билдеры, что у автономной.
    // PlanningSystem бесстатусен (ни одного поля), поэтому исполнителю хватает
    // собственного экземпляра — второй симуляции это не заводит.
    private static readonly PlanningSystem ManualPlanner = new();

    // §121.9: самодействия. Крик о помощи не сносит текущий план (крик — не
    // действие тела в очереди, а голос); всё остальное — обычная транзакция
    // приказа: снять старое, поставить родную цель, построить штатный план.
    private static void ApplySelfAction(
        WorldState world, SelfActionCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "SelfAction", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "SelfAction", "Incapacitated", admission);
            return;
        }

        switch (command.Kind)
        {
            case SelfActionKind.CallForHelp:
                if (!CombatHelpSystem.TryCallForHelpManual(world, npc, out var cryReason))
                {
                    Reject(world, npc.Id, "SelfAction", cryReason, admission);
                    return;
                }

                // Крик не трогает план — только продлевает внимание игрока.
                ManualControlMath.RenewInactivityLease(world, npc);
                break;

            case SelfActionKind.TreatSelf:
            {
                var bandages = MedicalSupplyMath.BandageCount(npc) +
                    (MedicalSupplyMath.TryFindReachableBandageSource(world, npc, out _) ? 1 : 0);
                if (!DecisionSystem.SelfTreatmentIndicated(npc, bandages))
                {
                    Reject(world, npc.Id, "SelfAction",
                        bandages > 0 ? "NotNeeded" : "NoBandage", admission);
                    return;
                }

                ClearForNewOrder(world, npc, "Приказ перевязаться");
                ClearAttackOrder(world, npc);
                if (!PlanningSystem.TryBuildSelfTreatmentPlan(world, npc))
                {
                    npc.Mind.CurrentGoal = GoalType.None;
                    Reject(world, npc.Id, "SelfAction", "NoBandage", admission);
                    return;
                }

                npc.Plan.Goal = GoalType.TreatWounds;
                npc.Mind.CurrentGoal = GoalType.TreatWounds;
                break;
            }

            case SelfActionKind.GroundSit:
                // Сначала штатное «присесть на уступ» (§137-геометрия), а без
                // уступа рядом — честный §137 IdleRest: сесть там, где стоишь.
                // Замер e2e: на полу хижины GroundSit отказывал NoGroundSpot,
                // хотя «присесть» игрок понимает как «сядь здесь».
                InstallSelfPlan(world, npc, admission, GoalType.Sit,
                    "Приказ присесть", "NoGroundSpot",
                    () =>
                    {
                        ManualPlanner.BuildGroundSitPlan(world, npc);
                        if (npc.Plan.Status != PlanStatus.Active)
                        {
                            npc.Plan.Steps.Clear();
                            npc.Plan.Status = PlanStatus.None;
                            ManualPlanner.BuildIdleRestPlan(world, npc);
                        }
                    });
                break;

            case SelfActionKind.GroundSleep:
                var groundSleepReason = ExecutionSystem.GetSleepInterruptReason(
                    world, npc, manualOrder: true);
                if (groundSleepReason is not null)
                {
                    Reject(world, npc.Id, "SelfAction", groundSleepReason, admission);
                    ExecutionSystem.StampSleepRefusal(world, npc, groundSleepReason);
                    return;
                }
                InstallSelfPlan(world, npc, admission, GoalType.Sleep,
                    "Приказ лечь спать", "NoGroundSpot",
                    () => ManualPlanner.BuildLocalGroundSleepPlan(world, npc));
                break;

            case SelfActionKind.Bathe:
                InstallSelfPlan(world, npc, admission, GoalType.Bathe,
                    "Приказ искупаться", "NoSpot",
                    () => ManualPlanner.BuildBathePlan(world, npc));
                break;

            case SelfActionKind.WashClothes:
                InstallSelfPlan(world, npc, admission, GoalType.WashClothes,
                    "Приказ постирать", "NothingToWash",
                    () => ManualPlanner.BuildWashClothesPlan(world, npc));
                break;

            case SelfActionKind.EatFromPack:
                if (npc.Inventory.FindFirstFood(world.Content) is null &&
                    !DecisionSystem.HasInventoryCoconutMeal(npc))
                {
                    Reject(world, npc.Id, "SelfAction", "NothingToEat", admission);
                    return;
                }

                ClearForNewOrder(world, npc, "Приказ поесть", keepCarriedPerson: true);
                ClearAttackOrder(world, npc);
                // План построит штатный планировщик: Eat в списке
                // MayPlanGoal ручной (§121.6, inventory-only).
                npc.Mind.CurrentGoal = GoalType.Eat;
                break;

            case SelfActionKind.DrinkFromPack:
                if (!DecisionSystem.HasInventoryCoconutWater(npc) &&
                    npc.Inventory.FindFirstDrink(world.Content) is null)
                {
                    Reject(world, npc.Id, "SelfAction", "NothingToDrink", admission);
                    return;
                }

                ClearForNewOrder(world, npc, "Приказ попить", keepCarriedPerson: true);
                ClearAttackOrder(world, npc);
                npc.Mind.CurrentGoal = GoalType.Drink;
                break;

            case SelfActionKind.GoHome:
                InstallSelfPlan(world, npc, admission, GoalType.Homeward,
                    "Приказ бежать домой", "NoRouteToCamp",
                    () => ManualPlanner.BuildHomewardPlan(world, npc));
                break;

            default:
                admission.Reject("UnsupportedCommand");
                return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=SelfAction Kind={command.Kind}");
        }
    }

    // §121.9 (тёмная фаза): выследить соседку ради мяса (§56). Гейты выбора
    // (сострадание, голод, «другой еды нет») — решение, и его принял игрок;
    // остаются только физические: нож-разделочник и рабочие руки. Удары ведёт
    // штатная PredationSystem по смежной союзнице; погоню держит
    // ManualOrderSystem.KeepPreying, как у приказа атаки.
    private static void ApplyPreyPerson(
        WorldState world, PreyPersonCommand command, AdmissionTracker admission)
    {
        if (!Spec121.ManualDarkOrdersEnabled || !SimBalance.PredationEnabled)
        {
            admission.Reject("FeatureDisabled");
            return;
        }

        if (!TryTakeOrder(world, command.Npc, "Prey", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc) || !npc.Body.CanUseToolsOrWeapons)
        {
            Reject(world, npc.Id, "Prey", "Incapacitated", admission);
            return;
        }

        if (!Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.Butcher))
        {
            Reject(world, npc.Id, "Prey", "MissingTool", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Prey", "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var victim) ||
            victim.Health <= 0f)
        {
            Reject(world, npc.Id, "Prey", "TargetGone", admission);
            return;
        }

        // §56 — каннибализм СВОИХ; чужака бьют приказом атаки (§72 налёт).
        if (!FactionRelations.AreAllies(npc, victim))
        {
            Reject(world, npc.Id, "Prey", "NotAlly", admission);
            return;
        }

        if (victim.CarriedByNpcId is not null ||
            victim.CurrentJunction is not { } victimJunction)
        {
            Reject(world, npc.Id, "Prey", "TargetUnavailable", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ выследить соседку");
        ClearAttackOrder(world, npc);
        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, victimJunction, npc.Body.CanJump))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            Reject(world, npc.Id, "Prey", "Unreachable", admission);
            return;
        }

        // Move-only сталк, как строит планировщик для §56: без TargetAgentId —
        // иначе диспетчер исполнения примет план за разговор. Жертву погони
        // держит ManualAttackNpcId (тот же якорь, что у приказа атаки).
        npc.Plan.Goal = GoalType.Prey;
        npc.Plan.TargetJunctionId = victimJunction;
        npc.Plan.TargetTile = victim.Tile;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = victimJunction
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.Prey;
        npc.Mind.ManualAttackNpcId = command.Target;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Prey Target=NPC{victim.Id.Value}");
        }
    }

    // §121.9 (тёмная фаза): сцена травли §81 против чужака. Сцену ведёт
    // штатный RunAbuse (латч боя, свидетельницы и защитницы жертвы работают
    // как у автономной — латч-цикл TryStartAbuse не различает, кто начал).
    private static void ApplyAbusePerson(
        WorldState world, AbusePersonCommand command, AdmissionTracker admission)
    {
        if (!Spec121.ManualDarkOrdersEnabled || !Spec81.AbuseEnabled)
        {
            admission.Reject("FeatureDisabled");
            return;
        }

        if (!TryTakeOrder(world, command.Npc, "Abuse", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc) || npc.Body.IsProne ||
            !npc.Body.CanUseToolsOrWeapons)
        {
            Reject(world, npc.Id, "Abuse", "Incapacitated", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Abuse", "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var mark) ||
            mark.Health <= 0f)
        {
            Reject(world, npc.Id, "Abuse", "TargetGone", admission);
            return;
        }

        // §81: сцена строится на враждебности (Ratio/AnswersBack) — своих не
        // травят приказом; беспомощного обирают §111 (обыском), спящую сцена
        // не разыгрывает, из святилища и воды жертву не достать.
        if (!FactionRelations.AreHostile(world, npc, mark))
        {
            Reject(world, npc.Id, "Abuse", "NotHostile", admission);
            return;
        }

        if (mark.IsUnconscious(world.Tick) || mark.Body.IsProne ||
            mark.IsPlayingDead(world.Tick) ||
            mark.Execution.CurrentInteraction == InteractionType.Sleep ||
            mark.CarriedByNpcId is not null ||
            (Spec81.AbuseRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, mark)) ||
            (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, mark)) ||
            mark.CurrentJunction is not { } markJunction)
        {
            Reject(world, npc.Id, "Abuse", "TargetUnavailable", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ затеять сцену");
        ClearAttackOrder(world, npc);
        npc.Mind.CurrentGoal = GoalType.Abuse;
        npc.Mind.AbuseTargetNpcId = mark.Id;
        npc.Mind.AbuseHasLoot =
            AbuseMath.WhatToTake(world, npc, mark) != AidKind.None;
        npc.Mind.AbuseBeat = 0;
        npc.Mind.AbuseBlows = 0;
        if (!PlanningSystem.TryInstallAbusePlan(world, npc, mark, markJunction))
        {
            PlanningSystem.AbandonAbuse(world, npc, "ManualNoApproach", 0);
            npc.Mind.CurrentGoal = GoalType.None;
            Reject(world, npc.Id, "Abuse", "Unreachable", admission);
            return;
        }

        npc.Plan.Goal = GoalType.Abuse;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Abuse Mark=NPC{mark.Id.Value} Loot={npc.Mind.AbuseHasLoot}");
        }
    }

    // Общая транзакция самодействия с штатным билдером: билдер сам решает
    // «есть ли где» (Plan.Status == Active — принято; иначе честный отказ).
    private static void InstallSelfPlan(
        WorldState world, NPCState npc, AdmissionTracker admission, GoalType goal,
        string clearReason, string failReason, System.Action build)
    {
        ClearForNewOrder(world, npc, clearReason);
        ClearAttackOrder(world, npc);
        npc.Mind.CurrentGoal = goal;
        npc.Plan.Goal = goal;
        build();
        if (npc.Plan.Status != PlanStatus.Active)
        {
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Plan.Status = PlanStatus.None;
            Reject(world, npc.Id, "SelfAction", failReason, admission);
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

        if (!PlayerInventoryCommandExecutor.TryApply(
                world, npc, command.Item, command.Action, out var reason))
        {
            Reject(world, npc.Id, "Inventory", reason, admission);
            return;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "PlayerInventoryCompleted",
                $"Order=Inventory Action={command.Action} Source={command.Item.Source} " +
                $"Index={command.Item.Index} Def={command.Item.ExpectedDefinitionId} " +
                $"GoalPreserved={npc.Mind.CurrentGoal}");
        }
    }

    private static void ApplyTransferInventory(
        WorldState world, TransferInventoryCommand command, AdmissionTracker admission)
    {
        if (!PlayerAuthority.CanMutateInventory(world, command.Looter, out var looter) ||
            looter.Health <= 0f)
        {
            Reject(world, command.Looter, "TransferInventory", "NotOwned", admission);
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

        // §128 r2 (#164): кого можно обыскать — один предикат на приём приказа и
        // на его исполнение (мёртвая, спящая, без сознания; несомый — только
        // своими же руками).
        if (!PlayerLootTargets.TryResolve(
                world, looter, command.Other, out var other, out var carriedBySelf))
        {
            Reject(world, looter.Id, "TransferInventory", "PersonNotAvailable", admission);
            return;
        }

        // §146.12: an explicit player order uses the same moral boundary as
        // autonomous looting. Own camp/corpses keep §128 semantics; a living
        // neutral neighbour needs hate or desperate hunger.
        if (command.Direction == InventoryTransferDirection.Take &&
            world.Entities.Npcs.ContainsKey(other.Id) &&
            looter.Faction != other.Faction &&
            !CampDiplomacyMath.CanLoot(world, looter, other))
        {
            Reject(world, looter.Id, "TransferInventory", "NoLootMotive", admission);
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

        // §128: транзакция плана не роняет того, в чьих карманах роемся.
        ClearForNewOrder(world, looter, "Ручной обмен с лежащим человеком",
            keepCarriedPerson: carriedBySelf);
        ClearAttackOrder(world, looter);

        looter.Plan.Goal = GoalType.PlayerInventory;
        looter.Plan.TargetAgentId = other.Id;
        looter.Plan.TargetItemDefinitionId = command.Item.ExpectedDefinitionId;
        looter.Plan.TargetTile = other.Tile;

        // §128: тело в руках — участники уже ближе некуда. Ни станции у ног,
        // ни подхода: сразу шаг передачи (у несомого нет ни CurrentJunction,
        // ни валидной геометрии лежания — станцию считать не по чему).
        var closeEnough = carriedBySelf;
        if (!carriedBySelf)
        {
            // §111.13: приказ игрока идёт мимо планировщика, поэтому станцию он
            // занимает прямо здесь — иначе ручной обмен остался бы единственным
            // путём, который по-прежнему делит точку у ног с чужой сценой.
            var orderSlot = LyingStations.TryClaim(world, looter, other, out var claimed)
                ? claimed
                : LyingStations.SlotFor(world, looter, other);
            closeEnough = InteractionReach.CheckPersonStart(
                world, looter, other, LyingStations.Point(other, orderSlot),
                LyingStations.Reach(orderSlot),
                $"Player inventory transfer with NPC{other.Id.Value}");
        }

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

    /// <summary>
    /// §128.5: обмен с ВЕЩЬЮ — истлевшим телом, снятым рюкзаком, аптечкой.
    /// Зеркало ApplyTransferInventory, только вторая сторона — объект мира,
    /// поэтому и подход считается по объекту (обод вокруг его футпринта), а не
    /// по станции у ног лежащего человека.
    /// </summary>
    private static void ApplyTransferContainer(
        WorldState world, TransferContainerCommand command, AdmissionTracker admission)
    {
        if (!PlayerAuthority.CanMutateInventory(world, command.Looter, out var looter) ||
            looter.Health <= 0f)
        {
            Reject(world, command.Looter, "TransferContainer", "NotOwned", admission);
            return;
        }

        if (Incapacitated(world, looter))
        {
            Reject(world, looter.Id, "TransferContainer", "Incapacitated", admission);
            return;
        }

        if (command.Direction is not InventoryTransferDirection.Take and
            not InventoryTransferDirection.Give)
        {
            Reject(world, looter.Id, "TransferContainer", "InvalidDirection", admission);
            return;
        }

        // Правило 2: объект берётся ИЗ МИРА, а не из восприятия — игрок видит
        // остров целиком, и приказ на замеченный им мешок обязан работать.
        if (!world.Entities.Objects.TryGetValue(command.Container, out var container) ||
            !ContainerLootMath.IsLootable(world, container))
        {
            Reject(world, looter.Id, "TransferContainer", "ContainerNotAvailable", admission);
            return;
        }

        ClearForNewOrder(world, looter, "Ручной обмен с вещью");
        ClearAttackOrder(world, looter);

        looter.Plan.Goal = GoalType.PlayerInventory;
        looter.Plan.TargetObjectId = container.Id;
        looter.Plan.TargetItemDefinitionId = command.ExpectedDefinitionId;
        looter.Plan.TargetTile = container.Tile;

        var definition = world.Content.ObjectDefinitions.TryGetValue(
            container.DefinitionId, out var found) ? found : null;
        var closeEnough = definition is not null && InteractionReach.CheckObjectStart(
            world, looter, container, definition.ObstacleRadius);
        if (!closeEnough)
        {
            // Обод вокруг футпринта — тот же, которым подходят к любому объекту
            // (§26.6A). Мешок может лежать под пальмой или у стены, поэтому
            // «первый свободный сосед» тут не годится.
            _containerRimScratch.Clear();
            SpatialQueries.CollectStandableAround(
                world, container.Junctions.Count > 0
                    ? container.Junctions[0]
                    : looter.CurrentJunction ?? default,
                _containerRimScratch, 96,
                SpatialQueries.BesideReach(definition?.ObstacleRadius ?? 0f),
                container, SpatialQueries.RimPurpose.Reach);

            JunctionId? approach = null;
            foreach (var rim in _containerRimScratch)
            {
                if ((looter.CurrentJunction is not { } from ||
                     Connectivity.Reachable(world, from, rim, looter.Body.CanJump)) &&
                    SpatialQueries.IsJunctionFree(world, rim) &&
                    SpatialMutations.TryReserveJunction(world, rim, looter.Id, world.Tick, 96))
                {
                    approach = rim;
                    break;
                }
            }

            if (approach is not { } approachJunction)
            {
                looter.Mind.CurrentGoal = GoalType.None;
                looter.Plan.Goal = GoalType.None;
                looter.Plan.Status = PlanStatus.Failed;
                looter.Plan.CurrentStepIndex = 0;
                looter.Plan.Steps.Clear();
                looter.Plan.TargetObjectId = null;
                looter.Plan.TargetItemDefinitionId = null;
                looter.Plan.TargetJunctionId = null;
                looter.Plan.TargetTile = null;
                Reject(world, looter.Id, "TransferContainer", "Unreachable", admission);
                return;
            }

            looter.Plan.TargetJunctionId = approachJunction;
            looter.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = approachJunction
            });
        }

        looter.Plan.Steps.Add(new PlanStep
        {
            Type = command.Direction == InventoryTransferDirection.Take
                ? PlanStepType.PlayerTakeFromContainer
                : PlanStepType.PlayerGiveToContainer,
            TargetObject = container.Id,
            TimeoutEndTick = PlayerInventoryTransferMath.PackCursor(
                command.SlotIndex, command.Count)
        });
        looter.Plan.CurrentStepIndex = 0;
        looter.Plan.Status = PlanStatus.Active;
        looter.Mind.CurrentGoal = GoalType.PlayerInventory;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, looter.Id, "ManualOrderAccepted",
                $"Order=TransferContainer Direction={command.Direction} " +
                $"Container={container.Id.Value} Def={container.DefinitionId} " +
                $"Slot={command.SlotIndex} Count={command.Count} " +
                $"Item={command.ExpectedDefinitionId}");
        }
    }

    private static readonly System.Collections.Generic.List<JunctionId> _containerRimScratch = new();

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

        if (command.Interaction == InteractionType.Sleep)
        {
            var sleepReason = ExecutionSystem.GetSleepInterruptReason(
                world, npc, manualOrder: true);
            if (sleepReason is not null)
            {
                Reject(world, npc.Id, "Interact", sleepReason, admission);
                ExecutionSystem.StampSleepRefusal(world, npc, sleepReason);
                return;
            }
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

    private static void ApplyCraft(
        WorldState world, CraftItemCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "Craft", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "Craft", "Incapacitated", admission);
            return;
        }

        var option = CraftingOptions.Resolve(world, npc, command.RecipeGoal);
        if (!option.CanCraft)
        {
            Reject(world, npc.Id, "Craft", option.BlockReason.ToString(), admission);
            return;
        }

        ClearForNewOrder(world, npc, "Ручной заказ крафта");
        ClearAttackOrder(world, npc);

        npc.Plan.Goal = command.RecipeGoal;
        npc.Plan.TargetTile = option.WorkTile;
        npc.Plan.TargetJunctionId = option.WorkJunction;
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetItemDefinitionId = option.OutputDefinitionId;

        if (string.IsNullOrEmpty(option.StationTag))
        {
            // Bug #197: an in-place recipe may be payable only around a
            // reachable ground pile. The option deliberately reports that
            // pile's junction; make it an actual leg of the manual plan
            // instead of leaving RunCraftInPlace waiting there forever.
            if (option.WorkJunction is { } workJunction &&
                (npc.CurrentJunction is not { } currentJunction ||
                 !currentJunction.Equals(workJunction)))
            {
                npc.Plan.Steps.Add(new PlanStep
                {
                    Type = PlanStepType.MoveToJunction,
                    TargetJunction = workJunction
                });
            }
            npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.CraftInPlace });
        }
        else
        {
            if (option.StationObjectId is not { } stationId ||
                !world.Entities.Objects.TryGetValue(stationId, out var station) ||
                station.Junctions.Count == 0)
            {
                Reject(world, npc.Id, "Craft", "NoStation", admission);
                ResetRejectedPlan(npc);
                return;
            }

            JunctionId target;
            if (station.CraftJunction is { } authoredWorkPoint)
            {
                target = authoredWorkPoint;
                if (!SpatialMutations.TryReserveJunction(
                        world, target, npc.Id, world.Tick, Spec121.ManualReserveTicks))
                {
                    Reject(world, npc.Id, "Craft", "StationBusy", admission);
                    ResetRejectedPlan(npc);
                    return;
                }
            }
            else
            {
                var anchor = station.Junctions[0];
                var besideReach = world.Content.ObjectDefinitions.TryGetValue(
                        station.DefinitionId, out var stationDefinition)
                    ? SpatialQueries.BesideReach(stationDefinition.ObstacleRadius)
                    : float.MaxValue;
                if (!PlanningSystem.TryReserveBesideJunction(
                        world, npc, anchor, Spec121.ManualReserveTicks,
                        out target, besideReach, station))
                {
                    Reject(world, npc.Id, "Craft", "StationBusy", admission);
                    ResetRejectedPlan(npc);
                    return;
                }
            }

            if (npc.CurrentJunction is not { } start ||
                !Connectivity.Reachable(world, start, target, npc.Body.CanJump))
            {
                SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
                Reject(world, npc.Id, "Craft", "Unreachable", admission);
                ResetRejectedPlan(npc);
                return;
            }

            npc.Plan.TargetObjectId = station.Id;
            npc.Plan.TargetJunctionId = target;
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.MoveToJunction,
                TargetJunction = target,
                TargetObject = station.Id
            });
            npc.Plan.Steps.Add(new PlanStep
            {
                Type = PlanStepType.Interact,
                TargetObject = station.Id,
                TargetJunction = target,
                Interaction = InteractionType.Craft
            });
        }

        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = command.RecipeGoal;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Craft Goal={command.RecipeGoal} Output={option.OutputDefinitionId} " +
                $"Station={option.StationObjectId?.Value.ToString() ?? "ground"} " +
                $"Tile={option.WorkTile.Q},{option.WorkTile.R} Resume={(option.IsResume ? 1 : 0)}");
        }
    }

    private static void ResetRejectedPlan(NPCState npc)
    {
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.Failed;
        npc.Plan.Steps.Clear();
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetItemDefinitionId = null;
        npc.Mind.CurrentGoal = GoalType.None;
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

    // ─────────────────────────────────────────────────────────────────────────
    // §120.7: мировые команды стройки. Актора-NPC у них нет: игрок размечает
    // площадку, а строят её девушки штатным циклом §120 (доставка → модули →
    // Complete). Правда о пригодности места живёт ЗДЕСЬ, не в ghost'е UI.
    // ─────────────────────────────────────────────────────────────────────────

    private static void ApplyPlaceBuildingPlan(
        WorldState world, PlaceBuildingPlanCommand command, AdmissionTracker admission)
    {
        // CreateHutPlanSite сам проверяет каждый гекс футпринта (CanPlaceHut) и
        // возвращает null на негодном месте — та же валидация, что у bootstrap.
        var site = Bootstrap.BuildingBootstrap.CreateHutPlanSite(
            world, command.Tile,
            command.RotationDegrees + BuildingRules.DoorLocalOutwardYaw(ContentIds.HutPlan));
        if (site == null)
        {
            admission.Reject("PlacementBlocked");
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order=PlaceBuildingPlan Reason=PlacementBlocked Tile={command.Tile}");
            return;
        }

        Bootstrap.BuildingBootstrap.RememberPlanSite(world, site);
        Trace.EmitSystem(world, "BuildSitePlanned",
            $"Product={ContentIds.HutPlan} Tile={command.Tile} Site={site.Id.Value}");
    }

    private static void ApplyPlaceFurnitureSite(
        WorldState world, PlaceFurnitureSiteCommand command, AdmissionTracker admission)
    {
        void RejectWorld(string reason)
        {
            admission.Reject(reason);
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order=PlaceFurnitureSite Reason={reason} Tile={command.Tile}");
        }

        // Только строка каталога — произвольный id объекта провод не провезёт
        // до площадки: чужой клиент не может заказать «постройку» волка.
        if (!BuildCatalogDefinition.TryGet(command.CatalogId, out var entry) ||
            entry.PlacementKind != BuildCatalogPlacementKind.Furniture)
        {
            RejectWorld("UnknownProduct");
            return;
        }

        var product = Bootstrap.BuildingBootstrap.PlanFurnitureProduct(command.CatalogId);
        if (product == null || !world.Content.ObjectDefinitions.ContainsKey(product))
        {
            RejectWorld("UnknownProduct");
            return;
        }

        if (!world.Tiles.Items.TryGetValue(command.Tile, out var tile) ||
            !tile.Flags.HasFlag(TileFlags.Walkable) ||
            tile.Flags.HasFlag(TileFlags.Water) ||
            tile.Flags.HasFlag(TileFlags.Blocked))
        {
            RejectWorld("PlacementBlocked");
            return;
        }

        // §66: одна постройка на гекс, всегда в его центре.
        if (!StructurePlacement.HexFreeForBuild(world, command.Tile))
        {
            RejectWorld("HexOccupied");
            return;
        }

        if (StructurePlacement.CenterJunction(world, command.Tile) is not { } anchor)
        {
            RejectWorld("PlacementBlocked");
            return;
        }

        var householdHearth = command.CatalogId == "furniture.hearth";
        var site = Core.WorldObjectMutations.SpawnObject(
            world, ContentIds.BuildSite,
            world.Junctions.Items[anchor].Fragment, command.Tile, anchor);
        site.BuildProduct = product;
        if (householdHearth) site.Variant = BuildingRules.HutHearthVariant;
        site.RotationDegrees = StructurePlacement.QuantizeHexYaw(command.RotationDegrees);
        // §54.9A: площадка сразу владеет футпринтом будущего изделия — поверх
        // неё нельзя разметить вторую стройку и через раму не ходят.
        Core.WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);
        Bootstrap.BuildingBootstrap.ApplyFurnitureBill(site, product, householdHearth);
        Bootstrap.BuildingBootstrap.RememberPlanSite(world, site);
        Trace.EmitSystem(world, "BuildSitePlanned",
            $"Product={product} Tile={command.Tile} Site={site.Id.Value}");
    }

    /// <summary>Пустая ли площадка: ни предмета в складе, ни материала или
    /// работы в модулях. Повернуть/снять можно только такую — начатая стройка
    /// не телепортируется и не испаряет доставленное.</summary>
    private static bool BuildSiteEmpty(WorldState world, WorldObjectState site)
    {
        if (site.Contents.Count > 0) return false;
        foreach (var element in BuildingRules.Elements(world, site))
        {
            if (element.DeliveredTotal > 0 || element.WorkDone > 0) return false;
        }

        return true;
    }

    private static bool TryTakeBuildSite(
        WorldState world, ObjectId id, string verb, AdmissionTracker admission,
        out WorldObjectState site)
    {
        void RejectWorld(string reason)
        {
            admission.Reject(reason);
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order={verb} Reason={reason} Site={id.Value}");
        }

        if (!world.Entities.Objects.TryGetValue(id, out site) ||
            site.DefinitionId != ContentIds.BuildSite ||
            string.IsNullOrEmpty(site.BuildProduct))
        {
            RejectWorld("NoSuchSite");
            return false;
        }

        if (!BuildSiteEmpty(world, site))
        {
            RejectWorld("SiteNotEmpty");
            return false;
        }

        return true;
    }

    // Потолок сырого JSON чертежа. Committed-план ~10 КБ; большой ручной дом —
    // десятки килобайт. Всё сверх — мусор или атака на память, не чертёж.
    private const int MaxBlueprintJsonChars = 128 * 1024;

    private static void ApplyPlaceBuildingBlueprint(
        WorldState world, PlaceBuildingBlueprintCommand command, AdmissionTracker admission)
    {
        void RejectWorld(string reason)
        {
            admission.Reject(reason);
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order=PlaceBuildingBlueprint Reason={reason} Tile={command.Tile}");
        }

        if (string.IsNullOrEmpty(command.BlueprintJson) ||
            command.BlueprintJson.Length > MaxBlueprintJsonChars)
        {
            RejectWorld("InvalidBlueprint");
            return;
        }

        // TryDeserialize уже гоняет полный BlueprintValidator; невалидный
        // чертёж не доходит даже до реестра.
        if (!Blueprints.BuildingBlueprintJson.TryDeserialize(
                command.BlueprintJson, out var draft, out _))
        {
            RejectWorld("InvalidBlueprint");
            return;
        }

        // Минимум содержательности: без пола нет якоря и футпринта, без двери
        // дом не имеет портала (DoorLocalOutwardYaw кидает, §120.2).
        var hasFloor = false;
        var hasDoor = false;
        foreach (var element in draft.Elements)
        {
            hasFloor |= element.Kind == Blueprints.BlueprintElementKind.FloorSector;
            hasDoor |= element.Kind == Blueprints.BlueprintElementKind.Door;
        }

        if (!hasFloor || !hasDoor)
        {
            RejectWorld("InvalidBlueprint");
            return;
        }

        if (!draft.HasAnchor)
        {
            var anchor = Blueprints.BlueprintBuildingPlan.AnchorTile(draft);
            draft.HasAnchor = true;
            draft.AnchorQ = anchor.Q;
            draft.AnchorR = anchor.R;
            draft.Normalize();
        }

        var blueprintId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[blueprintId] = draft;
        var site = Bootstrap.BuildingBootstrap.CreatePlayerBlueprintSite(
            world, command.Tile, command.RotationDegrees, blueprintId);
        if (site == null)
        {
            // Негодное место — чертёж не остаётся сиротой в реестре.
            world.PlayerBlueprints.Remove(blueprintId);
            RejectWorld("PlacementBlocked");
            return;
        }

        Bootstrap.BuildingBootstrap.RememberPlanSite(world, site);
        Trace.EmitSystem(world, "BuildSitePlanned",
            $"Product={ContentIds.HutPlan} Blueprint={blueprintId} " +
            $"Tile={command.Tile} Site={site.Id.Value}");
    }

    private static void ApplyUpdateBuildingBlueprint(
        WorldState world, UpdateBuildingBlueprintCommand command, AdmissionTracker admission)
    {
        void RejectWorld(string reason)
        {
            admission.Reject(reason);
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order=UpdateBuildingBlueprint Reason={reason} Owner={command.Owner.Value}");
        }

        if (string.IsNullOrEmpty(command.BlueprintJson) ||
            command.BlueprintJson.Length > MaxBlueprintJsonChars ||
            !Blueprints.BuildingBlueprintJson.TryDeserialize(
                command.BlueprintJson, out var draft, out _))
        {
            RejectWorld("InvalidBlueprint");
            return;
        }

        var hasFloor = draft.Elements.Any(element =>
            element.Kind == Blueprints.BlueprintElementKind.FloorSector);
        var hasDoor = draft.Elements.Any(element =>
            element.Kind == Blueprints.BlueprintElementKind.Door);
        if (!hasFloor || !hasDoor ||
            !world.Entities.Objects.TryGetValue(command.Owner, out var owner))
        {
            RejectWorld(!hasFloor || !hasDoor ? "InvalidBlueprint" : "NotEditableBuilding");
            return;
        }

        if (!Bootstrap.BuildingBootstrap.ApplyBlueprintRevision(
                world, owner, draft, out var error))
        {
            RejectWorld(error);
            return;
        }

        Trace.EmitSystem(world, "BuildingBlueprintUpdated",
            $"Owner={owner.Id.Value} Blueprint={owner.BlueprintId} " +
            $"Modules={draft.Elements.Count}");
    }

    private static void ApplyFreeArchitecture(
        WorldState world, ApplyFreeArchitectureCommand command, AdmissionTracker admission)
    {
        if (!Blueprints.FreeArchitectureRules.Apply(
                world, command.Placements, command.RemovedSlotKeys, out var error))
        {
            admission.Reject(error);
            Trace.EmitSystem(world, "ManualOrderRejected",
                $"Order=ApplyFreeArchitecture Reason={error} " +
                $"Place={command.Placements.Count} Remove={command.RemovedSlotKeys.Count}");
            return;
        }

        Trace.EmitSystem(world, "FreeArchitectureChanged",
            $"Place={command.Placements.Count} Remove={command.RemovedSlotKeys.Count}");
    }

    private static void ApplyRotateBuildSite(
        WorldState world, RotateBuildSiteCommand command, AdmissionTracker admission)
    {
        if (!TryTakeBuildSite(world, command.Site, "RotateBuildSite", admission, out var site))
        {
            return;
        }

        if (site.BuildProduct == ContentIds.HutPlan)
        {
            var plan = BuildingRules.PlanFor(world, site);
            var rotation = StructurePlacement.QuantizeHexSymmetryYaw(command.RotationDegrees);
            var oldFootprint = Bootstrap.BuildingBootstrap.FootprintTiles(
                plan, site.Tile, site.RotationDegrees);
            foreach (var tile in Bootstrap.BuildingBootstrap.FootprintTiles(
                         plan, site.Tile, rotation))
            {
                // Свои прежние гексы не проверяются заново: на них стоит сама
                // площадка, и CanPlaceHut честно счёл бы её чужой постройкой.
                if (oldFootprint.Contains(tile)) continue;
                if (Bootstrap.BuildingBootstrap.CanPlaceHut(world, tile)) continue;
                admission.Reject("PlacementBlocked");
                Trace.EmitSystem(world, "ManualOrderRejected",
                    $"Order=RotateBuildSite Reason=PlacementBlocked Site={site.Id.Value}");
                return;
            }

            site.RotationDegrees = rotation;
            foreach (var piece in BuildingRules.ArchitectureObjects(world, site))
                piece.RotationDegrees = rotation;
            Bootstrap.BuildingBootstrap.RepairPlanTopology(world, site);
        }
        else
        {
            // Футпринт мебели повёрнут вместе с рамой: старые узлы отпустить,
            // новые занять — тем же симметричным SetObstacleBlocking.
            Core.WorldObjectMutations.SetObstacleBlocking(world, site, blocked: false);
            site.RotationDegrees = StructurePlacement.QuantizeHexYaw(command.RotationDegrees);
            Core.WorldObjectMutations.SetObstacleBlocking(world, site, blocked: true);
        }

        Trace.EmitSystem(world, "BuildSiteAdjusted",
            $"Site={site.Id.Value} Product={site.BuildProduct} Rotation={site.RotationDegrees:0}");
    }

    private static void ApplyCancelBuildSite(
        WorldState world, CancelBuildSiteCommand command, AdmissionTracker admission)
    {
        if (!TryTakeBuildSite(world, command.Site, "CancelBuildSite", admission, out var site))
        {
            return;
        }

        if (site.BuildProduct == ContentIds.HutPlan)
        {
            // Сначала исчезают модули, затем Repair отпускает всё, чем план
            // владел (пройдясь по уже пустому списку), и только потом сама
            // площадка — порядок, при котором не остаётся ничьих Blocked.
            foreach (var piece in BuildingRules.ArchitectureObjects(world, site).ToArray())
                Core.WorldObjectMutations.DespawnObject(world, piece.Id);
            Bootstrap.BuildingBootstrap.RepairPlanTopology(world, site);
            // §120.8: чертёж живёт, пока жива его площадка.
            if (site.BlueprintId != 0) world.PlayerBlueprints.Remove(site.BlueprintId);
        }

        Core.WorldObjectMutations.DespawnObject(world, site.Id);
        // Девушки знали площадку как постоянную (§72) — забыть, иначе походы
        // к призраку. Память по id, поэтому одним проходом.
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Memory.KnownObjects.Remove(site.Id))
            {
                npc.Memory.Version++;
            }
        }

        Trace.EmitSystem(world, "BuildSiteCancelled",
            $"Site={site.Id.Value} Product={site.BuildProduct}");
    }
}

}
