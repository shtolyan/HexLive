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

// §72: the stalk half of the raid. The fight itself belongs to RaidSystem /
// HumanCombatSystem — everything here is "walk to her, and know when to stop".
public sealed partial class PlanningSystem
{
    // The committed victim, re-validated. He keeps hunting the SAME girl for as
    // long as she is worth hunting; only when the commitment lapses does he look
    // for a fresh one.
    private static NPCState ResolveRaidVictim(WorldState world, NPCState npc)
    {
        if (npc.Mind.RaidTargetNpcId is { } targetId &&
            world.Entities.Npcs.TryGetValue(targetId, out var committed) &&
            committed.Health > 0f &&
            FactionRelations.AreHostile(npc, committed))
        {
            // Give-up checks, in the order they cost least to evaluate. Each one
            // mirrors a valve the dogs already have (§29C.4A) — a hunter that
            // never lets go reads as a bug and grinds the colony down.
            var elapsed = world.Tick - npc.Mind.RaidStartedTick;
            if (elapsed > Spec72.RaidPursuitMaxTicks)
            {
                AbandonRaid(world, npc, "Timeout");
                return null;
            }

            if (Spec72.RaidRespectsSanctuary && MobSystem.IsNpcInSanctuary(world, committed))
            {
                AbandonRaid(world, npc, "Sanctuary");
                return null;
            }

            // §106: she jumped into the water — the second sanctuary. He does
            // not follow; a full "Swimming" abandon (not in the attempted-free
            // list) so diving costs him the whole raid cooldown, like a door.
            if (Spec106.WaterSanctuaryEnabled && CombatMedium.IsNpcSwimming(world, committed))
            {
                AbandonRaid(world, npc, "Swimming");
                return null;
            }

            // Her friends arrived, or she healed up — the odds turned.
            if (RaidMath.Opportunity(world, npc, committed) < Spec72.RaidAbandonOpportunity)
            {
                AbandonRaid(world, npc, "OpportunityLost");
                return null;
            }

            // Stuck: two rebuilds without leaving the junction means the route
            // is not getting him anywhere (the mob chase valve, MobSystem).
            if (npc.CurrentJunction is { } here)
            {
                if (npc.Mind.RaidLastJunction is { } last && last.Equals(here))
                {
                    if (npc.Mind.RaidStallSinceTick == 0)
                    {
                        npc.Mind.RaidStallSinceTick = world.Tick;
                    }
                    else if (world.Tick - npc.Mind.RaidStallSinceTick > Spec72.RaidStallGiveUpTicks)
                    {
                        AbandonRaid(world, npc, "Stalled");
                        return null;
                    }
                }
                else
                {
                    npc.Mind.RaidLastJunction = here;
                    npc.Mind.RaidStallSinceTick = 0;
                }
            }

            return committed;
        }

        // No live commitment — pick one and start the clock.
        var victim = RaidMath.BestVictim(world, npc, out _);
        if (victim is null)
        {
            return null;
        }

        npc.Mind.RaidTargetNpcId = victim.Id;
        npc.Mind.RaidStartedTick = world.Tick;
        npc.Mind.RaidLastJunction = npc.CurrentJunction;
        npc.Mind.RaidStallSinceTick = 0;
        npc.Mind.GoalLock = new GoalLock
        {
            Goal = GoalType.Raid,
            StartTick = world.Tick,
            EndTick = world.Tick + Spec72.RaidLockTicks
        };
        return victim;
    }

    // Walk toward the enemy camp. Deliberately NOT a committed hunt: no victim,
    // no goal-lock, no cooldown on failure — he is just closing the distance so
    // the ordinary victim scan has something to find.
    private static bool TryBuildProwlPlan(WorldState world, NPCState npc)
    {
        if (RaidMath.ProwlTarget(world, npc) is not { } camp ||
            npc.CurrentJunction is not { } from)
        {
            return false;
        }

        // Stop short of the camp itself: he stalks the edges, he does not walk
        // into the middle of four women and their fire.
        var approach = FindProwlJunction(world, npc, camp);
        if (approach is not { } target || target.Equals(from))
        {
            return false;
        }

        npc.Plan.TargetJunctionId = target;
        npc.Plan.TargetTile = camp;
        npc.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.MoveToJunction,
            TargetJunction = target
        });
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.Status = PlanStatus.Active;
        Trace.Emit(world, npc.Id, "RaidProwl",
            $"Toward=({camp.Q},{camp.R}) " +
            $"Dist={HexSpatialMath.HexDistance(npc.Tile, camp)}");
        return true;
    }

    // A free junction on a tile roughly ProwlArrivedTiles out from the camp
    // centre — the ring he loiters on while he looks for a straggler.
    private static JunctionId? FindProwlJunction(WorldState world, NPCState npc, TileCoord camp)
    {
        JunctionId? best = null;
        var bestScore = int.MaxValue;
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) || tile.Flags.HasFlag(TileFlags.Water))
            {
                continue;
            }

            var toCamp = HexSpatialMath.HexDistance(tile.Coord, camp);
            if (toCamp > Spec72.ProwlArrivedTiles)
            {
                continue;
            }

            // Prefer the outer edge of the ring, then the nearest to him.
            var score = (Spec72.ProwlArrivedTiles - toCamp) * 4 +
                HexSpatialMath.HexDistance(tile.Coord, npc.Tile);
            if (score >= bestScore)
            {
                continue;
            }

            foreach (var junctionId in tile.Junctions)
            {
                if (!SpatialQueries.IsJunctionFree(world, junctionId) ||
                    !world.Junctions.Items.TryGetValue(junctionId, out var junction) ||
                    junction.Blocked)
                {
                    continue;
                }

                if (npc.CurrentJunction is { } from &&
                    !Connectivity.Reachable(world, from, junctionId, npc.Body.CanJump))
                {
                    continue;
                }

                bestScore = score;
                best = junctionId;
                break;
            }
        }

        return best;
    }

    // Drop the hunt and take the breather. Public to Runtime so RaidSystem can
    // call the identical teardown when the FIGHT (not the stalk) ends.
    internal static void AbandonRaid(WorldState world, NPCState npc, string reason)
    {
        npc.Mind.RaidTargetNpcId = null;
        npc.Mind.RaidLastJunction = null;
        npc.Mind.RaidStallSinceTick = 0;
        npc.Mind.CombatOpponentNpcId = null;

        // The long breather is the price of an ATTEMPT — a fight broken off, a
        // pursuit timed out, a kill made. Simply finding nobody worth taking is
        // not an attempt: charging the full cooldown for it would park him for
        // 500 seconds every time he looked around and saw the girls together,
        // which is most of the time.
        var attempted = reason is not ("NoVictim" or "NoApproach" or "OpportunityLost");
        if (attempted)
        {
            npc.Mind.RaidCooldownUntilTick = world.Tick + Spec72.RaidCooldownTicks;
        }

        SetGoalCooldown(world, npc, GoalType.Raid);
        if (npc.Mind.CurrentGoal == GoalType.Raid)
        {
            npc.Mind.CurrentGoal = GoalType.None;
        }

        Trace.Emit(world, npc.Id, "RaidAbandoned", $"Reason={reason}");
    }

    internal static bool IsAdjacentJunction(WorldState world, JunctionId a, JunctionId b)
    {
        return a.Equals(b) ||
            (world.Junctions.Items.TryGetValue(b, out var junction) &&
             junction.Neighbors.Contains(a));
    }

    // A FREE junction beside her, reserved so two hunters (later: a raiding
    // party) cannot claim the same square.
    private static JunctionId? PickApproachJunction(WorldState world, NPCState npc, JunctionId target)
    {
        foreach (var neighbor in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (SpatialQueries.IsJunctionFree(world, neighbor) &&
                SpatialMutations.TryReserveJunction(world, neighbor, npc.Id, world.Tick, 48))
            {
                return neighbor;
            }
        }

        return null;
    }
}

}
