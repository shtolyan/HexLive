using HexLive.Simulation.Common;
namespace HexLive.Simulation.Runtime
{
public sealed class KnockDoorCommand : ISimulationCommand
{
    public KnockDoorCommand(EntityId npc, ObjectId door) { Npc = npc; Door = door; }
    public EntityId Npc { get; }
    public ObjectId Door { get; }
    public EntityId? TargetEntity => Npc;
}
public sealed class BreakDoorCommand : ISimulationCommand
{
    public BreakDoorCommand(EntityId npc, ObjectId door) { Npc = npc; Door = door; }
    public EntityId Npc { get; }
    public ObjectId Door { get; }
    public EntityId? TargetEntity => Npc;
}
}
