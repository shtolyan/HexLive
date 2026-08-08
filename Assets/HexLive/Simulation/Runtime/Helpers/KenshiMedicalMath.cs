using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§116 slow-tick wound progression and recovery.</summary>
internal static class KenshiMedicalMath
{
    private static readonly BodyPart[] Parts =
    {
        BodyPart.Head, BodyPart.Torso, BodyPart.Pelvis,
        BodyPart.ArmL, BodyPart.ArmR, BodyPart.LegL, BodyPart.LegR
    };

    internal static void Tick(WorldState world, NPCState npc)
    {
        // Health==0 is the terminal latch until MobSystem moves the NPC to
        // Corpses. No recovery pass is allowed to reconstruct it from Body.Mean.
        if (npc.Health <= 0f)
        {
            return;
        }

        BodyDamageResolver.DecayAllHitBias(world, npc);
        var toughness = MathUtil.Clamp01(npc.Attributes.Toughness);
        RestFactors(world, npc, out var healMultiplier, out var degenerationMultiplier);

        var clotGain = Lerp(Spec118.ClotPerSlowTickLowToughness,
            Spec118.ClotPerSlowTickHighToughness, toughness);
        var bleedToughness = Lerp(1.3f, 0.7f, toughness);
        var totalBleed = 0f;
        foreach (var wound in npc.Wounds)
        {
            var openCut = wound.Severity * (1f - wound.Heal01);
            if (!wound.Stabilized && wound.Clot01 < 1f && openCut > 0f)
            {
                totalBleed += openCut * wound.BleedFactor * (1f - wound.Clot01) *
                    Spec118.SteadyBleedPerSlowTick * bleedToughness;
            }

            wound.Clot01 = wound.Stabilized
                ? 1f
                : MathUtil.Clamp01(wound.Clot01 + clotGain);
        }

        if (totalBleed > 0f)
        {
            BodyDamageResolver.DrainBlood(npc, totalBleed);
            Trace.Emit(world, npc.Id, "Bleeding",
                $"Loss={totalBleed:F4} Blood={npc.Needs.Blood:F3} " +
                $"Deficit={npc.Body.BloodDeficit:F3}");
        }

        foreach (var part in Parts)
        {
            TickDegeneration(world, npc, part, toughness, degenerationMultiplier);
            if (npc.Health <= 0f)
            {
                // Bug #54: fatal degeneration used to fall through into blunt/
                // cut recovery and the final medical-progression recompute,
                // resurrecting the body inside this same slow tick.
                return;
            }

            TickBluntRecovery(npc, part, healMultiplier);
            TickStabilizedCutRecovery(world, npc, part, healMultiplier);
        }

        if (npc.Needs.Hunger < SimBalance.HealHungerGate &&
            (npc.Needs.Blood < 1f || npc.Body.BloodDeficit > 0f))
        {
            BodyDamageResolver.RestoreBlood(npc,
                SimBalance.BloodRefillPerTick * healMultiplier);
        }

        npc.Health = npc.IsDying
            ? System.Math.Max(npc.Body.Mean(), Spec105.BodyFloor)
            : npc.Body.Mean();
        MortalityHelpers.ResolveTrauma(world, npc, 0f, "medical progression");
    }

    private static void TickDegeneration(
        WorldState world, NPCState npc, BodyPart part, float toughness, float restMultiplier)
    {
        if (npc.Body.IsSevered(part))
        {
            return;
        }

        var openCut = 0f;
        foreach (var wound in npc.Wounds)
        {
            if (wound.Zone == part && !wound.Stabilized)
            {
                openCut += wound.Severity * (1f - wound.Heal01);
            }
        }

        if (openCut <= Spec118.DegenerationCutThreshold)
        {
            return;
        }

        var steps = System.Math.Max(0f,
            (openCut - Spec118.DegenerationCutThreshold) / Spec118.DegenerationStep);
        var amount = steps * Spec118.DegenerationPerStep *
            Lerp(1.7f, 0.03f, toughness) * restMultiplier;
        BodyDamageResolver.ApplyCutDegeneration(world, npc, part, amount);
    }

    private static void TickBluntRecovery(NPCState npc, BodyPart part, float restMultiplier)
    {
        var condition = npc.Body.Condition(part);
        if (condition.SplintSupport > 0f && npc.Body.Parts[part] >= condition.SplintSupport)
        {
            condition.SplintSupport = 0f;
        }
        if (condition.BluntDamage <= 0f)
        {
            return;
        }

        var amount = System.Math.Min(condition.BluntDamage,
            Spec118.BluntRecoveryPerSlowTick * restMultiplier);
        condition.BluntDamage -= amount;
        BodyDamageResolver.RestorePart(npc, part, amount);
    }

    private static void TickStabilizedCutRecovery(
        WorldState world, NPCState npc, BodyPart part, float restMultiplier)
    {
        var budget = Spec118.CutRecoveryPerSlowTick * restMultiplier;
        for (var i = npc.Wounds.Count - 1; i >= 0 && budget > 0f; i--)
        {
            var wound = npc.Wounds[i];
            if (wound.Zone != part || !wound.Stabilized || wound.Heal01 >= 1f)
            {
                continue;
            }

            var open = wound.Severity * (1f - wound.Heal01);
            var amount = System.Math.Min(open, budget);
            wound.Heal01 = MathUtil.Clamp01(wound.Heal01 + amount / wound.Severity);
            BodyDamageResolver.RestorePart(npc, part, amount);
            budget -= amount;

            if (wound.Heal01 >= 1f)
            {
                Trace.Emit(world, npc.Id, "WoundHealed",
                    $"{part} wound #{wound.Id} closed");
                npc.Wounds.RemoveAt(i);
            }
        }

        if (WoundMath.OpenCutDamage(npc, part) <= 0f)
        {
            npc.BandagedZones.Remove(part);
            npc.GauzeZones.Remove(part);
        }
    }

    private static void RestFactors(
        WorldState world, NPCState npc, out float heal, out float degeneration)
    {
        heal = 1f;
        degeneration = 1f;
        var lying = npc.Execution.CurrentInteraction == InteractionType.Sleep ||
            npc.IsUnconscious(world.Tick);
        if (!lying)
        {
            return;
        }

        heal = Spec118.GroundRestHealMultiplier;
        if (npc.Execution.TargetObject is not { } objectId ||
            !world.Entities.Objects.TryGetValue(objectId, out var bed))
        {
            return;
        }

        if (bed.DefinitionId == ContentIds.BedLeaf)
        {
            heal = Spec118.LeafBedHealMultiplier;
            degeneration = Spec118.LeafBedDegenerationMultiplier;
        }
        else if (bed.DefinitionId == ContentIds.BedBasic)
        {
            heal = Spec118.BasicBedHealMultiplier;
            degeneration = Spec118.BasicBedDegenerationMultiplier;
        }
    }

    private static float Lerp(float from, float to, float value) =>
        from + (to - from) * MathUtil.Clamp01(value);
}

}
