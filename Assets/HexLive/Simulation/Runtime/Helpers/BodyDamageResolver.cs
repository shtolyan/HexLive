using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public readonly struct DamageProfile
{
    public DamageProfile(float cutFraction, float bloodLossMultiplier)
    {
        CutFraction = MathUtil.Clamp01(cutFraction);
        BloodLossMultiplier = System.Math.Max(0f, bloodLossMultiplier);
    }

    public float CutFraction { get; }
    public float BloodLossMultiplier { get; }

    public static DamageProfile ForGear(string id)
    {
        var stats = GearCatalog.For(id ?? string.Empty);
        return new DamageProfile(stats.CutFraction, stats.BloodLossMultiplier);
    }

    public static DamageProfile ForMob(string id)
    {
        var stats = MobCatalog.For(id);
        return new DamageProfile(stats.CutFraction, stats.BloodLossMultiplier);
    }
}

public readonly struct BodyDamageResult
{
    public BodyDamageResult(float landed, float cut, float blunt, bool hitProsthetic)
    {
        Landed = landed;
        Cut = cut;
        Blunt = blunt;
        HitProsthetic = hitProsthetic;
    }

    public float Landed { get; }
    public float Cut { get; }
    public float Blunt { get; }
    public bool HitProsthetic { get; }
}

/// <summary>
/// §116: the single organic-damage pipeline. Combat callers may preview armor
/// for mercy, but every landed blow reaches this class exactly once.
/// </summary>
public static class BodyDamageResolver
{
    public static float PreviewLanded(
        WorldState world, NPCState target, BodyPart part, float rawDamage) =>
        EquipmentMath.Mitigate(world, target, part, rawDamage);

    public static BodyDamageResult Apply(
        WorldState world, NPCState target, BodyPart part, float rawDamage,
        in DamageProfile profile, string source, bool useArmor = true)
    {
        var landed = useArmor
            ? EquipmentMath.Mitigate(world, target, part, rawDamage)
            : System.Math.Max(0f, rawDamage * AttributeMath.IncomingDamageMult(target));
        return ApplyLanded(world, target, part, landed, profile, source);
    }

    public static BodyDamageResult ApplyLanded(
        WorldState world, NPCState target, BodyPart part, float landed,
        in DamageProfile profile, string source)
    {
        landed = System.Math.Max(0f, landed);
        if (landed <= 0f || target.Health <= 0f)
        {
            return new BodyDamageResult(0f, 0f, 0f, false);
        }

        var condition = target.Body.Condition(part);
        DecayHitBias(world, condition);
        condition.HitBias = System.Math.Min(
            Spec118.HitBiasMax, condition.HitBias + Spec118.HitBiasGain);
        condition.HitBiasChangedTick = world.Tick;

        // A prosthetic occupies the severed zone. It takes condition damage,
        // never files an organic wound and never drains blood.
        if (target.Body.IsSevered(part) && condition.Prosthetic is { } device)
        {
            device.Condition = System.Math.Max(0f, device.Condition - landed);
            if (device.Condition <= 0f)
            {
                var id = device.DefinitionId;
                condition.Prosthetic = null;
                EquipmentMath.RecalculateCapacity(world, target);
                InventoryMath.SpillOverflow(world, target);
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, target.Id, "ProstheticBroken", $"{part} {id}");

                }
            }

            target.Health = target.Body.Mean();
            return new BodyDamageResult(landed, 0f, landed, true);
        }

        WoundMath.RegisterLandedHit(world, target, part, landed);
        DamageReactionSystemHelpers.GrantAdrenaline(world, target, landed, source);

        var cutFraction = Spec118.Enabled && Spec118.DamageTypesEnabled
            ? profile.CutFraction
            : 1f;
        var cut = landed * cutFraction;
        var blunt = landed - cut;

        var partHp = target.Body.Parts[part];
        var intoPositive = System.Math.Min(partHp, landed);
        target.Body.Parts[part] = System.Math.Max(0f, partHp - intoPositive);
        var overkill = landed - intoPositive;
        if (overkill > 0f)
        {
            condition.CriticalTrauma = MathUtil.Clamp01(
                condition.CriticalTrauma + overkill);
        }

        condition.BluntDamage = System.Math.Max(0f, condition.BluntDamage + blunt);
        if (cut > 0f)
        {
            WoundMath.InflictCut(world, target, part, cut, profile.BloodLossMultiplier);
            DrainBlood(target, cut * profile.BloodLossMultiplier *
                Spec118.InstantBloodLossFactor);
        }

        target.Health = target.Body.Mean();
        AmputateSystemHelpers.TrySeverCritical(world, target, part, cut, source);
        MortalityHelpers.ResolveTrauma(world, target, landed, source);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, target.Id, "BodyDamage",
                $"Source={source} Part={part} Landed={landed:F3} Cut={cut:F3} " +
                $"Blunt={blunt:F3} HP={target.Body.Parts[part]:F3} " +
                $"Critical={condition.CriticalTrauma:F3} Blood={target.Needs.Blood:F3} " +
                $"BloodDeficit={target.Body.BloodDeficit:F3}");
        }
        return new BodyDamageResult(landed, cut, blunt, false);
    }

    public static void DrainBlood(NPCState npc, float amount)
    {
        amount = System.Math.Max(0f, amount);
        if (amount <= npc.Needs.Blood)
        {
            npc.Needs.Blood -= amount;
            return;
        }

        var excess = amount - npc.Needs.Blood;
        npc.Needs.Blood = 0f;
        npc.Body.BloodDeficit = MathUtil.Clamp01(npc.Body.BloodDeficit + excess);
    }

    public static void RestoreBlood(NPCState npc, float amount)
    {
        amount = System.Math.Max(0f, amount);
        if (npc.Body.BloodDeficit > 0f)
        {
            var paid = System.Math.Min(amount, npc.Body.BloodDeficit);
            npc.Body.BloodDeficit -= paid;
            amount -= paid;
        }

        if (amount > 0f)
        {
            npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood + amount);
        }
    }

    // Healing follows Kenshi's negative-depth order: return from critical
    // trauma to zero first, then refill the familiar positive part bar.
    public static float RestorePart(NPCState npc, BodyPart part, float amount)
    {
        amount = System.Math.Max(0f, amount);
        if (npc.Health <= 0f)
        {
            return amount;
        }

        var condition = npc.Body.Condition(part);
        if (condition.CriticalTrauma > 0f)
        {
            var paid = System.Math.Min(amount, condition.CriticalTrauma);
            condition.CriticalTrauma -= paid;
            amount -= paid;
        }

        if (amount > 0f && !npc.Body.IsSevered(part))
        {
            var room = 1f - npc.Body.Parts[part];
            var restored = System.Math.Min(amount, room);
            npc.Body.Parts[part] += restored;
            amount -= restored;
        }

        npc.Health = npc.Body.Mean();
        return amount;
    }

    // Untreated cut damage deepens the same injury without creating another
    // decal or another instant blood-loss burst. It still shares the critical
    // depth, amputation and mortality rules with a landed blow.
    public static void ApplyCutDegeneration(
        WorldState world, NPCState npc, BodyPart part, float amount)
    {
        amount = System.Math.Max(0f, amount);
        if (amount <= 0f || npc.Health <= 0f || npc.Body.IsSevered(part))
        {
            return;
        }

        var condition = npc.Body.Condition(part);
        var positive = System.Math.Min(npc.Body.Parts[part], amount);
        npc.Body.Parts[part] -= positive;
        var critical = amount - positive;
        if (critical > 0f)
        {
            condition.CriticalTrauma = MathUtil.Clamp01(
                condition.CriticalTrauma + critical);
        }

        npc.Health = npc.Body.Mean();
        AmputateSystemHelpers.TrySeverCritical(world, npc, part, amount, "wound degeneration");
        MortalityHelpers.ResolveTrauma(world, npc, amount, "wound degeneration");
    }

    public static float ComaThreshold(NPCState npc) =>
        Spec118.ComaThresholdMin + Spec118.ComaThresholdToughnessGain *
        MathUtil.Clamp01(npc.Attributes.Toughness);

    public static void DecayAllHitBias(WorldState world, NPCState npc)
    {
        foreach (var condition in npc.Body.Conditions.Values)
        {
            DecayHitBias(world, condition);
        }
    }

    private static void DecayHitBias(WorldState world, BodyPartCondition condition)
    {
        if (condition.HitBias <= 1f || condition.HitBiasChangedTick < 0)
        {
            condition.HitBias = System.Math.Max(1f, condition.HitBias);
            return;
        }

        var elapsed = world.Tick - condition.HitBiasChangedTick;
        if (elapsed < Spec118.HitBiasDecayTicks)
        {
            return;
        }

        var steps = elapsed / Spec118.HitBiasDecayTicks;
        condition.HitBias = System.Math.Max(1f, condition.HitBias - steps);
        condition.HitBiasChangedTick += steps * Spec118.HitBiasDecayTicks;
    }
}

}
