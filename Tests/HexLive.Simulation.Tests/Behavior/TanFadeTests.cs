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
    public void FullTanFadesLinearlyToZeroInFourNoUvGameDays()
    {
        // Load the shipped SimData balance, exactly as the headless game does.
        TestWorld.CreateWorld(40702);
        var slowTicksPerDay = WorldBalance.DayLengthTicks / DefaultSlowInterval;
        var fourDays = slowTicksPerDay * 4;

        Assert.That(SimBalance.TanFadeRate * fourDays, Is.EqualTo(1f).Within(0.0001f),
            "Балансная ручка больше не соответствует обещанным четырём дням.");

        var tan = 1f;
        for (var tick = 0; tick < slowTicksPerDay * 2; tick++)
        {
            tan = TemperatureSystem.FadeTan(tan);
        }

        Assert.That(tan, Is.EqualTo(0.5f).Within(0.0001f),
            "Выцветание должно быть постепенным, а не ступенькой в последний день.");

        for (var tick = slowTicksPerDay * 2; tick < fourDays; tick++)
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
}

}
