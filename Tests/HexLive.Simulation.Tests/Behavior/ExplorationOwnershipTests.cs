using System.Linq;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// Bug #337 (частичная §148.1): на сервере карту разведывают только девушки,
/// выданные игрокам (<c>WorldState.PlayerControlledNpcs</c>). Локально набор
/// пуст и действует прежнее правило «любой девичий лагерь» (§149).
/// </summary>
public sealed class ExplorationOwnershipTests
{
    [Test]
    public void OnlyPlayerControlledNpcsExploreWhenTheServerSetIsFilled()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var controlled = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();

        world.PlayerControlledNpcs.Add(controlled.Id.Value);
        world.ExploredTiles.Clear();
        engine.Step();

        Assert.That(world.ExploredTiles, Is.Not.Empty,
            "Выданная девушка обязана разведывать.");
        var maxRadius = 12; // с запасом больше любого личного радиуса §125
        foreach (var coord in world.ExploredTiles)
        {
            Assert.That(
                HexSpatialMath.HexDistance(coord, controlled.Tile),
                Is.LessThanOrEqualTo(maxRadius),
                "Разведанное дальше радиуса выданной девушки — писал кто-то ещё.");
        }
    }

    [Test]
    public void EveryGirlCampExploresInLocalPlayWhereTheSetIsEmpty()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        Assert.That(world.PlayerControlledNpcs, Is.Empty,
            "Фикстура локальной игры не должна иметь серверных назначений.");

        world.ExploredTiles.Clear();
        engine.Step();

        // Все живые колонистки пишут — покрытие обязано быть шире личного
        // диска одной девушки, когда они стоят на разных тайлах.
        var npcs = world.Entities.Npcs.Values
            .Where(n => n.Health > 0f &&
                HexLive.Simulation.Runtime.FactionRelations.IsGirlCamp(n.Faction))
            .ToList();
        Assert.That(world.ExploredTiles, Is.Not.Empty);
        foreach (var npc in npcs)
        {
            Assert.That(world.ExploredTiles.Contains(npc.Tile), Is.True,
                $"Тайл живой {npc.Id.Value} должен быть разведан ею самой.");
        }
    }
}
