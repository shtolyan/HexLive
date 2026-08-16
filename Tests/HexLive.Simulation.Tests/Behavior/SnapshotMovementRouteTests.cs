using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class SnapshotMovementRouteTests
{
    [Test]
    public void Report158SparseAidPoseRoutesAroundHutWallThroughDoorPortal()
    {
        const int reportSeed = -28147312;
        var world = TestWorld.CreateWorld(reportSeed);
        var hut = world.Entities.Objects.Values.Single(
            obj => obj.DefinitionId == ContentIds.Hut1Hex);
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var portal = world.Junctions.Items.Values.Single(junction =>
            junction.Door && junction.Tiles.Contains(hut.Tile));
        var outward = Normalize(portal.WorldPosition - center);

        // Same topology as the reported Console aid: one sparse sample inside,
        // the next outside beyond a solid side that is not the door side.
        var inside = world.Junctions.Items.Values
            .Where(junction => !junction.Blocked && junction.Tiles.Contains(hut.Tile))
            .OrderBy(junction => DistanceSquared(junction.WorldPosition, center))
            .First();
        var oppositeOutsideTarget = center - outward * (HexSpatialMath.HexRadius * 1.5f);
        var outside = world.Junctions.Items.Values
            .Where(junction => !junction.Blocked && !junction.Tiles.Contains(hut.Tile))
            .OrderBy(junction => DistanceSquared(junction.WorldPosition, oppositeOutsideTarget))
            .First();
        var snapshot = WorldSnapshotExporter.Export(world);
        var route = new List<Float2>();

        var kind = SnapshotMovementRoute.Build(
            snapshot.Junctions, inside.WorldPosition, outside.WorldPosition, route);

        TestContext.WriteLine($"seed={reportSeed} hut={hut.Tile} " +
            $"from={inside.Id}:{inside.WorldPosition} " +
            $"door={portal.Id}:{portal.WorldPosition} " +
            $"to={outside.Id}:{outside.WorldPosition}");
        TestContext.WriteLine("route=" + string.Join(" -> ", route));

        Assert.Multiple(() =>
        {
            Assert.That(kind, Is.EqualTo(SnapshotMovementRoute.RouteKind.Routed));
            Assert.That(route.Any(point =>
                    DistanceSquared(point, portal.WorldPosition) < 0.000001f),
                Is.True, "Ломаная интерполяции обязана пройти через настоящий door portal.");
            Assert.That(route.Any(point => snapshot.Junctions.Any(junction =>
                    junction.Blocked &&
                    DistanceSquared(point, junction.WorldPosition) < 0.000001f)),
                Is.False, "Визуальный маршрут не владеет ни одним solid junction.");
        });
    }

    [Test]
    public void RouteEvaluationUsesDistanceRatherThanEqualTimePerJunction()
    {
        var route = new List<Float2>
        {
            new(0f, 0f),
            new(1f, 0f),
            new(4f, 0f)
        };

        var halfway = SnapshotMovementRoute.Evaluate(route, 0.5f);

        Assert.That(halfway.X, Is.EqualTo(2f).Within(0.0001f));
        Assert.That(halfway.Y, Is.Zero.Within(0.0001f));
    }

    [Test]
    public void ReusedProductionSnapshotRefreshesBlockedTopologyWithoutDebugFlags()
    {
        var world = TestWorld.CreateWorld(-28147312);
        var first = WorldSnapshotExporter.Export(world);
        var wall = world.Junctions.Items.Values.First(junction => junction.Blocked);
        var cached = first.Junctions.Single(junction => junction.Id == wall.Id);
        Assert.That(cached.Blocked, Is.True);

        wall.Blocked = false;
        var refreshed = WorldSnapshotExporter.Export(world, first);

        Assert.That(refreshed.Junctions.Single(junction => junction.Id == wall.Id).Blocked,
            Is.False, "Presentation topology must follow built/repaired walls without debug mode.");
    }

    private static Float2 Normalize(Float2 value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y);
        return new Float2(value.X / length, value.Y / length);
    }

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return dx * dx + dy * dy;
    }
}

}
