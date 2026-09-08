using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§52 / bug #355: bottle contents belong to physical items.</summary>
public sealed class BottleInstanceRegressionTests
{
    private static NPCState Girl(WorldState world) =>
        world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();

    [Test]
    public void MultipleBottlesCanCarryDifferentLiquidsAndCharges()
    {
        var npc = Girl(TestWorld.CreateWorld(35501));
        npc.Inventory.Items.Clear();
        var raw = new ItemInstance(ContentIds.Bottle);
        var rain = new ItemInstance(ContentIds.Bottle);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(rain);

        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 3);
        BottleInventoryMath.SetContents(rain, WaterKind.Rain, 8);

        Assert.Multiple(() =>
        {
            Assert.That(BottleInventoryMath.Charges(raw), Is.EqualTo(3));
            Assert.That(raw.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(rain), Is.EqualTo(8));
            Assert.That(rain.WaterKind, Is.EqualTo(WaterKind.Rain));
        });
    }

    [Test]
    public void HydrationTheftMovesOneSipBetweenMatchingPhysicalBottles()
    {
        var world = TestWorld.CreateWorld(35508);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).Take(2).ToArray();
        var abuser = people[0];
        var mark = people[1];
        abuser.Inventory.Items.Clear();
        mark.Inventory.Items.Clear();
        abuser.Inventory.Capacity = 2;

        var destination = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(destination, WaterKind.Raw, 1);
        abuser.Inventory.Items.Add(destination);
        abuser.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        var source = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(source, WaterKind.Raw, 2);
        var untouched = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(untouched, WaterKind.Rain, 3);
        mark.Inventory.Items.Add(source);
        mark.Inventory.Items.Add(untouched);

        var moved = AbuseMath.TryTake(
            world, abuser, mark, AidKind.Hydrate, out var taken);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True,
                "A full pack can still accept a sip into its existing bottle.");
            Assert.That(taken, Is.EqualTo("bottle.water"));
            Assert.That(BottleInventoryMath.Charges(destination), Is.EqualTo(2));
            Assert.That(BottleInventoryMath.Charges(source), Is.EqualTo(1));
            Assert.That(source.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(untouched), Is.EqualTo(3));
            Assert.That(untouched.WaterKind, Is.EqualTo(WaterKind.Rain));
        });
    }

    [Test]
    public void WholeBottleTheftRemovesTheExactFilledInstance()
    {
        var world = TestWorld.CreateWorld(35510);
        var people = world.Entities.Npcs.Values.OrderBy(npc => npc.Id.Value).Take(2).ToArray();
        var abuser = people[0];
        var mark = people[1];
        abuser.Inventory.Items.Clear();
        mark.Inventory.Items.Clear();
        abuser.Inventory.Capacity = 8;

        var emptyFirst = new ItemInstance(ContentIds.Bottle);
        var filledSecond = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(filledSecond, WaterKind.Boiled, 6);
        mark.Inventory.Items.Add(emptyFirst);
        mark.Inventory.Items.Add(filledSecond);

        var moved = AbuseMath.TryTake(
            world, abuser, mark, AidKind.Hydrate, out var taken);

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(taken, Is.EqualTo(ContentIds.Bottle));
            Assert.That(mark.Inventory.Items.Count, Is.EqualTo(1));
            Assert.That(mark.Inventory.Items[0], Is.SameAs(emptyFirst),
                "Definition equality must not remove the empty first bottle.");
            Assert.That(abuser.Inventory.Items.Single(), Is.SameAs(filledSecond));
            Assert.That(BottleInventoryMath.Charges(filledSecond), Is.EqualTo(6));
            Assert.That(filledSecond.WaterKind, Is.EqualTo(WaterKind.Boiled));
        });
    }

    [Test]
    public void GroundBottleCanBePickedUpAlongsideExistingBottle()
    {
        var engine = TestWorld.CreateEngine(35502);
        var world = engine.World;
        var npc = Girl(world);
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var coconut = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(coconut, WaterKind.Coconut, 2);
        npc.Inventory.Items.Add(coconut);

        PlaceAtOpenJunction(world, npc);
        var neighbor = SpatialQueries.GetPassableNeighbors(
            world, npc.CurrentJunction!.Value).First();
        var tile = world.Junctions.Items[neighbor].Tiles[0];
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, npc.Fragment, tile, neighbor);
        ground.WaterKind = WaterKind.Rain;
        ground.ResourceAmount = 4f;

        var admission = ManualCommandExecutor.Apply(
            world, new InteractCommand(npc.Id, ground.Id, InteractionType.PickUp));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            admission.Reason);
        for (var i = 0; i < 1000 && world.Entities.Objects.ContainsKey(ground.Id); i++)
        {
            engine.Step();
        }

        var bottles = npc.Inventory.Items.Where(BottleInventoryMath.IsBottle).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(ground.Id), Is.False);
            Assert.That(bottles, Has.Length.EqualTo(2));
            Assert.That(bottles.Single(b => b.WaterKind == WaterKind.Coconut).ResourceAmount,
                Is.EqualTo(2f));
            Assert.That(bottles.Single(b => b.WaterKind == WaterKind.Rain).ResourceAmount,
                Is.EqualTo(4f));
        });
    }

    [Test]
    public void CorpseSpoilRemovesTheSelectedBottleByReference()
    {
        var world = TestWorld.CreateWorld(35515);
        var npc = Girl(world);
        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        var emptyFirst = new ItemInstance(ContentIds.Bottle);
        var selectedFilled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(selectedFilled, WaterKind.Rain, 6);
        remains.Contents.Add(emptyFirst);
        remains.Contents.Add(selectedFilled);

        Assert.That(CorpseMath.TakeSpoil(
            world, remains, selectedFilled, CorpseMath.SpoilSource.Bag), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(remains.Contents.Single(), Is.SameAs(emptyFirst));
            Assert.That(BottleInventoryMath.Charges(selectedFilled), Is.EqualTo(6));
            Assert.That(selectedFilled.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(npc.Inventory.Items.Any(item =>
                ReferenceEquals(item, selectedFilled)), Is.False,
                "TakeSpoil only detaches; the caller owns the later destination move.");
        });
    }

    [Test]
    public void CurrentSavePreservesEveryBottleAndLegacyV67MigratesFirstBottle()
    {
        var world = TestWorld.CreateWorld(35503);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        var raw = new ItemInstance(ContentIds.Bottle);
        var boiled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 3);
        BottleInventoryMath.SetContents(boiled, WaterKind.Boiled, 7);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(boiled);
        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        ground.WaterKind = WaterKind.Coconut;
        ground.ResourceAmount = 9f;

        var current = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        var currentBottles = Girl(current).Inventory.Items
            .Where(BottleInventoryMath.IsBottle).ToArray();
        var currentGround = current.Entities.Objects[ground.Id];
        Assert.Multiple(() =>
        {
            Assert.That(currentBottles, Has.Length.EqualTo(2));
            Assert.That(currentBottles[0].WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(currentBottles[0]), Is.EqualTo(3));
            Assert.That(currentBottles[1].WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(BottleInventoryMath.Charges(currentBottles[1]), Is.EqualTo(7));
            Assert.That(currentGround.WaterKind, Is.EqualTo(WaterKind.Coconut));
            Assert.That(currentGround.ResourceAmount, Is.EqualTo(9f));
        });

        npc.Inventory.Items.RemoveAt(1);
        var legacy = RoundTrip(world, 67);
        var migrated = BottleInventoryMath.FirstBottle(Girl(legacy));
        Assert.Multiple(() =>
        {
            Assert.That(migrated, Is.Not.Null);
            Assert.That(migrated.WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(migrated), Is.EqualTo(3));
        });
    }

    [Test]
    public void ExportAndWireKeepPerSlotAndGroundBottleWater()
    {
        var world = TestWorld.CreateWorld(35504);
        var npc = Girl(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var raw = new ItemInstance(ContentIds.Bottle);
        var rain = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 1);
        BottleInventoryMath.SetContents(rain, WaterKind.Rain, 6);
        npc.Inventory.Items.Add(raw);
        npc.Inventory.Items.Add(rain);

        var anchor = world.Junctions.Items.Values.First(junction => !junction.Blocked);
        var ground = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, anchor.Fragment, anchor.Tiles[0], anchor.Id);
        ground.WaterKind = WaterKind.Boiled;
        ground.ResourceAmount = 5f;

        var sent = WorldSnapshotExporter.Export(world);
        var received = RoundTrip(sent);
        var npcSnapshot = received.Npcs.Single(n => n.Id.Equals(npc.Id));
        var bottleSlots = npcSnapshot.InventoryContainers
            .SelectMany(c => c.Slots)
            .Where(s => s.ItemDefinitionId == ContentIds.Bottle)
            .OrderBy(s => s.SourceIndex).ToArray();
        var objectSnapshot = received.Objects.Single(o => o.Id.Equals(ground.Id));

        Assert.Multiple(() =>
        {
            Assert.That(bottleSlots, Has.Length.EqualTo(2));
            Assert.That(bottleSlots[0].WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(bottleSlots[0].ResourceAmount, Is.EqualTo(1f));
            Assert.That(bottleSlots[1].WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(bottleSlots[1].ResourceAmount, Is.EqualTo(6f));
            Assert.That(objectSnapshot.WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(objectSnapshot.ResourceAmount, Is.EqualTo(5f));
        });
    }

    private static WorldState RoundTrip(WorldState source, int version)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        }
        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }

    private static WorldSnapshot RoundTrip(WorldSnapshot source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            WorldSnapshotCodec.Write(source, writer, includeDebugDetails: false);
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var result = new WorldSnapshot();
        WorldSnapshotCodec.Read(reader, result);
        return result;
    }

    private static void PlaceAtOpenJunction(WorldState world, NPCState npc)
    {
        var destination = world.Junctions.Items.Values.First(junction =>
            !junction.Blocked && SpatialQueries.IsJunctionFree(world, junction.Id) &&
            junction.Neighbors.Any(neighbor =>
                SpatialQueries.IsJunctionFree(world, neighbor)));

        if (npc.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }

        var oldTile = npc.Tile;
        npc.CurrentJunction = destination.Id;
        npc.Position = destination.WorldPosition;
        npc.Tile = destination.Tiles[0];
        npc.Fragment = destination.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, destination.Id, npc.Id);
    }
}

}
