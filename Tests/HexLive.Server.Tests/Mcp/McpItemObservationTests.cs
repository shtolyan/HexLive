using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpItemObservationTests
{
    [Test]
    public void VisibleOwnershipUsesTheOwnerAndCampNeverTheCurrentUser()
    {
        using var host = CreateHost();
        var ids = host.Read(world =>
        {
            var actor = Observer(world);
            var fellow = world.Entities.Npcs.Values.First(n => n.Id != actor.Id && n.Faction == actor.Faction);
            var foreign = world.Entities.Npcs.Values.First(n => n.Faction != actor.Faction);
            actor.Perception.Objects.Clear();
            var owned = Add(world, actor, 900001, ContentIds.Bottle, fellow.Id);
            owned.CurrentUser = actor.Id;
            Add(world, actor, 900002, ContentIds.Bottle, foreign.Id);
            Add(world, actor, 900003, ContentIds.Bottle);
            var camp = Add(world, actor, 900004, ContentIds.WaterCollector);
            camp.OwnerFaction = foreign.Faction;
            return (actor.Id.Value, fellow.Id.Value, foreign.Id.Value, foreign.Faction);
        });
        using var response = Describe(host, ids.Item1);
        var rows = response.RootElement.GetProperty("visibleItems");
        Assert.Multiple(() =>
        {
            Assert.That(rows.GetArrayLength(), Is.EqualTo(4));
            Assert.That(rows[0].GetProperty("objectId").GetInt32(), Is.EqualTo(900001));
            Assert.That(rows[0].GetProperty("ownerNpcId").GetInt32(), Is.EqualTo(ids.Item2));
            Assert.That(rows[0].GetProperty("sameCamp").GetBoolean(), Is.True);
            Assert.That(rows[1].GetProperty("ownerNpcId").GetInt32(), Is.EqualTo(ids.Item3));
            Assert.That(rows[1].GetProperty("sameCamp").GetBoolean(), Is.False);
            Assert.That(rows[2].GetProperty("ownerNpcId").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(rows[2].GetProperty("sameCamp").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(rows[3].GetProperty("ownerFaction").GetString(), Is.EqualTo(ids.Item4.ToString()));
        });
    }

    [Test]
    public void EveryVisibleVesselSurvivesAggregationButMemoryAndForeignPocketsStayHidden()
    {
        using var host = CreateHost();
        var id = host.Read(world =>
        {
            var actor = Observer(world);
            actor.Perception.Objects.Clear();
            for (var i = 0; i < 20; i++)
            {
                var bottle = Add(world, actor, 901000 + i, ContentIds.Bottle);
                bottle.WaterKind = i == 0 ? WaterKind.None : WaterKind.Raw;
                bottle.ResourceAmount = i % SimBalance.BottleCapacity;
            }
            var remembered = Add(world, actor, 901050, ContentIds.Bottle);
            actor.Perception.Objects.Last().FromMemory = true;
            remembered.ResourceAmount = 9;
            remembered.WaterKind = WaterKind.Boiled;
            Add(world, actor, 901051, ContentIds.Bottle);
            actor.Perception.Objects.RemoveAt(actor.Perception.Objects.Count - 1);
            actor.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(999999), DefinitionId = ContentIds.Bottle });
            var foreign = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
            foreign.Inventory.Items.Add(new ItemInstance("private-pocket-sentinel"));
            return actor.Id.Value;
        });
        using var response = Describe(host, id);
        var rows = response.RootElement.GetProperty("visibleItems");
        Assert.Multiple(() =>
        {
            Assert.That(rows.GetArrayLength(), Is.EqualTo(20), "Each visible instance remains addressable despite the summary's nearest-per-definition budget");
            Assert.That(rows[0].GetProperty("waterSips").GetSingle(), Is.Zero);
            Assert.That(rows[0].GetProperty("waterKind").GetString(), Is.EqualTo("None"));
            Assert.That(rows[19].GetProperty("waterSips").GetSingle(), Is.EqualTo(9));
            Assert.That(response.RootElement.GetRawText(), Does.Not.Contain("private-pocket-sentinel"));
            Assert.That(rows.GetRawText(), Does.Not.Contain("901050").And.Not.Contain("901051").And.Not.Contain("999999"));
        });
    }

    [TestCase(0f, 0)]
    [TestCase(0.075f, 0)]
    [TestCase(0.3f, 3)]
    [TestCase(1f, 10)]
    public void CollectorFractionsAreConvertedWhileGroundBottlesAlreadyStoreSips(float fill, int drinkable)
    {
        using var host = CreateHost();
        var id = host.Read(world =>
        {
            var actor = Observer(world);
            actor.Perception.Objects.Clear();
            var collector = Add(world, actor, 902001, ContentIds.WaterCollector);
            var vessel = Add(world, actor, 902002, ContentIds.Bottle, actor.Id);
            // Fresh hosts need not have ticked an NPC pose yet. The fixture
            // only needs an existing shared station slot, not an actor anchor.
            var slot = world.Junctions.Items.Keys.First();
            collector.Junctions.Add(slot);
            vessel.Junctions.Add(slot);
            vessel.ResourceAmount = fill;
            vessel.WaterKind = fill > 0 ? WaterKind.Rain : WaterKind.None;
            world.Caches.ObjectsByTile[actor.Tile] = new List<ObjectId> { collector.Id, vessel.Id };
            var ground = Add(world, actor, 902003, ContentIds.Bottle);
            ground.ResourceAmount = 3;
            ground.WaterKind = WaterKind.Coconut;
            return actor.Id.Value;
        });
        using var response = Describe(host, id);
        var rows = response.RootElement.GetProperty("visibleItems");
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].TryGetProperty("waterSips", out _), Is.False, "The station is not itself the vessel");
            Assert.That(rows[1].GetProperty("waterSips").GetSingle(), Is.EqualTo(fill * SimBalance.BottleCapacity).Within(0.0001));
            Assert.That(rows[1].GetProperty("drinkableSips").GetInt32(), Is.EqualTo(drinkable));
            Assert.That(rows[1].GetProperty("capacitySips").GetInt32(), Is.EqualTo(SimBalance.BottleCapacity));
            Assert.That(rows[2].GetProperty("waterSips").GetSingle(), Is.EqualTo(3));
            Assert.That(rows[2].GetProperty("waterKind").GetString(), Is.EqualTo("Coconut"));
        });
    }

    [Test]
    public void CarriedAndWornInstancesKeepSeparateWaterOwnershipAndSnapshotIndices()
    {
        using var host = CreateHost();
        var setup = host.Read(world =>
        {
            var actor = Observer(world);
            var other = world.Entities.Npcs.Values.First(n => n.Id != actor.Id && n.Faction == actor.Faction);
            actor.Inventory.Items.Clear();
            actor.WornItems.Clear();
            actor.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle) { WaterKind = WaterKind.Raw, ResourceAmount = 2, OwnerId = actor.Id.Value });
            actor.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle) { WaterKind = WaterKind.Boiled, ResourceAmount = 7, OwnerId = other.Id.Value });
            actor.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 3 });
            actor.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutPierced));
            actor.WornItems.Add(new ItemInstance("borrowed-garment") { OwnerId = other.Id.Value });
            return (actor.Id.Value, other.Id.Value);
        });
        using var response = Describe(host, setup.Item1);
        var carried = response.RootElement.GetProperty("inventoryItems");
        var worn = response.RootElement.GetProperty("wornItems");
        Assert.Multiple(() =>
        {
            Assert.That(carried.GetArrayLength(), Is.EqualTo(4));
            Assert.That(carried[0].GetProperty("waterSips").GetSingle(), Is.EqualTo(2));
            Assert.That(carried[1].GetProperty("waterSips").GetSingle(), Is.EqualTo(7));
            Assert.That(carried[1].GetProperty("waterKind").GetString(), Is.EqualTo("Boiled"));
            Assert.That(carried[1].GetProperty("sourceIndex").GetInt32(), Is.EqualTo(1));
            Assert.That(carried[1].GetProperty("ownerNpcId").GetInt32(), Is.EqualTo(setup.Item2));
            Assert.That(carried[2].GetProperty("waterKind").GetString(), Is.EqualTo("Coconut"));
            Assert.That(carried[3].GetProperty("waterSips").GetSingle(), Is.Zero);
            Assert.That(carried[3].GetProperty("waterKind").GetString(), Is.EqualTo("None"));
            Assert.That(worn[0].GetProperty("ownerNpcId").GetInt32(), Is.EqualTo(setup.Item2));
            Assert.That(worn[0].TryGetProperty("waterSips", out _), Is.False);
            Assert.That(response.RootElement.GetProperty("inventory")[0].GetProperty("count").GetInt32(), Is.EqualTo(2));
        });
        host.Read(world =>
        {
            var actor = Observer(world);
            Assert.That(actor.Inventory.Items[0].ResourceAmount, Is.EqualTo(2), "Observation must not mutate water");
            Assert.That(actor.Inventory.Items[1].OwnerId, Is.EqualTo(setup.Item2), "Observation must not claim borrowed items");
            return true;
        });
    }

    private static NPCState Observer(WorldState world) => world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);

    private static WorldObjectState Add(WorldState world, NPCState actor, int id, string itemId, EntityId? owner = null)
    {
        var item = new WorldObjectState { Id = new ObjectId(id), DefinitionId = itemId, Owner = owner, Tile = actor.Tile };
        world.Entities.Objects[item.Id] = item;
        actor.Perception.Objects.Add(new PerceivedObject { Id = item.Id, DefinitionId = itemId, Distance = 1, IsReachable = true });
        return item;
    }

    private static JsonDocument Describe(WorldHost host, int npcId)
    {
        var tools = new McpTools(host, new ControlLeases(45));
        var result = tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId }), "mcp:item-observation-test", out var error);
        Assert.That(error, Is.False, result);
        return JsonDocument.Parse(result);
    }

    private static WorldHost CreateHost()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("Source root");
        // This contract needs two housemates and a foreign camp; HugeIsland
        // intentionally starts every woman in a separate solo camp.
        return new WorldHost(12345, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), "mcp-items-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(dir.FullName, "SimData/simdata.json"), false);
    }
}
