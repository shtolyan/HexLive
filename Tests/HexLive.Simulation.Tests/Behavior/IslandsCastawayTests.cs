using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§157.5–§157.6: потерпевшая «Островов» — прибытие, потолок, срок жизни, бинт до подъёма.</summary>
public sealed class IslandsCastawayTests
{
    private const int Seed = 12345;
    private static WorldState _islands;

    private static WorldState Islands() =>
        _islands ??= new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(Seed, GameMode.Islands));

    private static int CalendarBoundary(int day) =>
        (day - 1) * EnvironmentSystem.DayLengthTicks - EnvironmentSystem.DayLengthTicks / 4;

    private static EntityId CastawayId(Faction faction, int arrival) =>
        new(2000 + (int)faction * 500 + arrival);

    [Test]
    public void Day7_EachCampGetsOneNakedComatoseCastawayOnItsOwnShore()
    {
        var world = Islands();
        world.Tick = CalendarBoundary(8);
        SimTrace.EnableAll();
        var starters = world.Entities.Npcs.Values.ToDictionary(n => n.Faction, n => n);

        new ColonyArrivalSystem().Run(world);

        var girlCamps = world.FactionHomes.Keys.Where(FactionRelations.IsGirlCamp).OrderBy(f => (int)f).ToArray();
        Assert.That(girlCamps, Has.Length.EqualTo(6));
        foreach (var faction in girlCamps)
        {
            var id = CastawayId(faction, 1);
            Assert.That(world.Entities.Npcs.ContainsKey(id), Is.True, $"{faction}: потерпевшая {id.Value} не появилась");
            var her = world.Entities.Npcs[id];
            var home = world.FactionHomes[faction];
            Assert.Multiple(() =>
            {
                Assert.That(her.Faction, Is.EqualTo(faction), "она сразу член лагеря острова");
                Assert.That(her.WornItems, Is.Empty, "голая");
                Assert.That(her.Inventory.Items, Is.Empty, "с пустыми руками");
                Assert.That(her.Mind.ComaCause, Is.EqualTo(ComaCause.Exhaustion), "спит, где упала");
                Assert.That(her.Needs.Energy, Is.Zero);
                Assert.That(her.Attributes.Toughness, Is.EqualTo(IslandsCastawayMath.Toughness).Within(1e-5f));
                Assert.That(her.Body.IsCrawling, Is.True, "ходить не может");
                Assert.That(her.Wounds.Any(w => w.Zone == BodyPart.Torso && !w.Stabilized), Is.True);
                Assert.That(AidAssessment.NeedsDressing(her), Is.True, "рану надо бинтовать");
                var distance = HexSpatialMath.HexDistance(her.Tile, home);
                Assert.That(distance, Is.InRange(IslandsCastawayMath.ShoreMinDistanceTiles,
                    IslandsCastawayMath.ShoreMaxDistanceTiles));
                foreach (var other in girlCamps.Where(f => f != faction))
                {
                    Assert.That(HexSpatialMath.HexDistance(her.Tile, world.FactionHomes[other]),
                        Is.GreaterThanOrEqualTo(distance), "лежит на своём острове");
                }
                Assert.That(her.CurrentJunction, Is.Not.Null);
                // §60: кома переанкоривает тело в центр гекса — море проверяем по тайлу.
                Assert.That(world.Tiles.Items[her.Tile].Junctions.Any(j =>
                        world.Junctions.Items[j].Neighbors.Any(n => SpatialQueries.IsAllWaterJunction(world, n))),
                    Is.True, "гекс высадки касается моря");
                Assert.That(starters[faction].Memory.KnownAgents.TryGetValue(id, out var met) &&
                            met.AidKind == AidKind.Treat && met.Suffering >= Spec53.HeavyAidSuffering &&
                            met.Helpless, Is.True, "SOS записан в память своей девушки");
            });
        }

        Assert.That(world.Events.Items.Count(e => e.Type == "CastawayWashedAshore"), Is.EqualTo(6));
        Assert.That(world.Entities.Npcs.Values.Any(n => n.Faction == Faction.Castaway), Is.False);

        new ColonyArrivalSystem().Run(world);
        Assert.That(world.Entities.Npcs.Count, Is.EqualTo(12), "повторный проход не дублирует");
    }

    [Test]
    public void FullCampOfThreeSkipsTheWeek()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Mode = GameMode.Islands;
        Assert.That(world.Entities.Npcs.Values.Count(n => n.Faction == Faction.Colony), Is.EqualTo(3));

        world.Tick = CalendarBoundary(8);
        new ColonyArrivalSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Npcs.ContainsKey(CastawayId(Faction.Colony, 1)), Is.False);
            Assert.That(world.ColonyArrivalsProcessedByFaction[Faction.Colony], Is.EqualTo(1),
                "полный лагерь съедает неделю, бэклога нет");
        });
    }

    [Test]
    public void UntreatedTorsoWoundKillsAfterHalfADayButNotBefore()
    {
        var her = SpawnAlone(out var engine);
        var world = engine.World;

        StepPinned(engine, her, WorldBalance.DayLengthTicks / 2);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Npcs.ContainsKey(her.Id), Is.True, "полдня — гарантированы");
            Assert.That(her.Health, Is.GreaterThan(0f));
            Assert.That(her.IsDying, Is.False, $"через полдня ещё не умирает (Torso={her.Body.Parts[BodyPart.Torso]:F2})");
        });

        // Замер §157.10: грудь 0.60 → 0 ровно за ~13 000 тиков (0.00074 за
        // медленный тик при Toughness 0.9), дальше §105-циклы умирания, пока
        // CriticalTrauma не доползёт до единицы — смерть на ~70 500-м тике.
        StepPinned(engine, her, 4000);
        Assert.That(her.IsDying || world.Entities.Corpses.ContainsKey(her.Id), Is.True,
            $"к 16 000 грудь должна пробиться (Torso={her.Body.Parts[BodyPart.Torso]:F2})");
        Assert.That(world.Events.Items.Any(e => e.Type == "Collapsed" && e.EntityId == her.Id.Value) ||
                    her.IsDying, Is.True, "падение — событие истории");

        StepPinned(engine, her, 80000 - 16000);
        Assert.That(world.Entities.Npcs.ContainsKey(her.Id), Is.False,
            $"без бинта умирает за трое суток (Torso={her.Body.Parts[BodyPart.Torso]:F2} " +
            $"crit={her.Body.Condition(BodyPart.Torso).CriticalTrauma:F2})");
        Assert.That(world.Entities.Corpses.ContainsKey(her.Id), Is.True);
    }

    [Test]
    public void BandageStopsTheDegenerationAndSheLives()
    {
        var her = SpawnAlone(out var engine);
        var world = engine.World;

        StepPinned(engine, her, 6000);
        Assert.That(WoundMath.StabilizeMostDangerous(her, herbal: false, out var dressed), Is.True);
        Assert.That(dressed.Zone, Is.EqualTo(BodyPart.Torso), "самая опасная — грудь");
        var torsoAtDressing = her.Body.Parts[BodyPart.Torso];

        StepPinned(engine, her, WorldBalance.DayLengthTicks * 2);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Npcs.ContainsKey(her.Id), Is.True, "с бинтом живёт");
            Assert.That(her.Body.Parts[BodyPart.Torso], Is.GreaterThanOrEqualTo(torsoAtDressing - 1e-3f),
                "грудь больше не деградирует");
        });
    }

    [Test]
    public void RescuerDressesTheTorsoBeforePickup()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Mode = GameMode.Islands;
        world.Mobs.Clear();
        var girls = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).OrderBy(n => n.Id.Value).ToArray();
        var rescuer = girls[0];
        foreach (var extra in girls.Skip(1)) RemoveLiving(world, extra);
        for (var i = 0; i < 5; i++) rescuer.Inventory.Items.Add(MedicalSupplyMath.CreateBandage(herbal: false));
        EquipmentMath.Recalculate(world, rescuer);

        var id = CastawayId(Faction.Colony, 1);
        Assert.That(IslandsCastawayMath.TrySpawn(world, Faction.Colony, world.FactionHomes[Faction.Colony], 1, id), Is.True);
        var her = world.Entities.Npcs[id];
        Assert.That(rescuer.Memory.KnownAgents.ContainsKey(id), Is.True, "SOS услышан");

        var dressedAt = -1;
        for (var tick = 0; tick < 6000; tick++)
        {
            engine.Step();
            if (her.Wounds.Any(w => w.Zone == BodyPart.Torso && w.Stabilized))
            {
                dressedAt = world.Tick;
                break;
            }
        }

        Assert.That(dressedAt, Is.GreaterThan(0),
            $"за 6000 тиков грудь не забинтована: rescuer goal={rescuer.Mind.CurrentGoal} " +
            $"carried={her.IsBeingCarried} pending={her.Mind.PendingAidFrom} tile={her.Tile.Q},{her.Tile.R}");
    }

    private int _wolfSlots, _crabSlots;

    // Арена про канал груди, а не про экологию: у прототипного мира слоты
    // зверей заполняются заново, и на 4000-м тике стаю уже не отличить от раны.
    [SetUp]
    public void QuietWildlife()
    {
        _wolfSlots = WildlifeBalance.IslandsWolfSlots;
        _crabSlots = WildlifeBalance.IslandsCrabSlots;
        WildlifeBalance.IslandsWolfSlots = 0;
        WildlifeBalance.IslandsCrabSlots = 0;
    }

    [TearDown]
    public void RestoreWildlife()
    {
        WildlifeBalance.IslandsWolfSlots = _wolfSlots;
        WildlifeBalance.IslandsCrabSlots = _crabSlots;
    }

    private static NPCState SpawnAlone(out SimulationEngine engine)
    {
        engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Mode = GameMode.Islands;
        world.Mobs.Clear();
        foreach (var npc in world.Entities.Npcs.Values.ToArray()) RemoveLiving(world, npc);
        var id = CastawayId(Faction.Colony, 1);
        Assert.That(IslandsCastawayMath.TrySpawn(world, Faction.Colony, world.FactionHomes[Faction.Colony], 1, id), Is.True);
        return world.Entities.Npcs[id];
    }

    // Арена — про канал груди. Голод и жажда пиннятся сытыми (самый сильный
    // встречный реген), энергия — нулём (спит, где упала: ни ИИ, ни
    // самолечения — проснувшись, она сама крафтит бинт, и это уже «вылечили»).
    private static void StepPinned(SimulationEngine engine, NPCState her, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            if (engine.World.Entities.Npcs.ContainsKey(her.Id))
            {
                her.Needs.Hunger = System.Math.Min(her.Needs.Hunger, 0.45f);
                her.Needs.Thirst = System.Math.Min(her.Needs.Thirst, 0.35f);
                her.Needs.Energy = 0f;
            }
            engine.Step();
        }
    }

    private static void RemoveLiving(WorldState world, NPCState npc)
    {
        world.Entities.Npcs.Remove(npc.Id);
        if (world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var occupied)) occupied.Remove(npc.Id);
        if (world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var byTile)) byTile.Remove(npc.Id);
        if (world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var byFragment)) byFragment.Remove(npc.Id);
        if (npc.CurrentJunction is { } junction &&
            world.Occupancy.JunctionOwner.TryGetValue(junction, out var owner) && owner == npc.Id)
        {
            world.Occupancy.JunctionOwner[junction] = null;
        }
    }
}

}
