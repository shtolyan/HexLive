using HexLive.Simulation.AI;
using HexLive.Simulation.Wire;

namespace HexLive.Server;

public sealed partial class AgentSessionRegistry
{
    // Resolve before world.Read, preserving registry -> world lock order.
    public ExternalNpcControl? BindControl(string attachmentId, string owner, int generation)
    {
        lock (_gate)
        {
            SweepLocked(null, generation);
            if (!TryOwned(attachmentId, owner, generation, out var attachment, out _)) return null;
            if ((attachment.Capabilities & AgentCapabilities.WorldActions) == 0) return null;
            return attachment.Control ??= new ExternalNpcControl();
        }
    }
}
