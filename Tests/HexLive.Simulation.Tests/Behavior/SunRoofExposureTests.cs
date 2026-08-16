using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §35.4 r2 (баг #167). Солнце перекрывает КРЫША, а не флаг Indoor. Indoor у нас
/// значит «санктуарий» (§72.12) и стоит на стартовом дворе колонии и на стоянке
/// чужака — под открытым небом. Спящая там на кровати колонистка ловила ровный
/// ноль ультрафиолета, хотя над ней ничего нет.
/// </summary>
public sealed class SunRoofExposureTests
{
    // Полдень: UvIndex = 0.9 * sin(pi * p / 0.5) максимален при p = 0.25.
    private static int MiddayTick(WorldState world) =>
        EnvironmentSystem.DayLengthTicks / 4;

    private static WorldState WorldAtMidday(int seed = 51423)
    {
        var world = TestWorld.CreateWorld(seed);
        world.Tick = MiddayTick(world);
        new EnvironmentSystem().Run(world);
        return world;
    }

    [Test]
    public void SanctuaryTileWithoutRoof_StillCatchesFullSun()
    {
        var world = WorldAtMidday();
        var yard = world.Tiles.Items.First(pair =>
            pair.Value.Flags.HasFlag(TileFlags.Indoor) &&
            !pair.Value.Flags.HasFlag(TileFlags.Roofed));

        Assert.That(world.ShadedTiles.Contains(yard.Key), Is.False,
            "санктуарий без перекрытия не должен считаться вечной тенью");
        Assert.That(TemperatureSystem.EffectiveUv(world, yard.Key),
            Is.EqualTo(world.Environment.UvIndex).Within(0.0001f));
        Assert.That(world.Environment.UvIndex, Is.GreaterThan(0.5f),
            "тест обязан стоять на полудне, иначе он ничего не проверяет");
    }

    [Test]
    public void RoofedTile_BlocksTheSunEntirely()
    {
        var world = WorldAtMidday();
        var roofed = world.Tiles.Items.First(pair =>
            pair.Value.Flags.HasFlag(TileFlags.Roofed));

        Assert.That(TemperatureSystem.EffectiveUv(world, roofed.Key), Is.EqualTo(0f));
    }

    [Test]
    public void CompletedHut_GetsBothSanctuaryAndRoof()
    {
        var world = TestWorld.CreateWorld();
        var hutTile = world.Tiles.Items.First(pair =>
            pair.Value.Flags.HasFlag(TileFlags.HasFloor));

        Assert.That(hutTile.Value.Flags.HasFlag(TileFlags.Indoor), Is.True);
        Assert.That(hutTile.Value.Flags.HasFlag(TileFlags.Roofed), Is.True);
    }

    [Test]
    public void SleeperOnAnOpenAirBed_Tans()
    {
        var world = WorldAtMidday();
        var yard = world.Tiles.Items.First(pair =>
            pair.Value.Flags.HasFlag(TileFlags.Indoor) &&
            !pair.Value.Flags.HasFlag(TileFlags.Roofed) &&
            pair.Value.Flags.HasFlag(TileFlags.Walkable));

        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = yard.Key;
        npc.Needs.TanLevel = 0f;
        npc.Needs.Sunburn = 0f;

        new TemperatureSystem().Run(world);

        Assert.That(npc.Needs.TanLevel, Is.GreaterThan(0f),
            "спящая под открытым небом обязана загорать");
    }

    [Test]
    public void SaveWithoutTheRoofFlag_RestoresRoofsFromTheFloor()
    {
        var world = TestWorld.CreateWorld();
        var roofed = world.Tiles.Items
            .Where(pair => pair.Value.Flags.HasFlag(TileFlags.Roofed))
            .Select(pair => pair.Key)
            .ToArray();
        Assert.That(roofed, Is.Not.Empty, "в стартовом мире обязан быть достроенный дом");

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld();
        // Имитируем сейв v48: снимаем флаг со свежесозданного мира, чтобы
        // проверка ловила именно миграцию, а не совпадение с worldgen.
        foreach (var tile in loaded.Tiles.Items.Values)
        {
            tile.Flags &= ~TileFlags.Roofed;
        }

        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        foreach (var coord in roofed)
        {
            Assert.That(loaded.Tiles.Items[coord].Flags.HasFlag(TileFlags.Roofed), Is.True,
                $"тайл {coord} потерял крышу при загрузке");
        }
    }
}

}
