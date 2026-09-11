using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>Bug #411: natural produce cleanup must not delete a gathered whole stock.</summary>
public sealed class CoconutStockLifetimeTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void GatheredWholeCoconutSurvivesNaturalProduceCleanup(bool reload)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var colonist in world.Entities.Npcs.Values)
            engine.ApplyManualCommand(new SetManualControlCommand(colonist.Id, true));
        npc.Inventory.Items.Clear();
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        engine.Step();

        // Use the established ManualControlTests pickup fixture: fruit on a
        // passable adjacent node, then the real timed pickup and drop command.
        world.Tick = 100;
        var neighbor = SpatialQueries.GetPassableNeighbors(world, npc.CurrentJunction!.Value).First();
        var initial = WorldObjectMutations.SpawnObject(world, ContentIds.Coconut,
            npc.Fragment, world.Junctions.Items[neighbor].Tiles[0], neighbor);
        var pickup = engine.ApplyManualCommand(
            new InteractCommand(npc.Id, initial.Id, InteractionType.PickUp));
        Assert.That(pickup.Accepted, Is.True, pickup.Reason);
        for (var i = 0; i < 200 && !npc.Inventory.Items.Contains(ContentIds.Coconut); i++)
            engine.Step();
        Assert.That(npc.Inventory.Items.Contains(ContentIds.Coconut), Is.True,
            "The real pickup must complete before checking stock lifetime.");
        Assert.That(world.Entities.Objects.ContainsKey(initial.Id), Is.False);
        var stock = Drop(engine, npc);
        Assert.That(stock.ProduceOrigin, Is.EqualTo(ProduceOrigin.Gathered));
        var id = stock.Id;
        var droppedAt = stock.SpawnTick;
        Assert.That(droppedAt, Is.EqualTo(world.Tick));
        Assert.That(id, Is.Not.EqualTo(initial.Id));

        if (reload) world = RoundTrip(world);
        Assert.That(world.Entities.Objects[id].SpawnTick, Is.EqualTo(droppedAt));
        Assert.That(world.Entities.Objects[id].ProduceOrigin, Is.EqualTo(ProduceOrigin.Gathered));
        var rot = new FruitProductionSystem();
        // No decision/execution system runs during these two clock probes:
        // any lost stock is the cleanup, not another NPC eating or taking it.
        world.Tick = droppedAt + WorldBalance.FruitRotTicks;
        rot.Run(world);
        Assert.That(world.Entities.Objects.ContainsKey(id), Is.True);
        world.Tick++;
        world.Events.Clear();
        rot.Run(world);
        TestContext.WriteLine($"Stock={id.Value} drop={droppedAt} now={world.Tick} " +
            $"exists={world.Entities.Objects.ContainsKey(id)} " +
            $"rotEvents={string.Join(";", world.Events.Items.Where(e => e.Type == "ProduceRotted").Select(e => e.Message))}");
        Assert.That(world.Entities.Objects.ContainsKey(id), Is.True,
            "Natural litter cleanup must not erase the gathered whole coconut stock.");
    }

    [TestCase(ContentIds.CoconutPierced)]
    [TestCase(ContentIds.CoconutOpen)]
    public void OpenedFoodStillUsesItsExistingGroundLifetime(string definitionId)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        world.Tick = 100;
        npc.Inventory.Items.Add(new ItemInstance(definitionId));
        var item = Drop(engine, npc);
        world.Tick = item.SpawnTick + WorldBalance.FruitRotTicks + 1;
        new FruitProductionSystem().Run(world);
        Assert.That(world.Entities.Objects.ContainsKey(item.Id), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnpickedNaturalFruitStillRotsAfterItsProducerIsGone(bool reload)
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 100;
        var rot = new FruitProductionSystem();
        rot.Run(world);
        var producer = world.Entities.Objects.Values.First(o => o.ProducedItems.Count > 0);
        var fruit = world.Entities.Objects[producer.ProducedItems[0]];
        Assert.That(fruit.DefinitionId, Is.EqualTo(ContentIds.Coconut));
        Assert.That(fruit.SpawnTick, Is.EqualTo(100));
        Assert.That(fruit.ProduceOrigin, Is.EqualTo(ProduceOrigin.Natural));
        WorldObjectMutations.DespawnObject(world, producer.Id);
        if (reload) world = RoundTrip(world);
        world.Tick = fruit.SpawnTick + WorldBalance.FruitRotTicks + 1;
        rot.Run(world);
        Assert.That(world.Entities.Objects.ContainsKey(fruit.Id), Is.False,
            "Removing a producer must not make its untouched natural fruit immortal.");
    }

    [Test]
    public void HarvestedBranchYieldsAreGatheredWithoutNeedingAPickupFirst()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        world.Tick = 100;
        var source = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Palm);
        var harvest = world.Content.ObjectDefinitions["palm.coconut_branch"].Interactions
            .Single(i => i.Type == InteractionType.Harvest);
        var before = world.Entities.Objects.Keys.ToHashSet();
        ExecutionSystem.ApplyHarvestYields(world, npc, source, harvest.Yields);
        var stock = world.Entities.Objects.Values
            .Where(o => !before.Contains(o.Id) && o.DefinitionId == ContentIds.Coconut).ToArray();
        Assert.That(stock, Has.Length.EqualTo(3), "Exercise the real scattered branch yield path.");
        Assert.That(stock.All(o => o.ProduceOrigin == ProduceOrigin.Gathered), Is.True);
        world.Tick += WorldBalance.FruitRotTicks + 1;
        new FruitProductionSystem().Run(world);
        Assert.That(stock.All(o => world.Entities.Objects.ContainsKey(o.Id)), Is.True);
    }

    [Test]
    public void LegacySaveInfersOnlyProvenNaturalProduce([Range(66, 73)] int version)
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        world.Tick = 100;
        new FruitProductionSystem().Run(world);
        var producer = world.Entities.Objects.Values.First(o => o.ProducedItems.Count > 0);
        var naturalId = producer.ProducedItems[0];
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Coconut));
        var stock = Drop(engine, npc);
        Assert.That(stock.ProduceOrigin, Is.EqualTo(ProduceOrigin.Gathered));
        var loaded = RoundTrip(world, version);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Entities.Objects[naturalId].ProduceOrigin,
                Is.EqualTo(ProduceOrigin.Natural));
            Assert.That(loaded.Entities.Objects[stock.Id].ProduceOrigin,
                Is.EqualTo(ProduceOrigin.Unknown), "Old data cannot prove that this fruit was carried.");
            Assert.That(loaded.Entities.Objects[stock.Id].SpawnTick, Is.EqualTo(stock.SpawnTick));
        });
        // A subsequent current-version save must preserve the uncertainty too.
        loaded = RoundTrip(loaded);
        Assert.That(loaded.Entities.Objects[stock.Id].ProduceOrigin, Is.EqualTo(ProduceOrigin.Unknown));
        loaded.Tick = stock.SpawnTick + WorldBalance.FruitRotTicks + 1;
        new FruitProductionSystem().Run(loaded);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Entities.Objects.ContainsKey(naturalId), Is.False);
            Assert.That(loaded.Entities.Objects.ContainsKey(stock.Id), Is.True,
                "Unknown legacy history must not silently destroy a possible gathered stock.");
        });
    }

    [Test]
    public void SleepingChunkDefersNaturalCleanupButDoesNotDeleteGatheredStockOnWake()
    {
        var previous = ChunkBalance.ChunkSleepEnabled;
        try
        {
            ChunkBalance.ChunkSleepEnabled = true;
            var engine = TestWorld.CreateEngine();
            var world = engine.World;
            world.Tick = 100;
            var rot = new FruitProductionSystem();
            rot.Run(world);
            var producer = world.Entities.Objects.Values.First(o => o.ProducedItems.Count > 0);
            var natural = world.Entities.Objects[producer.ProducedItems[0]];
            var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
            npc.Inventory.Items.Clear();
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Coconut));
            var stock = Drop(engine, npc);
            world.Caches.ActiveChunksComputed = true;
            world.Caches.ActiveChunks.Clear();
            world.Caches.ActiveChunksOrdered.Clear();
            ChunkMath.EnsureObjectIndex(world);
            world.Tick += WorldBalance.FruitRotTicks + 1;
            rot.Run(world);
            Assert.That(world.Entities.Objects.ContainsKey(natural.Id), Is.True);
            Assert.That(world.Entities.Objects.ContainsKey(stock.Id), Is.True);
            foreach (var chunk in new[] { ChunkMath.ChunkOf(natural.Tile), ChunkMath.ChunkOf(stock.Tile) }.Distinct())
            {
                world.Caches.ActiveChunks.Add(chunk);
                world.Caches.ActiveChunksOrdered.Add(chunk);
            }
            rot.Run(world);
            Assert.That(world.Entities.Objects.ContainsKey(natural.Id), Is.False);
            Assert.That(world.Entities.Objects.ContainsKey(stock.Id), Is.True);
        }
        finally { ChunkBalance.ChunkSleepEnabled = previous; }
    }

    private static WorldObjectState Drop(SimulationEngine engine, NPCState npc)
    {
        var world = engine.World;
        var before = world.Entities.Objects.Keys.ToHashSet();
        var itemId = npc.Inventory.Items[0].DefinitionId;
        var dropped = engine.ApplyManualCommand(new ManageInventoryCommand(npc.Id,
            new InventoryItemRef(InventoryItemSource.Carried, 0, itemId), InventoryAction.Drop));
        Assert.That(dropped.Accepted, Is.True, dropped.Reason);
        return world.Entities.Objects.Values.Single(o => !before.Contains(o.Id) && o.DefinitionId == itemId);
    }

    private static WorldState RoundTrip(WorldState source, int version = WorldSaveSerializer.BlobVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }
}
}
