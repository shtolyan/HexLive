using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// Spec §113 r2 — one physical answer to "can this whole body lie here?".
//
// There are no special cases for the centre, a rank, or the first/second/third
// sleeper. The 37 interior HexPointLayout nodes are ordinary candidate centres.
// For every node the solver tries the six hex headings and accepts the first
// oriented body rectangle that has level walkable support under its entire area
// and intersects neither a solid object nor another body.
internal static class LyingSpot
{
    private const float GeometryEpsilon = 0.0001f;

    private static readonly int[] HeadingOffsets = { 0, 60, -60, 120, -120, 180 };

    internal readonly struct Placement
    {
        internal Placement(Float2 position, float heading, JunctionId node, int nodeSlot)
        {
            Position = position;
            Heading = heading;
            Node = node;
            NodeSlot = nodeSlot;
        }

        internal Float2 Position { get; }

        internal float Heading { get; }

        internal JunctionId Node { get; }

        internal int NodeSlot { get; }
    }

    private readonly struct Candidate
    {
        internal Candidate(Float2 position, JunctionId node, int nodeSlot, float distanceSq)
        {
            Position = position;
            Node = node;
            NodeSlot = nodeSlot;
            DistanceSq = distanceSq;
        }

        internal Float2 Position { get; }

        internal JunctionId Node { get; }

        internal int NodeSlot { get; }

        internal float DistanceSq { get; }
    }

    // Full body size = 0.88R x 0.24R = 1.32 x 0.36 world units.
    internal static float BodyHalfLength =>
        HexSpatialMath.HexRadius * Spec49.LieBodyLengthFactor * 0.5f;

    internal static float BodyHalfWidth =>
        HexSpatialMath.HexRadius * Spec49.LieBodyWidthFactor * 0.5f;

    // §111.9: the lying actor root runs HEAD -> FEET along +forward.
    internal static Float2 InteractionFeet(NPCState target) =>
        target.Position + Forward(target.RotationDegrees) * BodyHalfLength;

    internal static float InteractionHeading(NPCState target) =>
        Wrap360(target.RotationDegrees + 180f);

    internal static float InteractionStationReach =>
        InteractionReach.Aid + BodyHalfLength;

    internal static void AlignInteractorAtFeet(NPCState actor, NPCState target)
    {
        var heading = InteractionHeading(target);
        actor.Position = InteractionFeet(target);
        actor.RotationDegrees = heading;
        actor.Movement.DesiredRotationDegrees = heading;
        actor.Movement.DesiredDirection = Forward(heading);
    }

    // Beds own an authored attach pose. They deliberately bypass the ground
    // footprint solver: the object itself promises support for that pose.
    internal static void AlignBodyToObject(
        WorldState world, NPCState body, WorldObjectState worldObject, Float2 anchorPosition)
    {
        if (body.Tile != worldObject.Tile)
        {
            var previousTile = body.Tile;
            body.Tile = worldObject.Tile;
            SpatialMutations.MoveEntityToTile(world, body.Id, previousTile, body.Tile);
        }

        body.Position = anchorPosition;
        body.RotationDegrees = Wrap360(worldObject.RotationDegrees);
        body.Movement.DesiredRotationDegrees = body.RotationDegrees;
        body.Movement.DesiredDirection = Forward(body.RotationDegrees);
    }

    /// <summary>
    /// Searches the 37 interior sub-grid nodes of the NPC's CURRENT tile.
    /// Candidates are nearest-position first; ties use the stable template slot.
    /// </summary>
    internal static bool TrySolve(WorldState world, NPCState npc, out Placement placement)
    {
        return TrySolveOnTile(
            world, npc, npc.Tile, npc.Position, npc.RotationDegrees, out placement);
    }

    /// <summary>
    /// Non-mutating preflight used before rescue pickup: proves that the
    /// patient's full body can be placed somewhere on the destination tile.
    /// </summary>
    internal static bool CanSolveOnTile(WorldState world, NPCState npc, TileCoord tile)
    {
        return TrySolveOnTile(
            world, npc, tile, HexSpatialMath.TileToWorld(tile), npc.RotationDegrees, out _);
    }

    private static bool TrySolveOnTile(
        WorldState world, NPCState npc, TileCoord tileCoord,
        Float2 preferredPosition, float preferredHeading, out Placement placement)
    {
        placement = default;
        if (!world.Tiles.Items.TryGetValue(tileCoord, out var tile) ||
            !SupportsBody(tile, tile.Elevation))
        {
            return false;
        }

        var center = HexSpatialMath.TileToWorld(tileCoord);
        var candidates = new List<Candidate>(1 + 3 * HexPointLayout.InteriorRadius *
            (HexPointLayout.InteriorRadius + 1));
        foreach (var template in HexPointLayout.GetInteriorTemplates())
        {
            var position = center + template.Offset;
            if (!TryFindNode(world, tile, position, out var node) || node.Blocked)
            {
                continue;
            }

            var delta = position - preferredPosition;
            candidates.Add(new Candidate(position, node.Id, template.Slot,
                delta.X * delta.X + delta.Y * delta.Y));
        }

        candidates.Sort((a, b) =>
        {
            var byDistance = a.DistanceSq.CompareTo(b.DistanceSq);
            return byDistance != 0 ? byDistance : a.NodeSlot.CompareTo(b.NodeSlot);
        });

        var baseHeading = SnapToHexAxis(preferredHeading);
        foreach (var candidate in candidates)
        {
            foreach (var offset in HeadingOffsets)
            {
                var heading = Wrap360(baseHeading + offset);
                var forward = Forward(heading);
                var lateral = Lateral(forward);
                if (!BodyClear(world, npc, tileCoord, candidate.Position, forward, lateral))
                {
                    continue;
                }

                placement = new Placement(
                    candidate.Position, heading, candidate.Node, candidate.NodeSlot);
                return true;
            }
        }

        return false;
    }

    internal static float SolidRadius(WorldState world, WorldObjectState worldObject)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(worldObject.DefinitionId, out var definition))
        {
            return 0f;
        }

        // A build site already occupies the footprint of its promised product.
        if (!string.IsNullOrEmpty(worldObject.BuildProduct) &&
            world.Content.ObjectDefinitions.TryGetValue(worldObject.BuildProduct, out var product))
        {
            definition = product;
        }

        if (definition.SolidRadius > 0f)
        {
            return definition.SolidRadius;
        }

        if (!definition.Tags.Contains(ObjectTags.Obstacle))
        {
            return 0f;
        }

        var floor = HexSpatialMath.HexRadius * Spec49.LieSolidRadiusFloorFactor;
        return definition.ObstacleRadius > floor ? definition.ObstacleRadius : floor;
    }

    internal static bool TryAnchor(
        WorldState world, WorldObjectState worldObject, out Float2 position)
    {
        if (worldObject.Junctions.Count > 0 &&
            world.Junctions.Items.TryGetValue(worldObject.Junctions[0], out var anchor))
        {
            position = anchor.WorldPosition;
            return true;
        }

        position = default;
        return false;
    }

    internal static bool BodyClear(
        WorldState world, NPCState npc, TileCoord tile, Float2 spot, Float2 forward, Float2 lateral)
    {
        if (!TerrainSupports(world, tile, spot, forward, lateral))
        {
            return false;
        }

        // A 1.32 wu body centred on an interior node can reach only its own tile
        // and the six neighbours. Objects are indexed by exactly that region.
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? tile
                : new TileCoord(tile.Q + HexDirection.All[i].DQ, tile.R + HexDirection.All[i].DR);
            if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var objects))
            {
                continue;
            }

            foreach (var objectId in objects)
            {
                if (!world.Entities.Objects.TryGetValue(objectId, out var worldObject))
                {
                    continue;
                }

                var radius = SolidRadius(world, worldObject);
                if (radius > 0f && TryAnchor(world, worldObject, out var anchor) &&
                    CircleOverlapsBody(anchor, radius, spot, forward, lateral))
                {
                    return false;
                }
            }
        }

        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id.Equals(npc.Id) || other.Health <= 0f ||
                !other.IsLyingDown(world.Tick))
            {
                continue;
            }

            var otherForward = Forward(other.RotationDegrees);
            if (BodiesOverlap(spot, forward, lateral, other.Position,
                otherForward, Lateral(otherForward)))
            {
                return false;
            }
        }

        // Corpses retain the safe pose found while they were living NPCs. Their
        // object anchor is only an interaction handle; collision uses the body.
        foreach (var corpse in world.Entities.Corpses.Values)
        {
            var corpseForward = Forward(corpse.RotationDegrees);
            if (BodiesOverlap(spot, forward, lateral, corpse.Position,
                corpseForward, Lateral(corpseForward)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TerrainSupports(
        WorldState world, TileCoord bodyTile, Float2 spot, Float2 forward, Float2 lateral)
    {
        if (!world.Tiles.Items.TryGetValue(bodyTile, out var origin) ||
            !SupportsBody(origin, origin.Elevation))
        {
            return false;
        }

        var elevation = origin.Elevation;
        for (var i = -1; i < HexDirection.All.Length; i++)
        {
            var coord = i < 0
                ? bodyTile
                : new TileCoord(
                    bodyTile.Q + HexDirection.All[i].DQ,
                    bodyTile.R + HexDirection.All[i].DR);
            var hexCenter = HexSpatialMath.TileToWorld(coord);
            if (!RectangleOverlapsHex(spot, forward, lateral, hexCenter))
            {
                continue;
            }

            if (!world.Tiles.Items.TryGetValue(coord, out var support) ||
                !SupportsBody(support, elevation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SupportsBody(Tile tile, int elevation) =>
        tile.Elevation == elevation &&
        tile.Flags.HasFlag(TileFlags.Walkable) &&
        !tile.Flags.HasFlag(TileFlags.Blocked);

    // SAT: body rectangle vs pointy-top regular hex. Touching an edge has zero
    // area and therefore does not require support from the tile across it.
    private static bool RectangleOverlapsHex(
        Float2 spot, Float2 forward, Float2 lateral, Float2 hexCenter)
    {
        var d = hexCenter - spot;
        if (SeparatedOnAxis(d, forward, RectangleRadius(forward, forward, lateral),
                HexRadiusOnAxis(forward)) ||
            SeparatedOnAxis(d, lateral, RectangleRadius(lateral, forward, lateral),
                HexRadiusOnAxis(lateral)))
        {
            return false;
        }

        // The three unique edge normals of a pointy-top hex.
        var x = new Float2(1f, 0f);
        var sixty = new Float2(0.5f, HexSpatialMath.Sqrt3 * 0.5f);
        var oneTwenty = new Float2(-0.5f, HexSpatialMath.Sqrt3 * 0.5f);
        return !SeparatedOnAxis(d, x, RectangleRadius(x, forward, lateral),
                   HexRadiusOnAxis(x)) &&
               !SeparatedOnAxis(d, sixty, RectangleRadius(sixty, forward, lateral),
                   HexRadiusOnAxis(sixty)) &&
               !SeparatedOnAxis(d, oneTwenty,
                   RectangleRadius(oneTwenty, forward, lateral),
                   HexRadiusOnAxis(oneTwenty));
    }

    private static float HexRadiusOnAxis(Float2 axis)
    {
        var radius = 0f;
        for (var i = 0; i < 6; i++)
        {
            var radians = (-90f + i * 60f) * (System.MathF.PI / 180f);
            var vertex = new Float2(
                HexSpatialMath.HexRadius * System.MathF.Cos(radians),
                HexSpatialMath.HexRadius * System.MathF.Sin(radians));
            radius = System.MathF.Max(radius, System.MathF.Abs(Dot(vertex, axis)));
        }

        return radius;
    }

    private static bool SeparatedOnAxis(
        Float2 centerDelta, Float2 axis, float firstRadius, float secondRadius) =>
        System.MathF.Abs(Dot(centerDelta, axis)) >=
        firstRadius + secondRadius - GeometryEpsilon;

    private static float RectangleRadius(Float2 axis, Float2 forward, Float2 lateral) =>
        BodyHalfLength * System.MathF.Abs(Dot(forward, axis)) +
        BodyHalfWidth * System.MathF.Abs(Dot(lateral, axis));

    // Exact circle-vs-OBB: squared distance from the circle centre to the body.
    private static bool CircleOverlapsBody(
        Float2 point, float radius, Float2 spot, Float2 forward, Float2 lateral)
    {
        var d = point - spot;
        var outsideAlong = System.MathF.Max(
            System.MathF.Abs(Dot(d, forward)) - BodyHalfLength, 0f);
        var outsideSide = System.MathF.Max(
            System.MathF.Abs(Dot(d, lateral)) - BodyHalfWidth, 0f);
        return outsideAlong * outsideAlong + outsideSide * outsideSide <
            radius * radius - GeometryEpsilon;
    }

    // Exact OBB-vs-OBB SAT. Both bodies share dimensions but may have unrelated
    // headings and centres, including centres on adjacent tiles.
    private static bool BodiesOverlap(
        Float2 aCenter, Float2 aForward, Float2 aLateral,
        Float2 bCenter, Float2 bForward, Float2 bLateral)
    {
        var d = bCenter - aCenter;
        return !BodiesSeparated(d, aForward, aForward, aLateral, bForward, bLateral) &&
               !BodiesSeparated(d, aLateral, aForward, aLateral, bForward, bLateral) &&
               !BodiesSeparated(d, bForward, aForward, aLateral, bForward, bLateral) &&
               !BodiesSeparated(d, bLateral, aForward, aLateral, bForward, bLateral);
    }

    private static bool BodiesSeparated(
        Float2 delta, Float2 axis,
        Float2 aForward, Float2 aLateral, Float2 bForward, Float2 bLateral)
    {
        var aRadius = RectangleRadius(axis, aForward, aLateral);
        var bRadius = RectangleRadius(axis, bForward, bLateral);
        return System.MathF.Abs(Dot(delta, axis)) >=
            aRadius + bRadius - GeometryEpsilon;
    }

    private static bool TryFindNode(
        WorldState world, Tile tile, Float2 position, out Junction node)
    {
        foreach (var junctionId in tile.Junctions)
        {
            if (!world.Junctions.Items.TryGetValue(junctionId, out var candidate))
            {
                continue;
            }

            var d = candidate.WorldPosition - position;
            if (d.X * d.X + d.Y * d.Y <= GeometryEpsilon * GeometryEpsilon)
            {
                node = candidate;
                return true;
            }
        }

        node = null;
        return false;
    }

    internal static bool ContainsBodyPoint(NPCState body, Float2 point, float padding = 0f)
    {
        var forward = Forward(body.RotationDegrees);
        var lateral = Lateral(forward);
        var d = point - body.Position;
        return System.MathF.Abs(Dot(d, forward)) <= BodyHalfLength + padding &&
               System.MathF.Abs(Dot(d, lateral)) <= BodyHalfWidth + padding;
    }

    private static float Dot(Float2 a, Float2 b) => a.X * b.X + a.Y * b.Y;

    private static Float2 Forward(float headingDegrees)
    {
        var radians = headingDegrees * (System.MathF.PI / 180f);
        return new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
    }

    private static Float2 Lateral(float headingDegrees) => Lateral(Forward(headingDegrees));

    private static Float2 Lateral(Float2 forward) => new Float2(-forward.Y, forward.X);

    internal static float SnapToHexAxis(float degrees) =>
        System.MathF.Round(Wrap360(degrees) / 60f) % 6f * 60f;

    private static float Wrap360(float degrees)
    {
        var wrapped = degrees % 360f;
        return wrapped < 0f ? wrapped + 360f : wrapped;
    }
}

}
