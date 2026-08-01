using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Runtime
{

// Spec §76: the learned half of a colonist. Skills grow ONLY here — every
// system that finishes a job calls Award, and this file owns both the verb→
// trade map and the growth curve. Keeping the map in one switch is the point:
// scattered "if goal == CraftAxe then Crafting += x" is how two verbs end up
// feeding two different trades for no reason anyone can reconstruct later.
internal static class SkillMath
{
    // Which trade a finished job counts towards. Discriminate on the
    // INTERACTION first and the GOAL second — cooking and crafting are both
    // InteractionType.Craft and are told apart only by the goal that planned
    // them (PlanningSystem), so a goal-only map would credit roasting meat to
    // the carpenter.
    //
    // null = this verb teaches nothing. Eating, sleeping, dressing, bathing and
    // hauling are things a body does, not a trade it practises.
    public static SkillKind? For(InteractionType type, GoalType goal)
    {
        switch (type)
        {
            case InteractionType.Harvest:
            case InteractionType.Process:
                return SkillKind.Harvesting;

            case InteractionType.Build:
            case InteractionType.BuildRaft:
                return SkillKind.Building;

            case InteractionType.Craft:
                // The one place the goal breaks the tie.
                return goal == GoalType.CookMeat ? SkillKind.Cooking : SkillKind.Crafting;

            case InteractionType.Butcher:
            case InteractionType.Fuel:
            case InteractionType.FillBottle:
                return SkillKind.Survival;

            case InteractionType.TreatSelf:
            case InteractionType.TreatOther:
            case InteractionType.MedicateOther:
                return SkillKind.Medicine;

            case InteractionType.Talk:
            case InteractionType.ConsoleOther:
                return SkillKind.Social;

            default:
                return null;
        }
    }

    // Award for a completed job. `durationTicks` is what the job ACTUALLY took
    // (after gear and skill sped it up), so getting better at a trade also
    // slows how fast you keep getting better — a small, welcome extra brake on
    // top of the diminishing-returns curve.
    //
    // Returns the trade ONLY when the award crossed a displayed level boundary,
    // so the caller (which has the world and can trace) fires ~10 SkillUp lines
    // per girl per soak instead of ~10000.
    public static SkillKind? Award(NPCState npc, InteractionType type, GoalType goal, int durationTicks)
    {
        if (durationTicks <= 0)
        {
            return null;
        }

        var kind = For(type, goal);
        if (!kind.HasValue)
        {
            return null;
        }

        return Grant(npc, kind.Value, Spec76.SkillXpPerWorkTick * durationTicks);
    }

    // A landed melee blow. Worth roughly ten ticks of ordinary labour: fights
    // are rare and short, and at the work rate Combat would never move at all.
    public static SkillKind? AwardHit(NPCState npc) =>
        Grant(npc, SkillKind.Combat, Spec76.SkillXpPerHit);

    // The growth curve. Two brakes and one accelerator:
    //  - Wits speeds learning (the one thing Смекалка does outside crafting);
    //  - (1 − skill)^exp means the last quarter of a trade costs about as much
    //    as the first three, so a colony ends with ONE master smith rather than
    //    three girls maxed at everything;
    //  - no decay at all: a trade once learned is not forgotten, only
    //    out-paced.
    //
    // Returns the trade when the gain crossed a displayed level boundary.
    private static SkillKind? Grant(NPCState npc, SkillKind kind, float baseGain)
    {
        if (!Spec76.Enabled || !Spec76.SkillsEnabled || baseGain <= 0f)
        {
            return null;
        }

        var current = MathUtil.Clamp01(npc.Skills.Get(kind));
        if (current >= 1f)
        {
            return null;
        }

        var wits = 1f + (npc.Attributes.Wits - Spec76.AttributeMean) * Spec76.SkillLearnWitsGain;
        if (wits <= 0f)
        {
            return null;
        }

        var gain = baseGain * wits *
            System.MathF.Pow(1f - current, Spec76.SkillDiminishExp);

        var next = MathUtil.Clamp01(current + gain);
        npc.Skills.Set(kind, next);

        return CrossedBand(current, next) ? kind : (SkillKind?)null;
    }

    // Did this award cross a displayed level boundary?
    private static bool CrossedBand(float before, float after)
    {
        if (Spec76.SkillTraceBand <= 0f)
        {
            return false;
        }

        return (int)(after / Spec76.SkillTraceBand) > (int)(before / Spec76.SkillTraceBand);
    }
}

}
