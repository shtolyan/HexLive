using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// #745: a modern table placed by the player on the ground must block the
/// same 11 junctions (rows 4-3-4, 0.65 x 1.125 wu) the construction overlay
/// draws, and a walking colonist must never enter or cut through them.
/// </summary>
public sealed class ModernTableBlockingTests
{
    private const string Table = ContentIds.ModernTable;

    private static (SimulationEngine Engine, NPCState Walker, WorldObjectState Table, JunctionKey Key,
        HashSet<JunctionId> Footprint) PlaceTable(int yawStep)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var walker = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        walker.Inventory.Items.Add(new ItemInstance(Table) { OwnerId = walker.Id.Value });
        Junction anchor = null;
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, walker.Tile) is >= 1 and <= 3)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, walker.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (world.Tiles.Items[tile].Flags.HasFlag(TileFlags.Indoor)) continue;
            var centre = HexSpatialMath.TileToWorld(tile);
            anchor = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
                .Where(j => HexSpatialMath.Distance(j.WorldPosition, centre) < .01f)
                .FirstOrDefault(j => FurniturePlacement.Validate(world, Table, j, yawStep) == null &&
                    Around(world, FurniturePlacement.KeyOf(j.WorldPosition), yawStep).All(p => p != null && !p.Blocked));
            if (anchor != null) break;
        }

        Assert.That(anchor, Is.Not.Null);
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        var receipt = engine.ApplyManualCommand(new PlaceFurnitureCommand(Table, key.XKey, key.YKey, yawStep));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var table = world.Entities.Objects.Values.Single(o => o.DefinitionId == Table);
        var footprint = FurniturePlacement.FootprintKeys(Table, key, yawStep)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j.Id : default).ToHashSet();
        return (engine, walker, table, key, footprint);
    }

    // Two points two lattice columns beyond the table's long sides (±1.30 wu).
    private static Junction[] Around(WorldState world, JunctionKey key, int yawStep)
    {
        var result = new Junction[2];
        for (var side = 0; side < 2; side++)
        {
            var local = Runtime.Blueprints.BlueprintGeometry.RotateJunctionOffset(
                new JunctionKey(side == 0 ? -4 : 4, 0), yawStep);
            result[side] = FurniturePlacement.TryFindJunction(world,
                new JunctionKey(key.XKey + local.XKey, key.YKey + local.YKey), out var j) ? j : null;
        }
        return result;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void PlacedTableBlocksItsAuthoredFootprintAndAWalkGoesAround(int yawStep)
    {
        var (engine, walker, table, key, footprint) = PlaceTable(yawStep);
        var world = engine.World;
        Assert.That(footprint.Count, Is.EqualTo(11));
        Assert.That(table.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True,
            $"blocked={table.BlockedJunctions.Count}");
        Assert.That(footprint.All(id => world.Junctions.Items[id].Blocked), Is.True);

        var (path, inside) = Walk(engine, walker, table, key, yawStep);
        TestContext.Out.WriteLine($"yaw={yawStep} path: {string.Join(" ", path)}");
        Assert.That(path.Where(v => footprint.Contains(new JunctionId(v))), Is.Empty, "The route stepped on a table junction.");
        Assert.That(inside, Is.Empty, "The body was interpolated under the tabletop: " + string.Join(", ", inside));
    }

    [Test]
    public void WithoutTheFootprintTheWalkCutsStraightUnderTheTable()
    {
        // The 0.1.120 state, reproduced: the same table with no blocking.
        var (engine, walker, table, key, footprint) = PlaceTable(0);
        var world = engine.World;
        WorldObjectMutations.ReleaseOwnedBlocking(world, table);
        Assert.That(footprint.Any(id => world.Junctions.Items[id].Blocked), Is.False);
        var (path, inside) = Walk(engine, walker, table, key, 0);
        TestContext.Out.WriteLine($"0.1.120 path: {string.Join(" ", path)}");
        TestContext.Out.WriteLine($"0.1.120 under the tabletop: {string.Join(", ", inside)}");
        Assert.That(path.Where(v => footprint.Contains(new JunctionId(v))), Is.Not.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AMoveOrderOntoTheTableStopsBesideIt(bool legacyUnblocked)
    {
        // The report's shape: a manual colonist "Arrived" in the middle of the
        // table. The click lands on the table's anchor junction.
        var (engine, walker, table, key, footprint) = PlaceTable(0);
        var world = engine.World;
        if (legacyUnblocked) WorldObjectMutations.ReleaseOwnedBlocking(world, table);
        var start = Around(world, key, 0)[0];
        Teleport(world, walker, start);
        walker.Mind.ManualControl = true;
        var centre = world.Junctions.Items[table.Junctions[0]].WorldPosition;
        var receipt = engine.ApplyManualCommand(new MoveToCommand(walker.Id, centre));
        TestContext.Out.WriteLine($"legacy={legacyUnblocked} accepted={receipt.Accepted} reason={receipt.Reason}");
        var path = new List<int>();
        for (var tick = 0; tick < 120; tick++)
        {
            engine.Step();
            if (walker.CurrentJunction is { } current && (path.Count == 0 || path[^1] != current.Value))
                path.Add(current.Value);
        }

        var offset = walker.Position - centre;
        TestContext.Out.WriteLine($"legacy={legacyUnblocked} path: {string.Join(" ", path)} " +
            $"final offset ({offset.X:0.000},{offset.Y:0.000}) |d|={HexSpatialMath.Distance(walker.Position, centre):0.000}");
        if (legacyUnblocked)
        {
            Assert.That(HexSpatialMath.Distance(walker.Position, centre), Is.LessThan(.01f),
                "0.1.120: the colonist walks onto the table's own anchor.");
            return;
        }

        Assert.That(path.Where(v => footprint.Contains(new JunctionId(v))), Is.Empty);
        Assert.That(walker.CurrentJunction is { } end && !footprint.Contains(end), Is.True);
        Assert.That(System.MathF.Abs(offset.X) >= .38f || System.MathF.Abs(offset.Y) >= .53f, Is.True,
            "The colonist stopped under the tabletop.");
    }

    [Test]
    public void PickingTheTableUpReleasesExactlyItsFootprintAndLoadStampsOldSaves()
    {
        var (engine, walker, table, _, footprint) = PlaceTable(1);
        var world = engine.World;

        // A 0.1.120 save: the table is there, its points are open.
        WorldObjectMutations.ReleaseOwnedBlocking(world, table);
        var loaded = RoundTrip(world);
        var restored = loaded.Entities.Objects[table.Id];
        Assert.That(restored.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True);
        Assert.That(footprint.All(id => loaded.Junctions.Items[id].Blocked), Is.True);

        WorldObjectMutations.DespawnObject(loaded, restored.Id);
        Assert.That(footprint.Any(id => loaded.Junctions.Items[id].Blocked), Is.False,
            "Despawn returns the footprint even though the table has no Obstacle tag.");
    }

    private static (List<int> Path, List<string> Inside) Walk(SimulationEngine engine, NPCState walker,
        WorldObjectState table, JunctionKey key, int yawStep)
    {
        var world = engine.World;
        var ends = Around(world, key, yawStep);
        Assert.That(ends.All(e => e != null && !e.Blocked), Is.True);
        Teleport(world, walker, ends[0]);
        walker.Mind.ManualControl = true;
        var receipt = engine.ApplyManualCommand(new MoveToCommand(walker.Id, ends[1].WorldPosition));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);

        var centre = world.Junctions.Items[table.Junctions[0]].WorldPosition;
        var along = HexSpatialMath.Normalize(Runtime.Blueprints.BlueprintGeometry.JunctionToWorld(
            Runtime.Blueprints.BlueprintGeometry.RotateJunctionOffset(new JunctionKey(0, 2), yawStep)));
        var path = new List<int>();
        var inside = new List<string>();
        for (var tick = 0; tick < 200; tick++)
        {
            engine.Step();
            if (walker.CurrentJunction is { } current && (path.Count == 0 || path[^1] != current.Value))
                path.Add(current.Value);
            // Tabletop 0.76 x 1.06 wu; the long side runs along local Y.
            var d = walker.Position - centre;
            var localY = d.X * along.X + d.Y * along.Y;
            var localX = -d.X * along.Y + d.Y * along.X;
            if (System.MathF.Abs(localX) < .38f && System.MathF.Abs(localY) < .53f)
                inside.Add($"t{tick}({localX:0.00},{localY:0.00})");
            if (HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition) < .01f) break;
        }

        Assert.That(HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition), Is.LessThan(.01f),
            "The walker never arrived.");
        return (path, inside);
    }

    private static void Teleport(WorldState world, NPCState npc, Junction point)
    {
        if (npc.CurrentJunction is { } old) SpatialMutations.FreeJunction(world, old, npc.Id);
        var tile = point.Tiles.Contains(HexSpatialMath.WorldToTile(point.WorldPosition))
            ? HexSpatialMath.WorldToTile(point.WorldPosition) : point.Tiles[0];
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        SpatialMutations.OccupyJunction(world, point.Id, npc.Id);
        npc.CurrentJunction = point.Id;
        npc.Tile = tile;
        npc.Position = point.WorldPosition;
    }

    private static WorldState RoundTrip(WorldState source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, WorldSaveSerializer.BlobVersion);
        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }
}
