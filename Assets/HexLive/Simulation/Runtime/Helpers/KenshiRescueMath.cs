using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>§116 shared rescue predicates, reservations and carry invariants.</summary>
internal static class KenshiRescueMath
{
    // Headless regression probe: a normal destination selection must perform
    // one weighted graph search, not one search per rim/interior junction.
    internal static int DestinationPathSearchesLastCall { get; private set; }

    internal static bool NeedsRescue(WorldState world, NPCState patient) =>
        patient.Health > 0f &&
        !patient.IsBeingCarried &&
        (patient.IsDying || patient.Mind.ComaCause != ComaCause.None) &&
        !IsRecoveryResting(world, patient);

    // §118.4: putting a critical patient down is the completion of one
    // evacuation, not a fresh rescue request on the next Medium tick. The
    // patient's own plan is already over, so this sleep is deliberately owned
    // by the recovery state until ExitDying/WakeFromComa clears it.
    internal static bool IsRecoveryResting(WorldState world, NPCState patient)
    {
        if (patient.Health <= 0f || patient.IsBeingCarried ||
            (!patient.IsDying && patient.Mind.ComaCause == ComaCause.None) ||
            patient.Execution.Status != ExecutionStatus.InProgress ||
            patient.Execution.CurrentInteraction != InteractionType.Sleep)
        {
            return false;
        }

        if (patient.Execution.TargetObject is { } bedId)
        {
            return world.Entities.Objects.TryGetValue(bedId, out var bed) &&
                IsBed(bed) && bed.IsOccupied && bed.CurrentUser == patient.Id;
        }

        // Ground was validated immediately before the carrier committed to the
        // route. Keep that result stable for this recovery episode: a looter
        // walking across the threat-radius edge must become a combat problem,
        // not restart the pick-up/put-down transport carousel.
        return true;
    }

    internal static bool HasOpenBleeding(NPCState patient)
    {
        foreach (var wound in patient.Wounds)
        {
            if (!wound.Stabilized && wound.Clot01 < 1f &&
                wound.Severity * (1f - wound.Heal01) > 0.001f)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsThreatened(WorldState world, NPCState patient)
    {
        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f &&
                HexSpatialMath.HexDistance(mob.Tile, patient.Tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return true;
            }
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id != patient.Id && other.Health > 0f &&
                !other.IsUnconscious(world.Tick) &&
                FactionRelations.AreHostile(other, patient) &&
                HexSpatialMath.HexDistance(other.Tile, patient.Tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool NeedsImmediateEvacuation(WorldState world, NPCState patient)
    {
        if (IsThreatened(world, patient) || System.MathF.Abs(patient.Needs.ThermalComfort) >= 0.85f)
        {
            return true;
        }

        return world.Tiles.Items.TryGetValue(patient.Tile, out var tile) &&
            SpatialQueries.IsSwimTile(tile);
    }

    internal static bool TryFindApproach(
        WorldState world, NPCState helper, NPCState patient, out JunctionId approach)
    {
        approach = default;
        var patientJunction = patient.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, patient.Position);
        if (patientJunction is not { } target)
        {
            return false;
        }

        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, helper);
        var best = float.MaxValue;
        foreach (var candidate in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (occupiedByActor.Contains(candidate) ||
                !SpatialQueries.IsJunctionFree(world, candidate) ||
                !world.Junctions.Items.TryGetValue(candidate, out var junction))
            {
                continue;
            }

            var distance = HexSpatialMath.Distance(helper.Position, junction.WorldPosition);
            if (distance < best)
            {
                best = distance;
                approach = candidate;
            }
        }

        return best < float.MaxValue &&
            SpatialMutations.TryReserveJunction(world, approach, helper.Id, world.Tick, 96);
    }

    internal static bool TryFindDestination(
        WorldState world, NPCState helper, NPCState patient,
        out WorldObjectState destination, out JunctionId approach,
        out TileCoord destinationTile, out List<JunctionId> route)
    {
        destination = null;
        approach = default;
        destinationTile = default;
        route = new List<JunctionId>();
        DestinationPathSearchesLastCall = 0;
        ReconcileBedOccupancy(world);
        if (helper.CurrentJunction is not { } from)
        {
            return false;
        }

        var occupiedByActor = PathfindingSystem.OtherActorJunctions(world, helper);
        var beds = new List<WorldObjectState>();
        foreach (var candidate in world.Entities.Objects.Values)
        {
            var priority = BedPriority(world, helper, patient, candidate);
            if (priority == int.MaxValue ||
                (candidate.IsOccupied && candidate.CurrentUser != patient.Id) ||
                !DestinationSafe(world, patient, candidate.Tile))
            {
                continue;
            }

            beds.Add(candidate);
        }

        beds.Sort((a, b) =>
        {
            var byPriority = BedPriority(world, helper, patient, a)
                .CompareTo(BedPriority(world, helper, patient, b));
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byDistance = HexSpatialMath.HexDistance(helper.Tile, a.Tile)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Tile));
            return byDistance != 0 ? byDistance : a.Id.Value.CompareTo(b.Id.Value);
        });

        foreach (var candidate in beds)
        {
            if (!TryObjectApproach(
                    world, helper, patient, candidate, from, occupiedByActor,
                    out var stand, out var candidateRoute))
            {
                continue;
            }

            destination = candidate;
            approach = stand;
            destinationTile = candidate.Tile;
            route = candidateRoute;
            break;
        }

        if (destination is null)
        {
            if (!TryGroundDestination(
                    world, helper, patient, from, occupiedByActor,
                    out destination, out approach, out destinationTile, out route))
            {
                return false;
            }
        }

        if (!SpatialMutations.TryReserveJunction(world, approach, helper.Id, world.Tick, 240))
        {
            destination = null;
            route.Clear();
            return false;
        }

        if (destination is not null && IsBed(destination))
        {
            destination.IsOccupied = true;
            destination.CurrentUser = patient.Id;
        }

        return true;
    }

    private static bool TryObjectApproach(
        WorldState world, NPCState helper, NPCState patient,
        WorldObjectState target, JunctionId from,
        HashSet<JunctionId> occupiedByActor, out JunctionId approach,
        out List<JunctionId> route)
    {
        approach = default;
        route = new List<JunctionId>();
        if (target.Junctions.Count == 0)
        {
            return false;
        }

        var obstacleRadius = world.Content.ObjectDefinitions.TryGetValue(
            target.DefinitionId, out var definition)
                ? definition.ObstacleRadius
                : 0f;
        var candidates = new List<JunctionId>();
        SpatialQueries.CollectStandableAround(
            world, target.Junctions[0], candidates, 96,
            SpatialQueries.BesideReach(obstacleRadius), target,
            SpatialQueries.RimPurpose.Route);

        candidates.Sort((a, b) =>
        {
            var aDistance = world.Junctions.Items.TryGetValue(a, out var aJunction)
                ? HexSpatialMath.Distance(helper.Position, aJunction.WorldPosition)
                : float.MaxValue;
            var bDistance = world.Junctions.Items.TryGetValue(b, out var bJunction)
                ? HexSpatialMath.Distance(helper.Position, bJunction.WorldPosition)
                : float.MaxValue;
            var byDistance = aDistance.CompareTo(bDistance);
            return byDistance != 0 ? byDistance : a.Value.CompareTo(b.Value);
        });

        foreach (var candidate in candidates)
        {
            if (!SpatialQueries.IsJunctionFree(world, candidate) ||
                !world.Junctions.Items.ContainsKey(candidate) ||
                !Connectivity.Reachable(world, from, candidate, helper.Body.CanJump))
            {
                continue;
            }

            if (TryBuildCarryRoute(
                    world, helper, patient, from, candidate, occupiedByActor,
                    out var candidateRoute))
            {
                approach = candidate;
                route = candidateRoute;
                return true;
            }
        }

        return false;
    }

    private static int BedPriority(
        WorldState world, NPCState helper, NPCState patient, WorldObjectState candidate)
    {
        if (!IsBed(candidate))
        {
            return int.MaxValue;
        }

        if (candidate.Owner == patient.Id)
        {
            return 0;
        }

        if (candidate.Owner == helper.Id)
        {
            return 1;
        }

        return ColonyQueries.InCamp(world, candidate.Tile, patient.Faction)
            ? 2
            : int.MaxValue;
    }

    private static bool TryGroundDestination(
        WorldState world, NPCState helper, NPCState patient, JunctionId from,
        HashSet<JunctionId> occupiedByActor, out WorldObjectState destination,
        out JunctionId approach, out TileCoord destinationTile,
        out List<JunctionId> route)
    {
        destination = null;
        approach = default;
        destinationTile = default;
        route = new List<JunctionId>();

        WorldObjectState hearth = null;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (candidate.DefinitionId != ContentIds.Campfire ||
                !ColonyQueries.InCamp(world, candidate.Tile, patient.Faction))
            {
                continue;
            }

            // A cold hearth still marks the camp. Stable id wins when a camp
            // has several fires, matching the ordinary ground-sleep anchor.
            if (hearth is null || candidate.Id.Value < hearth.Id.Value)
            {
                hearth = candidate;
            }
        }

        var anchor = hearth?.Tile ??
            ColonyQueries.Home(world, patient.Faction) ??
            ColonyQueries.Home(world, helper.Faction);
        if (anchor is not { } campAnchor)
        {
            return false;
        }

        var candidateTiles = new List<Tile>();
        foreach (var tile in world.Tiles.Items.Values)
        {
            if (!tile.Flags.HasFlag(TileFlags.Walkable) ||
                tile.Flags.HasFlag(TileFlags.Blocked) ||
                tile.Flags.HasFlag(TileFlags.Water) ||
                !ColonyQueries.InCamp(world, tile.Coord, patient.Faction) ||
                HexSpatialMath.HexDistance(tile.Coord, campAnchor) >
                    Spec72.MaxCampRadiusTiles)
            {
                continue;
            }

            candidateTiles.Add(tile);
        }

        candidateTiles.Sort((a, b) =>
        {
            var byAnchor = HexSpatialMath.HexDistance(a.Coord, campAnchor)
                .CompareTo(HexSpatialMath.HexDistance(b.Coord, campAnchor));
            if (byAnchor != 0)
            {
                return byAnchor;
            }

            var byHelper = HexSpatialMath.HexDistance(helper.Tile, a.Coord)
                .CompareTo(HexSpatialMath.HexDistance(helper.Tile, b.Coord));
            if (byHelper != 0)
            {
                return byHelper;
            }

            var byQ = a.Coord.Q.CompareTo(b.Coord.Q);
            return byQ != 0 ? byQ : a.Coord.R.CompareTo(b.Coord.R);
        });

        foreach (var tile in candidateTiles)
        {
            if (!DestinationSafe(world, patient, tile.Coord) ||
                !LyingSpot.CanSolveOnTile(world, patient, tile.Coord))
            {
                continue;
            }

            var center = HexSpatialMath.TileToWorld(tile.Coord);
            var junctions = new List<JunctionId>(tile.Junctions);
            junctions.Sort((a, b) =>
            {
                var aDistance = world.Junctions.Items.TryGetValue(a, out var aJunction)
                    ? HexSpatialMath.Distance(aJunction.WorldPosition, center)
                    : float.MaxValue;
                var bDistance = world.Junctions.Items.TryGetValue(b, out var bJunction)
                    ? HexSpatialMath.Distance(bJunction.WorldPosition, center)
                    : float.MaxValue;
                var byDistance = aDistance.CompareTo(bDistance);
                return byDistance != 0 ? byDistance : a.Value.CompareTo(b.Value);
            });

            foreach (var candidate in junctions)
            {
                if (SpatialQueries.IsJunctionFree(world, candidate) &&
                    world.Junctions.Items.TryGetValue(candidate, out var junction) &&
                    !junction.Blocked &&
                    !SpatialQueries.IsAllWaterJunction(world, candidate) &&
                    Connectivity.Reachable(world, from, candidate, helper.Body.CanJump) &&
                    TryBuildCarryRoute(
                        world, helper, patient, from, candidate, occupiedByActor,
                        out var candidateRoute))
                {
                    destination = hearth;
                    approach = candidate;
                    destinationTile = tile.Coord;
                    route = candidateRoute;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryBuildCarryRoute(
        WorldState world, NPCState helper, NPCState patient, JunctionId from,
        JunctionId approach, HashSet<JunctionId> occupiedByActor,
        out List<JunctionId> route)
    {
        // The current save often puts a valid ground-rest point on the same
        // hex, one lattice edge away. Sending that two-node move through the
        // island-wide weighted Dijkstra caused the visible 200-350 ms freeze
        // before every pickup. A direct graph edge has no alternate route to
        // compare: validate its carry semantics locally and execute it.
        route = new List<JunctionId> { from };
        if (from == approach)
        {
            return RouteSupportsCarryTransfers(world, helper, patient, route);
        }

        // The pathfinder deliberately permits its GOAL even when the broad
        // lying-body avoid set touches it; TryFindApproach/ground selection
        // already proved the concrete junction free. Match that rule here.
        if (world.Junctions.Items.TryGetValue(from, out var start) &&
            start.Neighbors.Contains(approach))
        {
            route.Add(approach);
            if (RouteSupportsCarryTransfers(world, helper, patient, route))
            {
                return true;
            }
        }

        DestinationPathSearchesLastCall++;
        route = HexPathfinder.FindPath(
            world, from, approach, occupiedByActor,
            weightClimb: true, canJump: helper.Body.CanJump,
            danger: PathfindingSystem.RouteAvoidRing(world, helper),
            dangerCost: Spec62.DangerStepCost);
        if (route.Count == 0 || !RouteSupportsCarryTransfers(world, helper, patient, route))
        {
            route.Clear();
            return false;
        }

        return true;
    }

    // The path itself is cheap to execute only if every directed step agrees
    // with the carry transaction: never swim, and every elevation crossing has
    // a full-body landing spot on the far side before the carrier takes off.
    private static bool RouteSupportsCarryTransfers(
        WorldState world, NPCState helper, NPCState patient, List<JunctionId> route)
    {
        if (!world.Tiles.Items.TryGetValue(helper.Tile, out var standTile))
        {
            return false;
        }

        for (var i = 1; i < route.Count; i++)
        {
            if (!world.Junctions.Items.TryGetValue(route[i], out var junction))
            {
                return false;
            }

            // §21.21B v24: the last boundary node is an arrival on the current
            // side when that side owns the node; it is not a crossing.
            if (i == route.Count - 1 && junction.Tiles.Contains(standTile.Coord))
            {
                continue;
            }

            if (!HexPathfinder.TryGetDirectedStepTile(world, route[i - 1], route[i], out var nextTile) ||
                SpatialQueries.IsSwimTile(nextTile))
            {
                return false;
            }

            if (nextTile.Elevation != standTile.Elevation &&
                !LyingSpot.CanSolveOnTile(world, patient, nextTile.Coord))
            {
                return false;
            }

            standTile = nextTile;
        }

        return true;
    }

    private static bool DestinationSafe(WorldState world, NPCState patient, TileCoord tile)
    {
        if (!world.Tiles.Items.TryGetValue(tile, out var tileState) ||
            SpatialQueries.IsSwimTile(tileState))
        {
            return false;
        }

        foreach (var mob in world.Mobs)
        {
            if (mob.Health > 0f &&
                HexSpatialMath.HexDistance(mob.Tile, tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return false;
            }
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Health > 0f && FactionRelations.AreHostile(npc, patient) &&
                HexSpatialMath.HexDistance(npc.Tile, tile) <= Spec118.RescueThreatRadiusTiles)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsBed(WorldObjectState worldObject) =>
        ContentIds.IsBed(worldObject.DefinitionId);

    internal static void BeginCarry(
        WorldState world, NPCState carrier, NPCState patient,
        WorldObjectState destination, JunctionId destinationJunction,
        TileCoord destinationTile, List<JunctionId> route)
    {
        carrier.Mind.InterruptedRescuePatientId = null;
        ReleasePatientBedOnWake(world, patient);
        ExecutionSystem.ReleaseClaims(world, patient);
        if (patient.CurrentJunction is { } lying)
        {
            SpatialMutations.FreeJunction(world, lying, patient.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lying, patient.Id);
        }

        patient.CurrentJunction = null;
        patient.CarriedByNpcId = carrier.Id;
        carrier.CarriedNpcId = patient.Id;
        carrier.RescueDestinationObjectId = destination?.Id;
        carrier.IsFighting = false;
        MeleeSwing.Cancel(carrier);
        carrier.StrikeReadyAtTick = 0;

        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = destinationJunction;
        carrier.Plan.TargetTile = destinationTile;
        carrier.Plan.Steps.Clear();
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PutPersonInBed,
            TargetJunction = destinationJunction,
            TargetObject = destination?.Id,
            Interaction = InteractionType.PutInBed
        });
        carrier.Plan.CurrentStepIndex = 0;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Movement.JunctionPath.Clear();
        foreach (var junction in route)
        {
            carrier.Movement.JunctionPath.Add(junction);
        }

        carrier.Movement.PathIndex = route.Count > 1 ? 1 : route.Count;
        carrier.Movement.BlockedWaitTicks = 0;
        carrier.Movement.HopArmed = false;
        carrier.Movement.HopPathIndex = -1;
        carrier.Movement.IsMoving = route.Count > 1;
        carrier.Movement.SetStatus(
            carrier.Movement.IsMoving ? MovementStatus.Moving : MovementStatus.Arrived);

        SyncPatient(world, carrier, patient);
        Trace.Emit(world, carrier.Id, "PersonPickedUp",
            destination is null
                ? $"NPC{patient.Id.Value} -> ground@{destinationTile.Q},{destinationTile.R} " +
                  $"Route={route.Count} Searches={DestinationPathSearchesLastCall}"
                : $"NPC{patient.Id.Value} -> {destination.DefinitionId}#{destination.Id.Value} " +
                  $"at {destinationTile.Q},{destinationTile.R} Route={route.Count} " +
                  $"Searches={DestinationPathSearchesLastCall}");
    }

    internal static void SyncAll(WorldState world)
    {
        // A normal rescue claim is already coherent. Only a loaded/interrupting
        // carry whose plan ownership vanished needs the object-wide repair on
        // a Fast tick; ordinary selection also repairs immediately above.
        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is not null &&
                (carrier.Plan.Status != PlanStatus.Active ||
                 carrier.Plan.Goal != GoalType.Rescue ||
                 carrier.Mind.CurrentGoal != GoalType.Rescue))
            {
                ReconcileBedOccupancy(world);
                break;
            }
        }

        // A staged patient waits on the far side only for the atomic hop
        // window. The first grounded tick reattaches that same patient before
        // ordinary walking can consume another route step.
        foreach (var carrier in world.Entities.Npcs.Values)
        {
            TryResumeCarryAfterHop(world, carrier);
        }

        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is not { } patientId)
            {
                continue;
            }

            if (!world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
                patient.CarriedByNpcId != carrier.Id || carrier.Health <= 0f ||
                carrier.IsLyingDown(world.Tick) ||
                patient.Health <= 0f ||
                carrier.Movement.Status == MovementStatus.Invalid)
            {
                DropSafely(world, carrier, "carry link/path/carrier invalid");
                continue;
            }

            SyncPatient(world, carrier, patient);
        }
    }

    internal static bool TryStagePatientForHop(
        WorldState world, NPCState carrier, TileCoord landingTile, Float2 landingPosition)
    {
        if (carrier.CarriedNpcId is not { } patientId ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            patient.CarriedByNpcId != carrier.Id ||
            carrier.Plan.Status != PlanStatus.Active ||
            carrier.Plan.Goal != GoalType.Rescue ||
            carrier.Plan.TargetAgentId != patient.Id ||
            !world.Tiles.Items.TryGetValue(landingTile, out var landingTileState) ||
            SpatialQueries.IsSwimTile(landingTileState) ||
            !LyingSpot.CanSolveOnTile(world, patient, landingTile))
        {
            return false;
        }

        SyncPatient(world, carrier, patient);
        patient.CarriedByNpcId = null;
        carrier.CarriedNpcId = null;
        carrier.Mind.InterruptedRescuePatientId = patient.Id;
        patient.Mind.PendingAidFrom = carrier.Id;
        patient.Mind.PendingAidSinceTick = world.Tick;

        var previousTile = patient.Tile;
        if (previousTile != landingTile)
        {
            patient.Tile = landingTile;
            SpatialMutations.MoveEntityToTile(world, patient.Id, previousTile, landingTile);
        }

        patient.Position = landingPosition;
        patient.CurrentJunction = null;
        MortalityHelpers.AnchorLyingBody(world, patient);
        Trace.Emit(world, carrier.Id, "RescuePatientStagedForHop",
            $"NPC{patient.Id.Value} Tile={landingTile.Q},{landingTile.R} " +
            $"Destination={carrier.RescueDestinationObjectId?.Value ?? 0}");
        return true;
    }

    internal static bool TryResumeCarryAfterHop(WorldState world, NPCState carrier)
    {
        if (carrier.CarriedNpcId is not null ||
            carrier.Mind.InterruptedRescuePatientId is not { } patientId ||
            carrier.Movement.HopTimer > 0f ||
            carrier.Plan.Status != PlanStatus.Active ||
            carrier.Plan.Goal != GoalType.Rescue ||
            carrier.Plan.TargetAgentId != patientId ||
            carrier.Health <= 0f || carrier.IsLyingDown(world.Tick) ||
            carrier.IsFighting ||
            !world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
            patient.CarriedByNpcId is not null || patient.Health <= 0f ||
            patient.Tile != carrier.Tile ||
            HexSpatialMath.Distance(patient.Position, carrier.Position) > HexSpatialMath.HexRadius)
        {
            return false;
        }

        ExecutionSystem.ReleaseClaims(world, patient);
        if (patient.CurrentJunction is { } lying)
        {
            SpatialMutations.FreeJunction(world, lying, patient.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lying, patient.Id);
        }

        patient.CurrentJunction = null;
        patient.CarriedByNpcId = carrier.Id;
        carrier.CarriedNpcId = patient.Id;
        carrier.Mind.InterruptedRescuePatientId = null;
        patient.Mind.PendingAidFrom = carrier.Id;
        SyncPatient(world, carrier, patient);
        Trace.Emit(world, carrier.Id, "RescuePatientRepickedAfterHop",
            $"NPC{patient.Id.Value} Step={carrier.Movement.PathIndex}/" +
            $"{carrier.Movement.JunctionPath.Count}");
        return true;
    }

    private static void SyncPatient(WorldState world, NPCState carrier, NPCState patient)
    {
        if (patient.Tile != carrier.Tile)
        {
            SpatialMutations.MoveEntityToTile(world, patient.Id, patient.Tile, carrier.Tile);
            patient.Tile = carrier.Tile;
        }

        patient.Position = carrier.Position;
        patient.RotationDegrees = carrier.RotationDegrees + 90f;
        patient.Movement.IsMoving = false;
        patient.Movement.JunctionPath.Clear();
    }

    internal static void PutDownAtDestination(WorldState world, NPCState carrier, NPCState patient)
    {
        WorldObjectState destination = null;
        if (carrier.RescueDestinationObjectId is { } destinationId)
        {
            world.Entities.Objects.TryGetValue(destinationId, out destination);
        }

        ClearLinks(carrier, patient);
        carrier.Mind.InterruptedRescuePatientId = null;
        if (destination is not null && IsBed(destination))
        {
            var anchor = LyingSpot.TryAnchor(world, destination, out var position)
                ? position
                : HexSpatialMath.TileToWorld(destination.Tile);
            LyingSpot.AlignBodyToObject(world, patient, destination, anchor);
            patient.CurrentJunction = null;
            patient.Execution.Status = ExecutionStatus.InProgress;
            patient.Execution.CurrentInteraction = InteractionType.Sleep;
            patient.Execution.TargetObject = destination.Id;
            patient.Execution.StartTick = world.Tick;
            patient.Execution.EndTick = int.MaxValue;
            destination.IsOccupied = true;
            destination.CurrentUser = patient.Id;
        }
        else
        {
            MortalityHelpers.AnchorLyingBody(world, patient);
            patient.Execution.Status = ExecutionStatus.InProgress;
            patient.Execution.CurrentInteraction = InteractionType.Sleep;
            patient.Execution.TargetObject = null;
            patient.Execution.StartTick = world.Tick;
            patient.Execution.EndTick = int.MaxValue;
        }

        CompleteCarrier(world, carrier);
        Trace.Emit(world, carrier.Id, "PersonPutDown",
            $"NPC{patient.Id.Value} at {(destination?.DefinitionId ?? "ground")}");
    }

    internal static void DropSafely(WorldState world, NPCState carrier, string reason)
    {
        var hadCarry = carrier.CarriedNpcId is not null;
        PutDownForPlanInterruption(world, carrier, reason);

        if (carrier.Plan.Status == PlanStatus.Active)
        {
            PlanningSystem.SetGoalCooldown(world, carrier, GoalType.Rescue);
            PlanInterruption.Abort(world, carrier, $"Rescue drop: {reason}");
        }
        carrier.Mind.CurrentGoal = GoalType.None;
        if (!hadCarry)
        {
            Trace.Emit(world, carrier.Id, "PersonDropped", reason);
        }
    }

    // §118.4: plan teardown and the carry link are one transaction. Before
    // this hook existed, combat could invalidate Rescue while leaving the
    // patient attached; the carrier then could neither swing nor replan.
    // This primitive deliberately does not touch the plan itself, so
    // PlanInterruption can call it without recursing through DropSafely.
    internal static EntityId? PutDownForPlanInterruption(
        WorldState world, NPCState carrier, string reason)
    {
        if (carrier.CarriedNpcId is not { } patientId)
        {
            // During a rescue hop the patient is already lying safely on the
            // landing side, but the destination reservation still belongs to
            // the active plan. An interruption must release it just like an
            // interruption one tick earlier while the patient was in hand.
            var stagedPatientId = carrier.Mind.InterruptedRescuePatientId ??
                (carrier.Plan.Goal == GoalType.Rescue ? carrier.Plan.TargetAgentId : null);
            if (stagedPatientId is { } stagedId)
            {
                world.Entities.Npcs.TryGetValue(stagedId, out var stagedPatient);
                ReleaseDestination(world, carrier, stagedPatient);
                if (stagedPatient?.Mind.PendingAidFrom == carrier.Id)
                {
                    stagedPatient.Mind.PendingAidFrom = null;
                }
                carrier.RescueDestinationObjectId = null;
            }

            return null;
        }

        world.Entities.Npcs.TryGetValue(patientId, out var patient);
        ReleaseDestination(world, carrier, patient);
        if (patient is not null)
        {
            SyncPatient(world, carrier, patient);
            ClearLinks(carrier, patient);
            MortalityHelpers.AnchorLyingBody(world, patient);
        }
        else
        {
            carrier.CarriedNpcId = null;
            carrier.RescueDestinationObjectId = null;
        }

        Trace.Emit(world, carrier.Id, "PersonDropped", reason);
        return patient?.Id;
    }

    private static void CompleteCarrier(WorldState world, NPCState carrier)
    {
        if (carrier.Plan.TargetJunctionId is { } destinationJunction)
        {
            SpatialMutations.ReleaseJunctionReservation(world, destinationJunction, carrier.Id);
        }

        carrier.Execution.Status = ExecutionStatus.None;
        carrier.Execution.CurrentInteraction = null;
        carrier.Execution.StartTick = 0;
        carrier.Execution.EndTick = 0;
        carrier.Plan.Status = PlanStatus.Completed;
        carrier.Plan.Steps.Clear();
        carrier.Plan.TargetAgentId = null;
        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetJunctionId = null;
        carrier.Plan.TargetTile = null;
        carrier.Mind.CurrentGoal = GoalType.None;
        carrier.Movement.JunctionPath.Clear();
        carrier.Movement.IsMoving = false;
        carrier.Movement.SetStatus(MovementStatus.Idle);
    }

    private static void ClearLinks(NPCState carrier, NPCState patient)
    {
        patient.CarriedByNpcId = null;
        patient.Mind.PendingAidFrom = null;
        carrier.CarriedNpcId = null;
        carrier.RescueDestinationObjectId = null;
    }

    private static void ReleaseDestination(WorldState world, NPCState carrier, NPCState patient)
    {
        if (carrier.RescueDestinationObjectId is not { } destinationId ||
            !world.Entities.Objects.TryGetValue(destinationId, out var destination))
        {
            return;
        }

        if (IsBed(destination) &&
            (patient is null || destination.CurrentUser == patient.Id))
        {
            destination.IsOccupied = false;
            destination.CurrentUser = null;
        }
    }

    internal static void ReleasePatientBedOnWake(WorldState world, NPCState patient)
    {
        if (patient.Execution.CurrentInteraction != InteractionType.Sleep)
        {
            return;
        }

        if (patient.Execution.TargetObject is { } bedId &&
            world.Entities.Objects.TryGetValue(bedId, out var bed) && IsBed(bed) &&
            bed.CurrentUser == patient.Id)
        {
            bed.IsOccupied = false;
            bed.CurrentUser = null;
        }

        patient.Execution.Status = ExecutionStatus.None;
        patient.Execution.CurrentInteraction = null;
        patient.Execution.TargetObject = null;
        patient.Execution.StartTick = 0;
        patient.Execution.EndTick = 0;
        patient.CurrentJunction = SpatialQueries.FindNearestJunction(world, patient.Position);
    }

    // Saves can contain an object claim after its sleeper was picked up or its
    // plan was interrupted. Such a claim must not force every later rescue to
    // the ground. The only live bed users are an actual sleeper, or the exact
    // patient currently being carried to that bed.
    private static void ReconcileBedOccupancy(WorldState world)
    {
        foreach (var bed in world.Entities.Objects.Values)
        {
            if (!IsBed(bed) || (!bed.IsOccupied && bed.CurrentUser is null))
            {
                continue;
            }

            var live = false;
            if (bed.CurrentUser is { } userId &&
                world.Entities.Npcs.TryGetValue(userId, out var user))
            {
                live = !user.IsBeingCarried &&
                    user.Execution.Status == ExecutionStatus.InProgress &&
                    user.Execution.CurrentInteraction == InteractionType.Sleep &&
                    user.Execution.TargetObject == bed.Id;

                if (!live && user.CarriedByNpcId is { } carrierId &&
                    world.Entities.Npcs.TryGetValue(carrierId, out var carrier))
                {
                    live = carrier.CarriedNpcId == user.Id &&
                        carrier.RescueDestinationObjectId == bed.Id;
                }

                if (!live)
                {
                    foreach (var candidateCarrier in world.Entities.Npcs.Values)
                    {
                        if (candidateCarrier.Plan.Status == PlanStatus.Active &&
                            candidateCarrier.Plan.Goal == GoalType.Rescue &&
                            candidateCarrier.Plan.TargetAgentId == user.Id &&
                            candidateCarrier.RescueDestinationObjectId == bed.Id &&
                            (candidateCarrier.CarriedNpcId == user.Id ||
                             candidateCarrier.Mind.InterruptedRescuePatientId == user.Id))
                        {
                            live = true;
                            break;
                        }
                    }
                }
            }

            if (live)
            {
                bed.IsOccupied = true;
                continue;
            }

            bed.IsOccupied = false;
            bed.CurrentUser = null;
        }
    }
}

}
