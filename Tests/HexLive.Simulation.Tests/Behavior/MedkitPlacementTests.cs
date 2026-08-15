using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§118.2: домашняя аптечка стоит отдельно от authored footprint гардероба.</summary>
public sealed class MedkitPlacementTests
{
    private static WorldObjectState Hut(WorldState world) =>
        world.Entities.Objects.Values.First(item => item.DefinitionId == ContentIds.Hut1Hex);

    private static WorldObjectState InHut(WorldState world, WorldObjectState hut, string id) =>
        world.Caches.ObjectsByTile[hut.Tile]
            .Select(objectId => world.Entities.Objects[objectId])
            .First(item => item.DefinitionId == id);

    [Test]
    public void MedkitUsesAuthoredFreeInteriorJunction()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        var wardrobe = InHut(world, hut, ContentIds.Wardrobe);
        var medkit = InHut(world, hut, ContentIds.MedkitBox);
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * System.MathF.PI / 180f;
        var expected = center + Rotate(
            new Float2(BuildingRules.HutMedkitLocalX, BuildingRules.HutMedkitLocalZ), radians);
        var actual = world.Junctions.Items[medkit.Junctions.Single()].WorldPosition;
        var delta = actual - expected;

        var footprint = new[] { 9, 4, 0 }
            .Select(slot => HexPointLayout.GetInteriorTemplates().Single(point => point.Slot == slot))
            .Select(point => center + Rotate(point.Offset, radians))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(medkit.Junctions, Has.Count.EqualTo(1));
            Assert.That(medkit.BlockedJunctions, Is.Empty);
            Assert.That(delta.X * delta.X + delta.Y * delta.Y, Is.LessThan(0.0001f * 0.0001f));
            Assert.That(footprint.Any(point => DistanceSquared(point, actual) < 0.0001f * 0.0001f),
                Is.False, "Аптечка пересекает authored footprint гардероба 9→4→0.");
            Assert.That(DistanceSquared(
                    world.Junctions.Items[wardrobe.Junctions.Single()].WorldPosition, actual),
                Is.EqualTo(0.375f * 0.375f).Within(0.000001f));
        });
    }

    [Test]
    public void LoadingOldSharedWardrobeAnchorRepairsMedkitWithoutReplacingContents()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        var wardrobe = InHut(world, hut, ContentIds.Wardrobe);
        var medkit = InHut(world, hut, ContentIds.MedkitBox);
        var originalContents = medkit.Contents.Count;
        medkit.Junctions.Clear();
        medkit.Junctions.Add(wardrobe.Junctions.Single());

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var loadedHut = Hut(loaded);
        var loadedWardrobe = InHut(loaded, loadedHut, ContentIds.Wardrobe);
        var loadedMedkit = InHut(loaded, loadedHut, ContentIds.MedkitBox);
        Assert.Multiple(() =>
        {
            Assert.That(loadedMedkit.Junctions.Single(), Is.Not.EqualTo(loadedWardrobe.Junctions.Single()));
            Assert.That(loadedMedkit.Contents, Has.Count.EqualTo(originalContents),
                "Load repair пересоздал или продублировал содержимое аптечки.");
        });
    }

    private static Float2 Rotate(Float2 local, float radians) => new(
        local.X * System.MathF.Cos(radians) - local.Y * System.MathF.Sin(radians),
        local.X * System.MathF.Sin(radians) + local.Y * System.MathF.Cos(radians));

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var delta = a - b;
        return delta.X * delta.X + delta.Y * delta.Y;
    }
}

}
