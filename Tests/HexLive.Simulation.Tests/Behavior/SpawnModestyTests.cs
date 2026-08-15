using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§133.5 r2: a woman never enters the world naked.</summary>
public sealed class SpawnModestyTests
{
    [Test]
    public void StartingColonistsAlwaysSpawnInBriefsAndBraAcrossSeeds()
    {
        // The former 80% bra roll failed frequently across a seed sweep. Keep
        // the sweep here because one friendly seed would let probability sneak
        // back into a rule that is meant to be absolute.
        for (var sample = 0; sample < 32; sample++)
        {
            var seed = 12345 + sample * 7919;
            var world = TestWorld.CreateWorld(seed);
            foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony))
            {
                AssertRequiredSpawnCover(world, npc, $"seed={seed} npc={npc.Id.Value}");
            }
        }
    }

    [Test]
    public void WeeklyArrivalAndFemaleRaidWaveUseTheSameMandatoryCover()
    {
        var oldInterval = WorldBalance.ColonyArrivalIntervalDays;
        var oldTotal = WorldBalance.MaxLivingNpcs;
        var oldColony = WorldBalance.MaxColonyNpcs;
        var oldOutsiders = WorldBalance.MaxOutsiderNpcs;
        var oldRaidInterval = Spec72.RaidWaveIntervalDays;
        try
        {
            WorldBalance.ColonyArrivalIntervalDays = 7;
            WorldBalance.MaxLivingNpcs = 10;
            WorldBalance.MaxColonyNpcs = 5;
            WorldBalance.MaxOutsiderNpcs = 5;
            Spec72.RaidWaveIntervalDays = 3;

            var colonyWorld = TestWorld.CreateWorld(45117);
            colonyWorld.Tick = CalendarBoundary(7);
            new ColonyArrivalSystem().Run(colonyWorld);
            AssertRequiredSpawnCover(
                colonyWorld,
                colonyWorld.Entities.Npcs[new EntityId(2001)],
                "weekly colony arrival");

            var raidWorld = TestWorld.CreateWorld(45117);
            raidWorld.Tick = CalendarBoundary(3);
            new RaidWaveSystem().Run(raidWorld);
            var raider = raidWorld.Entities.Npcs[new EntityId(1001)];
            Assert.That(raider.Sex, Is.EqualTo(GarmentSex.Female));
            AssertRequiredSpawnCover(raidWorld, raider, "female raid arrival");
        }
        finally
        {
            WorldBalance.ColonyArrivalIntervalDays = oldInterval;
            WorldBalance.MaxLivingNpcs = oldTotal;
            WorldBalance.MaxColonyNpcs = oldColony;
            WorldBalance.MaxOutsiderNpcs = oldOutsiders;
            Spec72.RaidWaveIntervalDays = oldRaidInterval;
        }
    }

    private static void AssertRequiredSpawnCover(WorldState world, NPCState npc, string context)
    {
        var briefs = npc.WornItems.Any(item =>
            world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
            definition.Layer == WearLayer.Underwear &&
            definition.Covers.Contains(BodyPart.Pelvis) &&
            !definition.Covers.Contains(BodyPart.Torso));
        var bra = npc.WornItems.Any(item =>
            world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition) &&
            definition.Layer == WearLayer.Underwear &&
            definition.Covers.Contains(BodyPart.Torso) &&
            !definition.Covers.Contains(BodyPart.Pelvis));

        Assert.Multiple(() =>
        {
            Assert.That(briefs, Is.True, $"No actual briefs at spawn: {context}");
            Assert.That(bra, Is.True, $"No bra/chest baseline at spawn: {context}");
            Assert.That(ModestyMath.MissingCover(world, npc), Is.False,
                $"Pelvis or chest remains exposed after the complete outfit is assembled: {context}");
        });
    }

    private static int CalendarBoundary(int day) =>
        (day - 1) * EnvironmentSystem.DayLengthTicks - EnvironmentSystem.DayLengthTicks / 4;
}

}
