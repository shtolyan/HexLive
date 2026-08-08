using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{

// §118: приказы игрока конкретному NPC. Очередь живёт на движке и опустошается
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

/// <summary>§118: взять персонажа под ручное управление или вернуть ИИ.</summary>
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

/// <summary>§118: идти в точку. Точка, а не узел: клик игрока приходит по
/// поверхности мира, а ближайший узел — уже дело симуляции.</summary>
public sealed class MoveToCommand : ISimulationCommand
{
    public MoveToCommand(EntityId npc, Float2 worldPosition)
    {
        Npc = npc;
        WorldPosition = worldPosition;
    }

    public EntityId Npc { get; }

    public Float2 WorldPosition { get; }

    public EntityId? TargetEntity => Npc;
}

/// <summary>§118: подойти к объекту и сделать с ним ровно это. Тип
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

/// <summary>§118: бить человека.</summary>
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

/// <summary>§118: бить зверя. Мобы живут отдельным списком со своей
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

/// <summary>§118: отставить. Отдельный глагол, а не «выключить ручной режим»:
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

}
