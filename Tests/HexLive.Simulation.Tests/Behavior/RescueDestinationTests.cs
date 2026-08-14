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
        helper.Execution.Status = ExecutionStatus.InProgress;
        helper.Execution.CurrentInteraction = InteractionType.Sleep;
        helper.Execution.TargetObject = helperBed.Id;
        var shared = SpawnUsableBed(world, helper, patient, sites, owner: null);
        helperBed.IsOccupied = false;
        helperBed.CurrentUser = null;
        helper.Execution.Status = ExecutionStatus.None;
        helper.Execution.CurrentInteraction = null;
        helper.Execution.TargetObject = null;

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
    public void GroundDelivery_BecomesRecoveryRestInsteadOfAnotherRescue_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        RemoveObjects(world, obj => KenshiRescueMath.IsBed(obj));

        Assert.That(KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile, out var route), Is.True);
        KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile, route);
        Relocate(world, helper, destinationTile, approach);
        KenshiRescueMath.SyncAll(world);

        KenshiRescueMath.PutDownAtDestination(world, helper, patient);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
            Assert.That(patient.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep));
            Assert.That(patient.Execution.TargetObject, Is.Null);
            Assert.That(KenshiRescueMath.IsRecoveryResting(world, patient), Is.True);
            Assert.That(KenshiRescueMath.NeedsRescue(world, patient), Is.False,
                "Safe camp ground must complete the evacuation instead of reopening it every Medium tick.");
        });

        // Exact save failure: a looter crossed the threat-radius edge after
        // delivery and caused two more complete carries. The route was already
        // validated; later danger is handled by combat, not transport churn.
        helper.Faction = Faction.Outsiders;
        helper.Tile = patient.Tile;
        helper.Position = patient.Position;

        new PlanningSystem().Run(world);
        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
                "The orphan-interaction guard must not wake recovery sleep.");
            Assert.That(world.Entities.Npcs.Values.Any(npc =>
                npc.Mind.CurrentGoal == GoalType.Rescue &&
                npc.Plan.TargetAgentId == patient.Id), Is.False,
                "The same safely delivered patient was auctioned again.");
        });
    }

    [Test]
    public void BedDelivery_SurvivesPatientsCompletedPlan_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        RemoveObjects(world, KenshiRescueMath.IsBed);
        var bed = SpawnUsableBed(
            world, helper, patient, ReachableBuildSites(world, helper, patient), patient.Id);

        Assert.That(KenshiRescueMath.TryFindDestination(
            world, helper, patient, out var destination, out var approach,
            out var destinationTile, out var route), Is.True);
        Assert.That(destination, Is.SameAs(bed));
        KenshiRescueMath.BeginCarry(
            world, helper, patient, destination, approach, destinationTile, route);
        Relocate(world, helper, destinationTile, approach);
        KenshiRescueMath.SyncAll(world);
        KenshiRescueMath.PutDownAtDestination(world, helper, patient);

        patient.Plan.Status = PlanStatus.Completed;
        patient.Plan.Goal = GoalType.None;
        patient.Mind.CurrentGoal = GoalType.None;
        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(patient.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep),
                "A completed personal plan must not tear down rescue-owned sleep.");
            Assert.That(bed.IsOccupied, Is.True);
            Assert.That(bed.CurrentUser, Is.EqualTo(patient.Id));
            Assert.That(KenshiRescueMath.NeedsRescue(world, patient), Is.False);
        });
    }

    [Test]
    public void SyncAll_ClearsCrossStaleBedClaimsFromSave_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (carrier, patient) = RescuePair(world);
        var beds = world.Entities.Objects.Values.Where(KenshiRescueMath.IsBed).Take(2).ToList();
        Assert.That(beds, Has.Count.EqualTo(2), "Fixture needs the two colony beds from the save scenario.");

        carrier.CarriedNpcId = patient.Id;
        patient.CarriedByNpcId = carrier.Id;
        patient.CurrentJunction = null;
        carrier.Mind.CurrentGoal = GoalType.None;
        carrier.Plan.Goal = GoalType.None;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.RescueDestinationObjectId = null; // the real destination is ground
        beds[0].IsOccupied = true;
        beds[0].CurrentUser = patient.Id;
        beds[1].IsOccupied = true;
        beds[1].CurrentUser = carrier.Id;

        KenshiRescueMath.SyncAll(world);

        Assert.Multiple(() =>
        {
            Assert.That(beds[0].IsOccupied, Is.False);
            Assert.That(beds[0].CurrentUser, Is.Null);
            Assert.That(beds[1].IsOccupied, Is.False);
            Assert.That(beds[1].CurrentUser, Is.Null);
        });
    }

    [Test]
    public void RescueAuction_PrearmsAdjacentPatientApproach_Bug91()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = npc.Id == helper.Id ? PlanStatus.Completed : PlanStatus.Active;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
        }

        patient.Plan.Status = PlanStatus.Completed;
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.Health = System.Math.Max(0.2f, patient.Body.Mean());
        Relocate(world, patient, helper.Tile, helper.CurrentJunction!.Value);

        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.Mind.CurrentGoal, Is.EqualTo(GoalType.Rescue));
            Assert.That(helper.Movement.JunctionPath, Has.Count.EqualTo(2));
            Assert.That(helper.Movement.JunctionPath[0], Is.EqualTo(helper.CurrentJunction));
            Assert.That(helper.Movement.JunctionPath[1], Is.EqualTo(helper.Plan.TargetJunctionId));
            Assert.That(helper.Movement.IsMoving, Is.True,
                "An adjacent patient approach must already be armed before PathfindingSystem runs.");
        });
    }

    [Test]
    public void InterruptedCarry_ReleasesFinalBedReservation()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.Exhaustion;
        var junction = helper.CurrentJunction!.Value;
        Relocate(world, patient, helper.Tile, junction);
        ExecutionSystem.ReleaseClaims(world, patient);
        patient.CurrentJunction = null;
        patient.CarriedByNpcId = helper.Id;
        helper.CarriedNpcId = patient.Id;
        helper.Mind.CurrentGoal = GoalType.Rescue;
        helper.Plan.Goal = GoalType.Rescue;
        helper.Plan.TargetAgentId = patient.Id;
        helper.Plan.TargetJunctionId = junction;
        helper.Plan.Status = PlanStatus.Active;
        var bed = world.Entities.Objects.Values.First(KenshiRescueMath.IsBed);
        bed.IsOccupied = true;
        bed.CurrentUser = patient.Id;
        helper.RescueDestinationObjectId = bed.Id;

        PlanInterruption.TryAbort(world, helper, InterruptionCause.Auction, "test interruption while carrying patient");

        Assert.Multiple(() =>
        {
            Assert.That(bed.IsOccupied, Is.False);
            Assert.That(bed.CurrentUser, Is.Null);
            Assert.That(helper.RescueDestinationObjectId, Is.Null);
            Assert.That(helper.Mind.InterruptedRescuePatientId, Is.Null);
            Assert.That(patient.Mind.PendingAidFrom, Is.Null);
            Assert.That(helper.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
        });
    }

    [Test]
    public void MovementSystem_KeepsPatientLinkedThroughoutRescueHop()
    {
        var world = TestWorld.CreateWorld(251173145);
        var (helper, patient) = RescuePair(world);
        patient.Mind.ComaCause = ComaCause.Exhaustion;
        Assert.That(TryFindDryJumpEdge(
            world, out var from, out var to, out var after,
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
        var finalDestination = world.Entities.Objects.Values
            .FirstOrDefault(KenshiRescueMath.IsBed)?.Id;
        Assert.That(finalDestination, Is.Not.Null);
        helper.RescueDestinationObjectId = finalDestination;

        var movement = new MovementSystem();
        var sawHopWindow = false;
        var landed = false;
        var linksStayedAttached = true;
        var patientStayedSynchronized = true;
        for (var tick = 0; tick < 96; tick++)
        {
            movement.Run(world);
            sawHopWindow |= helper.Movement.HopTimer > 0f;
            landed |= helper.Tile == landingTile;
            linksStayedAttached &= helper.CarriedNpcId == patient.Id &&
                patient.CarriedByNpcId == helper.Id &&
                helper.Mind.InterruptedRescuePatientId is null;
            patientStayedSynchronized &= patient.Tile == helper.Tile &&
                HexSpatialMath.Distance(patient.Position, helper.Position) <= 0.0001f;
            if (landed && helper.Movement.HopTimer <= 0f)
            {
                break;
            }

            world.Tick++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawHopWindow, Is.True, "The carrier must enter the real hop window.");
            Assert.That(landed, Is.True, "The carrier and patient must cross the elevation seam.");
            Assert.That(linksStayedAttached, Is.True,
                "A hop must never stage or detach the carried patient.");
            Assert.That(patientStayedSynchronized, Is.True,
                "The carried body must follow the carrier in the same simulation tick.");
            Assert.That(helper.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(helper.Movement.JunctionPath, Is.EqualTo(new[] { from, to, after }));
            Assert.That(helper.RescueDestinationObjectId, Is.EqualTo(finalDestination));
        });
    }

    [Test]
    public void RescueAuction_DoesNotSelectCryingCarrier_Bug99()
    {
        var world = TestWorld.CreateWorld(327779939);
        world.Tick = 1931;
        var (helper, patient) = RescuePair(world);

        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Plan.Status = PlanStatus.Active;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Mind.CurrentGoal = GoalType.None;
            npc.Mind.PendingAidFrom = null;
        }

        helper.Plan.Status = PlanStatus.Completed;
        helper.Mind.CryingUntilTick = world.Tick + 120;
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.Health = System.Math.Max(0.2f, patient.Body.Mean());

        new RescueSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(helper.IsCrying(world.Tick), Is.True);
            Assert.That(helper.IsLyingDown(world.Tick), Is.True);
            Assert.That(helper.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Rescue));
            Assert.That(helper.Plan.TargetAgentId, Is.Not.EqualTo(patient.Id));
            Assert.That(patient.Mind.PendingAidFrom, Is.Null);
        });
    }

    [Test]
    public void CryingCarrier_DropsPatientBeforeNextMovement_Bug99()
    {
        var world = TestWorld.CreateWorld(327779939);
        world.Tick = 1931;
        var (carrier, patient) = RescuePair(world);
        BeginTestCarry(world, carrier, patient);
        carrier.Mind.CryingUntilTick = world.Tick + 120;
        var carrierPosition = carrier.Position;

        new MovementSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(carrier.Movement.IsMoving, Is.False,
                "The lying pose must not slide along the old rescue route.");
            Assert.That(carrier.Position, Is.EqualTo(carrierPosition));
            Assert.That(patient.Tile, Is.EqualTo(carrier.Tile));
            Assert.That(patient.Position, Is.Not.EqualTo(carrier.Position),
                "The dropped patient must use a neighbouring non-overlapping lying spot.");
            Assert.That(patient.CurrentJunction, Is.Not.Null);
        });
    }

    [Test]
    public void ExhaustedCarrier_FallsAsleepAndLeavesPatientBesideHer_Bug99()
    {
        var world = TestWorld.CreateWorld(327779939);
        world.Tick = 1931;
        var (carrier, patient) = RescuePair(world);
        BeginTestCarry(world, carrier, patient);
        carrier.Needs.Energy = 0f;

        NeedsDecaySystem.EnterComa(world, carrier, ComaCause.Exhaustion);

        Assert.Multiple(() =>
        {
            Assert.That(carrier.Mind.ComaCause, Is.EqualTo(ComaCause.Exhaustion));
            Assert.That(carrier.IsLyingDown(world.Tick), Is.True);
            Assert.That(carrier.Plan.Status, Is.EqualTo(PlanStatus.Invalid));
            Assert.That(carrier.CarriedNpcId, Is.Null);
            Assert.That(patient.CarriedByNpcId, Is.Null);
            Assert.That(patient.Tile, Is.EqualTo(carrier.Tile));
            Assert.That(patient.Position, Is.Not.EqualTo(carrier.Position));
            Assert.That(HexSpatialMath.Distance(patient.Position, carrier.Position),
                Is.GreaterThanOrEqualTo(LyingSpot.BodyHalfWidth * 2f - 0.001f),
                "The solver must leave at least one full body width between centres.");
        });
    }

    private static void BeginTestCarry(WorldState world, NPCState carrier, NPCState patient)
    {
        patient.Mind.ComaCause = ComaCause.BloodLoss;
        patient.Health = System.Math.Max(0.2f, patient.Body.Mean());
        ExecutionSystem.ReleaseClaims(world, patient);
        if (patient.CurrentJunction is { } patientJunction)
        {
            SpatialMutations.FreeJunction(world, patientJunction, patient.Id);
            SpatialMutations.ReleaseJunctionReservation(world, patientJunction, patient.Id);
        }

        patient.CurrentJunction = null;
        patient.Tile = carrier.Tile;
        patient.Position = carrier.Position;
        patient.CarriedByNpcId = carrier.Id;
        patient.Mind.PendingAidFrom = carrier.Id;
        carrier.CarriedNpcId = patient.Id;
        carrier.Mind.CurrentGoal = GoalType.Rescue;
        carrier.Plan.Goal = GoalType.Rescue;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = carrier.CurrentJunction;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Movement.JunctionPath.Clear();
        if (carrier.CurrentJunction is { } carrierJunction)
        {
            carrier.Movement.JunctionPath.Add(carrierJunction);
        }
        carrier.Movement.PathIndex = 0;
        carrier.Movement.IsMoving = true;
        carrier.Movement.SetStatus(MovementStatus.Moving);
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

    private static bool TryFindDryJumpEdge(
        WorldState world, out JunctionId from, out JunctionId to,
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
                    SpatialQueries.IsSwimTile(landing))
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
