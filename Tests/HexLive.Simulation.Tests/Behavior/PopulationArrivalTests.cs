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

/// <summary>§132: weekly colony arrivals and the two-level living-population cap.</summary>
public sealed class PopulationArrivalTests
{
    [Test]
    public void WeeklyArrival_AddsOneGirlUntilCap_AndDoesNotBacklogSkippedWeeks()
    {
        var oldInterval = WorldBalance.ColonyArrivalIntervalDays;
        var oldTotal = WorldBalance.MaxLivingNpcs;
        var oldColony = WorldBalance.MaxColonyNpcs;
        var oldOutsiders = WorldBalance.MaxOutsiderNpcs;
        try
        {
            WorldBalance.ColonyArrivalIntervalDays = 7;
            WorldBalance.MaxLivingNpcs = 10;
            WorldBalance.MaxColonyNpcs = 5;
            WorldBalance.MaxOutsiderNpcs = 5;

            var world = TestWorld.CreateWorld();
            var system = new ColonyArrivalSystem();
            Assert.That(Count(world, Faction.Colony), Is.EqualTo(3));

            world.Tick = CalendarBoundary(7);
            system.Run(world);
            var first = world.Entities.Npcs[new EntityId(2001)];
            Assert.Multiple(() =>
            {
                Assert.That(Count(world, Faction.Colony), Is.EqualTo(4));
                Assert.That(first.Faction, Is.EqualTo(Faction.Colony));
                Assert.That(first.Sex, Is.EqualTo(HexLive.Simulation.Content.GarmentSex.Female));
                Assert.That(first.Inventory.Items.Any(i => i.DefinitionId == GearCatalog.Bottle), Is.True);
                Assert.That(first.CurrentJunction, Is.Not.Null,
                    "Прибытие должно владеть реальной свободной точкой, а не телепортом в центр предмета.");
            });

            system.Run(world);
            Assert.That(Count(world, Faction.Colony), Is.EqualTo(4),
                "Повторный Medium-pass на той же дате не дублирует прибытие.");

            world.Tick = CalendarBoundary(14);
            system.Run(world);
            Assert.That(Count(world, Faction.Colony), Is.EqualTo(5));

            world.Tick = CalendarBoundary(21);
            system.Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(Count(world, Faction.Colony), Is.EqualTo(5));
                Assert.That(world.ColonyArrivalsProcessed, Is.EqualTo(3));
                Assert.That(world.Entities.Npcs.ContainsKey(new EntityId(2003)), Is.False);
            });

            RemoveLiving(world, first);
            system.Run(world);
            Assert.That(Count(world, Faction.Colony), Is.EqualTo(4),
                "Освободившееся на следующий день место не вызывает отложенную девушку из бэклога.");

            world.Tick = CalendarBoundary(28);
            system.Run(world);
            Assert.Multiple(() =>
            {
                Assert.That(Count(world, Faction.Colony), Is.EqualTo(5));
                Assert.That(world.Entities.Npcs.ContainsKey(new EntityId(2004)), Is.True,
                    "Пропущенная по лимиту неделя оставляет видимый пробел в номерах, а не скрытую очередь.");
            });
        }
        finally
        {
            WorldBalance.ColonyArrivalIntervalDays = oldInterval;
            WorldBalance.MaxLivingNpcs = oldTotal;
            WorldBalance.MaxColonyNpcs = oldColony;
            WorldBalance.MaxOutsiderNpcs = oldOutsiders;
        }
    }

    [Test]
    public void GlobalCapBlocksArrivalEvenWhenFactionHasRoom()
    {
        var oldInterval = WorldBalance.ColonyArrivalIntervalDays;
        var oldTotal = WorldBalance.MaxLivingNpcs;
        var oldColony = WorldBalance.MaxColonyNpcs;
        try
        {
            WorldBalance.ColonyArrivalIntervalDays = 7;
            WorldBalance.MaxLivingNpcs = 4;
            WorldBalance.MaxColonyNpcs = 5;

            var world = TestWorld.CreateWorld(); // 3 colony + 1 outsider
            world.Tick = CalendarBoundary(7);
            new ColonyArrivalSystem().Run(world);

            Assert.Multiple(() =>
            {
                Assert.That(world.Entities.Npcs.Count, Is.EqualTo(4));
                Assert.That(Count(world, Faction.Colony), Is.EqualTo(3));
                Assert.That(world.ColonyArrivalsProcessed, Is.EqualTo(1));
            });
        }
        finally
        {
            WorldBalance.ColonyArrivalIntervalDays = oldInterval;
            WorldBalance.MaxLivingNpcs = oldTotal;
            WorldBalance.MaxColonyNpcs = oldColony;
        }
    }

    [Test]
    public void SaveRoundTripPreservesWeeklyScheduleCursor()
    {
        var world = TestWorld.CreateWorld();
        world.ColonyArrivalsProcessed = 4;

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

        Assert.That(loaded.ColonyArrivalsProcessed, Is.EqualTo(4));
    }

    private static int CalendarBoundary(int day) =>
        (day - 1) * EnvironmentSystem.DayLengthTicks - EnvironmentSystem.DayLengthTicks / 4;

    private static int Count(WorldState world, Faction faction) =>
        world.Entities.Npcs.Values.Count(n => n.Faction == faction);

    private static void RemoveLiving(WorldState world, NPCState npc)
    {
        world.Entities.Npcs.Remove(npc.Id);
        if (world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var occupied))
        {
            occupied.Remove(npc.Id);
        }
        if (npc.CurrentJunction is { } junction &&
            world.Occupancy.JunctionOwner.TryGetValue(junction, out var owner) &&
            owner == npc.Id)
        {
            world.Occupancy.JunctionOwner[junction] = null;
        }
        if (world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var byTile))
        {
            byTile.Remove(npc.Id);
        }
        if (world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var byFragment))
        {
            byFragment.Remove(npc.Id);
        }
    }
}

}
