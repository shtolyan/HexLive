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

public sealed class PathfindingSystem : ISimulationSystem
{
    public string Name => nameof(PathfindingSystem);

    private static readonly System.Collections.Generic.HashSet<JunctionId> _avoidScratch = new();

    // Spec 24.3: the junctions other living actors currently stand on.
    // NOTE (spec 34, climb): soft-avoiding elevation-step "climb seams" here
    // was tried to make hillside routes prefer the flat way around, but a hard
    // avoid over-penalizes (forces long detours) and whack-a-moled the fragile
    // economy across seeds. The correct form is a WEIGHTED path cost (climb =
    // 2x, per the user), which needs the BFS turned into a cost-aware search —
    // deferred to a focused pass (with the climb animation in Unity).
    internal static System.Collections.Generic.HashSet<JunctionId> OtherActorJunctions(
        WorldState world, NPCState self)
    {
        _avoidScratch.Clear();
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Value == self.Id.Value)
            {
                continue;
            }

            if (other.CurrentJunction is { } standing)
            {
                _avoidScratch.Add(standing);
            }

            foreach (var claimed in other.ClaimedJunctions)
            {
                _avoidScratch.Add(claimed);
            }
        }

        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f)
            {
                _avoidScratch.Add(mob.Junction);
            }
        }

        return _avoidScratch;
    }

    // Spec §62: the junctions within DangerRingTiles of any live mob — the
    // soft-cost ring an unfit girl's routes detour around. Grown by BFS from
    // each mob's junction (tile-distance gated), cached for the tick.
    private static readonly System.Collections.Generic.HashSet<JunctionId> _dangerScratch = new();
    private static readonly System.Collections.Generic.Queue<JunctionId> _dangerQueue = new();
    private static int _dangerScratchTick = -1;

    public static System.Collections.Generic.HashSet<JunctionId> DangerRing(WorldState world)
    {
        if (_dangerScratchTick == world.Tick)
        {
            return _dangerScratch;
        }

        _dangerScratchTick = world.Tick;
        _dangerScratch.Clear();
        foreach (var mob in world.Mobs)
        {
            if (mob.Health <= 0f)
            {
                continue;
            }

            _dangerQueue.Clear();
            if (_dangerScratch.Add(mob.Junction))
            {
                _dangerQueue.Enqueue(mob.Junction);
            }

            while (_dangerQueue.Count > 0)
            {
                var currentId = _dangerQueue.Dequeue();
                if (!world.Junctions.Items.TryGetValue(currentId, out var junction))
                {
                    continue;
                }

                foreach (var neighborId in junction.Neighbors)
                {
                    if (_dangerScratch.Contains(neighborId) ||
                        !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                    {
                        continue;
                    }

                    var within = false;
                    foreach (var tile in neighbor.Tiles)
                    {
                        if (HexSpatialMath.HexDistance(tile, mob.Tile) <= Spec62.DangerRingTiles)
                        {
                            within = true;
                            break;
                        }
                    }

                    if (!within)
                    {
                        continue;
                    }

                    _dangerScratch.Add(neighborId);
                    _dangerQueue.Enqueue(neighborId);
                }
            }
        }

        return _dangerScratch;
    }

    // §72: the same soft ring, grown around hostile PEOPLE instead of mobs —
    // and per faction, because the ring is cached per tick and a single shared
    // set would make the raider detour around himself.
    private static readonly System.Collections.Generic.Dictionary<Faction,
        System.Collections.Generic.HashSet<JunctionId>> _hostileRings = new();
    private static readonly System.Collections.Generic.Dictionary<Faction, int> _hostileRingTick = new();
    private static readonly System.Collections.Generic.Queue<JunctionId> _hostileQueue = new();

    public static System.Collections.Generic.HashSet<JunctionId> HostileRing(
        WorldState world, Faction forFaction)
    {
        if (!_hostileRings.TryGetValue(forFaction, out var ring))
        {
            ring = new System.Collections.Generic.HashSet<JunctionId>();
            _hostileRings[forFaction] = ring;
            _hostileRingTick[forFaction] = -1;
        }

        if (_hostileRingTick[forFaction] == world.Tick)
        {
            return ring;
        }

        _hostileRingTick[forFaction] = world.Tick;
        ring.Clear();
        if (!Spec72.Enabled)
        {
            return ring;
        }

        foreach (var hostile in world.Entities.Npcs.Values)
        {
            if (hostile.Health <= 0f ||
                !FactionRelations.AreHostile(hostile.Faction, forFaction) ||
                hostile.CurrentJunction is not { } hostileJunction)
            {
                continue;
            }

            _hostileQueue.Clear();
            if (ring.Add(hostileJunction))
            {
                _hostileQueue.Enqueue(hostileJunction);
            }

            while (_hostileQueue.Count > 0)
            {
                var currentId = _hostileQueue.Dequeue();
                if (!world.Junctions.Items.TryGetValue(currentId, out var junction))
                {
                    continue;
                }

                foreach (var neighborId in junction.Neighbors)
                {
                    if (ring.Contains(neighborId) ||
                        !world.Junctions.Items.TryGetValue(neighborId, out var neighbor))
                    {
                        continue;
                    }

                    var within = false;
                    foreach (var tile in neighbor.Tiles)
                    {
                        if (HexSpatialMath.HexDistance(tile, hostile.Tile) <= Spec72.DangerRingTiles)
                        {
                            within = true;
                            break;
                        }
                    }

                    if (!within)
                    {
                        continue;
                    }

                    ring.Add(neighborId);
                    _hostileQueue.Enqueue(neighborId);
                }
            }
        }

        return ring;
    }

    // §72: the ring an NPC actually routes around — mobs plus hostile people.
    // Unioned into one set so HexPathfinder keeps its single-set signature.
    private static readonly System.Collections.Generic.HashSet<JunctionId> _combinedRing = new();

    private static System.Collections.Generic.HashSet<JunctionId> RouteAvoidRing(
        WorldState world, NPCState npc)
    {
        var mobs = AvoidsThreatRings(npc) ? DangerRing(world) : null;
        var people = AvoidsHostileRings(npc) ? HostileRing(world, npc.Faction) : null;

        if (people is null || people.Count == 0)
        {
            return mobs;
        }

        if (mobs is null || mobs.Count == 0)
        {
            return people;
        }

        _combinedRing.Clear();
        foreach (var id in mobs)
        {
            _combinedRing.Add(id);
        }

        foreach (var id in people)
        {
            _combinedRing.Add(id);
        }

        return _combinedRing;
    }

    // §72: who walks around the outsider. Unlike a wolf ring this is NOT gated
    // on being unfit — the girls give a hostile stranger a wide berth whatever
    // shape they are in, because they never mean to fight him. A raider mid-hunt
    // and anyone already fighting or fleeing obviously ignore it.
    internal static bool AvoidsHostileRings(NPCState npc)
    {
        // Кто обходит кольца ВРАЖДЕБНОЙ ФРАКЦИИ — колонка IgnoresHostileRings.
        // Кольца зверя (§62, AvoidsThreatRings ниже) — ОТДЕЛЬНЫЙ набор, уже
        // этого: налётчика в нём нет, потому что волк страшен обеим сторонам.
        return Spec72.Enabled &&
            !npc.IsFighting &&
            !AI.GoalCatalog.IgnoresHostileRings(npc.Mind.CurrentGoal);
    }

    // Spec §62: who pays the danger-ring cost. Fit fighters walk wherever they
    // like (they would attack anyway); a girl already fleeing or defending
    // must not have her escape/approach route bent around the very mob she is
    // running from or charging at.
    internal static bool AvoidsThreatRings(NPCState npc)
    {
        if (!Spec62.ThreatAlertEnabled ||
            npc.IsFighting ||
            npc.Mind.CurrentGoal == GoalType.Flee ||
            npc.Mind.CurrentGoal == GoalType.Defend)
        {
            return false;
        }

        return !ThreatAlertSystem.IsFitToFight(npc);
    }

    public TickLayer Layer => TickLayer.Fast;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Plan.Status != PlanStatus.Active || npc.Plan.TargetJunctionId is null)
            {
                continue;
            }

            if (npc.Movement.IsMoving && npc.Movement.JunctionPath.Count > 0)
            {
                continue;
            }

            if (npc.CurrentJunction.HasValue && npc.CurrentJunction.Value.Equals(npc.Plan.TargetJunctionId.Value))
            {
                if (SimTrace.Verbose)
                {
                    Trace.Emit(world, npc.Id, "PathAlreadyAtTarget",
                        $"Junction={npc.CurrentJunction.Value.Value} (already at destination)");
                }

                continue;
            }

            var startJunction = npc.CurrentJunction ?? SpatialQueries.FindNearestJunction(world, npc.Position);
            if (startJunction is null)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No current junction";
                Trace.Emit(world, npc.Id, "PathBlocked",
                    $"No current junction found at Pos={Trace.FormatPos(npc.Position)}");
                continue;
            }

            if (SimTrace.Verbose)
            {
                Trace.Emit(world, npc.Id, "PathSearching",
                    $"From={startJunction.Value.Value} To={npc.Plan.TargetJunctionId.Value.Value} " +
                    $"Pos={Trace.FormatPos(npc.Position)}");
            }

            // Spec 40.17: prefer flat routes by default. The old gate exempted
            // every hungry/thirsty route, including non-emergency material runs,
            // so GatherStone could sawtooth over ledges as if hops were flat.
            // Only immediate survival movement keeps the shortest-path override.
            var preferFlat = ShouldWeightClimbs(npc);
            // Spec §62: wounded/unarmed girls pay a soft cost near live mobs,
            // so their routes bend around a spotted wolf instead of past it.
            // §72: …and around a hostile person, on the same soft terms.
            var danger = RouteAvoidRing(world, npc);
            var path = HexPathfinder.FindPath(world, startJunction.Value, npc.Plan.TargetJunctionId.Value,
                OtherActorJunctions(world, npc), preferFlat, npc.Body.CanJump,
                danger, Spec62.DangerStepCost);
            if (path.Count == 0)
            {
                npc.Movement.Status = MovementStatus.Blocked;
                npc.Movement.StopReason = "No path";
                Trace.Emit(world, npc.Id, "PathFailed",
                    $"No route from Junction={startJunction.Value.Value} to Junction={npc.Plan.TargetJunctionId.Value.Value}");
                continue;
            }

            npc.Movement.JunctionPath.Clear();
            foreach (var step in path)
            {
                npc.Movement.JunctionPath.Add(step);
            }

            npc.Movement.PathIndex = 1;
            // §21.21B v15: a new path renumbers the steps, so hop state carried
            // over from the old one is nonsense. HopPathIndex is the dangerous
            // half: the wall scan is gated on `HopPathIndex != PathIndex`, and a
            // hop that started on step 1 of the PREVIOUS path left it at 1 — the
            // same value every new path starts with, so the scan was skipped on
            // the first step and she crossed the elevation border WALKING. No
            // hop, no arc, just a silent step up the cliff.
            npc.Movement.HopArmed = false;
            npc.Movement.HopPathIndex = -1;
            npc.Movement.IsMoving = path.Count > 1;
            npc.Movement.Status = npc.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived;
            npc.Movement.StopReason = string.Empty;

            var pathJunctions = new System.Text.StringBuilder();
            for (var i = 0; i < path.Count; i++)
            {
                if (i > 0) pathJunctions.Append("->");
                pathJunctions.Append(path[i].Value);
            }
            Trace.Emit(world, npc.Id, "PathBuilt",
                $"Length={path.Count} Route=[{pathJunctions}] IsMoving={npc.Movement.IsMoving}");
        }
    }

    private static bool ShouldWeightClimbs(NPCState npc)
    {
        if (!npc.Body.CanJump)
        {
            return true;
        }

        if (npc.Mind.CurrentGoal == GoalType.Flee)
        {
            return false;
        }

        return npc.Mind.CurrentGoal switch
        {
            GoalType.GetFood => !npc.Mind.IsStarving,
            GoalType.GetWater => !npc.Mind.IsDehydrated,
            GoalType.Drink => !npc.Mind.IsDehydrated,
            _ => true
        };
    }
}

}
