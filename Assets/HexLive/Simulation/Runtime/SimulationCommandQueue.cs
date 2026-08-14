using System.Collections.Generic;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §121: приказы игрока конкретному NPC. Очередь живёт на движке и опустошается
// в НАЧАЛЕ Step(), до всех систем: приказ, отданный между тиками, действует с
// того же тика, и порядок применения не зависит от того, какая система когда
// проснулась. Отдельной системой это не сделано нарочно — тогда бы приказ ждал
// своей очереди внутри слоя и его место в реестре стало бы поведением.
//
// Кладёт сюда только презентация (ISimulationSource.EnqueueCommand); внутри
// симуляции команд не порождает никто.
public sealed class SimulationCommandQueue
{
    private readonly Queue<ISimulationCommand> _commands = new();

    public int Count => _commands.Count;

    public void Enqueue(ISimulationCommand command) => _commands.Enqueue(command);

    public bool TryDequeue(out ISimulationCommand? command)
    {
        if (_commands.Count == 0)
        {
            command = null;
            return false;
        }

        command = _commands.Dequeue();
        return true;
    }
}

public interface ISimulationCommand
{
    EntityId? TargetEntity { get; }
}

public interface IGroupSimulationCommand : ISimulationCommand
{
    IReadOnlyList<EntityId> Actors { get; }
}

public abstract class GroupSimulationCommand : IGroupSimulationCommand
{
    protected GroupSimulationCommand(IEnumerable<EntityId> actors)
    {
        var unique = new HashSet<EntityId>();
        var ordered = new List<EntityId>();
        foreach (var actor in actors)
        {
            if (unique.Add(actor)) ordered.Add(actor);
        }
        Actors = ordered;
    }

    public IReadOnlyList<EntityId> Actors { get; }
    public EntityId? TargetEntity => null;
}

/// <summary>§121: взять персонажа под ручное управление или вернуть ИИ.</summary>
public sealed class SetManualControlCommand : ISimulationCommand
{
    public SetManualControlCommand(EntityId npc, bool enabled)
    {
        Npc = npc;
        Enabled = enabled;
    }

    public EntityId Npc { get; }

    public bool Enabled { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: идти в точку. Точка, а не узел: клик игрока приходит по
/// поверхности мира, а ближайший узел — уже дело симуляции.
/// Run=false — одиночный клик, Run=true — двойной.</summary>
public sealed class MoveToCommand : ISimulationCommand
{
    public MoveToCommand(EntityId npc, Float2 worldPosition, bool run = false)
    {
        Npc = npc;
        WorldPosition = worldPosition;
        Run = run;
    }

    public EntityId Npc { get; }

    public Float2 WorldPosition { get; }

    public bool Run { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: подойти к объекту и сделать с ним ровно это. Тип
/// взаимодействия приходит ИЗ МЕНЮ, то есть из каталога объекта, — поэтому
/// одной цели PlayerOrder хватает на все глаголы сразу.</summary>
public sealed class InteractCommand : ISimulationCommand
{
    public InteractCommand(EntityId npc, ObjectId target, InteractionType interaction)
    {
        Npc = npc;
        Target = target;
        Interaction = interaction;
    }

    public EntityId Npc { get; }

    public ObjectId Target { get; }

    public InteractionType Interaction { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: бить человека.</summary>
public sealed class AttackNpcCommand : ISimulationCommand
{
    public AttackNpcCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§124: вручную поднять лежащего живого человека или свежее тело.</summary>
public sealed class CarryPersonCommand : ISimulationCommand
{
    public CarryPersonCommand(EntityId npc, EntityId target)
    {
        Npc = npc;
        Target = target;
    }

    public EntityId Npc { get; }

    public EntityId Target { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§124: положить переносимого человека у ног носильщика.</summary>
public sealed class PutDownPersonCommand : ISimulationCommand
{
    public PutDownPersonCommand(EntityId npc) => Npc = npc;

    public EntityId Npc { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: бить зверя. Мобы живут отдельным списком со своей
/// нумерацией, поэтому цель — int, а не EntityId.</summary>
public sealed class AttackMobCommand : ISimulationCommand
{
    public AttackMobCommand(EntityId npc, int mobId)
    {
        Npc = npc;
        MobId = mobId;
    }

    public EntityId Npc { get; }

    public int MobId { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§121: отставить. Отдельный глагол, а не «выключить ручной режим»:
/// в Kenshi это разные кнопки, и путаница между ними — известный источник
/// «персонаж не слушается» (тумблер не отменяет уже начатое действие).</summary>
public sealed class StopCommand : ISimulationCommand
{
    public StopCommand(EntityId npc)
    {
        Npc = npc;
    }

    public EntityId Npc { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§138: craft one authored item recipe through the normal plan and
/// execution pipeline. The goal is revalidated against RecipeCatalog when the
/// queue is drained; presentation cannot smuggle an arbitrary output.</summary>
public sealed class CraftItemCommand : ISimulationCommand
{
    public CraftItemCommand(EntityId npc, GoalType recipeGoal)
    {
        Npc = npc;
        RecipeGoal = recipeGoal;
    }

    public EntityId Npc { get; }

    public GoalType RecipeGoal { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§123: one target point, many independently placed actors.</summary>
public sealed class GroupMoveCommand : GroupSimulationCommand
{
    public GroupMoveCommand(
        IEnumerable<EntityId> actors, Float2 worldPosition, bool run = false)
        : base(actors)
    {
        WorldPosition = worldPosition;
        Run = run;
    }

    public Float2 WorldPosition { get; }

    public bool Run { get; }
}

public sealed class GroupStopCommand : GroupSimulationCommand
{
    public GroupStopCommand(IEnumerable<EntityId> actors) : base(actors) { }
}

public sealed class GroupAttackNpcCommand : GroupSimulationCommand
{
    public GroupAttackNpcCommand(IEnumerable<EntityId> actors, EntityId target)
        : base(actors) => Target = target;

    public EntityId Target { get; }
}

public sealed class GroupAttackMobCommand : GroupSimulationCommand
{
    public GroupAttackMobCommand(IEnumerable<EntityId> actors, int mobId)
        : base(actors) => MobId = mobId;

    public int MobId { get; }
}

public sealed class SetGroupManualControlCommand : GroupSimulationCommand
{
    public SetGroupManualControlCommand(IEnumerable<EntityId> actors, bool enabled)
        : base(actors) => Enabled = enabled;

    public bool Enabled { get; }
}

public enum InventoryItemSource
{
    Carried,
    Worn
}

public readonly struct InventoryItemRef
{
    public InventoryItemRef(InventoryItemSource source, int index, string expectedDefinitionId)
    {
        Source = source;
        Index = index;
        ExpectedDefinitionId = expectedDefinitionId ?? string.Empty;
    }

    public InventoryItemSource Source { get; }
    public int Index { get; }
    public string ExpectedDefinitionId { get; }
}

public enum InventoryAction
{
    Wear,
    Stow,
    Drop
}

public sealed class ManageInventoryCommand : ISimulationCommand
{
    public ManageInventoryCommand(EntityId npc, InventoryItemRef item, InventoryAction action)
    {
        Npc = npc;
        Item = item;
        Action = action;
    }

    public EntityId Npc { get; }
    public InventoryItemRef Item { get; }
    public InventoryAction Action { get; }
    public EntityId? TargetEntity => Npc;
}

public enum InventoryTransferDirection
{
    Take,
    Give
}

/// <summary>
/// §128: move one visible inventory cell between the selected colonist and an
/// unconscious person. The simulation resolves the physical instances again
/// after the approach, so a stale UI can never duplicate or delete an item.
/// </summary>
public sealed class TransferInventoryCommand : ISimulationCommand
{
    public TransferInventoryCommand(
        EntityId looter,
        EntityId other,
        InventoryItemRef item,
        int count,
        InventoryTransferDirection direction)
    {
        Looter = looter;
        Other = other;
        Item = item;
        Count = count > 0 ? count : 1;
        Direction = direction;
    }

    public EntityId Looter { get; }
    public EntityId Other { get; }
    public InventoryItemRef Item { get; }
    public int Count { get; }
    public InventoryTransferDirection Direction { get; }
    public EntityId? TargetEntity => Looter;
}

}
