using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class SurfGiftTests
{
    [Test]
    public void FiveDayDawnSpawnsOneFemaleGarmentPerColonyGirl()
    {
        var priorInterval = WorldBalance.SurfGiftIntervalDays;
        try
        {
            WorldBalance.SurfGiftIntervalDays = 5;
            var world = TestWorld.CreateWorld(6363);
            var before = world.Entities.Objects.Keys.ToHashSet();
            var weather = new WeatherSystem();

            world.Tick = EnvironmentSystem.DayLengthTicks * 4;
            weather.Run(world);
            Assert.That(world.Entities.Objects.Keys, Is.EquivalentTo(before),
                "Четыре суток — ещё не срок пятидневной поставки.");

            var girls = world.Entities.Npcs.Values.Count(npc =>
                npc.Faction == Faction.Colony && npc.Sex == GarmentSex.Female);

            world.Tick = EnvironmentSystem.DayLengthTicks * 5;
            weather.Run(world);

            var gifts = world.Entities.Objects.Values
                .Where(obj => !before.Contains(obj.Id))
                .ToList();
            Assert.That(gifts, Has.Count.EqualTo(girls));
            Assert.That(gifts.Select(obj => obj.Junctions.Single()).Distinct().Count(),
                Is.EqualTo(gifts.Count), "Каждой вещи нужна отдельная свободная точка берега.");

            foreach (var gift in gifts)
            {
                var garment = GarmentLibrary.Active.First(g => g.Id == gift.DefinitionId);
                Assert.That(garment.Sex, Is.Not.EqualTo(GarmentSex.Male));
                Assert.That(gift.Wetness, Is.EqualTo(1f));
                Assert.That(gift.Durability, Is.InRange(0.55f, 0.95f));
                Assert.That(gift.Dirtiness, Is.InRange(0.1f, 0.3f));
            }
        }
        finally
        {
            WorldBalance.SurfGiftIntervalDays = priorInterval;
        }
    }
}

}
