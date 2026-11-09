using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{
public sealed class RequestTravelPartyCommand : ISimulationCommand
{
    public RequestTravelPartyCommand(EntityId npc, EntityId target) { Npc = npc; Target = target; }
    public EntityId Npc { get; }
    public EntityId Target { get; }
    public EntityId? TargetEntity => Npc;
}
public sealed class ReleaseTravelPartyCommand : ISimulationCommand
{
    public ReleaseTravelPartyCommand(EntityId npc, EntityId target) { Npc = npc; Target = target; }
    public EntityId Npc { get; }
    public EntityId Target { get; }
    public EntityId? TargetEntity => Npc;
}
}
