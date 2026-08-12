using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: гардероб — домашняя родня сушилки. Стоит у свободной стены хижины,
/// ничего не перегораживает и сушит вещи от очага.
/// </summary>
public sealed class WardrobeTests
{
    private static WorldObjectState WardrobeIn(WorldState world, WorldObjectState hut) =>
        world.Caches.ObjectsByTile[hut.Tile]
            .Select(id => world.Entities.Objects[id])
            .FirstOrDefault(o => o.DefinitionId == ContentIds.Wardrobe);

    private static WorldObjectState Hut(WorldState world) =>
        world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Hut1Hex);

    [Test]
    public void CompletedHutGetsAWardrobeThatBlocksNothing()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        var wardrobe = WardrobeIn(world, hut);

        Assert.That(wardrobe, Is.Not.Null, "В достроенной хижине нет гардероба.");
        Assert.That(wardrobe.Junctions.Count, Is.EqualTo(1), "Гардероб без якоря — вешать некуда.");
        Assert.That(wardrobe.BlockedJunctions, Is.Empty,
            "Гардероб занял джанкшен: комната в один гекс, так запирается дверь или койка.");
        Assert.That(world.Junctions.Items[wardrobe.Junctions[0]].Blocked, Is.False);
    }

    /// <summary>
    /// ⭐ Ради этого считалась геометрия: мебель стоит у стены и не отрезает
    /// путь «дверь → кровати/очаг».
    /// </summary>
    [Test]
    public void WardrobeKeepsItsDistanceFromTheDoorTheBedsAndTheHearth()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        var wardrobe = WardrobeIn(world, hut);
        var spot = world.Junctions.Items[wardrobe.Junctions[0]].WorldPosition;

        var interior = world.Caches.ObjectsByTile[hut.Tile]
            .Select(id => world.Entities.Objects[id])
            .Where(o => o.Id.Value != wardrobe.Id.Value && o.Junctions.Count > 0 &&
                        (o.DefinitionId == ContentIds.BedBasic || o.DefinitionId == ContentIds.Campfire))
            .ToArray();
        Assert.That(interior, Is.Not.Empty, "В хижине не нашлось ни коек, ни очага — тест бессмысленен.");

        foreach (var other in interior)
        {
            var delta = spot - world.Junctions.Items[other.Junctions[0]].WorldPosition;
            Assert.That(System.MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y),
                Is.GreaterThanOrEqualTo(0.5f),
                $"Гардероб сел вплотную к {other.DefinitionId}.");
        }

        var local = BuildingRules.DoorLocalCenter(world, hut);
        var radians = hut.RotationDegrees * System.MathF.PI / 180f;
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var door = center + new Float2(
            local.X * System.MathF.Cos(radians) - local.Y * System.MathF.Sin(radians),
            local.X * System.MathF.Sin(radians) + local.Y * System.MathF.Cos(radians));
        var toDoor = spot - door;
        Assert.That(System.MathF.Sqrt(toDoor.X * toDoor.X + toDoor.Y * toDoor.Y),
            Is.GreaterThanOrEqualTo(1f), "Гардероб встал в дверях.");
    }

    /// <summary>Дом из старого сейва получает гардероб на загрузке, а не остаётся без него.</summary>
    [Test]
    public void LoadingAHutWithoutAWardrobeInstallsOne()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = Hut(world);
        var wardrobe = WardrobeIn(world, hut);
        WorldObjectMutations.DespawnObject(world, wardrobe.Id);
        Assert.That(WardrobeIn(world, hut), Is.Null, "Гардероб не удалился — проверять нечего.");

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

        Assert.That(WardrobeIn(loaded, Hut(loaded)), Is.Not.Null,
            "После загрузки в доме нет гардероба — старые сейвы останутся без мебели.");
    }

    /// <summary>
    /// Вещь в гардеробе сохнет от ЖИВОГО очага; потухший — просто шкаф.
    /// </summary>
    [Test]
    public void WardrobeDriesFasterWhileTheHearthBurns()
    {
        static float DryRun(bool lit)
        {
            var engine = TestWorld.CreateEngine(12345);
            var world = engine.World;
            var hut = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Hut1Hex);
            var wardrobe = world.Caches.ObjectsByTile[hut.Tile]
                .Select(id => world.Entities.Objects[id])
                .First(o => o.DefinitionId == ContentIds.Wardrobe);
            var hearth = world.Caches.ObjectsByTile[hut.Tile]
                .Select(id => world.Entities.Objects[id])
                .First(o => o.DefinitionId == ContentIds.Campfire);
            hearth.ResourceAmount = lit ? 5f : 0f;

            var garment = WorldObjectMutations.SpawnObject(
                world, "underwear.bra_riot", hut.Fragment, hut.Tile, wardrobe.Junctions[0]);
            garment.Wetness = 1f;

            for (var i = 0; i < 200; i++)
            {
                engine.Step();
                hearth.ResourceAmount = lit ? 5f : 0f; // очаг не должен прогореть по ходу замера
            }

            return world.Entities.Objects.TryGetValue(garment.Id, out var after) ? after.Wetness : 0f;
        }

        var wetWithFire = DryRun(lit: true);
        var wetWithoutFire = DryRun(lit: false);

        Assert.That(wetWithFire, Is.LessThan(wetWithoutFire),
            "При горящем очаге вещь в гардеробе сохнет не быстрее — сушка не подключена.");
    }
}

}
