using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class ShipwreckSurvivorTests
{
    private static WorldState Build(int seed = 12345) =>
        new WorldStateFactory().Create(
            PrototypeWorldDefinitionFactory.Create(seed, GameMode.HugeIsland));

    [Test]
    public void EventSpawnsOneNakedWoundedCastawayNearThePlayerShore()
    {
        var world = Build();
        world.Tick = WorldBalance.DayLengthTicks * 6;

        new ShipwreckSurvivorSystem().Run(world);

        var castaway = world.Entities.Npcs[new EntityId(ShipwreckSurvivorSystem.SurvivorId)];
        Assert.That(castaway.Faction, Is.EqualTo(Faction.Castaway));
        Assert.That(castaway.WornItems, Is.Empty);
        Assert.That(castaway.Inventory.Items, Is.Empty);
        Assert.That(castaway.Wounds, Is.Not.Empty);
        Assert.That(castaway.Needs.Hunger, Is.GreaterThan(
            ShipwreckSurvivorSystem.JoinHungerMax));
        Assert.That(castaway.Needs.Thirst, Is.GreaterThan(
            ShipwreckSurvivorSystem.JoinThirstMax));
        Assert.That(HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
                castaway.Tile, world.FactionHomes[Faction.Colony]),
            Is.LessThanOrEqualTo(18));
        Assert.That(castaway.CurrentJunction, Is.Not.Null);
        var landing = world.Junctions.Items[castaway.CurrentJunction!.Value];
        Assert.That(landing.Neighbors.Any(neighbor =>
            SpatialQueries.IsAllWaterJunction(world, neighbor)), Is.True,
            "потерпевшая должна лежать на сухом узле у самого моря");
        Assert.That(FactionRelations.AreAllies(Faction.Colony, Faction.Castaway), Is.True);
        Assert.That(FactionRelations.AreAllies(Faction.Colony2, Faction.Castaway), Is.False);

        new ShipwreckSurvivorSystem().Run(world);
        Assert.That(world.Entities.Npcs.Keys.Count(id =>
            id.Value == ShipwreckSurvivorSystem.SurvivorId), Is.EqualTo(1));
    }

    [Test]
    public void WorldRecipeSwitchKeepsTheCastawayOutAndDefaultKeepsHer()
    {
        // §146.10: the scripted castaway is a per-world creation choice, never a static.
        var off = Build();
        off.CreationConfig = new WorldCreationConfig { ShipwreckSurvivorEnabled = false };
        off.Tick = WorldBalance.DayLengthTicks * 6;
        new ShipwreckSurvivorSystem().Run(off);
        Assert.That(off.Entities.Npcs.ContainsKey(new EntityId(ShipwreckSurvivorSystem.SurvivorId)), Is.False);

        var on = Build();
        on.CreationConfig = new WorldCreationConfig();
        on.Tick = WorldBalance.DayLengthTicks * 6;
        new ShipwreckSurvivorSystem().Run(on);
        Assert.That(on.Entities.Npcs.ContainsKey(new EntityId(ShipwreckSurvivorSystem.SurvivorId)), Is.True,
            "a recipe that does not mention the castaway keeps the shipped story");
    }

    [Test]
    public void TreatedFedAndWateredCastawayJoinsThePlayerCamp()
    {
        var world = Build();
        world.Tick = WorldBalance.DayLengthTicks * 6;
        var system = new ShipwreckSurvivorSystem();
        system.Run(world);
        var castaway = world.Entities.Npcs[new EntityId(ShipwreckSurvivorSystem.SurvivorId)];

        castaway.Health = 1f;
        castaway.Needs.Blood = 1f;
        castaway.Needs.Hunger = 0.20f;
        castaway.Needs.Thirst = 0.20f;
        castaway.Wounds.Clear();
        castaway.Mind.FaintedUntilTick = 0;
        system.Run(world);

        Assert.That(castaway.Faction, Is.EqualTo(Faction.Colony));
        Assert.That(world.Events.Items.Any(evt =>
            evt.Type == "ShipwreckSurvivorJoined" && evt.EntityId == castaway.Id.Value), Is.True);
    }
}

}
