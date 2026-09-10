using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;

namespace HexLive.Server;

/// <summary>Shared cleanup for explicit release, session close and lease expiry.
/// An attachment suppresses AI independently of its currently leased action.</summary>
internal static class AgentControlActions
{
    internal static ManualCommandAdmission Release(WorldHost host, int npcId)
    {
        var external = host.Read(world =>
            world.Entities.Npcs.TryGetValue(new EntityId(npcId), out var npc)
                ? ExternalNpcControl.TryReleaseAction(world, npc)
                : null);
        return external ?? host.SubmitManualCommand(
            new SetManualControlCommand(new EntityId(npcId), false));
    }
}
