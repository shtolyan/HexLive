using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// §158.1: «is this junction of mine also blocked by somebody else?» is asked
/// for every target-owned junction of every rim search. It used to walk every
/// object of the world (24% of the tick on the live Singapore world after
/// 0.1.123); it now looks only at the junction's own tiles and their
/// neighbours. The answer must be the same as the exhaustive one.
/// </summary>
public sealed class BarrierOwnershipLookupTests
{
    [TestCase(12345)]
    [TestCase(119096)]
    public void LocalLookupAgreesWithTheExhaustiveScanForEveryBlockedJunction(int seed)
    {
        var world = TestWorld.CreateWorld(seed);
        // Crowd some authored footprints together so shared junctions exist.
        var anchors = world.Tiles.Items.Values
            .Where(t => t.Flags.HasFlag(TileFlags.Walkable) && (t.Flags & (TileFlags.Water | TileFlags.Blocked)) == 0)
            .OrderBy(t => t.Coord.Q).ThenBy(t => t.Coord.R).Take(40).ToArray();
        var ids = new[] { ContentIds.BedBasic, ContentIds.ModernTable, ContentIds.StorageChest, ContentIds.Workbench };
        for (var i = 0; i < anchors.Length; i++)
        {
            if (StructurePlacement.CenterJunction(world, anchors[i].Coord) is not { } centre) continue;
            var fragment = world.Junctions.Items[centre].Fragment;
            var piece = WorldObjectMutations.SpawnObject(world, ids[i % ids.Length], fragment, anchors[i].Coord, centre);
            piece.RotationDegrees = 60f * (i % 6);
            WorldObjectMutations.SetAuthoredFurnitureBlocking(world, piece, blocked: true);
        }

        var checkedPairs = 0;
        var shared = 0;
        foreach (var owner in world.Entities.Objects.Values.Where(o => o.BlockedJunctions.Count > 0).ToArray())
        {
            foreach (var junctionId in owner.BlockedJunctions)
            {
                if (!world.Junctions.Items.TryGetValue(junctionId, out var junction) || !junction.Blocked ||
                    SpatialQueries.IsAllWaterJunction(world, junctionId) ||
                    DoorTopology.IsClosedDoorPortal(world, junctionId)) continue;
                var exhaustive = world.Entities.Objects.Values.Any(other =>
                    !other.Id.Equals(owner.Id) && other.BlockedJunctions.Contains(junctionId));
                Assert.That(SpatialQueries.IsBarrierFor(world, junctionId, owner), Is.EqualTo(exhaustive),
                    $"object {owner.DefinitionId}#{owner.Id.Value} junction {junctionId.Value}");
                checkedPairs++;
                if (exhaustive) shared++;
            }
        }

        TestContext.Out.WriteLine($"seed {seed}: {checkedPairs} owned junctions checked, {shared} shared");
        Assert.That(checkedPairs, Is.GreaterThan(200));
        Assert.That(shared, Is.GreaterThan(0), "The arena must contain shared junctions, or the test proves nothing.");
    }
}
