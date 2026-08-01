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

// §72: one human swing, on the §29C.3 v2 timeline — windup → hit → recovery.
// Lifted from AnimalCombatSystem.RunCounterStrike so a girl swinging at a man
// feels exactly like a girl swinging at a wolf, and so presentation gets the
// same AttackAnimUntilTick / SwingStrikeIndex window to play a clip across.
//
// WHY this and not §56's per-pass model, which was the obvious thing to reuse:
// PredationSystem deals PredationStrikePerPass(0.35) x MeleeStrikeBonus every
// MEDIUM tick, and a medium tick is one second. With a knife that is 0.516
// damage/second — a full-health torso is destroyed in two seconds, and
// VitalDestroyed kills outright. The §57 help cry has a 6-tile radius and the
// responder still has to WALK: she cannot arrive. It is just as lethal in
// reverse (three defenders = 0.66/s, the raider dies in 1.5 s). The timed model
// lands 0.221 every 2.74 s instead, which is the only version of this fight
// where a collective defence is physically possible.
internal static class MeleeSwing
{
    private const int TicksPerSecond = 4;

    private static int SecondsToTicks(float seconds) =>
        System.Math.Max(1, (int)System.Math.Round(seconds * TicksPerSecond));

    // Advance this actor's swing by one FAST tick. Returns true when a blow
    // lands this tick (damage/weaponId then describe it); starts a fresh windup
    // when recovered and the target is in reach.
    internal static bool TryAdvanceSwing(
        WorldState world, NPCState actor, bool inReach, out float damage, out string weaponId)
    {
        damage = 0f;
        weaponId = actor.Body.CanUseToolsOrWeapons
            ? SimBalance.BestMeleeWeapon(actor.Inventory.Items, actor.Body.IntactHands)
            : string.Empty;

        var gear = GearCatalog.For(weaponId);

        if (actor.StrikeLandsAtTick > 0 && world.Tick >= actor.StrikeLandsAtTick)
        {
            actor.StrikeLandsAtTick = 0;
            StrikeTimings(gear, actor.SwingStrikeIndex,
                out var hitDelay, out var duration, out var cooldown);
            actor.StrikeReadyAtTick = world.Tick + SecondsToTicks(duration - hitDelay + cooldown);
            // Spec 19.3C: hurt arms strike weaker; the weapon owns its damage.
            damage = GearCatalog.Damage(weaponId) * actor.Body.StrikeFactor();
            return true;
        }

        if (actor.StrikeLandsAtTick == 0 && inReach && world.Tick >= actor.StrikeReadyAtTick)
        {
            actor.SwingStrikeIndex = gear.HasStrikeVariants
                ? System.Math.Min(gear.StrikeVariants.Length - 1,
                    (int)(MathUtil.Hash01(world.Seed, world.Tick, actor.Id.Value, 777) *
                        gear.StrikeVariants.Length))
                : -1;
            StrikeTimings(gear, actor.SwingStrikeIndex, out var hitDelay, out var duration, out _);
            actor.StrikeLandsAtTick = world.Tick + SecondsToTicks(hitDelay);
            actor.AttackAnimUntilTick = world.Tick + SecondsToTicks(duration);
        }

        return false;
    }

    private static void StrikeTimings(GearStats gear, int strikeIndex,
        out float hitDelaySeconds, out float durationSeconds, out float cooldownSeconds)
    {
        if (gear.HasStrikeVariants && strikeIndex >= 0 && strikeIndex < gear.StrikeVariants.Length)
        {
            var variant = gear.StrikeVariants[strikeIndex];
            hitDelaySeconds = variant.HitDelaySeconds;
            durationSeconds = variant.AttackDurationSeconds;
            cooldownSeconds = variant.CooldownSeconds;
            return;
        }

        hitDelaySeconds = gear.HitDelaySeconds;
        durationSeconds = gear.AttackDurationSeconds;
        cooldownSeconds = gear.CooldownSeconds;
    }

    // A man aims high — far more torso and head than a dog's leg-first bite.
    internal static BodyPart PickHumanPart(WorldState world, int actorId)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick, actorId, 703);
        if (roll < 0.45f) return BodyPart.Torso;
        if (roll < 0.65f) return BodyPart.Head;
        if (roll < 0.75f) return BodyPart.Pelvis;
        if (roll < 0.83f) return BodyPart.ArmR;
        if (roll < 0.90f) return BodyPart.ArmL;
        if (roll < 0.95f) return BodyPart.LegR;
        return BodyPart.LegL;
    }

    // Land a struck blow on a person, through the full established body
    // pipeline — armor, wound record, severance, garment wear, vitals — so a
    // human hit is bookkept exactly like a bite.
    internal static void ApplyHumanBlow(
        WorldState world, NPCState attacker, NPCState target,
        float damage, string weaponId, string traceName)
    {
        var part = AmputateSystemHelpers.RedirectFromStump(
            target, PickHumanPart(world, attacker.Id.Value));
        var partArmor = EquipmentMath.ArmorForPart(world, target, part);
        var landed = System.Math.Max(0f, damage * (1f - partArmor));

        target.Body.Parts[part] = System.Math.Max(0f, target.Body.Parts[part] - landed);
        target.Health = target.Body.Mean();
        DamageReactionSystemHelpers.GrantAdrenaline(world, target, landed, traceName);
        WoundMath.Inflict(world, target, part, landed);
        AmputateSystemHelpers.TrySeverOnBite(world, target, part, landed);
        EquipmentMath.WearCoveringItems(world, target, part, SimBalance.ClothingBiteDurabilityWear);

        if (target.Body.VitalDestroyed(out var vitalPart))
        {
            target.Health = 0f;
            Trace.Emit(world, target.Id, "VitalPartDestroyed",
                $"{vitalPart} destroyed by NPC{attacker.Id.Value}");
        }

        Trace.Emit(world, attacker.Id, traceName,
            $"Target=NPC{target.Id.Value} {part} -{landed:F3} (armor={partArmor:F2}) " +
            $"Weapon={(string.IsNullOrEmpty(weaponId) ? "fists" : weaponId)} " +
            $"TargetHealth={target.Health:F2}");
    }

    internal static bool InReach(WorldState world, NPCState a, NPCState b)
    {
        return a.CurrentJunction is { } aj && b.CurrentJunction is { } bj &&
            (aj.Equals(bj) ||
             (world.Junctions.Items.TryGetValue(bj, out var junction) &&
              junction.Neighbors.Contains(aj)));
    }
}

}
