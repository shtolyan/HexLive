using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class CampfireQueueTests
{
    private static WorldObjectState Fire(WorldState world, NPCState npc)
    {
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire, npc.Fragment,
            npc.Tile, npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0]);
        fire.ResourceAmount = 0;
        return fire;
    }
    private static NPCState Colonist(WorldState world) => world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
    private static string[] Definitions(WorldState world) => world.Content.ObjectDefinitions.Keys
        .Where(id => id.StartsWith("resource.", StringComparison.Ordinal) || id.StartsWith("tool.", StringComparison.Ordinal))
        .Where(id => id != ContentIds.Bottle).OrderBy(id => id, StringComparer.Ordinal).Take(21).ToArray();
    private static void Give(WorldState world, WorldObjectState fire, NPCState npc, params ItemInstance[] items)
    {
        npc.Inventory.Items.AddRange(items);
        Assert.That(ContainerLootMath.CanAccept(world, fire, items), Is.True);
        ContainerLootMath.GiveToContainer(world, fire, npc, items);
    }

    [Test]
    public void TwentyDefinitionCellsAcceptMoreInstancesButRejectTwentyFirstDefinition()
    {
        var world = TestWorld.CreateWorld(66801); var npc = Colonist(world); var fire = Fire(world, npc);
        var ids = Definitions(world); Assert.That(ids, Has.Length.EqualTo(21));
        Give(world, fire, npc, ids.Take(20).Select(id => new ItemInstance(id)).ToArray());
        Assert.That(ContainerLootMath.Capacity(world, fire), Is.EqualTo(20));
        Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { new ItemInstance(ids[20]) }), Is.False);
        Give(world, fire, npc, Enumerable.Range(0, 1000).Select(_ => new ItemInstance(ids[0])).ToArray());
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, fire, cells);
        Assert.That(cells, Has.Count.EqualTo(20));
        Assert.That(cells.Single(c => c.ItemId == ids[0]).Count, Is.EqualTo(1001));
        Assert.That(fire.Contents.Count, Is.EqualTo(1020));
    }

    [Test]
    public void NonStackableQueueKeepsEveryBottleAndDelayedSelectionMetadata()
    {
        var world = TestWorld.CreateWorld(66802); var npc = Colonist(world); var fire = Fire(world, npc);
        var bottles = Enumerable.Range(0, 150).Select(i => new ItemInstance(ContentIds.Bottle) {
            ResourceAmount = i % 7 + .25f, WaterKind = i % 2 == 0 ? WaterKind.Raw : WaterKind.Rain,
            LastAddedWaterKind = WaterKind.Coconut, Durability = .25f + i / 1000f, OwnerId = npc.Id.Value }).ToArray();
        Give(world, fire, npc, bottles);
        var cells = new List<(string ItemId, int Count, int SourceIndex)>(); ContainerLootMath.BuildCells(world, fire, cells);
        Assert.That(cells, Has.Count.EqualTo(1)); Assert.That(cells[0].Count, Is.EqualTo(150));
        Assert.That(ContainerLootMath.TryResolve(world, fire, 0, ContentIds.Bottle, 3, out var first, out _), Is.True);
        for (var i = 0; i < 3; i++) Assert.That(ReferenceEquals(first[i], bottles[i]), Is.True);
        fire.Contents.Insert(0, new ItemInstance(ContentIds.Stone)); // hidden construction item shifts physical indices
        Assert.That(ContainerLootMath.TryResolveReserved(world, fire, ContentIds.Bottle, 2, bottles[3], null,
            out var selected, out var ground), Is.True);
        Assert.That(ReferenceEquals(selected[0], bottles[3]), Is.True); Assert.That(ReferenceEquals(selected[1], bottles[4]), Is.True);
        ContainerLootMath.TakeFromContainer(world, fire, npc, selected, ground);
        Assert.That(bottles[3].ResourceAmount, Is.EqualTo(3.25f));
        Assert.That(bottles[3].WaterKind, Is.EqualTo(WaterKind.Rain));
        Assert.That(bottles[3].LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
        Assert.That(bottles[3].Durability, Is.EqualTo(.253f).Within(1e-6f));
        Assert.That(bottles[3].CampfireFuelRole, Is.EqualTo(CampfireFuelRole.None));
        Assert.That(fire.Contents.Count(item => item.DefinitionId == ContentIds.Bottle), Is.EqualTo(148));
    }

    [Test]
    public void BurningOrderFollowsPhysicalInsertionAcrossGroupedDefinitions()
    {
        var world = TestWorld.CreateWorld(66803); var npc = Colonist(world); var fire = Fire(world, npc);
        var bill = new ItemInstance(ContentIds.Stick); fire.Contents.Add(bill);
        var a = new ItemInstance(ContentIds.Bottle); var b = new ItemInstance(ContentIds.Log); var c = new ItemInstance(ContentIds.Bottle);
        Give(world, fire, npc, a, b, c);
        Assert.That(fire.ResourceAmount, Is.Zero, "Cold queue must wait for ignition.");
        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world, fire, out var ticks), Is.True);
        Assert.That(ticks, Is.EqualTo(1200f)); Assert.That(ReferenceEquals(fire.Contents[1], b), Is.True);
        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world, fire, out ticks), Is.True);
        Assert.That(ticks, Is.EqualTo(4800f)); Assert.That(ReferenceEquals(fire.Contents[1], c), Is.True);
        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world, fire, out ticks), Is.True);
        Assert.That(ticks, Is.EqualTo(1200f)); Assert.That(ReferenceEquals(fire.Contents.Single(), bill), Is.True);
    }

    [Test]
    public void CookedSpitItemDoesNotMergeIntoItsDiscardQueueCell()
    {
        var world = TestWorld.CreateWorld(66804); var npc = Colonist(world); var fire = Fire(world, npc);
        var cooked = new ItemInstance(ContentIds.MeatCooked); fire.Contents.Add(cooked);
        var discarded = new ItemInstance(ContentIds.MeatCooked); Give(world, fire, npc, discarded);
        var cells = new List<(string ItemId, int Count, int SourceIndex)>(); ContainerLootMath.BuildCells(world, fire, cells);
        Assert.That(cells, Has.Count.EqualTo(2));
        Assert.That(ContainerLootMath.TryResolve(world, fire, 0, ContentIds.MeatCooked, 1, out var food, out _), Is.True);
        Assert.That(ReferenceEquals(food.Single(), cooked), Is.True);
        Assert.That(ContainerLootMath.TryResolve(world, fire, 1, ContentIds.MeatCooked, 1, out var fuel, out _), Is.True);
        Assert.That(ReferenceEquals(fuel.Single(), discarded), Is.True);
        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world, fire, out _), Is.True);
        Assert.That(ReferenceEquals(fire.Contents.Single(), cooked), Is.True);
    }

    [Test]
    public void LargeNonStackableQueueSurvivesCurrentSaveAndExportsTwentyCells()
    {
        var world = TestWorld.CreateWorld(66805); var npc = Colonist(world); var fire = Fire(world, npc);
        var bottles = Enumerable.Range(0, 200).Select(i => new ItemInstance(ContentIds.Bottle) {
            ResourceAmount = i % 7 + .25f, WaterKind = WaterKind.Boiled, Durability = .125f + i / 1000f }).ToArray();
        Give(world, fire, npc, bottles);
        Give(world, fire, npc, Definitions(world).Take(19).Select(id => new ItemInstance(id)).ToArray());
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        data.Position = 0; var restored = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(data, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        Assert.That(data.Position, Is.EqualTo(data.Length));
        var actual = restored.Entities.Objects[fire.Id]; var stored = actual.Contents.Where(i => i.DefinitionId == ContentIds.Bottle).ToArray();
        Assert.That(stored, Has.Length.EqualTo(200));
        for (var i = 0; i < stored.Length; i++) {
            Assert.That(stored[i].ResourceAmount, Is.EqualTo(bottles[i].ResourceAmount));
            Assert.That(stored[i].Durability, Is.EqualTo(bottles[i].Durability));
            Assert.That(stored[i].WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(stored[i].CampfireFuelRole, Is.EqualTo(CampfireFuelRole.Discard));
        }
        var snapshot = WorldSnapshotExporter.Export(restored).Objects.Single(o => o.Id.Equals(fire.Id));
        Assert.That(snapshot.ContainerCapacity, Is.EqualTo(20)); Assert.That(snapshot.Contents, Has.Count.EqualTo(20));
        Assert.That(snapshot.Contents.Single(c => c.ItemDefinitionId == ContentIds.Bottle).StackCount, Is.EqualTo(200));
    }
    [Test]
    public void FullBurningQueueRejectsNewFuelTypeWithoutImmediateDisposal()
    {
        var engine = TestWorld.CreateEngine(66806); var world = engine.World; engine.Step();
        var npc = Colonist(world); ManualCommandExecutor.Apply(world, new SetManualControlCommand(npc.Id, true));
        npc.Inventory.Items.Clear(); var fire = Fire(world, npc); fire.ResourceAmount = 5000;
        var ids = Definitions(world).Where(id => id != ContentIds.Stick).Take(20).ToArray();
        Assert.That(ids, Has.Length.EqualTo(20));
        Give(world, fire, npc, ids.Select(id => new ItemInstance(id)).ToArray());
        var stick = new ItemInstance(ContentIds.Stick); npc.Inventory.Items.Add(stick);
        var result = ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, fire.Id, InteractionType.Fuel));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(result.Reason, Is.EqualTo("FuelBufferFull"));
        Assert.That(ReferenceEquals(npc.Inventory.Items.Single(), stick), Is.True);
        Assert.That(fire.ResourceAmount, Is.EqualTo(5000));
    }

    [Test]
    public void QueueFilledDuringApproachDoesNotBurnCarriedFuelOutOfOrder()
    {
        var engine = TestWorld.CreateEngine(66807); var world = engine.World; engine.Step();
        var npc = Colonist(world); npc.Needs.Hunger = npc.Needs.Thirst = 0;
        ManualCommandExecutor.Apply(world, new SetManualControlCommand(npc.Id, true)); npc.Inventory.Items.Clear();
        var start = npc.CurrentJunction.Value;
        var anchor = world.Junctions.Items.Values.First(j => !j.Blocked && j.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) is >= 2 and <= 4 && Connectivity.Reachable(world, start, j.Id));
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire, npc.Fragment, anchor.Tiles[0], anchor.Id);
        fire.ResourceAmount = 5000;
        var stick = new ItemInstance(ContentIds.Stick); npc.Inventory.Items.Add(stick);
        var admitted = ManualCommandExecutor.Apply(world, new InteractCommand(npc.Id, fire.Id, InteractionType.Fuel));
        Assert.That(admitted.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admitted.Reason);
        Give(world, fire, npc, Definitions(world).Where(id => id != ContentIds.Stick).Take(20).Select(id => new ItemInstance(id)).ToArray());
        for (var i = 0; i < 600; i++) engine.Step();
        Assert.That(npc.Inventory.Items.Any(item => ReferenceEquals(item, stick)), Is.True);
        Assert.That(fire.ResourceAmount, Is.LessThanOrEqualTo(5000));
        Assert.That(fire.Contents.Any(item => ReferenceEquals(item, stick)), Is.False);
    }

}
