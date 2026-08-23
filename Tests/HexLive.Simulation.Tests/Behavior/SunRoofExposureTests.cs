using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
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
    public void PalmShadowStartsAtItsRenderedJunction_NotTheTileCentre_Bug207()
    {
        var world = TestWorld.CreateWorld(1365566998);
        var palm = world.Entities.Objects.Values.First(obj =>
            obj.DefinitionId == "tree.palm" && obj.Junctions.Count > 0);
        var anchor = world.Junctions.Items[palm.Junctions[0]].WorldPosition;
        var legacyAnchor = HexSpatialMath.TileToWorld(palm.Tile);
        Assert.That(HexSpatialMath.Distance(anchor, legacyAnchor), Is.GreaterThan(0.1f),
            "Тесту нужна пальма, посаженная на junction, а не в центре гекса.");

        world.Entities.Objects.Clear();
        world.Entities.Objects[palm.Id] = palm;
        foreach (var tile in world.Tiles.Items.Values)
        {
            tile.Elevation = 1;
            tile.Flags &= ~TileFlags.Roofed;
        }

        TileCoord? anchoredOnly = null;
        TileCoord? legacyOnly = null;
        // Start with the exact report tick, then sweep the same seed's daylight
        // arc: the report can be filed a few in-game minutes after the player
        // first notices the moving mismatch.
        var probeTicks = new[] { 200160 }.Concat(
            Enumerable.Range(1, 15)
                .Select(slice => EnvironmentSystem.DayLengthTicks * slice / 32));
        foreach (var probeTick in probeTicks)
        {
            world.Tick = probeTick;
            new EnvironmentSystem().Run(world);
            var sun = world.SunDirection;
            var elevation = world.SunElevationDegrees;
            var anchoredProjection = Projection(world, anchor, sun, elevation, palm.Tile);
            var legacyProjection = Projection(world, legacyAnchor, sun, elevation, palm.Tile);
            var anchoredCandidate = anchoredProjection
                .Where(tile => !legacyProjection.Contains(tile))
                .Select(tile => (TileCoord?)tile)
                .FirstOrDefault();
            var legacyCandidate = legacyProjection
                .Where(tile => !anchoredProjection.Contains(tile))
                .Select(tile => (TileCoord?)tile)
                .FirstOrDefault();
            if (anchoredCandidate is not null && legacyCandidate is not null)
            {
                anchoredOnly = anchoredCandidate;
                legacyOnly = legacyCandidate;
                break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(anchoredOnly, Is.Not.Null,
                "Не нашлось гекса, который затеняет реальный junction пальмы.");
            Assert.That(legacyOnly, Is.Not.Null,
                "Тест не отличает старую тень из центра тайла от junction-тени.");
        });
        Assert.That(world.ShadedTiles.Contains(anchoredOnly!.Value), Is.True,
            "Симуляция не затенила гекс на рендерном луче от junction пальмы.");
        Assert.That(world.ShadedTiles.Contains(legacyOnly!.Value), Is.False,
            "Осталась фантомная тень от центра гекса, где пальмы нет.");
    }

    private static System.Collections.Generic.HashSet<TileCoord> Projection(
        WorldState world, Float2 caster, Float2 sun, float elevationDeg, TileCoord source)
    {
        var result = new System.Collections.Generic.HashSet<TileCoord> { source };
        var stepWorld = HexSpatialMath.HexRadius * HexSpatialMath.Sqrt3;
        var sampleDistance = stepWorld / 2f;
        var tanElevation = System.MathF.Tan(elevationDeg * System.MathF.PI / 180f);
        for (var sampleIndex = 1;
             sampleIndex <= WorldBalance.ShadowRaySteps * 2;
             sampleIndex++)
        {
            var distance = sampleDistance * sampleIndex;
            var coord = HexSpatialMath.WorldToTile(new Float2(
                caster.X - sun.X * distance,
                caster.Y - sun.Y * distance));
            if (!world.Tiles.Items.TryGetValue(coord, out var tile)) continue;
            var sunLine = tile.Elevation +
                tanElevation * distance / WorldBalance.ElevationWorldStep;
            if (1f + 7f >= sunLine) result.Add(coord);
        }

        return result;
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
