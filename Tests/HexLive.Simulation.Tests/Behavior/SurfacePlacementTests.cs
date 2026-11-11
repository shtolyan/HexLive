using System.IO;
using System.Linq;
using System.Text;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class SurfacePlacementTests
{
    private static readonly TileCoord Adjacent = new(1, 0);

    [Test]
    public void SlotsRejectDuplicatesAndReleaseWhenItemOrFurnitureIsRemoved()
    {
        var world = CreateWorld();
        var table = Spawn(world, "furniture.modern_table");
        var apple = Spawn(world, ContentIds.Apple);
        var banana = Spawn(world, ContentIds.Banana);
        var chair = Spawn(world, "furniture.modern_chair");
        Assert.That(SurfacePlacementMath.TryPlace(world, chair, table, 0), Is.False);
        Assert.That(SurfacePlacementMath.TryPlace(world, apple, table, 2), Is.False);
        Assert.That(SurfacePlacementMath.TryPlace(world, apple, table, 0), Is.True);
        Assert.That(SurfacePlacementMath.TryPlace(world, banana, table, 0), Is.False);

        Assert.That(WorldObjectMutations.DespawnObject(world, apple.Id), Is.True);
        Assert.That(apple.SurfaceObjectId, Is.Null);
        Assert.That(SurfacePlacementMath.TryPlace(world, banana, table, 0), Is.True);
        Assert.That(WorldObjectMutations.DespawnObject(world, table.Id), Is.True);
        Assert.That(banana.SurfaceObjectId, Is.Null);
        Assert.That(world.Entities.Objects[banana.Id], Is.SameAs(banana));
        Assert.That(banana.Tile, Is.EqualTo(TileCoord.Zero));
    }

    [Test]
    public void SurfaceLinkSurvivesCurrentSaveAndDefaultsToGroundInPreviousVersion()
    {
        var world = CreateWorld();
        var shelf = Spawn(world, "furniture.modern_shelf");
        var bottle = Spawn(world, ContentIds.Bottle);
        Assert.That(SurfacePlacementMath.TryPlace(world, bottle, shelf, 3), Is.True);

        var restored = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        var savedBottle = restored.Entities.Objects[bottle.Id];
        Assert.That(savedBottle.SurfaceObjectId, Is.EqualTo(shelf.Id));
        Assert.That(savedBottle.SurfaceSlot, Is.EqualTo(3));

        var oldBottle = RoundTrip(world, 91).Entities.Objects[bottle.Id];
        Assert.That(oldBottle.SurfaceObjectId, Is.Null);
        Assert.That(oldBottle.SurfaceSlot, Is.Zero);
    }

    [Test]
    public void KeyframeTransportsSlotAndDeltaClearsItWhenFurnitureMoves()
    {
        var world = CreateWorld();
        var table = Spawn(world, "furniture.modern_table");
        var apple = Spawn(world, ContentIds.Apple);
        Assert.That(SurfacePlacementMath.TryPlace(world, apple, table, 1), Is.True);
        var initial = WorldSnapshotExporter.Export(world);
        var mirror = new WorldSnapshot();
        using var keyframe = new MemoryStream();
        using (var writer = new BinaryWriter(keyframe, Encoding.UTF8, true))
            WorldSnapshotCodec.Write(initial, writer, includeDebugDetails: false);
        keyframe.Position = 0;
        using (var reader = new BinaryReader(keyframe)) WorldSnapshotCodec.Read(reader, mirror);
        var before = mirror.Objects.Single(obj => obj.Id == apple.Id);
        Assert.That(before.SurfaceObjectId, Is.EqualTo(table.Id.Value));
        Assert.That(before.SurfaceSlot, Is.EqualTo(1));

        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(initial, includeDebugDetails: false);
        WorldObjectMutations.MoveObjectTile(world, table, Adjacent);
        Assert.That(apple.Tile, Is.EqualTo(TileCoord.Zero));
        Assert.That(SurfacePlacementMath.TryPlace(world, apple, table, 1), Is.False);
        world.Tick++;
        var delta = encoder.Encode(WorldSnapshotExporter.Export(world), includeDebugDetails: false);
        using var patch = new BinaryReader(new MemoryStream(delta));
        SnapshotDeltaReader.Apply(patch, mirror, initial.Tick);
        var after = mirror.Objects.Single(obj => obj.Id == apple.Id);
        Assert.That(after.SurfaceObjectId, Is.Null);
        Assert.That(after.SurfaceSlot, Is.Zero);
    }

    private static WorldState CreateWorld()
    {
        var bootstrap = new WorldBootstrapDefinition();
        var island = new FragmentBootstrap { Id = 1 };
        island.Tiles.Add(new TileBootstrap { Q = 0, R = 0 });
        island.Tiles.Add(new TileBootstrap { Q = Adjacent.Q, R = Adjacent.R });
        bootstrap.Fragments.Add(island);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        DefinitionIdTable.Build(world.Content);
        return world;
    }

    private static WorldObjectState Spawn(WorldState world, string definitionId)
    {
        var anchor = StructurePlacement.CenterJunction(world, TileCoord.Zero).Value;
        return WorldObjectMutations.SpawnObject(world, definitionId, world.Junctions.Items[anchor].Fragment,
            TileCoord.Zero, anchor);
    }

    private static WorldState RoundTrip(WorldState world, int version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        var restored = CreateWorld();
        WorldSaveSerializer.Read(restored, reader);
        return restored;
    }
}
