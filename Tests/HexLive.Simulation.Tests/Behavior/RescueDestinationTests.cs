using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

[NonParallelizable]
public sealed class RescueDestinationTests
{
    [Test]
    public void PatientWithoutBed_UsesHelpersBedBeforeSharedBed_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj => KenshiRescueMath.IsBed(obj));

        var sites = ReachableBuildSites(world, helper, patient);
        Assert.That(sites.Count, Is.GreaterThanOrEqualTo(2));

        var helperBed = SpawnUsableBed(world, helper, patient, sites, helper.Id);

        helperBed.IsOccupied = true;
        helperBed.CurrentUser = helper.Id;
        var shared = SpawnUsableBed(world, helper, patient, sites, owner: null);
        helperBed.IsOccupied = false;
        helperBed.CurrentUser = null;

        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out _, out _, out var route);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.SameAs(helperBed),
                "When the patient has no bed, the rescuer's bed outranks a shared bed.");
            Assert.That(helperBed.CurrentUser, Is.EqualTo(patient.Id));
            Assert.That(route, Is.Not.Empty);
            Assert.That(KenshiRescueMath.DestinationPathSearchesLastCall, Is.EqualTo(1),
                "Bed selection must not run Dijkstra once per rim junction.");
        });
    }

    [Test]
    public void NoBeds_ColdCampfireStillProvidesSafeGround_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj =>
            KenshiRescueMath.IsBed(obj) || obj.DefinitionId == ContentIds.Campfire);

        var fireSite = ReachableBuildSites(world, helper, patient)[0];
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, helper.Fragment, fireSite.Tile, fireSite.Junction);
        fire.ResourceAmount = 0f;

        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile, out var route);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.SameAs(fire),
                "A cold hearth remains the camp anchor for a bedless rescue.");
            Assert.That(world.Tiles.Items[destinationTile].Flags.HasFlag(TileFlags.Water), Is.False);
            Assert.That(route, Is.Not.Empty,
                "Pickup must be preceded by the actual reusable carry route.");
            Assert.That(route[^1], Is.EqualTo(approach));
            Assert.That(KenshiRescueMath.DestinationPathSearchesLastCall, Is.EqualTo(1),
                "Ground selection must not run Dijkstra for all 37 interior nodes.");
        });

        KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile, route);
        Assert.Multiple(() =>
        {
            Assert.That(helper.CarriedNpcId, Is.EqualTo(patient.Id));
            Assert.That(patient.CarriedByNpcId, Is.EqualTo(helper.Id));
            Assert.That(helper.Plan.TargetTile, Is.EqualTo(destinationTile));
            Assert.That(helper.Movement.JunctionPath, Is.EqualTo(route),
                "The preflight route must be executed, not discarded and rebuilt.");
        });
    }

    [Test]
    public void NoBedsAndNoCampfire_UsesFactionHomeGround_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        RemoveObjects(world, obj =>
            KenshiRescueMath.IsBed(obj) || obj.DefinitionId == ContentIds.Campfire);

        Assert.That(ColonyQueries.Home(world, patient.Faction), Is.Not.Null);
        var found = KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile, out var route);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(destination, Is.Null);
            Assert.That(HexSpatialMath.HexDistance(
                    destinationTile, ColonyQueries.Home(world, patient.Faction)!.Value),
                Is.LessThanOrEqualTo(Spec72.MaxCampRadiusTiles));
            Assert.That(LyingSpot.CanSolveOnTile(world, patient, destinationTile), Is.True);
        });

        Assert.DoesNotThrow(() => KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile, route));
        Assert.That(helper.RescueDestinationObjectId, Is.Null);
        Assert.That(helper.CarriedNpcId, Is.EqualTo(patient.Id));
    }

    [Test]
    public void RescueHop_StagesOnLandingSide_ThenRepicksSamePatient_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.Exhaustion;

        Assert.That(TryFindSafeJumpEdge(
            world, patient, out var from, out var to, out var after,
            out var takeoffTile, out var landingTile),
            Is.True, "Fixture needs one dry elevation crossing with room for a full body.");

        Relocate(world, helper, takeoffTile, from);
        Relocate(world, patient, takeoffTile, from);
        ExecutionSystem.ReleaseClaims(world, patient);
        patient.CurrentJunction = null;
        patient.CarriedByNpcId = helper.Id;
        helper.CarriedNpcId = patient.Id;
        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = after;
        helper.Plan.TargetTile = landingTile;
        helper.Plan.Status = PlanStatus.Active;
        helper.Movement.JunctionPath.Clear();
        helper.Movement.JunctionPath.Add(from);
        helper.Movement.JunctionPath.Add(to);
        helper.Movement.JunctionPath.Add(after);
        helper.Movement.PathIndex = 1;
        var finalDestination = world.Entities.Objects.Values
            .FirstOrDefault(KenshiRescueMath.IsBed)?.Id;
        Assert.That(finalDestination, Is.Not.Null);
        helper.RescueDestinationObjectId = finalDestination;

        var staged = KenshiRescueMath.TryStagePatientForHop(
            world, helper, landingTile, HexSpatialMath.TileToWorld(landingTile));

        Assert.Multiple(() =>
        {
            Assert.That(staged, Is.True);
            Assert.That(helper.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(helper.Mind.InterruptedRescuePatientId, Is.EqualTo(patient.Id));
            Assert.That(patient.Tile, Is.EqualTo(landingTile));
            Assert.That(patient.CurrentJunction, Is.Not.Null,
                "The staged patient must occupy a real ground spot.");
            Assert.That(helper.RescueDestinationObjectId, Is.EqualTo(finalDestination),
                "The final bed/ground destination survives the hop transfer.");
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });

        Relocate(world, helper, landingTile, to);
        helper.Position = patient.Position;
        helper.Movement.HopTimer = 0f;

        var repicked = KenshiRescueMath.TryResumeCarryAfterHop(world, helper);

        Assert.Multiple(() =>
        {
            Assert.That(repicked, Is.True);
            Assert.That(helper.CarriedNpcId, Is.EqualTo(patient.Id));
            Assert.That(patient.CarriedByNpcId, Is.EqualTo(helper.Id));
            Assert.That(helper.Mind.InterruptedRescuePatientId, Is.Null);
            Assert.That(helper.RescueDestinationObjectId, Is.EqualTo(finalDestination));
            Assert.That(helper.Movement.JunctionPath, Is.EqualTo(new[] { from, to, after }),
                "Repick must continue the same route without a fresh destination search.");
        });
    }

    [Test]
    public void InterruptedStagedHop_ReleasesFinalBedReservation_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.Exhaustion;
        Assert.That(TryFindSafeJumpEdge(
            world, patient, out var from, out _, out var after,
            out var takeoffTile, out var landingTile), Is.True);
        Relocate(world, helper, takeoffTile, from);
        Relocate(world, patient, takeoffTile, from);
        patient.CurrentJunction = null;
        patient.CarriedByNpcId = helper.Id;
        helper.CarriedNpcId = patient.Id;
        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = after;
        helper.Plan.Status = PlanStatus.Active;
        var bed = world.Entities.Objects.Values.First(KenshiRescueMath.IsBed);
        bed.IsOccupied = true;
        bed.CurrentUser = patient.Id;
        helper.RescueDestinationObjectId = bed.Id;

        Assert.That(KenshiRescueMath.TryStagePatientForHop(
            world, helper, landingTile, HexSpatialMath.TileToWorld(landingTile)), Is.True);

        PlanInterruption.Abort(world, helper, "test interruption while patient is staged");

        Assert.Multiple(() =>
        {
            Assert.That(bed.IsOccupied, Is.False);
            Assert.That(bed.CurrentUser, Is.Null);
            Assert.That(helper.RescueDestinationObjectId, Is.Null);
            Assert.That(helper.Mind.InterruptedRescuePatientId, Is.Null);
            Assert.That(patient.Mind.PendingAidFrom, Is.Null);
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        });
    }

    [Test]
    public void MovementSystem_PerformsRescueHopTransferAtActualTakeoff_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.Exhaustion;
        Assert.That(TryFindSafeJumpEdge(
            world, patient, out var from, out var to, out var after,
            out var takeoffTile, out var landingTile), Is.True);

        Relocate(world, helper, takeoffTile, from);
        Relocate(world, patient, takeoffTile, from);
        ExecutionSystem.ReleaseClaims(world, patient);
        patient.CurrentJunction = null;
        patient.CarriedByNpcId = helper.Id;
        helper.CarriedNpcId = patient.Id;
        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = after;
        helper.Plan.TargetTile = landingTile;
        helper.Plan.Status = PlanStatus.Active;
        helper.Movement.JunctionPath.Clear();
        helper.Movement.JunctionPath.Add(from);
        helper.Movement.JunctionPath.Add(to);
        helper.Movement.JunctionPath.Add(after);
        helper.Movement.PathIndex = 1;
        helper.Movement.HopPathIndex = -1;
        helper.Movement.IsMoving = true;
        helper.Movement.SetStatus(MovementStatus.Moving);

        var movement = new MovementSystem();
        var staged = false;
        var repicked = false;
        for (var tick = 0; tick < 96; tick++)
        {
            movement.Run(world);
            staged |= helper.Mind.InterruptedRescuePatientId == patient.Id &&
                helper.CarriedNpcId is null && patient.Tile == landingTile;
            if (staged && helper.CarriedNpcId == patient.Id &&
                patient.CarriedByNpcId == helper.Id && helper.Tile == landingTile)
            {
                repicked = true;
                break;
            }

            world.Tick++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(staged, Is.True,
                "Patient must be put down only when the real hop launches.");
            Assert.That(repicked, Is.True,
                "The first grounded movement tick must pick the same patient up.");
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(helper.Movement.JunctionPath, Is.EqualTo(new[] { from, to, after }));
        });
    }

    private static (NPCState Helper, NPCState Patient) RescuePair(WorldState world)
    {
        var pair = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .Take(2)
            .ToList();
        Assert.That(pair, Has.Count.EqualTo(2));
        foreach (var npc in pair)
        {
            if (npc.CurrentJunction is null)
            {
                npc.CurrentJunction = SpatialQueries.FindNearestJunction(world, npc.Position);
            }

            Assert.That(npc.CurrentJunction, Is.Not.Null);
        }

        return (pair[0], pair[1]);
    }

    private static List<(TileCoord Tile, JunctionId Junction)> ReachableBuildSites(
        WorldState world, NPCState helper, NPCState patient)
    {
        var sites = new List<(TileCoord Tile, JunctionId Junction)>();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (tile.Coord == helper.Tile || tile.Coord == patient.Tile ||
                !ColonyQueries.InCamp(world, tile.Coord, patient.Faction) ||
                !StructurePlacement.HexFreeForBuild(world, tile.Coord) ||
                StructurePlacement.CenterJunction(world, tile.Coord) is not { } center ||
                HexPathfinder.FindPath(
                    world, helper.CurrentJunction!.Value, center, null,
                    weightClimb: true, canJump: false).Count == 0)
            {
                continue;
            }

            sites.Add((tile.Coord, center));
        }

        sites.Sort((a, b) =>
        {
            var byDistance = HexSpatialMath.HexDistance(helper.Tile, a.Tile)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Tile));
            if (byDistance != 0)
            {
                return byDistance;
            }

            var byQ = a.Tile.Q.CompareTo(b.Tile.Q);
            return byQ != 0 ? byQ : a.Tile.R.CompareTo(b.Tile.R);
        });
        return sites;
    }

    private static WorldObjectState SpawnUsableBed(
        WorldState world, NPCState helper, NPCState patient,
        List<(TileCoord Tile, JunctionId Junction)> sites, EntityId? owner)
    {
        for (var i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            if (!StructurePlacement.HexFreeForBuild(world, site.Tile))
            {
                continue;
            }

            var bed = WorldObjectMutations.SpawnObject(
                world, ContentIds.BedBasic, new FragmentId(1), site.Tile, site.Junction);
            bed.Owner = owner;
            var found = KenshiRescueMath.TryFindDestination(
                world, helper, patient, out var selected, out var approach, out _, out _);
            if (found)
            {
                SpatialMutations.ReleaseJunctionReservation(world, approach, helper.Id);
            }

            if (found && selected == bed)
            {
                if (bed.CurrentUser == patient.Id)
                {
                    bed.IsOccupied = false;
                    bed.CurrentUser = null;
                }

                sites.RemoveAt(i);
                return bed;
            }

            WorldObjectMutations.DespawnObject(world, bed.Id);
        }

        Assert.Fail("Test world has no physically reachable bed placement.");
        return null;
    }

    private static bool TryFindSafeJumpEdge(
        WorldState world, NPCState patient, out JunctionId from, out JunctionId to,
        out JunctionId after,
        out TileCoord takeoffTile, out TileCoord landingTile)
    {
        foreach (var junction in world.Junctions.Items.Values.OrderBy(j => j.Id.Value))
        {
            foreach (var neighborId in junction.Neighbors.OrderBy(id => id.Value))
            {
                if (!HexPathfinder.RequiresJump(world, junction.Id, neighborId) ||
                    !HexPathfinder.TryGetDirectedStepTile(
                        world, neighborId, junction.Id, out var source) ||
                    !HexPathfinder.TryGetDirectedStepTile(
                        world, junction.Id, neighborId, out var landing) ||
                    SpatialQueries.IsSwimTile(source) ||
                    SpatialQueries.IsSwimTile(landing) ||
                    !LyingSpot.CanSolveOnTile(world, patient, landing.Coord))
                {
                    continue;
                }

                if (!world.Junctions.Items.TryGetValue(neighborId, out var landingJunction))
                {
                    continue;
                }

                foreach (var afterId in landingJunction.Neighbors.OrderBy(id => id.Value))
                {
                    if (afterId == junction.Id ||
                        !HexPathfinder.TryGetDirectedStepTile(
                            world, neighborId, afterId, out var afterTile) ||
                        afterTile.Elevation != landing.Elevation ||
                        SpatialQueries.IsSwimTile(afterTile))
                    {
                        continue;
                    }

                    from = junction.Id;
                    to = neighborId;
                    after = afterId;
                    takeoffTile = source.Coord;
                    landingTile = landing.Coord;
                    return true;
                }
            }
        }

        from = default;
        to = default;
        after = default;
        takeoffTile = default;
        landingTile = default;
        return false;
    }

    private static void Relocate(
        WorldState world, NPCState npc, TileCoord tile, JunctionId junction)
    {
        if (npc.CurrentJunction is { } occupied)
        {
            SpatialMutations.FreeJunction(world, occupied, npc.Id);
        }

        var previous = npc.Tile;
        npc.Tile = tile;
        SpatialMutations.MoveEntityToTile(world, npc.Id, previous, tile);
        npc.CurrentJunction = junction;
        npc.Position = world.Junctions.Items[junction].WorldPosition;
    }

    private static void RemoveObjects(
        WorldState world, System.Func<WorldObjectState, bool> predicate)
    {
        foreach (var id in world.Entities.Objects.Values
                     .Where(predicate).Select(obj => obj.Id).ToList())
        {
            WorldObjectMutations.DespawnObject(world, id);
        }
    }
}

}
