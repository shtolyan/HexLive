using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§40.7 r2: without tanning UV, skin returns to its base tone.</summary>
public sealed class TanFadeTests
{
    private const int DefaultSlowInterval = 16;

    [Test]
    public void FullTanFadesGraduallyOverThirtyTwoNoUvGameDays()
    {
        // Load the shipped SimData balance, exactly as the headless game does.
        TestWorld.CreateWorld(40702);
        var slowTicksPerDay = WorldBalance.DayLengthTicks / DefaultSlowInterval;
        var fadeDuration = slowTicksPerDay * 32;

        Assert.That(SimBalance.TanFadeRate * fadeDuration, Is.EqualTo(1f).Within(0.0001f),
            "Полный загар должен выцветать примерно за 32 дня без солнца.");

        var tan = 1f;
        for (var tick = 0; tick < slowTicksPerDay * 16; tick++)
        {
            tan = TemperatureSystem.FadeTan(tan);
        }

        Assert.That(tan, Is.EqualTo(0.5f).Within(0.001f),
            "Выцветание должно быть постепенным, а не ступенькой в последний день.");

        for (var tick = slowTicksPerDay * 16; tick < fadeDuration + slowTicksPerDay; tick++)
        {
            tan = TemperatureSystem.FadeTan(tan);
        }

        Assert.That(tan, Is.Zero);
    }

    [Test]
    public void NightAndShadeBothFadeInsteadOfFreezingTan()
    {
        var world = TestWorld.CreateWorld(40703);
        var openTile = world.Tiles.Items.First(pair =>
            !pair.Value.Flags.HasFlag(TileFlags.Roofed) &&
            !pair.Value.Flags.HasFlag(TileFlags.Water));
        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = openTile.Key;
        npc.Needs.TanLevel = 0.5f;
        npc.Needs.Sunburn = 0f;
        world.Environment.GlobalTemperature = 20f;
        world.ShadedTiles.Clear();

        world.Environment.UvIndex = 0f; // night
        new TemperatureSystem().Run(world);
        var afterNight = npc.Needs.TanLevel;
        Assert.That(afterNight,
            Is.EqualTo(0.5f - SimBalance.TanFadeRate).Within(0.000001f));

        world.Environment.UvIndex = 0.9f;
        world.ShadedTiles.Add(openTile.Key); // effective UV = 0.18, below tanning threshold
        Assert.That(TemperatureSystem.EffectiveUv(world, openTile.Key),
            Is.EqualTo(0.18f).Within(0.000001f));
        new TemperatureSystem().Run(world);

        Assert.That(npc.Needs.TanLevel,
            Is.EqualTo(afterNight - SimBalance.TanFadeRate).Within(0.000001f),
            "Тень останавливала рост загара, но не запускала его выцветание.");
    }

    [Test]
    public void TwoHoursOfDailySunLeaveTanAfterNightAndRednessHeals()
    {
        var world = TestWorld.CreateWorld(40704);
        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = world.Tiles.Items.First(pair =>
            !pair.Value.Flags.HasFlag(TileFlags.Roofed) &&
            !pair.Value.Flags.HasFlag(TileFlags.Water)).Key;
        npc.WornItems.Clear();
        npc.Needs.TanLevel = 0f;
        npc.Needs.Sunburn = 0f;
        world.ShadedTiles.Clear();
        world.Environment.GlobalTemperature = 20f;
        var system = new TemperatureSystem();
        var previousDawnTan = 0f;
        var maxRedness = 0f;
        for (var day = 0; day < 3; day++)
        {
            for (var tick = 0; tick < WorldBalance.DayLengthTicks; tick += DefaultSlowInterval)
            {
                var progress = (float)tick / WorldBalance.DayLengthTicks;
                // Two game hours around noon; shade for the rest of the day.
                // A full naked day can hide excessive fade behind burn-to-tan conversion.
                world.Environment.UvIndex = System.MathF.Abs(progress - .25f) < 1f / 24f
                    ? .9f * System.MathF.Sin(2f * System.MathF.PI * progress) : 0f;
                // Isolate skin exposure from cumulative injury/death during the fixture.
                npc.SunExposure = 0f;
                system.Run(world);
                maxRedness = System.MathF.Max(maxRedness, npc.Needs.Sunburn);
            }
            Assert.That(npc.Needs.TanLevel, Is.GreaterThan(previousDawnTan + .03f),
                "Солнечный день должен оставлять заметный загар после ночи.");
            Assert.That(npc.Needs.Sunburn, Is.Zero, "Краснота заживает отдельно от загара.");
            previousDawnTan = npc.Needs.TanLevel;
        }
        Assert.That(maxRedness, Is.GreaterThan(.3f), "Открытая кожа должна краснеть на солнце.");
    }
}

}
