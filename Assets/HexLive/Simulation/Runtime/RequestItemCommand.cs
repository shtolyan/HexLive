using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

/// <summary>§153.4: ask a conscious owner for one carried item at hand-over
/// distance. Consent and the physical transfer resolve in the same world tick.</summary>
public sealed class RequestItemCommand : ISimulationCommand
{
    public RequestItemCommand(EntityId npc, EntityId target, string definitionId)
    {
        Npc = npc;
        Target = target;
        DefinitionId = definitionId ?? string.Empty;
    }

    public EntityId Npc { get; }
    public EntityId Target { get; }
    public string DefinitionId { get; }
    public EntityId? TargetEntity => Npc;
}

}
