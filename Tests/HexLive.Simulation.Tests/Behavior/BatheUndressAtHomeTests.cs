using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §133: раздеваться она идёт домой. Куча одежды больше не остаётся на берегу
/// на другом конце острова — вещи висят в гардеробе (или на сушилке), а сама
/// она возвращается туда же одеваться.
/// </summary>
public sealed class BatheUndressAtHomeTests
{
    private static WorldObjectState Wardrobe(WorldState world) =>
        world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Wardrobe);

    /// <summary>
    /// Мир после нескольких тиков: до первого шага у колонисток нет джанкшена,
    /// а без него планировать нечего (и StowMath честно отвечает «не знаю»).
    /// </summary>
    private static WorldState SettledWorld()
    {
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++)
        {
            engine.Step();
        }

        return engine.World;
    }

    /// <summary>⭐ Главное: гардероб в доме выигрывает у всего остального.</summary>
    [Test]
    public void UndressSpotPrefersTheWardrobeInTheHouse()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

        var spot = StowMath.FindUndressSpot(world, npc);

        Assert.That(spot, Is.Not.Null, "Место для раздевания не найдено, хотя дом есть.");
        Assert.That(spot.Value.StowObject, Is.EqualTo(Wardrobe(world).Id),
            "Раздеваться собираются не у гардероба — приоритет дома не работает.");
    }

    /// <summary>Гардероб полон — раздеваемся всё равно у дома, просто на землю.</summary>
    [Test]
    public void AFullWardrobeFallsBackToTheHomeGroundNotTheShore()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        for (var i = 0; i < Spec133.WardrobeCapacity; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "underwear.bra_riot", wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        }

        var rack = world.Entities.Objects.Values
            .FirstOrDefault(o => o.DefinitionId == ContentIds.DryingRack);
        if (rack != null)
        {
            for (var i = 0; i < SimBalance.RackCapacity; i++)
            {
                WorldObjectMutations.SpawnObject(
                    world, "underwear.bra_riot", rack.Fragment, rack.Tile, rack.Junctions[0]);
            }
        }

        var spot = StowMath.FindUndressSpot(world, npc);

        Assert.That(spot, Is.Not.Null, "С полным гардеробом раздеваться расхотелось совсем.");
        Assert.That(spot.Value.StowObject, Is.Null,
            "В полный гардероб всё равно вешают.");
        Assert.That(ColonyQueries.Home(world, npc.Faction), Is.Not.Null);
        Assert.That(
            HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
                world.Junctions.Items[spot.Value.Stand].Tiles[0],
                ColonyQueries.Home(world, npc.Faction).Value),
            Is.LessThanOrEqualTo(Spec133.HomeStowRadiusTiles),
            "Запасная точка раздевания оказалась не у дома.");
    }

    /// <summary>
    /// Снятая у гардероба вещь висит НА НЁМ и остаётся её собственной.
    /// </summary>
    [Test]
    public void DoffedGarmentHangsOnTheWardrobeAndKeepsItsOwner()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        var garment = new ItemInstance("underwear.bra_riot") { OwnerId = npc.Id.Value };

        var stowed = ExecutionSystem.StowGarmentWithContents(world, npc, garment, wardrobe.Id);

        Assert.That(stowed, Is.Not.Null);
        Assert.That(stowed.Junctions[0], Is.EqualTo(wardrobe.Junctions[0]),
            "Вещь легла не на гардероб.");
        Assert.That(stowed.Owner, Is.EqualTo(npc.Id), "Повешенная вещь потеряла хозяйку.");
        Assert.That(stowed.RotationDegrees, Is.EqualTo(wardrobe.RotationDegrees).Within(0.001f),
            "Вещь висит мимо поворота станции (§66).");
    }

    /// <summary>Полная станция не съедает вещь: она честно падает под ноги.</summary>
    [Test]
    public void AFullStationDropsTheGarmentAtHerFeetInstead()
    {
        var world = SettledWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var wardrobe = Wardrobe(world);
        for (var i = 0; i < Spec133.WardrobeCapacity; i++)
        {
            WorldObjectMutations.SpawnObject(
                world, "underwear.bra_riot", wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        }

        var garment = new ItemInstance("underwear.thong_anarchy") { OwnerId = npc.Id.Value };
        var stowed = ExecutionSystem.StowGarmentWithContents(world, npc, garment, wardrobe.Id);

        Assert.That(stowed, Is.Not.Null, "Вещь исчезла при переполненном гардеробе.");
        Assert.That(stowed.Junctions[0], Is.Not.EqualTo(wardrobe.Junctions[0]),
            "Вещь всё-таки повесили в полный гардероб.");
        Assert.That(stowed.Owner, Is.EqualTo(npc.Id));
    }
}

}
