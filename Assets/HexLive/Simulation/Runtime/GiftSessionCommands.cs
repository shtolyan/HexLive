using HexLive.Simulation.Common;
namespace HexLive.Simulation.Runtime
{
public sealed class BeginGiftSessionCommand : ISimulationCommand
{
    public BeginGiftSessionCommand(EntityId npc, EntityId recipient) { Npc = npc; Recipient = recipient; }
    public EntityId Npc { get; }
    public EntityId Recipient { get; }
    public EntityId? TargetEntity => Npc;
}
public sealed class EndGiftSessionCommand : ISimulationCommand
{
    public EndGiftSessionCommand(EntityId npc, EntityId recipient) { Npc = npc; Recipient = recipient; }
    public EntityId Npc { get; }
    public EntityId Recipient { get; }
    public EntityId? TargetEntity => Npc;
}
}
