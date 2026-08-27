using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class Probe237Tests
{
    [Test]
    public void ProbeShoreHutDoorFacingWater()
    {
        for (var seed = 12345; seed < 12351; seed++)
        {
            var scout = TestWorld.CreateWorld(seed);
            var shore = ShoreTiles(scout).ToArray();
            TestContext.Out.WriteLine($"seed={seed} shoreCandidates={shore.Length}");
            foreach (var coord in shore.Take(3))
            {
                for (var step = 0; step < 6; step++)
                {
                    Probe(seed, coord, step * 60f);
                }
            }
            if (shore.Length > 0) break;
        }
    }

    private static void Probe(int seed, TileCoord coord, float rotation)
    {
        var world = TestWorld.CreateWorld(seed);
        if (StructurePlacement.CenterJunction(world, coord) is not { } anchor) return;
        var hut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Hut1Hex, world.Junctions.Items[anchor].Fragment, coord, anchor);
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        hut.RotationDegrees = rotation;
        Bootstrap.BuildingBootstrap.CompleteHut(world, hut);

        var door = world.Entities.Objects.Values.FirstOrDefault(o =>
            o.DefinitionId == DoorTopology.DoorDefinitionId && o.ArchitectureOwnerId == hut.Id);
        if (door is null || door.Junctions.Count != 1)
        {
            TestContext.Out.WriteLine($"  rot={rotation}: no single-throat door");
            return;
        }

        var portalId = door.Junctions[0];
        var portal = world.Junctions.Items[portalId];
        var otherTiles = portal.Tiles.Where(t => t != coord).ToArray();
        var facing = otherTiles.Length > 0 ? otherTiles[0] : coord;
        var facingTile = world.Tiles.Items.TryGetValue(facing, out var ft) ? ft : null;

        var hutTile = world.Tiles.Items[coord];
        var inside = hutTile.Junctions.FirstOrDefault(id =>
            world.Junctions.Items[id].Tiles.Count == 1 &&
            !world.Junctions.Items[id].Blocked);

        // The colony's own camp centre — the place she actually needs to reach.
        var far = StructurePlacement.CenterJunction(
            world, world.FactionHomes[Faction.Colony]);

        var reach = far is { } f && Connectivity.Reachable(world, inside, f);
        var path = far is { } f2 ? HexPathfinder.FindPath(world, inside, f2, null).Count : -1;
        var freeOutward = portal.Neighbors.Count(id =>
            world.Junctions.Items.TryGetValue(id, out var n) && !n.Blocked && !n.Tiles.Contains(coord));

        TestContext.Out.WriteLine(
            $"  rot={rotation} facing={facing.Q},{facing.R} " +
            $"flags={(facingTile is null ? "MISSING" : facingTile.Flags.ToString())} " +
            $"portalBlocked={portal.Blocked} freeOutward={freeOutward} " +
            $"reachFar={reach} pathLen={path}");
    }

    private static System.Collections.Generic.IEnumerable<TileCoord> ShoreTiles(WorldState world) =>
        world.Tiles.Items.Values
            .Where(t => t.Flags.HasFlag(TileFlags.Walkable) &&
                        !t.Flags.HasFlag(TileFlags.Water) &&
                        !t.Flags.HasFlag(TileFlags.Blocked) &&
                        !t.Flags.HasFlag(TileFlags.Indoor) &&
                        Bootstrap.BuildingBootstrap.CanPlaceHut(world, t.Coord) &&
                        HasWetNeighbor(world, t.Coord))
            .OrderBy(t => t.Coord.Q).ThenBy(t => t.Coord.R)
            .Select(t => t.Coord);

    private static bool HasWetNeighbor(WorldState world, TileCoord coord)
    {
        foreach (var d in HexDirection.All)
        {
            var n = new TileCoord(coord.Q + d.DQ, coord.R + d.DR);
            if (!world.Tiles.Items.TryGetValue(n, out var nt) ||
                nt.Flags.HasFlag(TileFlags.Water) ||
                !nt.Flags.HasFlag(TileFlags.Walkable))
            {
                return true;
            }
        }

        return false;
    }
}

}
