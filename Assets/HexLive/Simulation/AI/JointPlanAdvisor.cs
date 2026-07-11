using HexLive.Simulation.Core;

namespace HexLive.Simulation.AI
{
    // Spec 40.16: when the colony hits dire straits, a joint-plan advisor may
    // suggest a high-level cooperative strategy the hand-written AI can't reach
    // (v2 wires this to an LLM). v1 formalizes only the TRIGGER + I/O contract:
    // the advisor is a null-object, so the seam is inert — no behaviour change —
    // until a host swaps in a real advisor.

    // What the advisor is told: a compact read of the colony's crisis.
    public sealed class DireStraitsContext
    {
        // Plain get/set (not init) — Unity's netstandard2.1 lacks IsExternalInit.
        public int StarvingCount { get; set; }
        public int WoundedCount { get; set; }
        public int LivingCount { get; set; }
    }

    // The advisor contract. Advise returns a high-level suggestion, or null for
    // "no advice". A real implementation (LLM-backed) lives outside the sim.
    public interface IJointPlanAdvisor
    {
        string Advise(DireStraitsContext context);
    }

    // Default: no advice. Keeps the seam inert until a host installs a real one.
    public sealed class NullJointPlanAdvisor : IJointPlanAdvisor
    {
        public string Advise(DireStraitsContext context) => null;
    }

    public static class DireStraits
    {
        // The swappable seam. A host (or a test) sets this to an LLM-backed
        // advisor; the default is inert.
        public static IJointPlanAdvisor Advisor { get; set; } = new NullJointPlanAdvisor();

        // Dire straits = at least half the living colony (and >= 2 of them) is
        // starving/parched or badly wounded at once. Pure detection: returns the
        // context when the colony is in crisis, else null. No side effects — the
        // caller owns the rising-edge trace and the advisor consult.
        public static DireStraitsContext Assess(WorldState world)
        {
            var living = 0;
            var starving = 0;
            var wounded = 0;
            foreach (var npc in world.Entities.Npcs.Values)
            {
                if (npc.Health <= 0f)
                {
                    continue;
                }

                living++;
                if (npc.Needs.Hunger >= 0.85f || npc.Needs.Thirst >= 0.85f)
                {
                    starving++;
                }

                if (npc.Health < 0.4f)
                {
                    wounded++;
                }
            }

            var afflicted = starving + wounded;
            var dire = living > 0 && afflicted >= 2 && afflicted * 2 >= living;
            return dire
                ? new DireStraitsContext
                {
                    StarvingCount = starving,
                    WoundedCount = wounded,
                    LivingCount = living
                }
                : null;
        }
    }
}
