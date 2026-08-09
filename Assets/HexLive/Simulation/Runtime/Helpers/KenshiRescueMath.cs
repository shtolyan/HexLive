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
    internal static bool NeedsRescue(WorldState world, NPCState patient) =>
        patient.Health > 0f &&
        !patient.IsBeingCarried &&
        (patient.IsDying || patient.Mind.ComaCause != ComaCause.None) &&
        !(patient.Execution.CurrentInteraction == InteractionType.Sleep &&
          patient.Execution.TargetObject is { } bedId &&
          world.Entities.Objects.TryGetValue(bedId, out var bed) &&
          IsBed(bed));

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

        var best = float.MaxValue;
        foreach (var candidate in SpatialQueries.GetPassableNeighbors(world, target))
        {
            if (!SpatialQueries.IsJunctionFree(world, candidate) ||
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
        out WorldObjectState destination, out JunctionId approach)
    {
        destination = null;
        approach = default;
        if (helper.CurrentJunction is not { } from)
        {
            return false;
        }

        var bestScore = float.MaxValue;
        foreach (var candidate in world.Entities.Objects.Values)
        {
            if (!IsBed(candidate) ||
                (candidate.IsOccupied && candidate.CurrentUser != patient.Id) ||
                !DestinationSafe(world, patient, candidate.Tile) ||
                !TryObjectApproach(world, helper, candidate, from, out var stand))
            {
                continue;
            }

            var score = HexSpatialMath.HexDistance(helper.Tile, candidate.Tile);
            if (score < bestScore ||
                (System.MathF.Abs(score - bestScore) <= 0.001f &&
                 (destination is null || candidate.Id.Value < destination.Id.Value)))
            {
                bestScore = score;
                destination = candidate;
                approach = stand;
            }
        }

        if (destination is null)
        {
            foreach (var fire in world.Entities.Objects.Values)
            {
                if (fire.DefinitionId != ContentIds.Campfire || fire.ResourceAmount <= 0f ||
                    !DestinationSafe(world, patient, fire.Tile) ||
                    !TryObjectApproach(world, helper, fire, from, out var stand))
                {
                    continue;
                }

                var score = HexSpatialMath.HexDistance(helper.Tile, fire.Tile);
                if (score < bestScore ||
                    (System.MathF.Abs(score - bestScore) <= 0.001f &&
                     (destination is null || fire.Id.Value < destination.Id.Value)))
                {
                    bestScore = score;
                    destination = fire;
                    approach = stand;
                }
            }
        }

        if (destination is null ||
            !SpatialMutations.TryReserveJunction(world, approach, helper.Id, world.Tick, 240))
        {
            destination = null;
            return false;
        }

        if (IsBed(destination))
        {
            destination.IsOccupied = true;
            destination.CurrentUser = patient.Id;
        }

        return true;
    }

    private static bool TryObjectApproach(
        WorldState world, NPCState helper, WorldObjectState target, JunctionId from,
        out JunctionId approach)
    {
        approach = default;
        if (target.Junctions.Count == 0)
        {
            return false;
        }

        var best = float.MaxValue;
        foreach (var candidate in SpatialQueries.GetPassableNeighbors(world, target.Junctions[0]))
        {
            if (!SpatialQueries.IsJunctionFree(world, candidate) ||
                !Connectivity.Reachable(world, from, candidate, canJump: false) ||
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

        return best < float.MaxValue;
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
        WorldObjectState destination, JunctionId destinationJunction)
    {
        ExecutionSystem.ReleaseClaims(world, patient);
        if (patient.CurrentJunction is { } lying)
        {
            SpatialMutations.FreeJunction(world, lying, patient.Id);
            SpatialMutations.ReleaseJunctionReservation(world, lying, patient.Id);
        }

        patient.CurrentJunction = null;
        patient.CarriedByNpcId = carrier.Id;
        carrier.CarriedNpcId = patient.Id;
        carrier.RescueDestinationObjectId = destination.Id;
        carrier.IsFighting = false;
        MeleeSwing.Cancel(carrier);
        carrier.StrikeReadyAtTick = 0;

        carrier.Plan.TargetObjectId = null;
        carrier.Plan.TargetAgentId = patient.Id;
        carrier.Plan.TargetJunctionId = destinationJunction;
        carrier.Plan.TargetTile = destination.Tile;
        carrier.Plan.Steps.Clear();
        carrier.Plan.Steps.Add(new PlanStep
        {
            Type = PlanStepType.PutPersonInBed,
            TargetJunction = destinationJunction,
            TargetObject = destination.Id,
            Interaction = InteractionType.PutInBed
        });
        carrier.Plan.CurrentStepIndex = 0;
        carrier.Plan.Status = PlanStatus.Active;
        carrier.Movement.JunctionPath.Clear();
        carrier.Movement.IsMoving = false;
        carrier.Movement.SetStatus(MovementStatus.Idle);

        SyncPatient(world, carrier, patient);
        Trace.Emit(world, carrier.Id, "PersonPickedUp",
            $"NPC{patient.Id.Value} -> {destination.DefinitionId}#{destination.Id.Value}");
    }

    internal static void SyncAll(WorldState world)
    {
        foreach (var carrier in world.Entities.Npcs.Values)
        {
            if (carrier.CarriedNpcId is not { } patientId)
            {
                continue;
            }

            if (!world.Entities.Npcs.TryGetValue(patientId, out var patient) ||
                patient.CarriedByNpcId != carrier.Id || carrier.Health <= 0f ||
                carrier.IsUnconscious(world.Tick) || carrier.Body.IsProne ||
                patient.Health <= 0f ||
                carrier.Movement.Status is MovementStatus.Blocked or MovementStatus.Invalid)
            {
                DropSafely(world, carrier, "carry link/path/carrier invalid");
                continue;
            }

            SyncPatient(world, carrier, patient);
        }
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
        }

        CompleteCarrier(world, carrier);
        Trace.Emit(world, carrier.Id, "PersonPutDown",
            $"NPC{patient.Id.Value} at {(destination?.DefinitionId ?? "ground")}");
    }

    internal static void DropSafely(WorldState world, NPCState carrier, string reason)
    {
        NPCState patient = null;
        if (carrier.CarriedNpcId is { } patientId)
        {
            world.Entities.Npcs.TryGetValue(patientId, out patient);
        }

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

        if (carrier.Plan.Status == PlanStatus.Active)
        {
            PlanningSystem.SetGoalCooldown(world, carrier, GoalType.Rescue);
            PlanInterruption.Abort(world, carrier, $"Rescue drop: {reason}");
        }
        carrier.Mind.CurrentGoal = GoalType.None;
        Trace.Emit(world, carrier.Id, "PersonDropped", reason);
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
        if (patient.Execution.CurrentInteraction != InteractionType.Sleep ||
            patient.Execution.TargetObject is not { } bedId ||
            !world.Entities.Objects.TryGetValue(bedId, out var bed) || !IsBed(bed))
        {
            return;
        }

        if (bed.CurrentUser == patient.Id)
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
}

}
