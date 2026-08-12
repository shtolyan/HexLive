using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// The one authoritative transition from a standing/carried actor into a bed.
/// It owns the simulation pose, sleep execution and the bed claim together, so
/// production plans, rescue and deterministic presentation fixtures cannot
/// construct subtly different sleepers.
/// </summary>
public static class BedSleep
{
    /// <summary>
    /// Places <paramref name="sleeper"/> on <paramref name="bed"/> and starts
    /// one in-progress Sleep block. <paramref name="wakeJunction"/> is the
    /// legal point from which the actor approached the furniture; it is kept
    /// only as a preferred standing location for the later wake-up.
    /// </summary>
    public static bool TryEnter(
        WorldState world, NPCState sleeper, WorldObjectState bed,
        int endTick, JunctionId? wakeJunction)
    {
        if (world is null || sleeper is null || bed is null ||
            !ContentIds.IsBed(bed.DefinitionId) ||
            !string.IsNullOrEmpty(bed.BuildProduct) ||
            bed.CurrentUser is { } currentUser && currentUser != sleeper.Id ||
            bed.IsOccupied && bed.CurrentUser is null ||
            !LyingSpot.TryAnchor(world, bed, out var routeAnchor))
        {
            return false;
        }

        ApplyPose(world, sleeper, bed, routeAnchor);
        sleeper.CurrentJunction = wakeJunction;
        sleeper.Movement.IsMoving = false;
        sleeper.Movement.JunctionPath.Clear();
        sleeper.Movement.PathIndex = 0;
        sleeper.Execution.Status = ExecutionStatus.InProgress;
        sleeper.Execution.CurrentInteraction = InteractionType.Sleep;
        sleeper.Execution.TargetObject = bed.Id;
        sleeper.Execution.StartTick = world.Tick;
        sleeper.Execution.EndTick = System.Math.Max(world.Tick + 1, endTick);
        sleeper.Execution.BuildDeposited = false;
        bed.IsOccupied = true;
        bed.CurrentUser = sleeper.Id;

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, sleeper.Id, "BedSleepEntered",
                $"Bed={bed.Id.Value} Anchor={routeAnchor.X:F3},{routeAnchor.Y:F3} " +
                $"Wake={(wakeJunction?.Value.ToString() ?? "none")} " +
                $"Position={sleeper.Position.X:F3},{sleeper.Position.Y:F3} " +
                $"Heading={sleeper.RotationDegrees:F1}");
        }

        return true;
    }

    /// <summary>
    /// Reasserts only the authored bed pose for an already sleeping actor.
    /// Used after loading legacy saves and while an interaction remains active;
    /// it deliberately does not restart the sleep clock or change ownership.
    /// </summary>
    internal static bool MaintainPose(
        WorldState world, NPCState sleeper, WorldObjectState bed)
    {
        if (world is null || sleeper is null || bed is null ||
            !ContentIds.IsBed(bed.DefinitionId) ||
            !LyingSpot.TryAnchor(world, bed, out var routeAnchor))
        {
            return false;
        }

        ApplyPose(world, sleeper, bed, routeAnchor);
        return true;
    }

    private static void ApplyPose(
        WorldState world, NPCState body, WorldObjectState bed, Float2 routeAnchor)
    {
        if (body.Tile != bed.Tile)
        {
            var previousTile = body.Tile;
            body.Tile = bed.Tile;
            SpatialMutations.MoveEntityToTile(world, body.Id, previousTile, body.Tile);
        }

        var integratedBed = IsIntegratedHutBed(world, bed);
        body.Position = integratedBed
            ? IntegratedHutBedVisualPosition(world, bed, routeAnchor)
            : routeAnchor;
        // Integrated furniture stores footprint yaw rather than character yaw.
        // A sleeper stores the head-to-feet heading, hence the +90° conversion.
        body.RotationDegrees = Wrap360(
            bed.RotationDegrees + (integratedBed ? 90f : 0f));
        body.Movement.DesiredRotationDegrees = body.RotationDegrees;
        body.Movement.DesiredDirection = Forward(body.RotationDegrees);
    }

    private static bool IsIntegratedHutBed(WorldState world, WorldObjectState bed) =>
        bed.DefinitionId == ContentIds.BedBasic &&
        (bed.Variant == ContentIds.HutBedVariant ||
         world.Tiles.Items.TryGetValue(bed.Tile, out var tile) &&
         tile.Flags.HasFlag(TileFlags.HasFloor));

    private static Float2 IntegratedHutBedVisualPosition(
        WorldState world, WorldObjectState bed, Float2 interactionAnchor)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(bed.Tile, out var objectIds))
        {
            return interactionAnchor;
        }

        foreach (var id in objectIds)
        {
            if (world.Entities.Objects.TryGetValue(id, out var candidate) &&
                candidate.DefinitionId == ContentIds.Hut1Hex)
            {
                return BuildingRules.HutBedVisualPosition(
                    HexSpatialMath.TileToWorld(bed.Tile),
                    candidate.RotationDegrees,
                    interactionAnchor);
            }
        }

        return interactionAnchor;
    }

    private static Float2 Forward(float degrees)
    {
        var radians = degrees * System.MathF.PI / 180f;
        return new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
    }

    private static float Wrap360(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }
}

}
