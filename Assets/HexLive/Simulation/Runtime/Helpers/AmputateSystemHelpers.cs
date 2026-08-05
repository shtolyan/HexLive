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

// Spec §50: severing a limb. Shared by the emergent triggers (dog/shark bites
// that overwhelm an already-mauled limb) and the prepared HazardSystem.
public static class AmputateSystemHelpers
{
    // Only arms and legs come off — never Head/Torso/Pelvis (those kill via the
    // existing VitalDestroyed path instead).
    public static bool CanSever(BodyPart part) =>
        part == BodyPart.ArmL || part == BodyPart.ArmR ||
        part == BodyPart.LegL || part == BodyPart.LegR;

    // §50: a stump can't be bitten — an attack aimed at a severed limb slides
    // onto the nearest remaining flesh: the mirror limb if it's still there,
    // otherwise the torso/pelvis the stump hangs from.
    public static BodyPart RedirectFromStump(NPCState npc, BodyPart part)
    {
        if (!npc.Body.IsSevered(part))
        {
            return part;
        }

        var mirror = part switch
        {
            BodyPart.ArmL => BodyPart.ArmR,
            BodyPart.ArmR => BodyPart.ArmL,
            BodyPart.LegL => BodyPart.LegR,
            BodyPart.LegR => BodyPart.LegL,
            _ => part,
        };
        if (!npc.Body.IsSevered(mirror))
        {
            return mirror;
        }

        return part is BodyPart.LegL or BodyPart.LegR ? BodyPart.Pelvis : BodyPart.Torso;
    }

    // §50: the emergent (bite) trigger. Call right after a bite has applied its
    // damage and filed its wound. Only a fully-destroyed limb (0 HP) comes off,
    // and only on a big blow or a rare grind roll — so a leg ground to 0 usually
    // just hobbles, and every so often is torn away entirely.
    public static void TrySeverOnBite(WorldState world, NPCState npc, BodyPart part, float blowDamage)
    {
        if (!Spec50.Enabled || !CanSever(part) || npc.Body.IsSevered(part) ||
            npc.Body.Parts[part] > 0f)
        {
            return;
        }

        var bigBlow = blowDamage >= Spec50.LimbSeverThreshold;
        var grind = MathUtil.Hash01(world.Seed, world.Tick, npc.Id.Value, 733) < Spec50.GrindSeverChance;
        if (bigBlow || grind)
        {
            Sever(world, npc, part);
        }
    }

    // §50 dev/test entry: land ONE bite on a part exactly like a dog/shark —
    // dock the zone's HP, bleed a little, file the wound decal, then run the
    // sever-on-bite check. Used by the AmputationTest scene's damage buttons so
    // a limb tears off "on damage when it should" through the real path.
    public static void DebugBite(WorldState world, NPCState npc, BodyPart part, float damage)
    {
        if (!npc.Body.Parts.ContainsKey(part))
        {
            return;
        }

        npc.Body.Parts[part] = System.Math.Max(0f, npc.Body.Parts[part] - damage);
        npc.Health = npc.Body.Mean();
        DamageReactionSystemHelpers.GrantAdrenaline(world, npc, damage, "DebugBite");
        npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - damage * 0.5f);
        WoundMath.Inflict(world, npc, part, damage);

        // §105: как в симуляции — голова в ноль убивает сразу, грудь роняет в
        // умирание, по уже лежащей удар срезает запас. Limbs may tear off.
        MortalityHelpers.ResolveTrauma(world, npc, damage, "debug");

        TrySeverOnBite(world, npc, part, damage);
    }

    // Take the limb off for good: pin the zone to 0 (never regenerates), dump
    // blood, file a deep slow-clotting stump wound, drop the limb in the world
    // as a decaying object, and interrupt whatever she was doing. If she dies,
    // it's through blood loss over the following ticks — not instantly here.
    public static void Sever(WorldState world, NPCState npc, BodyPart part)
    {
        if (!Spec50.Enabled || !CanSever(part) || npc.Body.IsSevered(part))
        {
            return;
        }

        npc.Body.Sever(part);
        npc.Health = npc.Body.Mean();
        npc.Needs.Blood = MathUtil.Clamp01(npc.Needs.Blood - Spec50.LimbSeverBloodLoss);

        // Spec §52: a lost arm is a lost hand slot — the pack shrinks. Anything
        // that no longer fits spills to the ground (handled by SpillOverflow).
        if (part == BodyPart.ArmL || part == BodyPart.ArmR)
        {
            EquipmentMath.RecalculateCapacity(world, npc);
            InventoryMath.SpillOverflow(world, npc);
        }

        // The stump bleeds: a deep fresh wound §44 clotting keeps open a while,
        // driving the ongoing Blood drain through the low-part bleed path.
        WoundMath.Inflict(world, npc, part, Spec50.LimbSeverWoundSeverity);

        // Drop the limb at her feet as a decaying world object (mirrors corpse).
        var dropJunction = npc.CurrentJunction;
        if (dropJunction is null &&
            world.Tiles.Items.TryGetValue(npc.Tile, out var tile) && tile.Junctions.Count > 0)
        {
            dropJunction = tile.Junctions[0];
        }

        if (dropJunction is { } junction)
        {
            var limb = WorldObjectMutations.SpawnObject(
                world, "body.limb_severed", npc.Fragment, npc.Tile, junction);
            limb.CurrentUser = npc.Id;          // whose limb (which actor mesh to bake)
            limb.Variant = part.ToString();     // which limb — the renderer bakes this chain
            limb.ResourceAmount = Spec50.SeveredLimbDecayTicks;
        }

        // Whatever she was mid-doing is over.
        if (npc.Plan.Status == PlanStatus.Active ||
            npc.Execution.Status == ExecutionStatus.InProgress)
        {
            PlanInterruption.Abort(world, npc, $"Lost {part}");
            npc.Mind.CurrentGoal = GoalType.None;
        }

        Trace.Emit(world, npc.Id, "LimbSevered",
            $"{part} severed (Blood={npc.Needs.Blood:F2} Health={npc.Health:F2})");
    }
}

}
