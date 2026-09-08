using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§157.7: чужаки «Островов» — по одному с моря на остров, и только пока предыдущий мёртв.</summary>
public sealed class IslandOutsiderWaveTests
{
    private static int Interval => Spec72.RaidWaveIntervalDays * EnvironmentSystem.DayLengthTicks;

    [Test]
    public void Day3_EachIslandGetsOneOutsiderOnItsOwnShore_NoOutsiderCamp()
    {
        var world = new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(12345, GameMode.Islands));
        SimTrace.EnableAll();
        Assert.That(world.FactionHomes.ContainsKey(Faction.Outsiders), Is.False, "стоянки чужаков нет");
        Assert.That(world.Entities.Npcs.Values.Any(n => n.Faction == Faction.Outsiders), Is.False);

        world.Tick = Interval;
        new RaidWaveSystem().Run(world);

        var islands = world.FactionHomes.Keys.Where(FactionRelations.IsGirlCamp).OrderBy(f => (int)f).ToArray();
        Assert.That(islands, Has.Length.EqualTo(6));
        foreach (var island in islands)
        {
            var id = RaidWaveSystem.IslandRaiderId(island, 1);
            Assert.That(world.Entities.Npcs.ContainsKey(id), Is.True, $"{island}: чужак {id.Value} не пришёл");
            var raider = world.Entities.Npcs[id];
            var home = world.FactionHomes[island];
            var distance = HexSpatialMath.HexDistance(raider.Tile, home);
            Assert.Multiple(() =>
            {
                Assert.That(raider.Faction, Is.EqualTo(Faction.Outsiders));
                Assert.That(distance, Is.InRange(RaidWaveSystem.IslandShoreMinDistanceTiles,
                    RaidWaveSystem.IslandShoreMaxDistanceTiles));
                foreach (var other in islands.Where(f => f != island))
                {
                    Assert.That(HexSpatialMath.HexDistance(raider.Tile, world.FactionHomes[other]),
                        Is.GreaterThanOrEqualTo(distance), "на своём острове");
                }
                Assert.That(world.Tiles.Items[raider.Tile].Junctions.Any(j =>
                        world.Junctions.Items[j].Neighbors.Any(n => SpatialQueries.IsAllWaterJunction(world, n))),
                    Is.True, "с моря — гекс касается воды");
                Assert.That(raider.Inventory.Items.Any(i => i.DefinitionId == RaidWaveSystem.WeaponForWave(1)), Is.True);
                Assert.That(world.IslandOutsiderWavesByFaction[island], Is.EqualTo(1));
            });
        }

        Assert.That(world.Entities.Npcs.Values.Count(n => n.Faction == Faction.Outsiders), Is.EqualTo(6),
            "шесть живых чужаков разом допустимы — MaxOutsiderNpcs здесь не читается");
        Assert.That(world.RaidWavesSpawned, Is.Zero, "общий курсор в «Островах» не трогается");
    }

    [Test]
    public void LivingOutsiderConsumesTheBoundary_DeadOneFreesTheNext()
    {
        var world = TestWorld.CreateWorld();
        world.Mode = GameMode.Islands;
        foreach (var npc in world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Outsiders).ToArray())
        {
            world.Entities.Npcs.Remove(npc.Id);
        }
        var system = new RaidWaveSystem();
        var first = RaidWaveSystem.IslandRaiderId(Faction.Colony, 1);
        var second = RaidWaveSystem.IslandRaiderId(Faction.Colony, 2);
        var third = RaidWaveSystem.IslandRaiderId(Faction.Colony, 3);

        world.Tick = Interval;
        system.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(first), Is.True, "день 3 — первый чужак");
        system.Run(world);
        Assert.That(world.Entities.Npcs.Values.Count(n => n.Faction == Faction.Outsiders), Is.EqualTo(1));

        world.Tick = Interval * 2;
        system.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Npcs.ContainsKey(second), Is.False, "живой чужак — второй не приходит");
            Assert.That(world.IslandOutsiderWavesByFaction[Faction.Colony], Is.EqualTo(2), "но граница съедена");
        });

        var raider = world.Entities.Npcs[first];
        raider.Health = 0f;
        world.Entities.Npcs.Remove(first);
        world.Entities.Corpses[first] = raider;
        system.Run(world);
        Assert.That(world.Entities.Npcs.ContainsKey(second), Is.False, "смерть не вызывает бэклог");

        world.Tick = Interval * 3;
        system.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Npcs.ContainsKey(third), Is.True, "день 9 после смерти — третий");
            Assert.That(world.Entities.Npcs[third].Sex, Is.EqualTo(Content.GarmentSex.Female),
                "номер волны = индекс границы: третья — женщина");
            Assert.That(world.IslandOutsiderWavesByFaction[Faction.Colony], Is.EqualTo(3));
        });
    }

    [Test]
    public void SaveRoundTripKeepsIslandCursorsAndFesteringWound()
    {
        var world = TestWorld.CreateWorld();
        world.Mode = GameMode.Islands;
        world.IslandOutsiderWavesByFaction[Faction.Colony] = 4;
        world.IslandOutsiderWavesByFaction[Faction.Colony3] = 2;
        var girl = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        girl.Wounds.Add(new WoundState
        {
            Id = girl.NextWoundId++, Zone = Content.BodyPart.Torso, Severity = 0.3f,
            Clot01 = 0.5f, BleedFactor = 0.4f, Festering = true, Seed = 7
        });

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld();
        loaded.Mode = GameMode.Islands; // сейв сверяет режим с миром, в который читается
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        WorldSaveSerializer.Read(loaded, reader);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.IslandOutsiderWavesByFaction[Faction.Colony], Is.EqualTo(4));
            Assert.That(loaded.IslandOutsiderWavesByFaction[Faction.Colony3], Is.EqualTo(2));
            Assert.That(loaded.IslandOutsiderWavesByFaction, Has.Count.EqualTo(2));
            var wound = loaded.Entities.Npcs[girl.Id].Wounds.Single(w => w.Zone == Content.BodyPart.Torso);
            Assert.That(wound.Festering, Is.True);
            Assert.That(WoundMath.IsFestering(loaded.Entities.Npcs[girl.Id], Content.BodyPart.Torso), Is.True);
        });
    }
}

}
