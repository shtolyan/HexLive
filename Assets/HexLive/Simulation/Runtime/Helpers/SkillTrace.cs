using HexLive.Simulation.Core;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;

namespace HexLive.Simulation.Runtime
{

// Spec §76: where finished work becomes a learned trade, and the ONLY place
// that emits the SkillUp trace. SkillMath owns the curve and the verb→trade
// map; this owns the world-facing half (the trace, and the "which goal was
// she on" lookup), so the completion sites scattered across ExecutionSystem,
// MeleeSwing and AnimalCombatSystem all read as one line.
internal static class SkillTrace
{
    // Credit a finished job. `durationTicks` is the ACTUAL run length, so a
    // shorter job teaches less — the diminishing-returns curve and the growing
    // speed brake each other, which is why a colony converges on one master
    // rather than three girls maxed at everything.
    public static void Award(WorldState world, NPCState npc, InteractionType type, int durationTicks)
    {
        Emit(world, npc, SkillMath.Award(npc, type, npc.Plan.Goal, durationTicks));
        // §76.13: the same job that taught her the trade also conditioned the
        // body it leans on — heavy work builds Strength, fiddly work Wits.
        AttributeMath.TrainFromWork(npc, type, npc.Plan.Goal, durationTicks);
    }

    // A landed blow. Separate entry because combat has no interaction and no
    // duration — the hit itself IS the unit of practice.
    public static void AwardHit(WorldState world, NPCState npc)
    {
        Emit(world, npc, SkillMath.AwardHit(npc));
        // §76.13: connecting is footwork before it is muscle — a landed blow
        // trains Agility. Strength is built by the day's labour, not by fights,
        // which are far too rare to condition anything.
        AttributeMath.Train(npc, AttributeKind.Agility, Spec76.AttributeTrainPerHit);
    }

    // Fires ONLY on a band crossing, which SkillMath decides. An award happens
    // on every finished job; a 240k-tick soak would otherwise bury every other
    // event under ~10000 SkillUp lines per girl.
    //
    // NOTE for the harness: "SkillUp" must be on the interesting-event
    // whitelist, or this metric silently reads zero.
    private static void Emit(WorldState world, NPCState npc, SkillKind? levelled)
    {
        if (levelled is not { } kind)
        {
            return;
        }

        Trace.Emit(world, npc.Id, "SkillUp",
            $"{kind}={npc.Skills.Get(kind):F3} Wits={npc.Attributes.Wits:F2}");
    }
}

}
