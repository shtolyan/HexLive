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

// Spec 31A.5A/31A.5B: equipment values derive from the worn list —
// warmth stacks across layers (sum), armor is per covered part (max).
// Spec 40.8B: wounds as first-class records — creation & healing constants.
// Only landed bites/hits call Inflict; starvation, heat, sunburn and sickness
// drain HP without ever creating a wound (no phantom decals while starving).
internal static class WoundMath
{
    // Full close in ~2 game days (2 x 150 slow ticks) at neutral pace;
    // sleeping doubles it, marching halves it.
    public static float HealPerSlowTick => SimBalance.HealPerSlowTick;

    // Raised 12 → 36 alongside multi-gash hits (one bite files three
    // records): at low HP the body should read MAULED all over — a dozen
    // bites' worth of marks before the reopen path freezes the count.
    private static int MaxWounds => SimBalance.MaxWounds;

    // Spec 40.8-E: a landed bite tears SEVERAL gashes, not one — each hit
    // splits into this many records (same zone, distinct seeds → distinct
    // painted marks). The DAMAGE is split too, so total hostage HP, healing
    // duration and the dog balance stay exactly as before; only the visual
    // density changes.
    private static int GashesPerHit => SimBalance.GashesPerHit;

    // Hits below this don't split — three sub-0.03 records are invisible
    // clutter that burns the cap for nothing.
    private static float MinSplittableDamage => SimBalance.MinSplittableDamage;

    // HP still held hostage by open wounds in a zone: Σ severity·(1−heal).
    // Generic fed-regen may not raise the zone above 1 − this value; the HP
    // returns only as each wound closes.
    public static float OpenWoundDamage(NPCState npc, BodyPart zone)
    {
        var held = 0f;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == zone)
            {
                held += wound.Severity * (1f - wound.Heal01);
            }
        }

        return held;
    }

    public static void Inflict(WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        foreach (var garment in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(garment.DefinitionId, out var definition) &&
                definition.Covers.Contains(zone))
            {
                var bloodContamination = damage * 1.5f;
                garment.Bloodiness = MathUtil.Clamp01(garment.Bloodiness + bloodContamination);
                garment.Dirtiness = MathUtil.Clamp01(garment.Dirtiness + bloodContamination);
            }
        }

        var pieces = damage < MinSplittableDamage ? 1 : GashesPerHit;
        var share = damage / pieces;
        for (var i = 0; i < pieces; i++)
        {
            InflictOne(world, npc, zone, share);
        }
    }

    private static void InflictOne(WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        // At the cap the next bite never EVICTS (dropping a record would
        // strand its hostage HP forever — the zone could stick at 0). It
        // REOPENS an existing wound instead: same-zone if possible (the bite
        // tears the old scar deeper — same spot, same decal, fade resets),
        // else the most-healed wound anywhere hands its held HP back to its
        // own zone and the record is repurposed for the new hit.
        if (npc.Wounds.Count >= MaxWounds)
        {
            WoundState reuse = null;
            foreach (var wound in npc.Wounds)
            {
                if (wound.Zone == zone && (reuse == null || wound.Heal01 > reuse.Heal01))
                {
                    reuse = wound;
                }
            }

            if (reuse != null)
            {
                // Deepen: the combined hostage = what it still held + new hit.
                reuse.Severity = reuse.Severity * (1f - reuse.Heal01) + damage;
                reuse.Heal01 = 0f;
            }
            else
            {
                foreach (var wound in npc.Wounds)
                {
                    if (reuse == null || wound.Heal01 > reuse.Heal01)
                    {
                        reuse = wound;
                    }
                }

                // Close the donor instantly: return its held HP to ITS zone,
                // then repurpose the record as a fresh wound at the new spot.
                // §50: a severed donor zone keeps its HP at 0 — it's gone.
                if (!npc.Body.IsSevered(reuse.Zone))
                {
                    npc.Body.Parts[reuse.Zone] = MathUtil.Clamp01(
                        npc.Body.Parts[reuse.Zone] + reuse.Severity * (1f - reuse.Heal01));
                }
                reuse.Zone = zone;
                reuse.Severity = damage;
                reuse.Heal01 = 0f;
                reuse.Id = npc.NextWoundId++;
                reuse.Seed = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 911 + npc.NextWoundId) * int.MaxValue);
            }

            Trace.Emit(world, npc.Id, "WoundInflicted",
                $"{zone} damage={damage:F2} wounds={npc.Wounds.Count} (reopened #{reuse.Id})");
            return;
        }

        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = zone,
            Severity = damage,
            Heal01 = 0f,
            // Deterministic per (seed, tick, npc, wound#): the decal's spot and
            // look replay identically after a save-restore (spec 41.2 replays
            // the same seed to the same tick).
            Seed = (int)(MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 911 + npc.NextWoundId) * int.MaxValue)
        });

        Trace.Emit(world, npc.Id, "WoundInflicted",
            $"{zone} damage={damage:F2} wounds={npc.Wounds.Count}");
    }
}

}
