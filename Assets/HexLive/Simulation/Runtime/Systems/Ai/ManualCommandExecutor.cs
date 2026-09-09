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
            case RecordAgentSocialCommand agentSocial:
                ApplyRecordAgentSocial(world, agentSocial, admission);
                break;
            case RecordCompanionTurnCommand companionTurn:
                ApplyRecordCompanionTurn(world, companionTurn, admission);
                break;
            case SetManualControlCommand setManual:
                ApplySetManual(world, setManual, admission);
                break;
            case SetRunByDefaultCommand setRunByDefault:
                ApplySetRunByDefault(world, setRunByDefault, admission);
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
            case GatherAllOnHexCommand gatherAll:
                ApplyGatherAllOnHex(world, gatherAll, admission);
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
            case RequestItemCommand requestItem:
                ApplyRequestItem(world, requestItem, admission);
                break;
            case RomancePersonCommand romance:
                ApplyRomancePerson(world, romance, admission);
                break;
            case MergeCampsCommand mergeCamps:
                ApplyMergeCamps(world, mergeCamps, admission);
                break;
            case SetCampHomeCommand setCampHome:
                ApplySetCampHome(world, setCampHome, admission);
                break;
            case AidPersonCommand aidPerson:
                ApplyAidPerson(world, aidPerson, admission);
                break;
            case TreatLimbsCommand treatLimbs:
                ApplyTreatLimbs(world, treatLimbs, admission);
                break;
            case MedicalAidCommand medicalAid:
                ApplyMedicalAid(world, medicalAid, admission);
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
            case FillVesselCommand fillVessel:
                ApplyFillVessel(world, fillVessel, admission);
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
        RecordAgentSocialCommand => "RecordAgentSocial",
        RecordCompanionTurnCommand => "RecordCompanionTurn",
        SetManualControlCommand => "SetManual",
        SetRunByDefaultCommand => "SetRunByDefault",
        SetOutfitLockCommand => "SetOutfitLock",
        MoveToCommand => "MoveTo",
        InteractCommand => "Interact",
        GatherAllOnHexCommand => "GatherAll",
        AttackNpcCommand => "AttackNpc",
        CarryPersonCommand => "CarryPerson",
        PutDownPersonCommand => "PutDownPerson",
        PutPersonInBedCommand => "PutPersonInBed",
        AttackMobCommand => "AttackMob",
        StopCommand => "Stop",
        CraftItemCommand => "Craft",
        TalkToCommand => "TalkTo",
        RequestItemCommand => "RequestItem",
        RomancePersonCommand c => c.Forced ? "ForceRomance" : "Romance",
        MergeCampsCommand => "MergeCamps",
        SetCampHomeCommand => "SetCampHome",
        AidPersonCommand => "Aid",
        TreatLimbsCommand => "TreatLimbs",
        MedicalAidCommand => "MedicalAid",
        SelfActionCommand => "SelfAction",
        PreyPersonCommand => "Prey",
        AbusePersonCommand => "Abuse",
        GroupMoveCommand => "GroupMove",
        GroupStopCommand => "GroupStop",
        GroupAttackNpcCommand => "GroupAttackNpc",
        GroupAttackMobCommand => "GroupAttackMob",
        SetGroupManualControlCommand => "SetManual",
        ManageInventoryCommand => "Inventory",
        FillVesselCommand => "FillVessel",
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

    private static void ApplyRecordAgentSocial(
        WorldState world,
        RecordAgentSocialCommand command,
        AdmissionTracker admission)
    {
        if (!world.Entities.Npcs.TryGetValue(command.Npc, out var npc))
        {
            admission.Reject("NpcMissing");
            return;
        }
        if (string.IsNullOrWhiteSpace(command.TurnId) || command.TurnId.Length > 80 ||
            !System.Enum.IsDefined(typeof(Agents.CompanionReaction), command.Reaction))
        {
            admission.Reject("InvalidAgentTurn");
            return;
        }

        if (npc.AppliedAgentTurnIds.Contains(command.TurnId)) return;
        var delta = command.Reaction switch
        {
            Agents.CompanionReaction.Warm => 0.18f,
            Agents.CompanionReaction.Neutral => 0.08f,
            Agents.CompanionReaction.Tense => -0.06f,
            Agents.CompanionReaction.Hostile => -0.12f,
            _ => 0f,
        };
        npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social + delta);
        npc.AppliedAgentTurnIds.Add(command.TurnId);
        while (npc.AppliedAgentTurnIds.Count > Agents.CompanionState.MaxAppliedTurnIds)
            npc.AppliedAgentTurnIds.RemoveAt(0);
    }

    private static void ApplyRecordCompanionTurn(
        WorldState world,
        RecordCompanionTurnCommand command,
        AdmissionTracker admission)
    {
        if (!world.Entities.Npcs.TryGetValue(command.Npc, out var npc))
        {
            admission.Reject("NpcMissing");
            return;
        }

        if (!string.Equals(npc.ProfileId, "masha", System.StringComparison.Ordinal))
        {
            admission.Reject("NotCompanion");
            return;
        }

        if (string.IsNullOrWhiteSpace(command.TurnId) || command.TurnId.Length > 80 ||
            !(string.Equals(command.Trigger, "voice", System.StringComparison.Ordinal) ||
              string.Equals(command.Trigger, "heartbeat", System.StringComparison.Ordinal) ||
              string.Equals(command.Trigger, "critical", System.StringComparison.Ordinal) ||
              string.Equals(command.Trigger, "voice_body", System.StringComparison.Ordinal)) ||
            command.IntentSummary.Length > Agents.CompanionState.MaxIntentCharacters ||
            command.JournalText.Length > Agents.CompanionState.MaxJournalCharacters ||
            command.MemoryUpserts.Count > 3 ||
            !System.Enum.IsDefined(typeof(Agents.CompanionReaction), command.Reaction))
        {
            admission.Reject("InvalidCompanionTurn");
            return;
        }

        for (var i = 0; i < command.MemoryUpserts.Count; i++)
        {
            var memory = command.MemoryUpserts[i];
            if (memory == null ||
                string.IsNullOrWhiteSpace(memory.Key) ||
                memory.Key.Length > Agents.CompanionState.MaxMemoryKeyCharacters ||
                string.IsNullOrWhiteSpace(memory.Value) ||
                memory.Value.Length > Agents.CompanionState.MaxMemoryValueCharacters ||
                float.IsNaN(memory.Importance) || float.IsInfinity(memory.Importance) ||
                memory.Importance < 0f || memory.Importance > 1f)
            {
                admission.Reject("InvalidCompanionMemory");
                return;
            }
        }

        if (string.Equals(command.Trigger, "voice_body", System.StringComparison.Ordinal))
        {
            if (command.IntentSummary.Length != 0 || command.JournalText.Length != 0 ||
                command.MemoryUpserts.Count != 0 || command.Reaction == Agents.CompanionReaction.None)
            {
                admission.Reject("InvalidCompanionBodyEffect");
                return;
            }

            var physicalSocial = npc.Needs.Social;
            npc.Companion.ApplyVoiceSocialEffect(command.TurnId, command.Reaction, ref physicalSocial);
            npc.Needs.Social = physicalSocial;
            return;
        }

        var social = npc.Needs.Social;
        var reaction = string.Equals(command.Trigger, "voice",
            System.StringComparison.OrdinalIgnoreCase)
            ? command.Reaction
            : Agents.CompanionReaction.None;
        if (!npc.Companion.ApplyTurn(
                command.TurnId,
                reaction,
                command.IntentSummary,
                command.MemoryUpserts,
                command.JournalText,
                world.Tick,
                Spec136.HourTicks,
                ref social))
        {
            // Idempotent replay is a successful no-op: a bridge may retry after
            // losing the HTTP response and must never double-apply the bond.
            return;
        }

        npc.Needs.Social = social;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CompanionTurnRecorded",
                $"Turn={command.TurnId} Trigger={command.Trigger} Reaction={command.Reaction}");
        }
    }

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

    // Тело не в состоянии слушаться: кома, умирание, обморок, рыдания.
    // Bug #319 (вердикт игрока): ПРИТВОРСТВО здесь больше не числится —
    // притворяться мёртвой она решила сама, и прямой приказ игрока её
    // «расталкивает»: ClearForNewOrder снимает окно притворства, тело встаёт
    // и выполняет приказ. Тумблер и «отставить» проходят как раньше.
    private static bool Incapacitated(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) ||
        world.Tick < npc.Mind.CryingUntilTick;

    // Bug #281 / §121: тумблер меняет РЕЖИМ, а не позу тела. Уложенная в
    // кровать беспамятная (§105.17) держит бессрочную Sleep-интеракцию —
    // снос приказа поднял бы её через LyingSpot.MoveToStand, т.е.
    // телепортировал бы на землю рядом с кроватью. Смена контроля обязана
    // оставить её лежать.
    private static bool KeepsRestPoseAcrossModeSwitch(WorldState world, NPCState npc) =>
        npc.IsUnconscious(world.Tick) &&
        npc.Execution.Status == ExecutionStatus.InProgress &&
        npc.Execution.CurrentInteraction == InteractionType.Sleep;

    // ⭐ Общее начало любого действия: снять с себя всё, что держал прошлый
    // приказ. Без этого спам кликов течёт резервациями (см. правило 1).
    // keepRestPose (bug #281): пропустить снос живой интеракции — только для
    // смены режима контроля над лежащей без сознания, никогда для приказов.
    private static void ClearForNewOrder(
        WorldState world, NPCState npc, string reason, bool keepCarriedPerson = false,
        bool keepRestPose = false)
    {
        // Bug #319: прямой приказ игрока расталкивает притворяющуюся мёртвой —
        // притворство добровольное, и хозяин решает, что хватит. Штатный
        // EndPlayDead освобождает лежанку/узел и даёт wake-grace, как любой
        // другой подъём.
        if (world.Tick < npc.Mind.PlayDeadUntilTick)
        {
            MortalityHelpers.EndPlayDead(world, npc, "PlayerOrder");
        }

        // Bug #95 / spec 41.5: a manual order may wake a sleeper, but it must
        // not make the sim translate the body while GetUp is still playing.
        // Capture this before Abort clears CurrentInteraction, then retain the
        // replacement order behind the same grace as a completed sleep.
        var interruptedSleep = !keepRestPose &&
            npc.Execution.Status == ExecutionStatus.InProgress &&
            npc.Execution.CurrentInteraction == InteractionType.Sleep;

        if (!keepRestPose &&
            (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress ||
            npc.IsCarryingPerson || npc.Mind.InterruptedRescuePatientId is not null))
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
        npc.Plan.RequestedTalkTopic = null;
        npc.Mind.GoalLock = null;
        // §121.10: очередь «собрать всё» живёт ровно до следующего приказа —
        // любого. Игрок сказал «иди туда» посреди сбора листьев: продолжать
        // сбор за его спиной значило бы, что приказ его не слушается.
        // Приём самого «собрать всё» взводит очередь ПОСЛЕ этого вызова.
        ManualGatherTargets.Clear(npc.Mind);
        // §53.9: назначенный игроком вид помощи живёт ровно один приказ —
        // следующий приказ (любой) снимает его, чтобы метка не досталась
        // чужому походу и не подменила автономный выбор §53.3.
        npc.Mind.OrderedAidKind = AidKind.None;
        npc.Mind.OrderedAidFor = null;
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

        ClearForNewOrder(world, npc, "Игрок взял управление",
            keepRestPose: KeepsRestPoseAcrossModeSwitch(world, npc));

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

    // §121.11 (bug #294): темп по умолчанию — СОСТОЯНИЕ персонажа, не приказ.
    // Ни ClearForNewOrder, ни ClearAttackOrder: игрок переключает «шагом/бегом»
    // ровно так же, как «сделать домом» (§146.14) — не роняя текущий поход и не
    // трогая тумблер управления (requireManual: false). Идущий приказ подхватит
    // новый темп сразу: MovementSystem каждый тик читает Plan.RunRequested,
    // который здесь и переписывается, пока это ручной поход.
    private static void ApplySetRunByDefault(
        WorldState world, SetRunByDefaultCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "SetRunByDefault", requireManual: false,
                admission, out var npc))
        {
            return;
        }

        npc.Mind.RunByDefault = command.Run;
        if (npc.Mind.ManualControl && npc.Mind.CurrentGoal == GoalType.PlayerOrder &&
            npc.Plan.Goal == GoalType.PlayerOrder)
        {
            npc.Plan.RunRequested = command.Run;
        }

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "RunByDefaultChanged",
                $"Pace={(command.Run ? "Run" : "Walk")}");
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

    // §146.14 (bug #291): сделать свой очаг домом лагеря. Состояние, не план:
    // ни ClearForNewOrder, ни ClearAttackOrder — тумблер и текущий приказ не
    // трогаются, работает и над не-ручной (requireManual: false).
    private static void ApplySetCampHome(
        WorldState world, SetCampHomeCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "SetCampHome", requireManual: false,
                admission, out var npc))
        {
            return;
        }

        if (!FactionRelations.IsGirlCamp(npc.Faction))
        {
            Reject(world, npc.Id, "SetCampHome", "NotGirlCamp", admission);
            return;
        }

        if (!world.Entities.Objects.TryGetValue(command.Hearth, out var fire))
        {
            Reject(world, npc.Id, "SetCampHome", "NoSuchObject", admission);
            return;
        }

        // Недостроенная площадка — ещё не очаг. Гореть костру НЕ обязательно:
        // очаг рождается холодным, требовать огня — запретить переезд ночью.
        if (fire.DefinitionId != ContentIds.Campfire)
        {
            Reject(world, npc.Id, "SetCampHome", "NotAHearth", admission);
            return;
        }

        if (CampHomeMath.IsForeignCampTile(world, npc.Faction, fire.Tile))
        {
            Reject(world, npc.Id, "SetCampHome", "ForeignCamp", admission);
            return;
        }

        if (world.FactionHomes.TryGetValue(npc.Faction, out var old) &&
            old.Equals(fire.Tile))
        {
            Reject(world, npc.Id, "SetCampHome", "AlreadyHome", admission);
            return;
        }

        world.FactionHomes[npc.Faction] = fire.Tile;
        // §129: у непроштампованных зданий право открыть дверь выводится по
        // ближайшему очагу, а кэш запретов ключуется на версии — без бампа
        // старый вердикт держится до следующего чиха (как CampDiplomacyMath).
        world.DoorStateVersion++;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "CampHomeMoved",
                $"Camp={npc.Faction} Hearth={fire.Id.Value} Home={fire.Tile.Q},{fire.Tile.R}");
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
        ClearForNewOrder(world, npc, reason,
            keepRestPose: KeepsRestPoseAcrossModeSwitch(world, npc));
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

        var run = PaceFor(npc, command.Run);
        InstallMovePlan(world, npc, destination, junction, run);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=MoveTo Junction={destination.Value} " +
                $"Tile={Trace.FormatTile(npc.Plan.TargetTile)} " +
                $"Pace={(run ? "Run" : "Walk")}");
        }
    }

    // §121.11 (bug #294): темп приказа. Клик игрока темпа не несёт (null) —
    // его решает постоянная настройка САМОГО персонажа, поэтому групповой
    // приказ ходит разным темпом у разных девушек. Явное значение остаётся за
    // теми, кто действительно знает темп (MCP-инструмент, тесты, сценарии).
    private static bool PaceFor(NPCState npc, bool? requested) =>
        requested ?? npc.Mind.RunByDefault;

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

    // §153.4: a nearby request resolves immediately, without taking over
    // either participant's plan or answering for a manually controlled owner.
    private static void ApplyRequestItem(
        WorldState world, RequestItemCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "RequestItem", requireManual: false,
                admission, out var requester)) return;

        var result = ItemRequestMath.Request(world, requester, command.Target, command.DefinitionId);
        if (result != ItemRequestOutcome.Transferred)
        {
            Reject(world, requester.Id, "RequestItem", result.ToString(), admission);
            return;
        }
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, requester.Id, "ManualOrderAccepted",
                $"Order=RequestItem Target=NPC{command.Target.Value} Def={command.DefinitionId} Transferred=1");
        }
    }

    // §121.9: подойти и поговорить. Цель занята, идёт или не в духе — приказ
    // всё равно принимается: отказ по прибытии сыграет штатный RunTalk
    // (видимый cue TalkRejected), ровно как у автономной инициаторки. На
    // приёме отклоняется только физически невозможное.
    private static void ApplyTalkTo(
        WorldState world, TalkToCommand command, AdmissionTracker admission)
    {
        if (command.RequestedTopic is { } topic && !Social.TalkTopicRequest.IsAllowed(topic))
        {
            Reject(world, command.Npc, "TalkTo", "InvalidTalkTopic", admission);
            return;
        }

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

        npc.Plan.RequestedTalkTopic = command.RequestedTopic;
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
        // §53.9: вид помощи выбрал ИГРОК — он и доезжает до исполнения. Метка
        // ставится ПОСЛЕ ClearForNewOrder (тот её как раз снимает) и адресована
        // конкретной подопечной: другой поход её не подберёт.
        npc.Mind.OrderedAidKind = command.Kind;
        npc.Mind.OrderedAidFor = partner.Id;
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=Aid Kind={command.Kind} Target=NPC{partner.Id.Value}");
        }
    }

    // §121.9: единая медицинская помощь. Игрок выбирает пациентку, а не вид
    // расходника: исполнительница сама делает самое срочное из того, что
    // сейчас нужно и чем она располагает. Протез намеренно не входит сюда.
    private static void ApplyMedicalAid(
        WorldState world, MedicalAidCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "MedicalAid", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            RejectMedical(world, npc, "Incapacitated", admission);
            return;
        }

        if (command.Target.Equals(npc.Id))
        {
            RejectMedical(world, npc, "TargetSelf", admission);
            return;
        }

        if (!world.Entities.Npcs.TryGetValue(command.Target, out var patient) ||
            patient.Health <= 0f)
        {
            RejectMedical(world, npc, "TargetGone", admission);
            return;
        }

        if (patient.CarriedByNpcId is not null)
        {
            RejectMedical(world, npc, "TargetUnavailable", admission);
            return;
        }

        var needsDressing = AidAssessment.NeedsDressing(patient);
        var needsSplint = Spec118.SplintsEnabled &&
            KenshiProstheticMath.TryFindSplintPart(patient, out _);
        var needsMedicine = AidAssessment.NeedsMedicine(patient, world.Tick);

        // Сначала останавливаем кровь/закрываем открытую рану, затем
        // фиксируем конечность, затем лечим болезнь. Если самого срочного
        // припаса нет, всё равно оказываем другую возможную помощь.
        if (needsDressing && AidSupply.Has(world, npc, AidKind.Treat))
        {
            if (TryInstallManualMedicalAid(world, npc, patient, AidKind.Treat))
            {
                return;
            }

            RejectMedical(world, npc, "Unreachable", admission);
            return;
        }

        if (needsSplint && KenshiProstheticMath.HasItem(npc, ContentIds.Splint))
        {
            ClearForNewOrder(world, npc, "Приказ оказать медицинскую помощь");
            ClearAttackOrder(world, npc);
            if (RescueSystem.TryInstallLimbCarePlan(world, npc, patient, GoalType.Splint))
            {
                TraceMedicalAccepted(world, npc, patient, "Splint");
                return;
            }

            npc.Mind.CurrentGoal = GoalType.None;
            RejectMedical(world, npc, "Unreachable", admission);
            return;
        }

        if (needsMedicine && AidSupply.Has(world, npc, AidKind.Medicate))
        {
            if (TryInstallManualMedicalAid(world, npc, patient, AidKind.Medicate))
            {
                return;
            }

            RejectMedical(world, npc, "Unreachable", admission);
            return;
        }

        var reason = needsDressing ? "NoBandage"
            : needsSplint ? "NoSplint"
            : needsMedicine ? "NoMedicine"
            : "NotNeeded";
        RejectMedical(world, npc, reason, admission);
    }

    private static bool TryInstallManualMedicalAid(
        WorldState world, NPCState npc, NPCState patient, AidKind kind)
    {
        if (patient.CurrentJunction is not { } patientJunction)
        {
            return false;
        }

        ClearForNewOrder(world, npc, "Приказ оказать медицинскую помощь",
            keepCarriedPerson: true);
        ClearAttackOrder(world, npc);
        AidAssessment.Assess(patient, world.Tick, out var suffering);
        if (!PlanningSystem.TryInstallAidPlan(
                world, npc, patient, patient.Id, kind, patient.Tile,
                suffering, patientJunction, fromMemory: false, out _))
        {
            npc.Mind.CurrentGoal = GoalType.None;
            return false;
        }

        npc.Plan.Goal = GoalType.Aid;
        npc.Mind.CurrentGoal = GoalType.Aid;
        // RunAid normally re-evaluates all §53 needs on arrival. Mark this
        // particular plan so hunger/thirst cannot turn a medical order into
        // feeding; it will re-evaluate only medical needs instead.
        foreach (var step in npc.Plan.Steps)
        {
            if (step.Type == PlanStepType.Interact)
            {
                step.Interaction = InteractionType.MedicalAid;
            }
        }
        TraceMedicalAccepted(world, npc, patient, kind.ToString());
        return true;
    }

    private static void TraceMedicalAccepted(
        WorldState world, NPCState npc, NPCState patient, string action)
    {
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=MedicalAid Action={action} Target=NPC{patient.Id.Value}");
        }
    }

    private static void RejectMedical(
        WorldState world, NPCState npc, string reason, AdmissionTracker admission)
    {
        // Жёлтый восклицательный знак над исполнительницей плюс точная
        // локализованная причина в штатном тосте карточки.
        SocialCueSignals.Stamp(world, npc, "MedicalAidRejected", null);
        Reject(world, npc.Id, "MedicalAid", reason, admission);
    }

    // §121.9: отдельный осознанный приказ на протез. Шины, перевязки и
    // лекарства сюда больше не попадают.
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

        if (patient.IsBeingCarried)
        {
            Reject(world, npc.Id, "TreatLimbs", "PersonNotAvailable", admission);
            return;
        }

        if (!Spec118.ProstheticsEnabled ||
            !KenshiProstheticMath.TryFindProstheticPart(
                world, npc, patient, out _, out _, out _))
        {
            var reason = KenshiProstheticMath.HasSeveredLimb(patient)
                ? "NoProsthetic"
                : "NoLimbDamage";
            Reject(world, npc.Id, "TreatLimbs", reason, admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ заняться конечностями");
        ClearAttackOrder(world, npc);
        if (!RescueSystem.TryInstallLimbCarePlan(
                world, npc, patient, GoalType.FitProsthetic))
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
                $"Order=TreatLimbs Goal=FitProsthetic Target=NPC{patient.Id.Value}");
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
                if (!Spec53.SelfTreatEnabled)
                {
                    Reject(world, npc.Id, "SelfAction", "FeatureDisabled", admission);
                    return;
                }

                if (!npc.Body.HasUsableHand)
                {
                    Reject(world, npc.Id, "SelfAction", "MissingHands", admission);
                    return;
                }

                if (npc.IsFighting || !AidAssessment.NeedsDressing(npc))
                {
                    Reject(world, npc.Id, "SelfAction", "NotNeeded", admission);
                    return;
                }

                var hasBandage = MedicalSupplyMath.BandageCount(npc) > 0 ||
                    MedicalSupplyMath.TryFindReachableBandageSource(world, npc, out _);
                if (!hasBandage)
                {
                    Reject(world, npc.Id, "SelfAction", "NoBandage", admission);
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
            var run = PaceFor(assignment.Npc, command.Run);
            InstallMovePlan(
                world, assignment.Npc, assignment.Destination, destination, run);
            if (SimTrace.Enabled)
            {
                Trace.Debug(world, assignment.Npc.Id, "ManualOrderAccepted",
                    $"Order=GroupMove Junction={assignment.Destination.Value} " +
                    $"Pace={(run ? "Run" : "Walk")}");
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
                : "Игрок вернул группу ИИ",
                keepRestPose: KeepsRestPoseAcrossModeSwitch(world, npc));
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

    // §55.4 (bug #317): «Наполнить» — перелить воду вскрытых кокосов в личную
    // бутылку. Приказ по образцу Interact (requireManual), но без подхода:
    // обе ёмкости уже в её карманах — сразу небыстрый шаг FillVessel на месте.
    private static void ApplyFillVessel(
        WorldState world, FillVesselCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(world, command.Npc, "FillVessel", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "FillVessel", "Incapacitated", admission);
            return;
        }

        // Ячейка панели могла устареть (панель рисует прошлый тик) — приказ
        // честно отклоняется, а не наполняет «что попало под этим номером».
        var items = npc.Inventory.Items;
        if (command.Item.Source != InventoryItemSource.Carried ||
            command.Item.Index < 0 || command.Item.Index >= items.Count ||
            items[command.Item.Index].DefinitionId != command.Item.ExpectedDefinitionId)
        {
            Reject(world, npc.Id, "FillVessel", "StaleItem", admission);
            return;
        }

        // Приёмник — только бутылка: у инстанса кокоса нет вида воды, и перелив
        // «в кокос» отмывал бы сырую воду от риска болезни (VesselTransferMath).
        if (command.Item.ExpectedDefinitionId != Content.ContentIds.Bottle)
        {
            Reject(world, npc.Id, "FillVessel", "NotAVessel", admission);
            return;
        }

        var selectedBottle = items[command.Item.Index];
        if (!VesselTransferMath.CanFillBottle(npc, selectedBottle))
        {
            Reject(world, npc.Id, "FillVessel", "NothingToPour", admission);
            return;
        }

        ClearForNewOrder(world, npc, "Приказ наполнить бутылку", keepCarriedPerson: true);
        ClearAttackOrder(world, npc);

        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetItemDefinitionId = Content.ContentIds.Bottle;
        npc.Execution.TargetInventoryItem = selectedBottle;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.FillVessel,
            // The selected physical bottle must survive the delayed action;
            // the definition alone is ambiguous once several are carried.
            TimeoutEndTick = command.Item.Index
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order=FillVessel Charges={BottleInventoryMath.Charges(selectedBottle)} " +
                $"CoconutSips={VesselTransferMath.CoconutSips(npc)}");
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
            not InventoryTransferDirection.Give and not InventoryTransferDirection.TakeAndWear)
        {
            Reject(world, looter.Id, "TransferInventory", "InvalidDirection", admission);
            return;
        }

        // §128 r2 (#164): кого можно обыскать — один предикат на приём приказа и
        // на его исполнение (мёртвая, спящая, без сознания; несомый — только
        // своими же руками). §153.1: с направлением Give тот же предикат
        // добавляет живого человека в сознании — это подарок.
        if (!PlayerLootTargets.TryResolve(
                world, looter, command.Other, command.Direction,
                out var other, out var carriedBySelf))
        {
            Reject(world, looter.Id, "TransferInventory", "PersonNotAvailable", admission);
            return;
        }

        // §146.12: an explicit player order uses the same moral boundary as
        // autonomous looting. Own camp/corpses keep §128 semantics; a living
        // neutral neighbour needs hate or desperate hunger.
        var wear = command.Direction == InventoryTransferDirection.TakeAndWear;
        var take = command.Direction != InventoryTransferDirection.Give;
        if (take &&
            world.Entities.Npcs.ContainsKey(other.Id) &&
            looter.Faction != other.Faction &&
            !CampDiplomacyMath.CanLoot(world, looter, other))
        {
            Reject(world, looter.Id, "TransferInventory", "NoLootMotive", admission);
            return;
        }

        var source = take ? other : looter;
        var destination = take ? looter : other;
        if (!PlayerInventoryTransferMath.TryResolveTransfer(
                world, source, command.Item, command.Count,
                out var selectedItems, out _) ||
            !PlayerInventoryTransferMath.FitsAfter(
                world, source, destination, command.Item, command.Count, wear))
        {
            Reject(world, looter.Id, "TransferInventory", "StaleOrNoSpace", admission);
            return;
        }

        // §128: транзакция плана не роняет того, в чьих карманах роемся.
        ClearForNewOrder(world, looter, "Ручной обмен вещами с человеком",
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
        if (!carriedBySelf && PlayerLootTargets.IsStandingRecipient(world, other))
        {
            // §153.1: у стоящей станции у ног нет — мерим до неё самой тем же
            // радиусом, которым к человеку подходит помощь §53.
            closeEnough = InteractionReach.CheckPersonStart(
                world, looter, other, other.Position, InteractionReach.Aid,
                $"Player gift to NPC{other.Id.Value}");
        }
        else if (!carriedBySelf)
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
            (InventoryTransferDirection.TakeAndWear, InventoryItemSource.Carried) =>
                PlanStepType.PlayerTakeAndWearCarried,
            (InventoryTransferDirection.TakeAndWear, InventoryItemSource.Worn) =>
                PlanStepType.PlayerTakeAndWearWorn,
            (InventoryTransferDirection.Take, InventoryItemSource.Carried) =>
                PlanStepType.PlayerTakeCarried,
            (InventoryTransferDirection.Take, InventoryItemSource.Worn) =>
                PlanStepType.PlayerTakeWorn,
            (InventoryTransferDirection.Give, InventoryItemSource.Carried) =>
                PlanStepType.PlayerGiveCarried,
            _ => PlanStepType.PlayerGiveWorn
        };
        // Bug #355: the trip to a body can span many ticks while the player
        // keeps managing inventories. Keep the selected physical item, not
        // merely its mutable index + definition (two bottles share the latter).
        looter.Execution.TargetInventoryItem = selectedItems[0];
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
            not InventoryTransferDirection.Give and not InventoryTransferDirection.TakeAndWear)
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

        ItemInstance selectedItem;
        ObjectId? selectedWorldObject = null;
        var wear = command.Direction == InventoryTransferDirection.TakeAndWear;
        var take = command.Direction != InventoryTransferDirection.Give;
        if (take)
        {
            if (!ContainerLootMath.TryResolve(
                    world, container, command.SlotIndex,
                    command.ExpectedDefinitionId, command.Count,
                    out var selected, out var groundSources) || selected.Count == 0)
            {
                Reject(world, looter.Id, "TransferContainer", "StaleItem", admission);
                return;
            }

            selectedItem = selected[0];
            if (wear && (command.Count != 1 ||
                !PlayerInventoryTransferMath.CanWearIncoming(world, looter, selectedItem, selected)))
            {
                Reject(world, looter.Id, "TransferContainer",
                    looter.Mind.OutfitLocked ? "OutfitLocked" : "StaleOrNoSpace", admission);
                return;
            }
            if (groundSources.Count > 0)
            {
                selectedWorldObject = groundSources[0];
            }
        }
        else
        {
            var itemRef = new InventoryItemRef(
                InventoryItemSource.Carried, command.SlotIndex,
                command.ExpectedDefinitionId);
            if (!PlayerInventoryTransferMath.TryResolveTransfer(
                    world, looter, itemRef, command.Count,
                    out var selected, out _) || selected.Count == 0)
            {
                Reject(world, looter.Id, "TransferContainer", "StaleItem", admission);
                return;
            }

            selectedItem = selected[0];
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

        looter.Execution.TargetInventoryItem = selectedItem;
        looter.Execution.TargetInventoryWorldObject = selectedWorldObject;
        looter.Plan.Steps.Add(new PlanStep
        {
            Type = wear ? PlanStepType.PlayerTakeAndWearFromContainer
                : take ? PlanStepType.PlayerTakeFromContainer
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

        TryStartInteractOrder(
            world, npc, command.Target, command.Interaction, command.InteractionId,
            "Interact", admission);
    }

    // ⭐ §121.10: ОДНО тело приказа «сделай с этим предметом вот это» — его
    // зовёт и одиночный Interact, и каждый подход очереди «собрать всё».
    // Разойдись эти две дороги, «собрать» и «собрать все» подходили бы к
    // одному и тому же листу по-разному, и вторая половина бага #270 (порядок
    // и подход) чинилась бы отдельно от первой.
    private static bool TryStartInteractOrder(
        WorldState world, NPCState npc, ObjectId targetId,
        InteractionType interactionType, string requestedInteractionId,
        string verb, AdmissionTracker admission)
    {
        if (interactionType == InteractionType.FillBottle &&
            world.Entities.Objects.TryGetValue(targetId, out var legacyTarget) &&
            world.Content.ObjectDefinitions.TryGetValue(legacyTarget.DefinitionId, out var legacyDefinition) &&
            legacyDefinition.HasTag("Campfire"))
        {
            Reject(world, npc.Id, verb, "RetiredInteraction", admission);
            return false;
        }
        // Правило 2: объект берётся из МИРА, а не из npc.Perception.
        if (!world.Entities.Objects.TryGetValue(targetId, out var worldObject))
        {
            Reject(world, npc.Id, verb, "TargetGone", admission);
            return false;
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            Reject(world, npc.Id, verb, "TargetGone", admission);
            return false;
        }

        // §120.10: a free architecture object keeps its real wall/floor
        // definition for rendering and topology. While it is being built or
        // demolished, its INSTANCE exposes the generic build-site verb.
        if (BuildSiteMath.UsesGenericSiteInteractions(worldObject) &&
            world.Content.ObjectDefinitions.TryGetValue(ContentIds.BuildSite, out var siteDefinition))
        {
            definition = siteDefinition;
        }

        InteractionDefinition? interaction = null;
        foreach (var candidate in definition.Interactions)
        {
            if (candidate.Type == interactionType &&
                (string.IsNullOrEmpty(requestedInteractionId) ||
                 candidate.Id == requestedInteractionId))
            {
                interaction = candidate;
                break;
            }
        }

        if (interaction is null)
        {
            Reject(world, npc.Id, verb, "NoSuchAction", admission);
            return false;
        }

        if (BuildSiteMath.IsDemolitionSite(worldObject) &&
            !Content.GearCatalog.HasCapability(
                npc.Inventory.Items, Content.GearCapability.ChopWood))
        {
            Reject(world, npc.Id, verb, "MissingTool", admission);
            return false;
        }

        // §54.14 (bug #292): «строить» осмысленно только на площадке (или на
        // анкере общинной стройки §35.3). Костёр носит глагол Build в своём
        // каталоге постоянно — он сам себе площадка, пока открыт upgrade-bill,
        // — и по закрытому счёту приказ молча уходил в ApplyBuildPiece, то есть
        // достраивал совсем ДРУГУЮ стройку колонии за счёт этих рук.
        if (interactionType == InteractionType.Build &&
            !BuildSiteMath.IsSite(worldObject) &&
            worldObject.DefinitionId != ContentIds.ConstructionSite)
        {
            Reject(world, npc.Id, verb, "NothingToBuild", admission);
            return false;
        }

        // Bug #310 (вердикт игрока): выдохшаяся не берётся за РАБОЧИЙ приказ —
        // отклоняет его и садится отдыхать. Порог тот же, что красит стамину
        // «выдохлась» (StaminaExhaustedThreshold). Еда/питьё/сон/подбор/лечение
        // не гейтятся: уставшей как раз положено пить и лечиться.
        if (npc.Needs.Stamina <= SimBalance.StaminaExhaustedThreshold &&
            interactionType is InteractionType.Harvest or InteractionType.Process
                or InteractionType.Build or InteractionType.BuildRaft
                or InteractionType.Craft or InteractionType.Butcher)
        {
            Reject(world, npc.Id, verb, "Exhausted", admission);
            ClearForNewOrder(world, npc, "Выдохлась — отдых вместо работы");
            npc.Mind.CurrentGoal = GoalType.Sit;
            ManualPlanner.BuildGroundSitPlan(world, npc);
            if (npc.Plan.Status != PlanStatus.Active)
            {
                npc.Plan.Steps.Clear();
                npc.Plan.Status = PlanStatus.None;
                ManualPlanner.BuildIdleRestPlan(world, npc);
            }

            if (npc.Plan.Status != PlanStatus.Active)
            {
                npc.Mind.CurrentGoal = GoalType.None;
            }

            return false;
        }

        if (interactionType == InteractionType.Sleep)
        {
            var sleepReason = ExecutionSystem.GetSleepInterruptReason(
                world, npc, manualOrder: true);
            if (sleepReason is not null)
            {
                Reject(world, npc.Id, verb, sleepReason, admission);
                ExecutionSystem.StampSleepRefusal(world, npc, sleepReason);
                return false;
            }
        }

        // Тот же ANY-OF гейт, что стоит на входе в ExecutionSystem. Здесь он —
        // ВТОРАЯ проверка (первая посерила пункт в меню), и обе нужны: меню
        // могло быть открыто до того, как она выронила нож.
        if (interaction.RequiredCapabilities.Count > 0 &&
            !DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities))
        {
            Reject(world, npc.Id, verb, "MissingTool", admission);
            return false;
        }

        if (interactionType == InteractionType.Ignite)
        {
            if (worldObject.ResourceAmount > 0f)
            {
                Reject(world, npc.Id, verb, "FireAlreadyLit", admission);
                return false;
            }

            if (!ContainerLootMath.HasQueuedCampfireFuel(world, worldObject))
            {
                Reject(world, npc.Id, verb, "FireHasNoFuel", admission);
                return false;
            }
        }

        if (interactionType == InteractionType.Fuel)
        {
            var fuel = ContainerLootMath.FindCarriedCampfireFuel(world, npc);
            if (fuel is null)
            {
                Reject(world, npc.Id, verb, "NoFuel", admission);
                return false;
            }

            if (worldObject.ResourceAmount <= 0f &&
                !ContainerLootMath.CanAccept(world, worldObject, new[] { fuel }))
            {
                Reject(world, npc.Id, verb, "FuelBufferFull", admission);
                return false;
            }
        }

        if (worldObject.IsOccupied && worldObject.CurrentUser is { } user && !user.Equals(npc.Id))
        {
            Reject(world, npc.Id, verb, "Occupied", admission);
            return false;
        }

        if (worldObject.Junctions.Count == 0)
        {
            Reject(world, npc.Id, verb, "Unreachable", admission);
            return false;
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока");
        ClearAttackOrder(world, npc);

        var anchor = worldObject.Junctions[0];
        // Manual and autonomous object orders share the exact same approach
        // predicate.  In particular Sit/Sleep always reserve the nearest free
        // rim junction; their anchor is a pose marker, never a standing spot.
        var standBeside = PlanningSystem.RequiresBesideApproach(
            world, anchor, interactionType);

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
                Reject(world, npc.Id, verb, "Unreachable", admission);
                npc.Mind.CurrentGoal = GoalType.None;
                return false;
            }
        }
        else
        {
            target = anchor;
            if (!SpatialMutations.TryReserveJunction(
                    world, target, npc.Id, world.Tick, Spec121.ManualReserveTicks))
            {
                Reject(world, npc.Id, verb, "Occupied", admission);
                npc.Mind.CurrentGoal = GoalType.None;
                return false;
            }
        }

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, target, npc.Body.CanJump))
        {
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            Reject(world, npc.Id, verb, "Unreachable", admission);
            npc.Mind.CurrentGoal = GoalType.None;
            return false;
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
            Interaction = interactionType,
            InteractionId = interaction.Id
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualOrderAccepted",
                $"Order={verb} Obj={worldObject.Id.Value} Def={worldObject.DefinitionId} " +
                $"Action={interaction.Id}/{interactionType} Junction={target.Value}");
        }

        return true;
    }

    // ── §121.10 «Собрать всё на гексе» (баг #270) ────────────────────────
    //
    // Очередь, а не пачка. Игрок просил ровно этого: «чтобы пальмовые листья
    // собирались по очереди, не все зараз». Поэтому приём НЕ делает ничего
    // особенного — он ставит обычный ручной приказ на ПЕРВЫЙ подходящий
    // предмет и взводит на NPC описание очереди; следующий подход выдаёт
    // ManualOrderSystem, когда предыдущий доигран.

    private static void ApplyGatherAllOnHex(
        WorldState world, GatherAllOnHexCommand command, AdmissionTracker admission)
    {
        if (!TryTakeOrder(
                world, command.Npc, "GatherAll", requireManual: true,
                admission, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "GatherAll", "Incapacitated", admission);
            return;
        }

        // Гекс и «однотипность» берутся из КЛИКНУТОГО предмета: игрок показал
        // пальцем на конкретный лист и сказал «все такие». Её собственный гекс
        // тут ни при чём — к моменту сбора она всё равно будет стоять у этого.
        if (!world.Entities.Objects.TryGetValue(command.Target, out var anchor))
        {
            Reject(world, npc.Id, "GatherAll", "TargetGone", admission);
            return;
        }

        var definitionId = anchor.DefinitionId;
        var tile = anchor.Tile;
        var matches = ManualGatherTargets.Collect(
            world, npc, definitionId, tile, command.Interaction);
        if (matches.Count == 0)
        {
            Reject(world, npc.Id, "GatherAll", "TargetGone", admission);
            return;
        }

        var budget = matches.Count < ManualGatherTargets.MaxQueueLength
            ? matches.Count
            : ManualGatherTargets.MaxQueueLength;

        // Первый подход — обычный ручной приказ. Он же снимает старую очередь
        // через ClearForNewOrder, поэтому взводим новую ПОСЛЕ него.
        if (!TryStartInteractOrder(
                world, npc, matches[0].Id, command.Interaction,
                command.InteractionId, "GatherAll", admission))
        {
            return;
        }

        ManualGatherTargets.Arm(
            npc.Mind, definitionId, tile, command.Interaction,
            command.InteractionId, budget);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualGatherAllArmed",
                $"Def={definitionId} Tile={tile.Q},{tile.R} " +
                $"Action={command.Interaction} Queue={budget}");
        }
    }

    /// <summary>
    /// §121.10: следующая задача очереди «собрать всё». Зовётся из
    /// <c>ManualOrderSystem</c> ровно тогда, когда предыдущий приказ доигран и
    /// цель уже вернулась в <c>None</c>.
    /// <para>
    /// <paramref name="previousCompleted"/> — исход предыдущего подхода. Оборванный
    /// (не дошла, предмет исчез, кто-то занял) очередь ЗАКРЫВАЕТ: молча ходить
    /// по кругу за недостижимым листом — это ровно тот «стоит и ничего не
    /// делает», ради которого существует §30.15.
    /// </para>
    /// </summary>
    internal static void ContinueGatherAll(
        WorldState world, NPCState npc, bool previousCompleted)
    {
        if (!ManualGatherTargets.IsActive(npc.Mind))
        {
            return;
        }

        // Причина остановки называется своим именем: очередь читают по трассе,
        // и «Incapacitated» на отпущенной под ИИ девушке — это ложный след.
        if (!previousCompleted)
        {
            FinishGatherAll(world, npc, "OrderFailed");
            return;
        }

        if (!npc.Mind.ManualControl)
        {
            FinishGatherAll(world, npc, "ManualReleased");
            return;
        }

        if (npc.Health <= 0f || Incapacitated(world, npc))
        {
            FinishGatherAll(world, npc, "Incapacitated");
            return;
        }

        var definitionId = npc.Mind.GatherAllDefinitionId;
        var tile = npc.Mind.GatherAllTile!.Value;
        var interaction = npc.Mind.GatherAllInteraction!.Value;
        var interactionId = npc.Mind.GatherAllInteractionId;
        var remaining = npc.Mind.GatherAllRemaining - 1;

        var next = ManualGatherTargets.Next(world, npc, definitionId, tile, interaction);
        if (next is null || remaining <= 0)
        {
            FinishGatherAll(world, npc, next is null ? "HexEmpty" : "QueueSpent");
            return;
        }

        if (!TryStartInteractOrder(
                world, npc, next.Id, interaction, interactionId, "GatherAll",
                new AdmissionTracker(npc.Id, "GatherAll")))
        {
            // Отказ уже оттрассирован причиной; очередь закрываем — иначе она
            // будет пытаться взять тот же предмет каждый средний проход.
            FinishGatherAll(world, npc, "OrderRefused");
            return;
        }

        ManualGatherTargets.Arm(
            npc.Mind, definitionId, tile, interaction, interactionId, remaining);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualGatherAllNext",
                $"Obj={next.Id.Value} Def={definitionId} Left={remaining}");
        }
    }

    private static void FinishGatherAll(WorldState world, NPCState npc, string outcome)
    {
        ManualGatherTargets.Clear(npc.Mind);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ManualGatherAllFinished", $"Outcome={outcome}");
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
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSitePlanned",
                $"Product={ContentIds.HutPlan} Tile={command.Tile} Site={site.Id.Value}");
        }
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

        var householdHearth = command.CatalogId == ContentIds.FurnitureHearth;
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
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSitePlanned",
                $"Product={product} Tile={command.Tile} Site={site.Id.Value}");
        }
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
        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSitePlanned",
                $"Product={ContentIds.HutPlan} Blueprint={blueprintId} " +
                $"Tile={command.Tile} Site={site.Id.Value}");
        }
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

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildingBlueprintUpdated",
                $"Owner={owner.Id.Value} Blueprint={owner.BlueprintId} " +
                $"Modules={draft.Elements.Count}");
        }
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

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "FreeArchitectureChanged",
                $"Place={command.Placements.Count} Remove={command.RemovedSlotKeys.Count}");
        }
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

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSiteAdjusted",
                $"Site={site.Id.Value} Product={site.BuildProduct} " +
                $"Rotation={site.RotationDegrees:0}");
        }
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

        if (SimTrace.Enabled)
        {
            Trace.DebugSystem(world, "BuildSiteCancelled",
                $"Site={site.Id.Value} Product={site.BuildProduct}");
        }
    }
}

}
