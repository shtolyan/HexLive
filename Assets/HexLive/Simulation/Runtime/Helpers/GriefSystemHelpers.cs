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

// Spec 28.15C: grief mechanics shared by the death handler (witnessing)
// and the decision pass (discovery).
internal static class GriefSystemHelpers
{
    public static void TriggerGrief(WorldState world, NPCState npc, WorldObjectState corpse)
    {
        if (npc.Mind.GrievedCorpses.Contains(corpse.Id))
        {
            return;
        }

        npc.Mind.GrievedCorpses.Add(corpse.Id);

        var affinity = corpse.CurrentUser is { } deadId
            ? npc.Social.GetOrCreate(deadId).Affinity
            : 0f;
        var socialLoss = System.Math.Max(0.15f, 0.3f + 0.3f * affinity);
        npc.Needs.Social = MathUtil.Clamp01(npc.Needs.Social - socialLoss);
        npc.Needs.Comfort = MathUtil.Clamp01(npc.Needs.Comfort - 0.2f);
        npc.Mind.GrievingUntilTick = world.Tick + 2400;

        // The death site is frightening (spec 29C.4A reuse).
        var alreadyRemembered = false;
        foreach (var danger in npc.Memory.Dangers)
        {
            if (danger.Tile == corpse.Tile)
            {
                danger.Tick = world.Tick;
                alreadyRemembered = true;
                break;
            }
        }

        if (!alreadyRemembered)
        {
            npc.Memory.Dangers.Add(new Memory.DangerMemory { Tile = corpse.Tile, Tick = world.Tick });
            if (npc.Memory.Dangers.Count > 8)
            {
                npc.Memory.Dangers.RemoveAt(0);
            }
        }

        // A witness knows where they fell (spatial memory, 27.18A) —
        // otherwise Mourn could never be planned.
        if (!npc.Memory.KnownObjects.ContainsKey(corpse.Id))
        {
            npc.Memory.KnownObjects[corpse.Id] = new Memory.ObjectMemory
            {
                Id = corpse.Id,
                DefinitionId = corpse.DefinitionId,
                Tile = corpse.Tile,
                Junction = corpse.Junctions.Count > 0 ? corpse.Junctions[0] : null,
                LastSeenTick = world.Tick
            };
        }

        Trace.Emit(world, npc.Id, "Grieving",
            $"For NPC{corpse.CurrentUser?.Value.ToString() ?? "?"} " +
            $"(Affinity={affinity:F2} SocialLoss={socialLoss:F2}) " +
            $"Mourning until tick {npc.Mind.GrievingUntilTick}");
    }
}

}
