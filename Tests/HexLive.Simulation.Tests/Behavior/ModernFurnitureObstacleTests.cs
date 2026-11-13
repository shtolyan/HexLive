using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// #765: every modern piece is a real obstacle. Measured on the live save
/// before the fix: only the bed (and a PLACED table) blocked anything; the
/// apartment table, the chair, sofa ends, fridge, shelf, wardrobe and TV
/// blocked 0 junctions and a colonist walked 0.04–0.34 wu from their centres.
/// </summary>
public sealed class ModernFurnitureObstacleTests
{
    private static readonly string[] Pieces =
    {
        "furniture.modern_table", "furniture.modern_chair", "furniture.modern_sofa", "furniture.modern_tv",
        ContentIds.ModernFridge, "furniture.modern_shelf", ContentIds.ModernWardrobe,
    };

    private static readonly Dictionary<string, int> ExpectedPoints = new()
    {
        ["furniture.modern_table"] = 11, ["furniture.modern_chair"] = 1, ["furniture.modern_sofa"] = 13,
        ["furniture.modern_tv"] = 3, [ContentIds.ModernFridge] = 7, ["furniture.modern_shelf"] = 3,
        [ContentIds.ModernWardrobe] = 3,
    };

    private static IEnumerable<TestCaseData> PieceYaw() =>
        from id in Pieces from yaw in new[] { 0, 1, 2 } select new TestCaseData(id, yaw);

    /// <summary>No lattice point a colonist could stand on lies inside the
    /// shipped model's rectangle: the footprint covers the mesh.</summary>
    [Test]
    public void EveryFootprintCoversEveryLatticePointUnderItsModel()
    {
        using var manifest = JsonDocument.Parse(RepoPaths.ReadText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Portal", "modern-apartment-art.json")));
        var records = manifest.RootElement.GetProperty("records").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString(), r => r.GetProperty("blenderBounds"));
        foreach (var id in Pieces)
        {
            Assert.That(PortalWorld.IsFurniture(id), Is.True, id);
            Assert.That(PortableFurniture.BlocksFootprintEverywhere(id), Is.True, id);
            Assert.That(BlueprintFurnitureFootprints.HasFootprint(id), Is.True, id);
            var footprint = BlueprintFurnitureFootprints.LocalOffsets(id).ToHashSet();
            Assert.That(footprint.Count, Is.EqualTo(ExpectedPoints[id]), id);
            var centroid = BlueprintFurnitureFootprints.CentroidOffset(id, 0);
            Assert.That(System.MathF.Abs(centroid.X) + System.MathF.Abs(centroid.Y), Is.LessThan(.0001f),
                id + ": a symmetric footprint keeps the piece drawn on its anchor");

            var min = records[id].GetProperty("min");
            var max = records[id].GetProperty("max");
            var halfX = System.MathF.Max(System.MathF.Abs(min[0].GetSingle()), System.MathF.Abs(max[0].GetSingle()));
            var halfY = System.MathF.Max(System.MathF.Abs(min[1].GetSingle()), System.MathF.Abs(max[1].GetSingle()));
            for (var x = -8; x <= 8; x++)
            for (var y = -12; y <= 12; y++)
            {
                if (((x + y) & 1) != 0) continue;
                var key = new JunctionKey(x, y);
                var point = BlueprintGeometry.JunctionToWorld(key);
                if (System.MathF.Abs(point.X) < halfX && System.MathF.Abs(point.Y) < halfY)
                    Assert.That(footprint.Contains(key), Is.True,
                        $"{id}: lattice point ({point.X:0.000},{point.Y:0.000}) is under the " +
                        $"{2 * halfX:0.00} x {2 * halfY:0.00} model and stays walkable");
            }
        }
    }

    [TestCaseSource(nameof(PieceYaw))]
    public void APlacedPieceBlocksItsFootprintAndAWalkGoesAround(string id, int yawStep)
    {
        var (engine, walker, piece, key, footprint) = Place(id, yawStep);
        var world = engine.World;
        Assert.That(footprint.Count, Is.EqualTo(ExpectedPoints[id]));
        Assert.That(piece.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True,
            $"blocked={piece.BlockedJunctions.Count}");
        Assert.That(footprint.All(point => world.Junctions.Items[point].Blocked), Is.True);

        var ends = Around(world, key, yawStep);
        Teleport(world, walker, ends[0]);
        walker.Mind.ManualControl = true;
        var receipt = engine.ApplyManualCommand(new MoveToCommand(walker.Id, ends[1].WorldPosition));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);

        var path = new List<int>();
        for (var tick = 0; tick < 240; tick++)
        {
            engine.Step();
            if (walker.CurrentJunction is { } current && (path.Count == 0 || path[^1] != current.Value))
                path.Add(current.Value);
            if (HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition) < .01f) break;
        }

        Assert.That(HexSpatialMath.Distance(walker.Position, ends[1].WorldPosition), Is.LessThan(.01f), "never arrived");
        Assert.That(path.Where(v => footprint.Contains(new JunctionId(v))), Is.Empty,
            "The route stepped on a furniture junction.");

        // Something to do with the piece is still within reach: a free rim.
        var rim = new List<JunctionId>();
        SpatialQueries.CollectStandableAround(world, piece.Junctions[0], rim, 96,
            SpatialQueries.BesideReach(0f), piece, InteractionReach.RimMode);
        Assert.That(rim.Any(point => Connectivity.Reachable(world, point, walker.CurrentJunction.Value, false)),
            Is.True, "Nobody can stand beside the piece any more.");

        WorldObjectMutations.DespawnObject(world, piece.Id);
        Assert.That(footprint.Any(point => world.Junctions.Items[point].Blocked), Is.False,
            "Taking the piece away must free its junctions.");
    }

    [TestCase("furniture.modern_chair")]
    [TestCase("furniture.modern_sofa")]
    public void ASeatWhoseOwnJunctionsAreBlockedCanStillBeSatOn(string id)
    {
        var (engine, walker, piece, key, _) = Place(id, 1);
        var world = engine.World;
        Teleport(world, walker, Around(world, key, 1)[0]);
        walker.Mind.ManualControl = true;
        var order = engine.ApplyManualCommand(new InteractCommand(walker.Id, piece.Id, InteractionType.Sit));
        Assert.That(order.Accepted, Is.True, order.Reason);
        for (var i = 0; i < 400 && walker.Execution.CurrentInteraction != InteractionType.Sit; i++) engine.Step();
        Assert.That(walker.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sit));
        Assert.That(world.Junctions.Items[walker.CurrentJunction.Value].Blocked, Is.False,
            "The sitter stands on a free rim junction, not inside the footprint.");
    }

    /// <summary>The whole apartment, several seeds and visits: every piece
    /// blocks its footprint, and blocking seals nothing — every room and every
    /// piece is still reachable from the way out.</summary>
    [TestCase(0)]
    [TestCase(7)]
    [TestCase(13)]
    [TestCase(629209070)]
    public void ApartmentFurnitureBlocksItsFootprintsAndSealsNoRoom(int seed)
    {
        var world = CreatePortalWorld(seed);
        for (var night = 1; night <= 6; night++)
        {
            world.Tick = night * WorldBalance.DayLengthTicks;
            Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
            var serial = world.Portal.VisitSerial;
            var door = world.Entities.Objects[world.Portal.InteriorDoor.Value].Junctions[0];
            var furniture = world.Entities.Objects.Values
                .Where(o => PortalWorld.IsFurniture(o.DefinitionId) &&
                    PortalWorld.TryGetApartmentFurnitureVisit(o.Variant, out _))
                .OrderBy(o => o.Id.Value).ToArray();
            Assert.That(furniture.Length, Is.GreaterThanOrEqualTo(7), $"seed={seed} visit={serial}");

            foreach (var piece in furniture)
            {
                var context = $"seed={seed} visit={serial} {piece.DefinitionId}#{piece.Id.Value}";
                var anchor = world.Junctions.Items[piece.Junctions[0]];
                var yaw = FurniturePlacement.YawStepOf(piece.RotationDegrees);
                var expected = FurniturePlacement
                    .FootprintKeys(piece.DefinitionId, FurniturePlacement.KeyOf(anchor.WorldPosition), yaw)
                    .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j : null)
                    .Where(j => j != null && !j.Door).Select(j => j.Id).ToHashSet();
                Assert.That(expected, Is.Not.Empty, context);
                Assert.That(piece.BlockedJunctions.ToHashSet().SetEquals(expected), Is.True,
                    $"{context}: blocked {piece.BlockedJunctions.Count}, footprint {expected.Count}");
                Assert.That(expected.All(point => world.Junctions.Items[point].Blocked), Is.True, context);

                var rim = new List<JunctionId>();
                SpatialQueries.CollectStandableAround(world, piece.Junctions[0], rim, 96,
                    SpatialQueries.BesideReach(0f), piece, InteractionReach.RimMode);
                Assert.That(rim.Any(point => Connectivity.Reachable(world, point, door, false)), Is.True,
                    context + ": no standing place beside it can be reached from the door");
            }

            foreach (var tile in PortalWorld.ApartmentTiles(seed, serial))
            {
                var free = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
                    .Where(j => !j.Blocked && j.Fragment == PortalWorld.ApartmentFragment).ToArray();
                Assert.That(free.Any(j => Connectivity.Reachable(world, j.Id, door, false)), Is.True,
                    $"seed={seed} visit={serial}: tile {tile} is cut off from the door");
            }
        }
    }

    /// <summary>Fridge, chest and rain collector are one size and one footprint
    /// (disc of 7, 0.75 wu across); the workbench is the table's 11. Outdoors
    /// the chest and the collector used to hold a single point.</summary>
    [TestCase(ContentIds.StorageChest, 7)]
    [TestCase(ContentIds.WaterCollector, 7)]
    [TestCase(ContentIds.Workbench, 11)]
    public void ChestCollectorAndWorkbenchBlockTheirAuthoredFootprintOutdoors(string id, int points)
    {
        Assert.That(BlueprintFurnitureFootprints.LocalOffsets(ContentIds.StorageChest),
            Is.EqualTo(BlueprintFurnitureFootprints.LocalOffsets(ContentIds.ModernFridge)));
        Assert.That(BlueprintFurnitureFootprints.LocalOffsets(ContentIds.WaterCollector),
            Is.EqualTo(BlueprintFurnitureFootprints.LocalOffsets(ContentIds.ModernFridge)));
        Assert.That(PortableFurniture.BlocksFootprintEverywhere(id), Is.True);

        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var walker = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        Junction anchor = null;
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, walker.Tile) is >= 1 and <= 3)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, walker.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (world.Tiles.Items[tile].Flags.HasFlag(TileFlags.Indoor)) continue;
            var centre = HexSpatialMath.TileToWorld(tile);
            anchor = world.Tiles.Items[tile].Junctions.Select(point => world.Junctions.Items[point])
                .Where(j => HexSpatialMath.Distance(j.WorldPosition, centre) < .01f)
                .FirstOrDefault(j => FurniturePlacement.Validate(world, id, j, 0) == null);
            if (anchor != null) break;
        }

        Assert.That(anchor, Is.Not.Null);
        var piece = WorldObjectMutations.SpawnObject(world, id, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        HexLive.Simulation.Runtime.ExecutionSystem.ApplyIndoorFurnitureFootprint(world, piece);
        var footprint = FurniturePlacement.FootprintKeys(id, FurniturePlacement.KeyOf(anchor.WorldPosition), 0)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j.Id : default).ToHashSet();
        Assert.That(footprint.Count, Is.EqualTo(points));
        Assert.That(piece.BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True,
            $"blocked={piece.BlockedJunctions.Count}");

        var rim = new List<JunctionId>();
        SpatialQueries.CollectStandableAround(world, piece.Junctions[0], rim, 96,
            SpatialQueries.BesideReach(world.Content.ObjectDefinitions[id].ObstacleRadius), piece,
            InteractionReach.RimMode);
        Assert.That(rim, Is.Not.Empty, "Nobody can stand beside it any more.");

        WorldObjectMutations.ReleaseOwnedBlocking(world, piece);
        var loaded = RoundTrip(world, () => TestWorld.CreateWorld(world.Seed));
        Assert.That(loaded.Entities.Objects[piece.Id].BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True,
            "An older save is stamped on load.");
    }

    [Test]
    public void LoadStampsPiecesThatBlockedNothingInOlderSaves()
    {
        var (engine, _, fridge, _, footprint) = Place(ContentIds.ModernFridge, 2);
        var world = engine.World;
        WorldObjectMutations.ReleaseOwnedBlocking(world, fridge);
        Assert.That(footprint.Any(point => world.Junctions.Items[point].Blocked), Is.False);

        var loaded = RoundTrip(world, () => TestWorld.CreateWorld(world.Seed));
        Assert.That(loaded.Entities.Objects[fridge.Id].BlockedJunctions.ToHashSet().SetEquals(footprint), Is.True);
        Assert.That(footprint.All(point => loaded.Junctions.Items[point].Blocked), Is.True);
    }

    [Test]
    public void LoadStampsTheApartmentOfAnOlderSave()
    {
        var world = CreatePortalWorld(7);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var pieces = world.Entities.Objects.Values.Where(o => o.DefinitionId != ContentIds.ModernBed &&
            PortalWorld.IsFurniture(o.DefinitionId)).ToArray();
        var before = pieces.ToDictionary(o => o.Id, o => o.BlockedJunctions.ToHashSet());
        foreach (var piece in pieces) WorldObjectMutations.ReleaseOwnedBlocking(world, piece);

        var loaded = RoundTrip(world, () => CreatePortalWorld(7));
        foreach (var piece in pieces)
            Assert.That(loaded.Entities.Objects[piece.Id].BlockedJunctions.ToHashSet().SetEquals(before[piece.Id]),
                Is.True, piece.DefinitionId);
    }

    private static (SimulationEngine Engine, NPCState Walker, WorldObjectState Piece, JunctionKey Key,
        HashSet<JunctionId> Footprint) Place(string id, int yawStep)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var walker = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        walker.Inventory.Items.Add(new ItemInstance(id) { OwnerId = walker.Id.Value });
        Junction anchor = null;
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, walker.Tile) is >= 1 and <= 3)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, walker.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (world.Tiles.Items[tile].Flags.HasFlag(TileFlags.Indoor)) continue;
            var centre = HexSpatialMath.TileToWorld(tile);
            anchor = world.Tiles.Items[tile].Junctions.Select(point => world.Junctions.Items[point])
                .Where(j => HexSpatialMath.Distance(j.WorldPosition, centre) < .01f)
                .FirstOrDefault(j => FurniturePlacement.Validate(world, id, j, yawStep) == null &&
                    Around(world, FurniturePlacement.KeyOf(j.WorldPosition), yawStep).All(p => p != null && !p.Blocked));
            if (anchor != null) break;
        }

        Assert.That(anchor, Is.Not.Null);
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        var receipt = engine.ApplyManualCommand(new PlaceFurnitureCommand(id, key.XKey, key.YKey, yawStep));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var piece = world.Entities.Objects.Values.Single(o => o.DefinitionId == id &&
            o.Variant == PortalWorld.PlacedFurnitureVariant);
        var footprint = FurniturePlacement.FootprintKeys(id, key, yawStep)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j.Id : default).ToHashSet();
        return (engine, walker, piece, key, footprint);
    }

    // Two points four lattice columns either side of the middle (±1.30 wu),
    // so the straight line between them runs through the piece's centre.
    private static Junction[] Around(WorldState world, JunctionKey key, int yawStep)
    {
        var result = new Junction[2];
        for (var side = 0; side < 2; side++)
        {
            var local = BlueprintGeometry.RotateJunctionOffset(new JunctionKey(side == 0 ? -4 : 4, 0), yawStep);
            result[side] = FurniturePlacement.TryFindJunction(world,
                new JunctionKey(key.XKey + local.XKey, key.YKey + local.YKey), out var j) ? j : null;
        }
        return result;
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

    private static WorldState CreatePortalWorld(int seed)
    {
        var bootstrap = new WorldBootstrapDefinition();
        bootstrap.Simulation.Seed = seed;
        bootstrap.Simulation.EnablePortalTopology = true;
        var island = new FragmentBootstrap { Id = 1 };
        for (var q = -4; q <= 4; q++)
        for (var r = -4; r <= 4; r++)
            if (HexSpatialMath.HexDistance(new TileCoord(q, r), TileCoord.Zero) <= 4)
                island.Tiles.Add(new TileBootstrap { Q = q, R = r });
        bootstrap.Fragments.Add(island);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        return world;
    }

    private static WorldState RoundTrip(WorldState source, System.Func<WorldState> create)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, WorldSaveSerializer.BlobVersion);
        stream.Position = 0;
        var loaded = create();
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }
}
