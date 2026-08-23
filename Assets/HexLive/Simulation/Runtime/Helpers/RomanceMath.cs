using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§127: pure eligibility, authored-pose placement and outcomes.</summary>
internal static class RomanceMath
{
    // This authored male piece is labelled "Nabedrenniki" and occupies only
    // ThighR/ThighL in the wear data, yet its side panels reach into the groin
    // in the fitted mesh. Pelvis-only undressing therefore left it visibly in
    // front of the genital mesh (§127.11).
    private static readonly HashSet<string> MaleGenitalBlockingGarments =
        new(StringComparer.Ordinal)
        {
            "FCO Legs Straps Male"
        };

    private static readonly string[] FloorKeys =
    {
        "Crouching_Doggy_Loop0",
        "Crouching_Mission_Loop0",
        "Standing_Doggy_Loop0",
        "Standing_Mission_Loop0"
    };

    internal readonly struct Placement
    {
        public Placement(string key, Float2 anchor, float facingDegrees)
        {
            Key = key;
            Anchor = anchor;
            FacingDegrees = facingDegrees;
        }

        public string Key { get; }
        public Float2 Anchor { get; }
        public float FacingDegrees { get; }
    }

    public static bool IsPair(NPCState a, NPCState b) =>
        a != null && b != null && a.Sex != b.Sex &&
        a.Sex != GarmentSex.Any && b.Sex != GarmentSex.Any;

    public static bool CanConsent(WorldState world, NPCState initiator, NPCState partner)
    {
        if (!Ready(world, initiator, partner) ||
            !FactionRelations.AreAllies(initiator, partner))
        {
            return false;
        }

        var towardPartner = initiator.Social.GetOrCreate(partner.Id);
        var towardInitiator = partner.Social.GetOrCreate(initiator.Id);
        return towardPartner.Affinity >= Spec127.MinAffinity &&
               towardPartner.Trust >= Spec127.MinTrust &&
               towardPartner.Familiarity >= Spec127.MinFamiliarity &&
               towardInitiator.Affinity >= Spec127.MinAffinity &&
               towardInitiator.Trust >= Spec127.MinTrust &&
               towardInitiator.Familiarity >= Spec127.MinFamiliarity;
    }

    public static bool CanForce(WorldState world, NPCState initiator, NPCState victim)
    {
        return Ready(world, initiator, victim) &&
               initiator.Sex == GarmentSex.Male &&
               victim.Sex == GarmentSex.Female &&
               initiator.Traits.Has(TraitKind.Abuser) &&
               !FactionRelations.AreAllies(initiator, victim);
    }

    private static bool Ready(WorldState world, NPCState a, NPCState b)
    {
        return Spec127.RomanceEnabled && IsPair(a, b) &&
               a.Health > 0f && b.Health > 0f &&
               !a.IsUnconscious(world.Tick) && !b.IsUnconscious(world.Tick) &&
               !a.IsDying && !b.IsDying &&
               !a.Body.IsProne && !b.Body.IsProne &&
               !a.IsFighting && !b.IsFighting &&
               a.CarriedByNpcId is null && b.CarriedByNpcId is null &&
               a.CarriedNpcId is null && b.CarriedNpcId is null &&
               world.Tick >= a.Mind.RomanceCooldownUntilTick &&
               world.Tick >= b.Mind.RomanceCooldownUntilTick;
    }

    public static Placement PickPlacement(WorldState world, NPCState female, NPCState male)
    {
        // Only the two positions saved in HexFlowerTest require terrain. The
        // other authored loops are floor poses and never search for an edge.
        var edgeChoices = new List<(string key, RomancePlacement.Placement p)>();
        CollectEdge("Wall_Doggy_Loop0", RomancePlacement.EdgeRequirement.Wall,
            faceIntoEdge: true, new Float2(-0.04f, -0.34f));
        CollectEdge("Sitting_Mission_Loop0", RomancePlacement.EdgeRequirement.StepUp,
            faceIntoEdge: false, new Float2(-0.01f, -0.16f));

        if (edgeChoices.Count > 0)
        {
            var pick = (int)(MathUtil.Hash01(world.Seed, world.Tick,
                female.Id.Value, male.Id.Value) * edgeChoices.Count);
            if (pick >= edgeChoices.Count) pick = edgeChoices.Count - 1;
            var chosen = edgeChoices[pick];
            return new Placement(chosen.key, chosen.p.HerWorld,
                NormalizeDegrees(chosen.p.HerFacingDeg));
        }

        var floorRoll = MathUtil.Hash01(world.Seed, world.Tick,
            male.Id.Value, female.Id.Value);
        var floorIndex = Math.Min(FloorKeys.Length - 1,
            (int)(floorRoll * FloorKeys.Length));
        var dx = male.Position.X - female.Position.X;
        var dy = male.Position.Y - female.Position.Y;
        var facing = MathF.Abs(dx) + MathF.Abs(dy) > 0.001f
            ? MathF.Atan2(dy, dx) * (180f / MathF.PI)
            : female.RotationDegrees;
        return new Placement(FloorKeys[floorIndex], female.Position,
            NormalizeDegrees(facing));

        void CollectEdge(string key, RomancePlacement.EdgeRequirement edge,
            bool faceIntoEdge, Float2 offset)
        {
            var candidates = RomancePlacement.FindCandidates(
                female.Tile,
                new RomancePlacement.PoseRule(edge, faceIntoEdge, offset),
                coord => world.Tiles.Items.TryGetValue(coord, out var tile) &&
                         tile.Flags.HasFlag(TileFlags.Walkable) &&
                         !tile.Flags.HasFlag(TileFlags.Water)
                    ? tile.Elevation
                    : (int?)null,
                (_, _) => true);

            foreach (var candidate in candidates)
            {
                // The handshake brings them together first; an edge pose may
                // shift the visual root by at most one near-edge row.
                if (HexSpatialMath.Distance(candidate.HerWorld, female.Position) >
                    HexSpatialMath.HexRadius * 0.85f)
                {
                    continue;
                }

                var blocked = false;
                foreach (var other in world.Entities.Npcs.Values)
                {
                    if (other.Id.Equals(female.Id) || other.Id.Equals(male.Id)) continue;
                    if (HexSpatialMath.Distance(other.Position, candidate.HerWorld) < 0.55f ||
                        HexSpatialMath.Distance(other.Position, candidate.HisWorld) < 0.55f)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (!blocked) edgeChoices.Add((key, candidate));
            }
        }
    }

    public static bool CoversPelvis(WorldState world, ItemInstance item)
    {
        foreach (var slot in WearSlotCatalog.For(item.DefinitionId))
        {
            if (slot == WearSlot.Pelvis) return true;
        }

        return world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
               definition.Covers.Contains(BodyPart.Pelvis);
    }

    public static bool MustRemoveForRomance(WorldState world, NPCState npc,
        ItemInstance item)
    {
        return CoversPelvis(world, item) ||
               (npc.Sex == GarmentSex.Male &&
                MaleGenitalBlockingGarments.Contains(item.DefinitionId));
    }

    public static void ApplyForcedPelvisDamage(WorldState world, NPCState victim,
        NPCState aggressor)
    {
        var roll = MathUtil.Hash01(world.Seed, world.Tick,
            aggressor.Id.Value, victim.Id.Value);
        var damage = Spec127.ForcedPelvisDamageMin +
            (Spec127.ForcedPelvisDamageMax - Spec127.ForcedPelvisDamageMin) * roll;
        victim.Body.Parts[BodyPart.Pelvis] = MathUtil.Clamp01(
            victim.Body.Parts[BodyPart.Pelvis] - damage);
        victim.Body.Condition(BodyPart.Pelvis).BluntDamage = MathUtil.Clamp01(
            victim.Body.Condition(BodyPart.Pelvis).BluntDamage + damage);
        victim.Health = victim.Body.Mean();
    }

    public static void WashIntimacySoil(NPCState npc, float amount)
    {
        if (amount <= 0f) return;
        foreach (var condition in npc.Body.Conditions.Values)
        {
            condition.IntimacySoil = MathUtil.Clamp01(condition.IntimacySoil - amount);
        }
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360f;
        return value < 0f ? value + 360f : value;
    }
}

}
