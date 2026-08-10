using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§125.3: память последней встречи — что она помнит о тех, кто уже
/// вышел из поля зрения, и когда перестаёт помнить.</summary>
[NonParallelizable]
public sealed class AgentMemoryTests
{
    [Test]
    public void SeenNeighbourIsRemembered_WithTileAndTick()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = Pair(world);
        observer.Attributes.Perception = 0.4f; // радиус 4

        var meeting = new TileCoord(observer.Tile.Q + 2, observer.Tile.R);
        Place(world, neighbour, meeting);
        world.Tick = 100;
        perception.Run(world);

        Assert.That(observer.Memory.KnownAgents.TryGetValue(neighbour.Id, out var met), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(met.Tile, Is.EqualTo(meeting));
            Assert.That(met.LastSeenTick, Is.EqualTo(100));
            Assert.That(met.Faction, Is.EqualTo(neighbour.Faction));
        });
    }

    [Test]
    public void WalkingOutOfSightKeepsTheOldSighting()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = Pair(world);
        observer.Attributes.Perception = 0.4f;

        var meeting = new TileCoord(observer.Tile.Q + 2, observer.Tile.R);
        Place(world, neighbour, meeting);
        world.Tick = 100;
        perception.Run(world);

        // Ушла далеко — из живых списков пропала, но память о встрече цела.
        Place(world, neighbour, new TileCoord(observer.Tile.Q + 10, observer.Tile.R));
        world.Tick = 200;
        perception.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(observer.Perception.Agents.Any(a => a.Id.Equals(neighbour.Id)), Is.False);
            Assert.That(observer.Memory.KnownAgents[neighbour.Id].Tile, Is.EqualTo(meeting));
            Assert.That(observer.Memory.KnownAgents[neighbour.Id].LastSeenTick, Is.EqualTo(100));
        });
    }

    [Test]
    public void ReSightingRefreshesTheRecord()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = Pair(world);
        observer.Attributes.Perception = 0.6f; // радиус 6

        Place(world, neighbour, new TileCoord(observer.Tile.Q + 2, observer.Tile.R));
        world.Tick = 100;
        perception.Run(world);

        // Отошла, но осталась в радиусе — запись обязана переехать за ней.
        var later = new TileCoord(observer.Tile.Q + 4, observer.Tile.R);
        Place(world, neighbour, later);
        world.Tick = 200;
        perception.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(observer.Memory.KnownAgents[neighbour.Id].Tile, Is.EqualTo(later));
            Assert.That(observer.Memory.KnownAgents[neighbour.Id].LastSeenTick, Is.EqualTo(200));
        });
    }

    [Test]
    public void MemoryExpiresByTtl()
    {
        var world = TestWorld.CreateWorld();
        var perception = new PerceptionSystem();
        var (observer, neighbour) = Pair(world);
        observer.Attributes.Perception = 0.3f;

        Place(world, neighbour, new TileCoord(observer.Tile.Q + 2, observer.Tile.R));
        world.Tick = 100;
        perception.Run(world);
        Assert.That(observer.Memory.KnownAgents.ContainsKey(neighbour.Id), Is.True);

        Place(world, neighbour, new TileCoord(observer.Tile.Q + 12, observer.Tile.R));
        world.Tick = 100 + AiBalance.MemoryTtlTicks + 1;
        perception.Run(world);

        Assert.That(observer.Memory.KnownAgents.ContainsKey(neighbour.Id), Is.False,
            "просроченная встреча забывается тем же сроком, что и объекты");
    }

    private static (NPCState Observer, NPCState Neighbour) Pair(WorldState world)
    {
        var colonists = world.Entities.Npcs.Values
            .Where(npc => npc.Faction == Faction.Colony)
            .OrderBy(npc => npc.Id.Value)
            .ToArray();
        Assert.That(colonists.Length, Is.GreaterThanOrEqualTo(2));

        foreach (var other in colonists.Skip(2))
        {
            Place(world, other, new TileCoord(colonists[0].Tile.Q + 13, colonists[0].Tile.R));
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
