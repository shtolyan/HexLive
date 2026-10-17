using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
/// <summary>Bug #477: three authored palms must leave their hex traversable.</summary>
public sealed class PalmPassabilityTests
{
    private static WorldState BigIsland(int seed) => new WorldStateFactory().Create(
        PrototypeWorldDefinitionFactory.Create(seed, GameMode.BigIsland));

    [TestCase(12345)]
    [TestCase(777)]
    public void ThreePalmsDoNotSealAFreeCentreOrItsDroppedItem(int seed)
    {
        var world = BigIsland(seed);
        var tripleTiles = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Palm)
            .GroupBy(obj => obj.Tile)
            .Where(group => group.Count() == 3)
            .Select(group => group.Key)
            .ToList();
        Assert.That(tripleTiles, Is.Not.Empty, $"seed {seed} должен содержать три пальмы на гексе");

        var checkedTiles = 0;
        foreach (var tileCoord in tripleTiles)
        {
            var tile = world.Tiles.Items[tileCoord];
            // WorldStateFactory appends 37 interior slots before 24 shared rim slots.
            var centre = tile.Junctions[18];
            if (!SpatialQueries.IsJunctionPassable(world, centre))
                continue;

            var rim = tile.Junctions.Skip(37)
                .FirstOrDefault(id => SpatialQueries.IsJunctionPassable(world, id));
            if (rim.Value == 0)
                continue;

            checkedTiles++;
            foreach (var interior in tile.Junctions.Take(37))
            {
                if (!SpatialQueries.IsJunctionPassable(world, interior)) continue;
                Assert.That(tile.Junctions.Skip(37).Any(edge =>
                    SpatialQueries.IsJunctionPassable(world, edge) &&
                    Connectivity.Reachable(world, edge, interior)), Is.True,
                    $"seed {seed}, гекс {tileCoord}: свободный внутренний узел {interior} не имеет выхода");
            }
            Assert.That(Connectivity.Reachable(world, rim, centre), Is.True,
                $"seed {seed}, гекс {tileCoord}: центр свободен, но отрезан тремя стволами");

            var item = WorldObjectMutations.SpawnObject(
                world, ContentIds.Coconut, world.Junctions.Items[centre].Fragment, tileCoord, centre);
            Assert.That(Connectivity.ReachableBeside(world, rim, item.Junctions[0]), Is.True,
                $"seed {seed}, гекс {tileCoord}: упавший в центре предмет недоступен для подбора");
            WorldObjectMutations.DespawnObject(world, item.Id);
        }

        Assert.That(checkedTiles, Is.GreaterThan(0), "Не проверен ни один гекс с тремя пальмами");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ScatterSkipsAnIsolatedLegacyPalmPocket(bool unsetCurrentJunction)
    {
        var world = BigIsland(12345);
        var palms = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Palm)
            .GroupBy(obj => obj.Tile)
            .First(group => group.Count() == 3);
        var tileCoord = palms.Key;
        var tile = world.Tiles.Items[tileCoord];

        // Recreate the authored three-palm layout of pre-fix saves. Such saves
        // retain the old anchors even after the generator is corrected.
        var third = palms.Single(obj => obj.Junctions[0] == tile.Junctions[9]);
        WorldObjectMutations.DespawnObject(world, third.Id);
        WorldObjectMutations.SpawnObject(world, ContentIds.Palm, third.Fragment, tileCoord,
            tile.Junctions[10]);

        var centre = tile.Junctions[18];
        var rim = tile.Junctions.Skip(37)
            .First(id => SpatialQueries.IsJunctionPassable(world, id));
        Assert.That(SpatialQueries.IsJunctionPassable(world, centre), Is.True);
        Assert.That(Connectivity.Reachable(world, rim, centre), Is.False,
            "Фикстура должна воспроизвести старый замкнутый карман");

        var actor = world.Entities.Npcs.Values.First();
        actor.Tile = tileCoord;
        actor.Fragment = third.Fragment;
        actor.Position = world.Junctions.Items[rim].WorldPosition;
        actor.CurrentJunction = unsetCurrentJunction ? null : rim;
        var found = GroundItemPlacement.TryFindNear(world, actor,
            new ItemInstance(ContentIds.PalmLeaf), tileCoord,
            world.Junctions.Items[centre].WorldPosition,
            out _, out var droppedAt, out _, scatter: true);

        Assert.That(found, Is.True, "Скаттер должен найти доступное место снаружи пальм");
        Assert.That(Connectivity.Reachable(world, rim, droppedAt, actor.Body.CanJump), Is.True,
            "Предмет из рук стоящей снаружи героини не должен падать в изолированный центр");
    }

    [Test]
    public void ProducerRetriesInsteadOfDroppingIntoASealedPocket()
    {
        var world = BigIsland(12345);
        var triple = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Palm)
            .GroupBy(obj => obj.Tile)
            .First(group => group.Count() == 3);
        var tile = world.Tiles.Items[triple.Key];
        var oldThird = triple.Single(obj => obj.Junctions[0] == tile.Junctions[9]);
        WorldObjectMutations.DespawnObject(world, oldThird.Id);
        WorldObjectMutations.SpawnObject(world, ContentIds.Palm, oldThird.Fragment,
            triple.Key, tile.Junctions[10]);
        var centre = tile.Junctions[18];
        Assert.That(SpatialQueries.IsJunctionPassable(world, centre), Is.True);
        Assert.That(InteractionReach.CountApproaches(world, centre), Is.Zero);

        // A neighboring producer can see the pocket within its one-tile drop
        // radius even though its own trunk clearance does not cover it.
        var centrePosition = world.Junctions.Items[centre].WorldPosition;
        var source = SpatialQueries.GetNeighbors(world, triple.Key)
            .Where(coord => world.Tiles.Items.TryGetValue(coord, out var t) &&
                            SpatialQueries.IsTileWalkable(world, coord))
            .SelectMany(coord => world.Tiles.Items[coord].Junctions.Select(id => (coord, id)))
            .First(pair => SpatialQueries.IsJunctionPassable(world, pair.id) &&
                           SpatialQueries.IsJunctionFree(world, pair.id) &&
                           DistanceSquared(world.Junctions.Items[pair.id].WorldPosition, centrePosition) > 2.25f);
        var producer = WorldObjectMutations.SpawnObject(world, ContentIds.Palm,
            world.Junctions.Items[source.id].Fragment, source.coord, source.id);
        foreach (var palm in world.Entities.Objects.Values.Where(obj => obj.DefinitionId == ContentIds.Palm))
            palm.NextProductionTick = int.MaxValue;
        producer.NextProductionTick = 0;

        // Leave exactly one free candidate, the inaccessible pocket centre.
        var candidateTiles = new[] { producer.Tile }
            .Concat(SpatialQueries.GetNeighbors(world, producer.Tile));
        foreach (var candidateTile in candidateTiles)
        {
            if (!world.Tiles.Items.TryGetValue(candidateTile, out var candidate)) continue;
            foreach (var id in candidate.Junctions)
            {
                if (id != centre && SpatialQueries.IsJunctionPassable(world, id))
                    world.Occupancy.JunctionOwner[id] = new EntityId(999999);
            }
        }
        Assert.That(SpatialQueries.IsJunctionFree(world, centre), Is.True);
        new FruitProductionSystem().Run(world);
        Assert.That(producer.ProducedItems, Is.Empty,
            "Пальма должна дождаться следующего цикла вместо падения кокоса в карман");
        Assert.That(producer.NextProductionTick, Is.GreaterThan(world.Tick));
    }

    private static float DistanceSquared(Float2 a, Float2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
}
