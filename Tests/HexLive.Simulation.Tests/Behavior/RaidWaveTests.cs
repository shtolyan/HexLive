using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §72.14: рейдер каждые три дня, чередование пола и рост угрозы.
/// Счётчик волн — часть сейва: загрузка не воскрешает убитых.
/// </summary>
public sealed class RaidWaveTests
{
    [Test]
    public void EveryThreeDays_AlternatesSex_AndEscalatesWeaponsAndStats()
    {
        var world = TestWorld.CreateWorld();
        var system = new RaidWaveSystem();
        var interval = Spec72.RaidWaveIntervalDays * EnvironmentSystem.DayLengthTicks;

        var initial = world.Entities.Npcs.Values.Single(n => n.Faction == Faction.Outsiders);
        Assert.That(initial.Sex, Is.EqualTo(GarmentSex.Male));
        Assert.That(initial.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Knife), Is.True,
            "Авторский чужак сохраняет нож как второй трофей §79.");
        Assert.That(initial.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Machete), Is.True,
            "Трёхдневные волны не должны молча перебалансировать стартовую сцену §72.");

        world.Tick = interval;
        system.Run(world);
        var wave1 = world.Entities.Npcs[new EntityId(1001)];
        Assert.That(wave1.Sex, Is.EqualTo(GarmentSex.Female));
        Assert.That(wave1.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Axe), Is.True);
        Assert.That(wave1.EquippedArmor, Is.GreaterThan(0f),
            "Новая противница должна приходить в защитном комплекте.");

        world.Tick = interval * 2;
        system.Run(world);
        var wave2 = world.Entities.Npcs[new EntityId(1002)];
        Assert.That(wave2.Sex, Is.EqualTo(GarmentSex.Male));
        Assert.That(wave2.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Spear), Is.True);
        Assert.That(wave2.Attributes.Strength, Is.GreaterThan(wave1.Attributes.Strength));
        Assert.That(wave2.Skills.Combat, Is.GreaterThan(wave1.Skills.Combat));

        world.Tick = interval * 3;
        system.Run(world);
        var wave3 = world.Entities.Npcs[new EntityId(1003)];
        Assert.That(wave3.Sex, Is.EqualTo(GarmentSex.Female));
        Assert.That(wave3.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Machete), Is.True);
        Assert.That(world.RaidWavesSpawned, Is.EqualTo(3));
    }

    [Test]
    public void FirstWave_ArrivesOnCalendarDayThree_NotTickDayThree()
    {
        var world = TestWorld.CreateWorld();
        var system = new RaidWaveSystem();

        // Последний тик календарного дня 2: волны ещё нет.
        world.Tick = 2 * EnvironmentSystem.DayLengthTicks -
            EnvironmentSystem.DayLengthTicks / 4 - 1;
        Assert.That(EnvironmentSystem.CalendarDay(world.Tick), Is.EqualTo(2));
        system.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(new EntityId(1001)), Is.False,
            "Волна пришла раньше обещанного третьего дня.");

        // Полночь дня 3 — игрок видит «День 3», враг уже должен высадиться.
        world.Tick += 1;
        Assert.That(EnvironmentSystem.CalendarDay(world.Tick), Is.EqualTo(3));
        system.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(new EntityId(1001)), Is.True,
            "«Каждые три дня» отсчитывается по календарю на экране, " +
            "а не по сырым тик-суткам (те сдвинуты на четверть дня).");
        Assert.That(world.RaidWavesSpawned, Is.EqualTo(1));
    }

    [Test]
    public void SaveRoundTrip_PreservesWaveCounter()
    {
        var world = TestWorld.CreateWorld();
        world.RaidWavesSpawned = 7;

        var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.That(loaded.RaidWavesSpawned, Is.EqualTo(7),
            "Без счётчика сейв повторно спавнит уже пройденные волны.");
    }
}

}
