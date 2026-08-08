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
    public static void Apply(WorldState world, ISimulationCommand command)
    {
        if (!Spec121.ManualControlEnabled)
        {
            return;
        }

        switch (command)
        {
            case SetManualControlCommand setManual:
                ApplySetManual(world, setManual);
                break;
            case MoveToCommand moveTo:
                ApplyMoveTo(world, moveTo);
                break;
            case InteractCommand interact:
                ApplyInteract(world, interact);
                break;
            case AttackNpcCommand attackNpc:
                ApplyAttackNpc(world, attackNpc);
                break;
            case AttackMobCommand attackMob:
                ApplyAttackMob(world, attackMob);
                break;
            case StopCommand stop:
                ApplyStop(world, stop);
                break;
        }
    }

    /// <summary>Приказ отклонён и почему. Тип НЕ в GameEventTypes намеренно:
    /// это сигнал игроку в момент клика, а не строка в летописи колонии.</summary>
    private static void Reject(WorldState world, EntityId npc, string verb, string reason) =>
        Trace.Emit(world, npc, "ManualOrderRejected", $"Order={verb} Reason={reason}");

    // Общий вход: NPC существует, жив и (кроме тумблера) действительно ручной.
    private static bool TryTakeOrder(
        WorldState world, EntityId id, string verb, bool requireManual, out NPCState npc)
    {
        if (!world.Entities.Npcs.TryGetValue(id, out npc) || npc.Health <= 0f)
        {
            Reject(world, id, verb, "NoSuchNpc");
            return false;
        }

        if (requireManual && !npc.Mind.ManualControl)
        {
            // Клик, отправленный до того, как игрок вернул её ИИ, — приказ
            // молча применять нельзя: он бы перебил только что выбранную цель.
            Reject(world, id, verb, "NotManual");
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
    private static void ClearForNewOrder(WorldState world, NPCState npc, string reason)
    {
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, reason);
        }

        npc.Plan.Steps.Clear();
        npc.Mind.GoalLock = null;
    }

    private static void ApplySetManual(WorldState world, SetManualControlCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "SetManual", requireManual: false, out var npc))
        {
            return;
        }

        if (npc.Mind.ManualControl == command.Enabled)
        {
            return;
        }

        ClearForNewOrder(world, npc, command.Enabled
            ? "Игрок взял управление"
            : "Игрок вернул управление ИИ");

        npc.Mind.CurrentGoal = GoalType.None;
        npc.Mind.ManualAttackNpcId = null;
        npc.Mind.ManualAttackMobId = null;

        if (command.Enabled)
        {
            // Приглашения снимаются вместе с автономией: ждать разговора или
            // помощи она больше не станет, и оставленная заявка подвесила бы
            // ЗВАВШУЮ — та стоит и ждёт ответа, которого уже не будет.
            npc.Mind.PendingTalkFrom = null;
            npc.Mind.PendingAidFrom = null;
        }

        npc.Mind.ManualControl = command.Enabled;
        Trace.Emit(world, npc.Id, "ManualControlChanged",
            $"Enabled={(command.Enabled ? 1 : 0)}");
    }

    private static void ApplyStop(WorldState world, StopCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "Stop", requireManual: true, out var npc))
        {
            return;
        }

        ClearForNewOrder(world, npc, "Приказ отставить");
        npc.Mind.CurrentGoal = GoalType.None;
        ClearAttackOrder(world, npc);
        Trace.Emit(world, npc.Id, "ManualOrderStopped", "Order=Stop");
    }

    private static void ApplyMoveTo(WorldState world, MoveToCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "MoveTo", requireManual: true, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "MoveTo", "Incapacitated");
            return;
        }

        if (SpatialQueries.FindNearestJunction(world, command.WorldPosition) is not { } destination ||
            !world.Junctions.Items.TryGetValue(destination, out var junction) ||
            junction.Blocked)
        {
            Reject(world, npc.Id, "MoveTo", "Unreachable");
            return;
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока");
        ClearAttackOrder(world, npc);

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, destination, npc.Body.CanJump))
        {
            Reject(world, npc.Id, "MoveTo", "Unreachable");
            npc.Mind.CurrentGoal = GoalType.None;
            return;
        }

        // Форма плана — ровно как у прогулки (BuildExplorePlan): один шаг и
        // никакой резервации. Резервируют те, кто идёт К ЧЕМУ-ТО занимаемому;
        // «встань вон там» ничего не занимает, и держать за ней клетку значило
        // бы мешать остальным ходить по острову.
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Plan.TargetJunctionId = destination;
        npc.Plan.TargetTile = junction.Tiles.Count > 0 ? junction.Tiles[0] : null;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = destination
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;

        Trace.Emit(world, npc.Id, "ManualOrderAccepted",
            $"Order=MoveTo Junction={destination.Value} " +
            $"Tile={Trace.FormatTile(npc.Plan.TargetTile)}");
    }

    private static void ApplyInteract(WorldState world, InteractCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "Interact", requireManual: true, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "Interact", "Incapacitated");
            return;
        }

        // Правило 2: объект берётся из МИРА, а не из npc.Perception.
        if (!world.Entities.Objects.TryGetValue(command.Target, out var worldObject))
        {
            Reject(world, npc.Id, "Interact", "TargetGone");
            return;
        }

        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            Reject(world, npc.Id, "Interact", "TargetGone");
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
            Reject(world, npc.Id, "Interact", "NoSuchAction");
            return;
        }

        // Тот же ANY-OF гейт, что стоит на входе в ExecutionSystem. Здесь он —
        // ВТОРАЯ проверка (первая посерила пункт в меню), и обе нужны: меню
        // могло быть открыто до того, как она выронила нож.
        if (interaction.RequiredCapabilities.Count > 0 &&
            !DecisionSystem.HasAnyCapability(npc, interaction.RequiredCapabilities))
        {
            Reject(world, npc.Id, "Interact", "MissingTool");
            return;
        }

        if (worldObject.IsOccupied && worldObject.CurrentUser is { } user && !user.Equals(npc.Id))
        {
            Reject(world, npc.Id, "Interact", "Occupied");
            return;
        }

        if (worldObject.Junctions.Count == 0)
        {
            Reject(world, npc.Id, "Interact", "Unreachable");
            return;
        }

        ClearForNewOrder(world, npc, "Новый приказ игрока");
        ClearAttackOrder(world, npc);

        var anchor = worldObject.Junctions[0];
        var standBeside = command.Interaction == InteractionType.PickUp ||
            SpatialQueries.IsAllWaterJunction(world, anchor) ||
            (world.Junctions.Items.TryGetValue(anchor, out var anchorJunction) && anchorJunction.Blocked);

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
                Reject(world, npc.Id, "Interact", "Unreachable");
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
                Reject(world, npc.Id, "Interact", "Occupied");
                npc.Mind.CurrentGoal = GoalType.None;
                return;
            }
        }

        if (npc.CurrentJunction is not { } start ||
            !Connectivity.Reachable(world, start, target, npc.Body.CanJump))
        {
            SpatialMutations.ReleaseJunctionReservation(world, target, npc.Id);
            Reject(world, npc.Id, "Interact", "Unreachable");
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

        Trace.Emit(world, npc.Id, "ManualOrderAccepted",
            $"Order=Interact Obj={worldObject.Id.Value} Def={worldObject.DefinitionId} " +
            $"Action={command.Interaction} Junction={target.Value}");
    }

    private static void ApplyAttackNpc(WorldState world, AttackNpcCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "AttackNpc", requireManual: true, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "AttackNpc", "Incapacitated");
            return;
        }

        if (command.Target.Equals(npc.Id) ||
            !world.Entities.Npcs.TryGetValue(command.Target, out var target) ||
            target.Health <= 0f)
        {
            Reject(world, npc.Id, "AttackNpc", "TargetGone");
            return;
        }

        ClearForNewOrder(world, npc, "Приказ атаковать");
        ClearAttackOrder(world, npc);

        npc.Mind.CurrentGoal = GoalType.PlayerAttack;
        npc.Mind.ManualAttackNpcId = target.Id;
        Trace.Emit(world, npc.Id, "ManualOrderAccepted",
            $"Order=AttackNpc Target=NPC{target.Id.Value}");
    }

    private static void ApplyAttackMob(WorldState world, AttackMobCommand command)
    {
        if (!TryTakeOrder(world, command.Npc, "AttackMob", requireManual: true, out var npc))
        {
            return;
        }

        if (Incapacitated(world, npc))
        {
            Reject(world, npc.Id, "AttackMob", "Incapacitated");
            return;
        }

        if (!ManualControlMath.TryGetMob(world, command.MobId, out _))
        {
            Reject(world, npc.Id, "AttackMob", "TargetGone");
            return;
        }

        ClearForNewOrder(world, npc, "Приказ атаковать зверя");
        ClearAttackOrder(world, npc);

        npc.Mind.CurrentGoal = GoalType.PlayerAttack;
        npc.Mind.ManualAttackMobId = command.MobId;
        // Сцепку со зверем держит §57: AnimalCombatSystem бьёт любого, у кого
        // стоит CombatAssistDogId, — приказ просто становится в тот же строй.
        npc.Mind.CombatAssistDogId = command.MobId;
        Trace.Emit(world, npc.Id, "ManualOrderAccepted",
            $"Order=AttackMob Target=Dog{command.MobId}");
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
