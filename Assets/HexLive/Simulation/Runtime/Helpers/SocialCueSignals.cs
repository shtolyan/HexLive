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
    // §80: peerId стал НУЛЕВЫМ. Вид рисует по нему лицо того, о ком кьюшка, а
    // «о ком» есть не всегда: зверь — не NPC, и его id живёт в другом
    // пространстве, где 3 значит волка, а не Марту. Раньше такие места
    // подставляли id самого кричащего, и это читалось бы как «боится себя».
    // null — честное «человека тут нет», и вид падает на прежнюю иконку.
    public static void Stamp(WorldState world, NPCState npc, string kind, EntityId? peerId)
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
        npc.Execution.LastSocialCueItemId = string.Empty;
    }

    // Bug #52: content payload for a silent action bubble. A normal Stamp
    // clears it above, so a later alarm can never inherit an old loot icon.
    public static void StampItem(WorldState world, NPCState npc, string kind, string itemId)
    {
        if (npc.IsUnconscious(world.Tick) ||
            npc.Execution.CurrentInteraction == InteractionType.Sleep)
        {
            return;
        }

        npc.Execution.LastSocialCueTick = world.Tick;
        npc.Execution.LastSocialCueKind = kind;
        npc.Execution.LastSocialCuePeerId = null;
        npc.Execution.LastSocialCueItemId = itemId ?? string.Empty;
    }
}

}
