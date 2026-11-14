using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
using System.Collections.Generic;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §176.3 r2 (bug #735): furniture with more than one seat. The sofa has two
/// cushions, so two people sit on it side by side at the same time.
///
/// <para>
/// A seat is not a separate object and not a second <c>CurrentUser</c>: the
/// object keeps the ordinary single-owner occupancy that pickup, moving and
/// the reservation reconciler already understand. What is exclusive is the
/// STANDING junction in front of each cushion. Each seat owns one rim junction,
/// chosen deterministically from geometry (occupancy-blind), so two sitters
/// can never pick the same one and every layer — planner, manual order,
/// execution, perception, the view — derives the same seat from the same data.
/// </para>
/// </summary>
public static class FurnitureSeats
{
    public const string SofaDefinitionId = "furniture.modern_sofa";
    internal const int MealStandReserveTicks = 48;

    // Measured from the shipped mesh furniture.modern_sofa.fbx (Blender import,
    // 12 Nov 2026): the two seat cushions span y ∈ [−0.688, −0.012] and
    // [0.012, 0.688] — centres ±0.350 wu along the 1.83 wu length; x ∈
    // [−0.351, 0.181] — 0.085 wu forward of the pivot; top at z = 0.421 wu.
    // The presentation's seat offset (.085, .4205, ∓.35) is these numbers.

    /// <summary>Half the distance between the two cushion centres, wu.</summary>
    public const float SofaSeatHalfSpacing = 0.35f;

    /// <summary>Cushion centre ahead of the pivot, wu.</summary>
    public const float SofaSeatForward = 0.085f;

    /// <summary>Cushion top above the floor, wu.</summary>
    public const float SofaSeatHeight = 0.4205f;

    /// <summary>The point a seat's stand is sought around: one sub-grid row in
    /// front of the cushion (√3 · 0.375 = 0.6495 wu ahead of the centre). The
    /// stand itself is the nearest real rim junction on the cushion's side —
    /// measured: 0.469 ahead / ±0.1875 aside against the apartment wall, 0.325
    /// ahead / ±0.1875 aside for a sofa set down on the island grid.</summary>
    internal const float StandForward = 0.6495f;

    // A rim junction belongs to a seat only when it is clearly on that seat's
    // side and in front — the centre line and the back of the sofa belong to
    // nobody, so the two seats can never resolve to one junction.
    private const float SideEpsilon = 0.05f;

    public static int SeatCount(string definitionId) =>
        definitionId == SofaDefinitionId ? 2 : 1;

    public static bool IsMultiSeat(string definitionId) => SeatCount(definitionId) > 1;

    public static bool SupportsMeal(string definitionId) =>
        definitionId is ContentIds.PalmStump or "chair.basic" or "furniture.modern_chair" or SofaDefinitionId;

    /// <summary>Signed offset of a seat along the long axis (left = +).</summary>
    public static float SeatLateral(string definitionId, int seat) =>
        definitionId == SofaDefinitionId ? (seat == 0 ? SofaSeatHalfSpacing : -SofaSeatHalfSpacing) : 0f;

    /// <summary>Sit on a multi-seat object goes through the seat rules.</summary>
    internal static bool Applies(WorldObjectState obj, InteractionType interaction) =>
        obj != null && interaction == InteractionType.Sit && IsMultiSeat(obj.DefinitionId);

    /// <summary>Centre and axes of the furniture, in the same place the view
    /// draws it: the authored apartment position, else the anchor junction.</summary>
    internal static bool TryGetFrame(WorldState world, WorldObjectState obj,
        out Float2 center, out Float2 forward, out Float2 left)
    {
        center = forward = left = default;
        if (obj.Junctions.Count == 0 ||
            !world.Junctions.Items.TryGetValue(obj.Junctions[0], out var anchor) ||
            !PortalWorld.TryGetModernSeatFacing(obj.DefinitionId, obj.RotationDegrees, out var facing))
            return false;

        center = PortalWorld.TryGetApartmentFurnitureVisit(obj.Variant, out var serial) &&
            PortalWorld.TryGetApartmentFurniturePlacement(obj.DefinitionId, obj.Tile, world.Seed,
                out var authored, out _, serial)
                ? authored
                : anchor.WorldPosition;
        var radians = facing * System.MathF.PI / 180f;
        forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        left = new Float2(-forward.Y, forward.X);
        return true;
    }

    /// <summary>The rim junction a sitter of <paramref name="seat"/> stands on.
    /// Pure geometry: who is standing where does not change the answer.</summary>
    public static bool TryGetSeatStand(WorldState world, WorldObjectState obj, int seat,
        out JunctionId stand)
    {
        stand = default;
        if (seat < 0 || seat >= SeatCount(obj.DefinitionId) ||
            !TryGetFrame(world, obj, out var center, out var forward, out var left))
            return false;

        var lateral = SeatLateral(obj.DefinitionId, seat);
        var target = center + left * lateral + forward * StandForward;
        var radius = world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition)
            ? definition.ObstacleRadius : 0f;
        var rim = _rimScratch;
        SpatialQueries.CollectStandableAround(world, obj.Junctions[0], rim, 96,
            SpatialQueries.BesideReach(radius), obj, InteractionReach.RimMode);

        var best = float.MaxValue;
        var found = false;
        foreach (var id in rim)
        {
            if (!world.Junctions.Items.TryGetValue(id, out var junction)) continue;
            var relative = junction.WorldPosition - center;
            var ahead = relative.X * forward.X + relative.Y * forward.Y;
            var side = relative.X * left.X + relative.Y * left.Y;
            if (ahead <= SideEpsilon || side * System.MathF.Sign(lateral) <= SideEpsilon) continue;
            var distance = HexSpatialMath.Distance(junction.WorldPosition, target);
            if (distance < best - 1e-5f || (distance < best + 1e-5f && found && id.Value < stand.Value))
            {
                best = distance;
                stand = id;
                found = true;
            }
        }

        return found;
    }

    // Own scratch: callers (the planner's rim loop) may be holding the shared
    // ObjectApproachJunctionsScratch. Single-threaded sim, as SpatialQueries.
    private static readonly System.Collections.Generic.List<JunctionId> _rimScratch = new();

    /// <summary>Which seat a body standing on <paramref name="junction"/> uses,
    /// or −1 when it is not a seat stand of this object.</summary>
    public static int SeatAt(WorldState world, WorldObjectState obj, JunctionId? junction)
    {
        if (junction is not { } at) return -1;
        for (var seat = 0; seat < SeatCount(obj.DefinitionId); seat++)
            if (TryGetSeatStand(world, obj, seat, out var stand) && stand == at)
                return seat;
        return -1;
    }

    /// <summary>Planner/manual order: reserve the nearest free seat's stand.</summary>
    internal static bool TryReserveSeat(WorldState world, NPCState npc, WorldObjectState obj,
        int durationTicks, out JunctionId stand)
    {
        stand = default;
        if (!TryPickSeat(world, npc, obj, requireReachable: true, out var chosen)) return false;
        if (!SpatialMutations.TryReserveJunction(world, chosen, npc.Id, world.Tick, durationTicks))
            return false;
        stand = chosen;
        return true;
    }

    /// <summary>Non-mutating half: is any seat free for this person?</summary>
    internal static bool HasFreeSeat(WorldState world, NPCState npc, WorldObjectState obj,
        bool requireReachable = false) =>
        TryPickSeat(world, npc, obj, requireReachable, out _);

    private static bool TryPickSeat(WorldState world, NPCState npc, WorldObjectState obj,
        bool requireReachable, out JunctionId chosen)
    {
        chosen = default;
        var bestDistance = float.MaxValue;
        var found = false;
        var others = PathfindingSystem.OtherActorJunctions(world, npc);
        for (var seat = 0; seat < SeatCount(obj.DefinitionId); seat++)
        {
            if (!TryGetSeatStand(world, obj, seat, out var stand) ||
                !world.Junctions.Items.TryGetValue(stand, out var junction)) continue;
            var mine = npc.CurrentJunction == stand;
            if (!mine)
            {
                if (others.Contains(stand) ||
                    (world.Occupancy.JunctionOwner.TryGetValue(stand, out var owner) &&
                     owner is { } holder && holder != npc.Id) ||
                    (world.Reservations.Junctions.TryGetValue(stand, out var reservation) &&
                     reservation.Owner != npc.Id && reservation.EndTick >= world.Tick))
                    continue;
                if (requireReachable && npc.CurrentJunction is { } from &&
                    !Connectivity.Reachable(world, from, stand, PlanningSystem.CanUseRoutineTraversal(npc)))
                    continue;
            }

            var distance = mine ? -1f : HexSpatialMath.Distance(junction.WorldPosition, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                chosen = stand;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Execution gate: a second sitter on her own seat stand is not
    /// "taking" the sofa from the first one.</summary>
    internal static bool SitsOwnSeat(WorldState world, NPCState npc, WorldObjectState obj) =>
        IsMultiSeat(obj.DefinitionId) && SeatAt(world, obj, npc.CurrentJunction) >= 0;

    /// <summary>While anyone still sits, the sofa stays occupied (pickup/move
    /// refuse it). The first sitter's departure clears the claim; whoever is
    /// still seated re-asserts it on her next tick.</summary>
    internal static void MaintainOccupancy(WorldObjectState obj, NPCState npc)
    {
        if (!IsMultiSeat(obj.DefinitionId)) return;
        obj.IsOccupied = true;
        obj.CurrentUser ??= npc.Id;
    }

    internal static WorldObjectState? CurrentMealSeat(WorldState world, NPCState npc)
    {
        var id = npc.Execution.ConsumptionSeat;
        if (id == null && npc.Execution.CurrentInteraction == InteractionType.Sit &&
            npc.Execution.Status == HexLive.Simulation.AI.ExecutionStatus.InProgress)
            id = npc.Execution.TargetObject;
        return id is { } seatId && world.Entities.Objects.TryGetValue(seatId, out var seat) &&
            (seat.CurrentUser == npc.Id || SitsOwnSeat(world, npc, seat)) ? seat : null;
    }

    internal static void KeepForMeal(NPCState npc, WorldObjectState? seat)
    {
        if (seat == null) return;
        npc.Execution.ConsumptionSeat = seat.Id;
        seat.IsOccupied = true;
        seat.CurrentUser ??= npc.Id;
    }

    // The first step remains the inventory executor. Its optional target and
    // the plan's junction describe the familiar walk to a furniture rim.
    internal static bool TryPlanMealSeat(WorldState world, NPCState npc, PlanStep step)
    {
        if (!CanApproachMealSeat(npc) || !npc.CurrentJunction.HasValue) return false;

        var candidates = new List<PerceivedObject>();
        foreach (var seen in npc.Perception.Objects)
            if (seen.IsReachable && !seen.FromMemory && SupportsMeal(seen.DefinitionId))
                candidates.Add(seen);
        candidates.Sort((a, b) =>
        {
            var order = a.Distance.CompareTo(b.Distance);
            return order != 0 ? order : a.Id.Value.CompareTo(b.Id.Value);
        });

        foreach (var seen in candidates)
        {
            if (!world.Entities.Objects.TryGetValue(seen.Id, out var seat) || seat.Junctions.Count == 0 ||
                (seat.IsOccupied && seat.CurrentUser != npc.Id && !IsMultiSeat(seat.DefinitionId)) ||
                !world.Content.ObjectDefinitions.TryGetValue(seat.DefinitionId, out var definition) ||
                definition.Interactions.Find(i => i.Type == InteractionType.Sit) == null)
                continue;

            var reserved = IsMultiSeat(seat.DefinitionId)
                ? TryReserveSeat(world, npc, seat, MealStandReserveTicks, out var stand)
                : PlanningSystem.TryReserveBesideJunction(world, npc, seat.Junctions[0], MealStandReserveTicks, out stand,
                    SpatialQueries.BesideReach(definition.ObstacleRadius), seat);
            if (!reserved) continue;
            if (npc.CurrentJunction == stand &&
                !SpatialMutations.TryReserveJunction(world, stand, npc.Id, world.Tick, MealStandReserveTicks)) continue;
            step.TargetObject = seat.Id;
            step.TargetJunction = stand;
            npc.Plan.TargetJunctionId = stand;
            KeepForMeal(npc, seat);
            return true;
        }

        return false;
    }

    internal static bool CanApproachMealSeat(NPCState npc) =>
        !npc.Body.IsCrawling && !npc.IsCarryingPerson && !npc.IsBeingCarried &&
        !npc.Mind.IsStarving && !npc.Mind.IsDehydrated;

    internal static void ReleaseMealSeat(WorldState world, NPCState npc)
    {
        if (npc.Execution.ConsumptionSeat is { } id)
        {
            if (world.Entities.Objects.TryGetValue(id, out var seat) && seat.CurrentUser == npc.Id)
            {
                seat.IsOccupied = false;
                seat.CurrentUser = null;
            }
        }
        npc.Execution.ConsumptionSeat = null;
    }
}

}
