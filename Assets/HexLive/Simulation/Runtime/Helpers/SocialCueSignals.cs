using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

internal static class SocialCueSignals
{
    public static void Stamp(WorldState world, NPCState npc, string kind, EntityId peerId)
    {
        // §60: no cue bubbles over a body that cannot react — a sleeping,
        // fainted or comatose girl shows no emoji at all (she ignores every
        // request; the single funnel here silences every stamp source).
        if (npc.IsUnconscious(world.Tick) ||
            npc.Execution.CurrentInteraction == InteractionType.Sleep)
        {
            return;
        }

        npc.Execution.LastSocialCueTick = world.Tick;
        npc.Execution.LastSocialCueKind = kind;
        npc.Execution.LastSocialCuePeerId = peerId;
    }
}

}
