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
    // Presentation-only age for a fully clotted but still unhealed cut. It
    // stays visible as a dry mark while losing the wet/fresh reaction cues;
    // authoritative Heal01 remains unchanged until real recovery advances it.
    internal const float ClottedVisualHealFloor = 0.65f;

    // Full close in 300 slow ticks (= 4800 ticks = 20 real minutes) at
    // neutral pace; sleeping doubles it, marching halves it.
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

    // §40.8-H r10: вода смывает кровяную подложку со всех зон. Единственный
    // путь вниз для BloodSoil — вызывается водяным тиком NeedsDecaySystem и
    // купанием (Bathe обнуляет через amount >= 1).
    public static void WashBloodSoil(NPCState npc, float amount)
    {
        if (amount <= 0f)
        {
            return;
        }

        foreach (var condition in npc.Body.Conditions.Values)
        {
            if (condition.BloodSoil > 0f)
            {
                condition.BloodSoil = MathUtil.Clamp01(condition.BloodSoil - amount);
            }
        }
    }

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

    public static float OpenCutDamage(NPCState npc, BodyPart zone)
    {
        var open = 0f;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == zone)
            {
                open += wound.Severity * (1f - wound.Heal01);
            }
        }

        return open;
    }

    public static bool NeedsAftercare(NPCState npc)
    {
        foreach (var wound in npc.Wounds)
        {
            // A clotted stump scars naturally (§118), so spending a dressing
            // on it after the bleeding window has closed would be wasteful.
            var naturallyClosingStump = npc.Body.IsSevered(wound.Zone) &&
                wound.Clot01 >= 1f;
            if (!wound.Stabilized && !naturallyClosingStump &&
                wound.Heal01 < 1f && wound.Severity > 0f)
            {
                return true;
            }
        }

        return false;
    }

    public static float UnstabilizedCutBurden(NPCState npc)
    {
        var burden = 0f;
        foreach (var wound in npc.Wounds)
        {
            var naturallyClosingStump = npc.Body.IsSevered(wound.Zone) &&
                wound.Clot01 >= 1f;
            if (!wound.Stabilized && !naturallyClosingStump && wound.Heal01 < 1f)
            {
                burden += wound.Severity * (1f - wound.Heal01) * wound.BleedFactor;
            }
        }

        return MathUtil.Clamp01(burden);
    }

    internal static float VisualHeal01(WoundState wound) =>
        MathUtil.Clamp01(System.Math.Max(
            wound.Heal01,
            wound.Clot01 * ClottedVisualHealFloor));

    public static int BandageTicks(NPCState healer)
    {
        var medicine = Spec76.Enabled && Spec76.SkillsEnabled
            ? MathUtil.Clamp01(healer.Skills.Medicine)
            : 0f;
        return System.Math.Max(1, (int)System.Math.Round(
            Spec118.BandageTicksNovice +
            (Spec118.BandageTicksExpert - Spec118.BandageTicksNovice) * medicine));
    }

    public static bool StabilizeMostDangerous(
        NPCState npc, bool herbal, out WoundState stabilized)
    {
        stabilized = null;
        var danger = 0f;
        var choseActiveBleed = false;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Stabilized || wound.Heal01 >= 1f)
            {
                continue;
            }

            // Active hemorrhage always wins. If every wound is already dry,
            // choose the deepest remaining cut instead of the first list item
            // (the old (1-clot) score made every clotted wound tie at zero).
            var openDanger = wound.Severity * (1f - wound.Heal01) * wound.BleedFactor;
            var activeBleed = wound.Clot01 < 1f;
            var score = activeBleed ? openDanger * (1f - wound.Clot01) : openDanger;
            if (stabilized == null ||
                (activeBleed && !choseActiveBleed) ||
                (activeBleed == choseActiveBleed && score > danger))
            {
                stabilized = wound;
                danger = score;
                choseActiveBleed = activeBleed;
            }
        }

        if (stabilized == null)
        {
            return false;
        }

        stabilized.Stabilized = true;
        stabilized.Clot01 = 1f;
        if (herbal)
        {
            npc.BandagedZones.Add(stabilized.Zone);
            npc.GauzeZones.Remove(stabilized.Zone);
        }
        else
        {
            npc.GauzeZones.Add(stabilized.Zone);
            npc.BandagedZones.Remove(stabilized.Zone);
        }

        return true;
    }

    public static void RegisterLandedHit(
        WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        AttributeMath.Train(npc, AttributeKind.Toughness,
            Spec76.AttributeTrainPerDamage * damage);

        if (npc.Mind.ComaCause == AI.ComaCause.Exhaustion)
        {
            NeedsDecaySystem.WakeFromComa(world, npc, $"Pain ({zone})");
        }

        if (world.Tick < npc.Mind.CryingUntilTick)
        {
            LyingSpot.EndCrying(world, npc);
        }

        foreach (var garment in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(garment.DefinitionId, out var definition) &&
                definition.Covers.Contains(zone))
            {
                var contamination = damage * 1.5f;
                garment.Bloodiness = MathUtil.Clamp01(garment.Bloodiness + contamination);
                garment.Dirtiness = MathUtil.Clamp01(garment.Dirtiness + contamination);
            }
        }
    }

    public static void Inflict(WorldState world, NPCState npc, BodyPart zone, float damage)
    {
        RegisterLandedHit(world, npc, zone, damage);
        InflictCut(world, npc, zone, damage, 1f);
    }

    public static void InflictCut(
        WorldState world, NPCState npc, BodyPart zone, float damage, float bleedFactor)
    {
        if (damage <= 0f)
        {
            return;
        }

        // §40.8-H r10: рана пачкает кожу кровью. Накопительная величина —
        // заживление её не убирает, только вода (WashBloodSoil).
        var condition = npc.Body.Condition(zone);
        condition.BloodSoil = MathUtil.Clamp01(
            condition.BloodSoil + damage * SimBalance.BloodSoilPerCut);

        // §118 wounds are medical records: one landed cutting event is one
        // wound that one dressing can stabilize. The older visual-only model
        // split a bite into three independent records, which accidentally made
        // a single hit consume three bandages once records gained clot state.
        var pieces = Spec118.Enabled
            ? 1
            : damage < MinSplittableDamage ? 1 : GashesPerHit;
        var share = damage / pieces;
        for (var i = 0; i < pieces; i++)
        {
            InflictOne(world, npc, zone, share, bleedFactor);
        }
    }

    private static void InflictOne(
        WorldState world, NPCState npc, BodyPart zone, float damage, float bleedFactor)
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
                reuse.Clot01 = 0f;
                reuse.Stabilized = false;
                reuse.BleedFactor = bleedFactor;
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
                reuse.Clot01 = 0f;
                reuse.Stabilized = false;
                reuse.BleedFactor = bleedFactor;
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
            Clot01 = 0f,
            Stabilized = false,
            BleedFactor = bleedFactor,
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
