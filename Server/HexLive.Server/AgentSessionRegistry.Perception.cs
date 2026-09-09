using HexLive.Simulation.AI;

namespace HexLive.Server;

public sealed partial class AgentSessionRegistry
{
    // Resolve before taking the world gate: existing Sweep(host) takes registry
    // then world. Never introduce the reverse order in describe/attach.
    public PerceptionObservationBuffer? BindPerception(string attachmentId, string owner, int generation)
    {
        lock (_gate)
        {
            SweepLocked(null, generation);
            if (!TryOwned(attachmentId, owner, generation, out var attachment, out _)) return null;
            return attachment.Perception ??= new PerceptionObservationBuffer();
        }
    }

    public PerceptionObservationBuffer? GetPerception(int npcId, string owner, int generation)
    {
        lock (_gate)
        {
            SweepLocked(null, generation);
            if (!_attachmentByNpc.TryGetValue(npcId, out var id) ||
                !TryOwned(id, owner, generation, out var attachment, out _)) return null;
            return attachment.Perception;
        }
    }
}
