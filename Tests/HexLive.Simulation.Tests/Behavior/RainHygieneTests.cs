using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class RainHygieneTests
{
    [Test]
    public void OutdoorRainWashesSkinAtOneTenthOfWaterRate()
    {
        var world = TestWorld.CreateWorld(149);
        var npc = world.Entities.Npcs.Values.First();
        var outdoor = world.Tiles.Items.First(pair =>
            !pair.Value.Flags.HasFlag(TileFlags.Water) &&
            !pair.Value.Flags.HasFlag(TileFlags.Indoor));
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, outdoor.Key);
        npc.Tile = outdoor.Key;
        npc.Position = HexSpatialMath.TileToWorld(outdoor.Key);
        var tile = outdoor.Value;
        Assert.That(tile.Flags.HasFlag(TileFlags.Water), Is.False, "fixture must start on land");
        Assert.That(tile.Flags.HasFlag(TileFlags.Indoor), Is.False, "fixture must be exposed to rain");

        npc.Needs.Hygiene = 0.5f;
        var leg = npc.Body.Condition(BodyPart.LegL);
        leg.BloodSoil = 0.75f;
        world.Environment.IsRaining = true;
        var hygieneBefore = npc.Needs.Hygiene;
        var bloodSoilBefore = leg.BloodSoil;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Hygiene - hygieneBefore,
            Is.EqualTo(SimBalance.HygieneWashGain * 0.1f).Within(0.000001f));
        Assert.That(bloodSoilBefore - leg.BloodSoil,
            Is.EqualTo(SimBalance.BloodSoilWashPerTick * 0.1f).Within(0.000001f));
    }
}
