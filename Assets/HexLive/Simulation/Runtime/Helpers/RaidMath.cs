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

// §72: how an outsider sizes up a kill. He is an OPPORTUNIST, not a berserker —
// he lives his own survival life and only goes for someone when the odds are
// clearly his. Expressed as arithmetic rather than special cases, so the "keeps
// away from a group" rule cannot be forgotten at a call site: a girl standing
// with two friends simply cannot score high enough to bid.
//
// Shared by DecisionSystem (should I hunt, and whom), PlanningSystem (walk to
// her) and RaidSystem (is this still worth it), so all three agree on one
// definition of a good target.
public static class RaidMath
{
    // Is he in shape to hunt at all? Whole body, a real weapon in hand, and his
    // own needs quiet. Fists never qualify — the same bar §62 sets for a girl
    // picking a fight with a wolf.
    public static bool IsFitToRaid(NPCState npc, WorldState world)
    {
        if (npc.Health <= 0f ||
            npc.Body.AnySevered ||
            npc.Body.IsProne ||
            !npc.Body.CanUseToolsOrWeapons ||
            npc.IsUnconscious(world.Tick) ||
            npc.Mind.IsStarving ||
            npc.Mind.IsDehydrated ||
            npc.Health < Spec72.RaidSelfHealthFloor ||
            MobSystem.WorstPartHealth(npc) < Spec72.RaidSelfWorstPartFloor)
        {
            return false;
        }

        var weaponId = SimBalance.BestMeleeWeapon(npc.Inventory.Items, npc.Body.IntactHands);
        return GearCatalog.For(weaponId).MeleePriority > 0;
    }

    // How many of the victim's OWN side stand close enough to pile in.
    public static int AlliesAround(WorldState world, NPCState victim)
    {
        var count = 0;
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(victim.Id) || other.Health <= 0f ||
                !FactionRelations.AreAllies(other, victim) ||
                other.IsUnconscious(world.Tick) ||
                other.Execution.CurrentInteraction == InteractionType.Sleep)
            {
                continue;
            }

            if (HexSpatialMath.HexDistance(other.Tile, victim.Tile) <= Spec72.RaidIsolationRadiusTiles)
            {
                count++;
            }
        }

        return count;
    }

    // 0 = not worth it, 1 = a gift. Isolation dominates on purpose: a lone
    // straggler is the whole premise, and a girl in company is off the menu
    // however hurt she is.
    public static float Opportunity(WorldState world, NPCState raider, NPCState victim)
    {
        if (victim.Health <= 0f)
        {
            return 0f;
        }

        var helpless = victim.IsUnconscious(world.Tick) ||
            victim.Execution.CurrentInteraction == InteractionType.Sleep ||
            victim.Body.IsProne;

        // ISOLATION is the only hard gate. Health used to be one too, and that
        // was wrong: "he hunts the ones who are alone" means a lone healthy girl
        // is prey, while a wounded one in a group is not. Health is a weight
        // below, not a veto — with a veto he simply never attacked anybody.
        var allies = AlliesAround(world, victim);
        if (allies > Spec72.RaidMaxVictimAllies)
        {
            return 0f;
        }

        var isolation = Spec72.RaidCrowdCount > 0
            ? 1f - MathUtil.Clamp(allies / (float)Spec72.RaidCrowdCount, 0f, 1f)
            : 1f;
        // Weakness saturates at the ceiling: whole = 0, at/below the ceiling = 1.
        var condition = System.Math.Min(victim.Health, MobSystem.WorstPartHealth(victim));
        var weakness = Spec72.RaidVictimHealthCeiling > 0f
            ? MathUtil.Clamp(
                (Spec72.RaidVictimHealthCeiling - condition) / Spec72.RaidVictimHealthCeiling, 0f, 1f)
            : 0f;
        var distance = HexSpatialMath.HexDistance(raider.Tile, victim.Tile);
        var proximity = Spec72.RaidScanRadiusTiles > 0
            ? 1f - MathUtil.Clamp(distance / (float)Spec72.RaidScanRadiusTiles, 0f, 1f)
            : 0f;

        return isolation * Spec72.RaidWeightIsolation +
            weakness * Spec72.RaidWeightWeakness +
            (helpless ? 1f : 0f) * Spec72.RaidWeightHelpless +
            proximity * Spec72.RaidWeightProximity;
    }

    // The best target in reach, or null. Ties break on the LOWER entity id —
    // never on iteration order — so a replay is identical.
    public static NPCState BestVictim(WorldState world, NPCState raider, out float opportunity)
    {
        opportunity = 0f;
        NPCState best = null;
        if (raider.CurrentJunction is not { } from)
        {
            return null;
        }

        foreach (var candidate in world.Entities.Npcs.Values)
        {
            if (candidate.Health <= 0f ||
                !FactionRelations.AreHostile(raider, candidate) ||
                candidate.CurrentJunction is not { } candidateJunction ||
                HexSpatialMath.HexDistance(raider.Tile, candidate.Tile) > Spec72.RaidScanRadiusTiles)
            {
                continue;
            }

            // He gives up at the door, exactly like a dog (§29C.4A sanctuary).
            if (Spec72.RaidRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, candidate))
            {
                continue;
            }

            // §106: a swimmer is the water's business, not his — without this
            // the hunt would pick her and the plan would walk him into the sea.
            if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, candidate))
            {
                continue;
            }

            // §105.14: притворяется мёртвой — исключение ДО скоринга, а не
            // штраф внутри него. Opportunity весит беспомощность В ПЛЮС
            // (RaidWeightHelpless), так что дай он ей попасть в оценку — и
            // притворство сделало бы её предпочтительной целью.
            if (candidate.IsPlayingDead(world.Tick))
            {
                continue;
            }

            if (!from.Equals(candidateJunction) &&
                !Connectivity.Reachable(world, from, candidateJunction, raider.Body.CanJump))
            {
                continue;
            }

            var score = Opportunity(world, raider, candidate);
            if (score < Spec72.RaidOpportunityFloor)
            {
                continue;
            }

            if (best is null || score > opportunity ||
                (score == opportunity && candidate.Id.Value < best.Id.Value))
            {
                opportunity = score;
                best = candidate;
            }
        }

        return best;
    }

    // §72: the missing motive. Without this he is a hermit, not a hunter — his
    // camp is deliberately far away and every need he has is met locally, so a
    // girl never enters his scan radius and the hunt never fires once in ten
    // game days (soak-observed: zero RaidEngaged over six seeds).
    //
    // So when he is in shape and nobody is in range, he goes LOOKING: a walk
    // toward the colony's camp anchor. It bids at the bare base score, so any
    // real need of his own outranks it and he only prowls on a full stomach.
    public static TileCoord? ProwlTarget(WorldState world, NPCState raider)
    {
        if (!Spec72.ProwlEnabled)
        {
            return null;
        }

        TileCoord? nearest = null;
        var bestDistance = int.MaxValue;
        foreach (var pair in world.FactionHomes)
        {
            if (!FactionRelations.AreHostile(pair.Key, raider.Faction))
            {
                continue;
            }

            var distance = HexSpatialMath.HexDistance(raider.Tile, pair.Value);
            // Already loitering at their camp and still no target — stop walking
            // in circles on their doorstep and let the auction move him on.
            if (distance <= Spec72.ProwlArrivedTiles || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            nearest = pair.Value;
        }

        return nearest;
    }
}

}
