using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§125.2: сенсор — человек попадает в восприятие только из кольца
/// радиуса наблюдательницы. Раньше списки были островными (§72.2).</summary>
[NonParallelizable]
public sealed class PerceptionSensorTests
{
    [Test]
    public void NeighbourInsideRadiusIsSeen_OutsideIsNot()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = TwoColonists(world);

        observer.Attributes.Perception = 0.3f; // радиус 3
        var origin = observer.Tile;

        Place(world, neighbour, new TileCoord(origin.Q + 3, origin.R));
        perception.Run(world);
        Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.True,
            "соседка ровно на радиусе обязана быть видна");

        Place(world, neighbour, new TileCoord(origin.Q + 4, origin.R));
        perception.Run(world);
        Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.False,
            "на шаг дальше радиуса — уже не видна");
    }

    [Test]
    public void KeenerEyeSeesFurther()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = TwoColonists(world);

        var origin = observer.Tile;
        Place(world, neighbour, new TileCoord(origin.Q + 7, origin.R));

        observer.Attributes.Perception = 0.5f; // радиус 5 — не дотягивает
        perception.Run(world);
        Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.False);

        observer.Attributes.Perception = 0.8f; // радиус 8 — уже видит
        perception.Run(world);
        Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.True);
    }

    [Test]
    public void TheLeastObservantStillSeesWhoStandsNextToHer()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = TwoColonists(world);

        // §125.1: ролл §76.2 обязан положить чью-то ось на дно, и у 10.8%
        // колонисток это Восприятие. Пол в один гекс — про присутствие, а не
        // про зоркость: иначе она выпала бы из жизни колонии целиком.
        observer.Attributes.Perception = 0f;

        Place(world, neighbour, new TileCoord(observer.Tile.Q + 1, observer.Tile.R));
        perception.Run(world);
        Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.True,
            "вплотную видит любая");

        // А на два гекса — уже нет: обуза остаётся обузой.
        Place(world, neighbour, new TileCoord(observer.Tile.Q + 2, observer.Tile.R));
        perception.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(observer.Perception.Agents, Is.Empty);
            Assert.That(observer.Perception.Environment.IsPrivate, Is.True,
                "никого не видит — значит, одна");
        });
    }

    [Test]
    public void CompanyIsCountedFromTheVisibleOnly()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var colonists = Colonists(world);
        var observer = colonists[0];
        observer.Attributes.Perception = 0.2f; // радиус 2

        // Все остальные — на дальнем краю карты, вне кольца.
        foreach (var other in colonists.Skip(1))
        {
            Place(world, other, new TileCoord(observer.Tile.Q + 9, observer.Tile.R));
        }

        perception.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(observer.Perception.Environment.NearbyAgentsCount, Is.Zero);
            Assert.That(observer.Perception.Environment.IsPrivate, Is.True);
            Assert.That(observer.Perception.Environment.IsCrowded, Is.False);
        });
    }

    private static NPCState[] Colonists(WorldState world) =>
        world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .OrderBy(npc => npc.Id.Value)
            .ToArray();

    private static (NPCState Observer, NPCState Neighbour) TwoColonists(WorldState world)
    {
        var colonists = Colonists(world);
        Assert.That(colonists.Length, Is.GreaterThanOrEqualTo(2), "нужны хотя бы двое");

        // Остальных уводим с глаз, чтобы тест мерил ровно одну пару.
        foreach (var other in colonists.Skip(2))
        {
            Place(world, other, new TileCoord(colonists[0].Tile.Q + 11, colonists[0].Tile.R));
        }

        return (colonists[0], colonists[1]);
    }

    private static void Place(WorldState world, NPCState npc, TileCoord tile)
    {
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        npc.Tile = tile;
        npc.Position = HexSpatialMath.TileToWorld(tile);
    }
}

}
