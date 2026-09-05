using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§128.5 r2 / bug #199: remains and wardrobe share container transfer.</summary>
public sealed class ContainerLootTests
{
    [Test]
    public void DuplicateBottleTransferUsesPhysicalIdentityAndKeepsWater()
    {
        var world = TestWorld.CreateWorld(35511);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var anchor = Junction(world, npc);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, npc.Fragment, npc.Tile, anchor);
        var empty = new ItemInstance(ContentIds.Bottle);
        var filled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(filled, WaterKind.Rain, 4);
        npc.Inventory.Items.Add(empty);
        npc.Inventory.Items.Add(filled);

        ContainerLootMath.GiveToContainer(world, remains, npc, new[] { filled });
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Single(), Is.SameAs(empty));
            Assert.That(remains.Contents.Single(), Is.SameAs(filled));
            Assert.That(BottleInventoryMath.Charges(filled), Is.EqualTo(4));
            Assert.That(filled.WaterKind, Is.EqualTo(WaterKind.Rain));
        });

        var otherEmpty = new ItemInstance(ContentIds.Bottle);
        remains.Contents.Insert(0, otherEmpty);
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, remains, cells);
        Assert.That(cells, Has.Count.EqualTo(2));
        Assert.That(ContainerLootMath.TryResolve(
            world, remains, 1, ContentIds.Bottle, 1,
            out var selected, out var groundSources), Is.True);
        Assert.That(selected.Single(), Is.SameAs(filled),
            "The second non-stackable bottle row must resolve to that physical bottle.");
        ContainerLootMath.TakeFromContainer(
            world, remains, npc, selected, groundSources);
        Assert.Multiple(() =>
        {
            Assert.That(remains.Contents.Single(), Is.SameAs(otherEmpty));
            Assert.That(npc.Inventory.Items.Last(), Is.SameAs(filled));
            Assert.That(BottleInventoryMath.Charges(filled), Is.EqualTo(4));
            Assert.That(filled.WaterKind, Is.EqualTo(WaterKind.Rain));
        });
    }

    [Test]
    public void DelayedContainerLootKeepsSelectedBottleWhenRowsShift()
    {
        var engine = TestWorld.CreateEngine(35512);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        var anchor = Junction(world, npc);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, npc.Fragment, npc.Tile, anchor);
        var emptyFirst = new ItemInstance(ContentIds.Bottle);
        var selectedFilled = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(selectedFilled, WaterKind.Boiled, 7);
        remains.Contents.Add(emptyFirst);
        remains.Contents.Add(selectedFilled);

        var admission = ManualCommandExecutor.Apply(
            world, new TransferContainerCommand(
                npc.Id, remains.Id, 1, ContentIds.Bottle, 1,
                InventoryTransferDirection.Take));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            admission.Reason);

        // The selected bottle shifts to row/index zero; an equal replacement
        // occupies its former index before the delayed interaction executes.
        remains.Contents.RemoveAt(0);
        var replacement = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(replacement, WaterKind.Raw, 2);
        remains.Contents.Add(replacement);
        for (var i = 0; i < 200 &&
             !npc.Inventory.Items.Any(item => ReferenceEquals(item, selectedFilled)) &&
             npc.Plan.Status == PlanStatus.Active; i++)
        {
            engine.Step();
        }

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Any(item =>
                ReferenceEquals(item, selectedFilled)), Is.True,
                $"Plan={npc.Plan.Status} Move={npc.Movement.Status} " +
                $"Events={string.Join(" | ", world.Events.Items.Select(e => e.Type + ":" + e.Message))}");
            Assert.That(remains.Contents.Any(item =>
                ReferenceEquals(item, replacement)), Is.True);
            Assert.That(remains.Contents.Any(item =>
                ReferenceEquals(item, selectedFilled)), Is.False);
            Assert.That(selectedFilled.WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(BottleInventoryMath.Charges(selectedFilled), Is.EqualTo(7));
        });
    }

    [Test]
    public void EmptyHumanRemainsStayAValidTwoWayContainer()
    {
        var world = TestWorld.CreateWorld(128501);
        var npc = Colonist(world);
        var anchor = Junction(world, npc);
        var remains = WorldObjectMutations.SpawnObject(
            world, ContentIds.HumanRemains, npc.Fragment, npc.Tile,
            anchor);

        Assert.That(ContainerLootMath.IsLootable(world, remains), Is.True);

        var stone = new ItemInstance(ContentIds.Stone);
        npc.Inventory.Items.Add(stone);
        ContainerLootMath.GiveToContainer(
            world, remains, npc, new[] { stone });
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items, Does.Not.Contain(stone));
            Assert.That(remains.Contents.Any(item => ReferenceEquals(item, stone)), Is.True);
        });
    }

    [Test]
    public void WardrobeRoundTripPreservesPhysicalGarmentAndItsState()
    {
        var world = TestWorld.CreateWorld(128502);
        var npc = Colonist(world);
        var anchor = Junction(world, npc);
        var wardrobe = WorldObjectMutations.SpawnObject(
            world, ContentIds.Wardrobe, npc.Fragment, npc.Tile, anchor);
        wardrobe.RotationDegrees = 120f;
        var garment = WorldObjectMutations.SpawnObject(
            world, ContentIds.LeatherPants, npc.Fragment, npc.Tile, anchor);
        garment.Wetness = 0.7f;
        garment.Durability = 0.6f;
        garment.Dirtiness = 0.5f;
        garment.Bloodiness = 0.4f;
        garment.Owner = npc.Id;
        garment.Contents.Add(new ItemInstance(ContentIds.Stone));

        Assert.That(ContainerLootMath.IsLootable(world, wardrobe), Is.True,
            "An empty wardrobe must remain a valid destination before its first garment.");
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, wardrobe, cells);
        Assert.That(cells.Select(cell => cell.ItemId), Does.Contain(ContentIds.LeatherPants));
        var slot = cells.FindIndex(cell => cell.ItemId == ContentIds.LeatherPants);
        Assert.That(ContainerLootMath.TryResolve(
            world, wardrobe, slot, ContentIds.LeatherPants, 1,
            out var moving, out var groundSources), Is.True);
        Assert.That(ContainerLootMath.FitsInLooter(world, npc, moving), Is.True);

        ContainerLootMath.TakeFromContainer(
            world, wardrobe, npc, moving, groundSources);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(garment.Id), Is.False);
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.LeatherPants), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Stone), Is.EqualTo(1));
            var carried = npc.Inventory.Items.Single(item =>
                item.DefinitionId == ContentIds.LeatherPants);
            Assert.That(carried.Wetness, Is.EqualTo(0.7f));
            Assert.That(carried.Durability, Is.EqualTo(0.6f));
            Assert.That(carried.OwnerId, Is.EqualTo(npc.Id.Value));
        });

        var returning = npc.Inventory.Items.Single(item =>
            item.DefinitionId == ContentIds.LeatherPants);
        Assert.That(ContainerLootMath.CanAccept(
            world, wardrobe, new[] { returning }), Is.True);
        ContainerLootMath.GiveToContainer(
            world, wardrobe, npc, new[] { returning });

        var restored = world.Entities.Objects.Values.Single(obj =>
            obj.Id != wardrobe.Id && obj.DefinitionId == ContentIds.LeatherPants &&
            obj.Junctions.Contains(anchor));
        Assert.Multiple(() =>
        {
            Assert.That(restored.RotationDegrees, Is.EqualTo(wardrobe.RotationDegrees));
            Assert.That(restored.Wetness, Is.EqualTo(0.7f));
            Assert.That(restored.Durability, Is.EqualTo(0.6f));
            Assert.That(restored.Owner, Is.EqualTo(npc.Id));
        });
    }

    [Test]
    public void RackAndCollectorExposeFilteredPhysicalSlots()
    {
        var world = TestWorld.CreateWorld(151001);
        var npc = Colonist(world);
        var anchor = Junction(world, npc);
        var rack = WorldObjectMutations.SpawnObject(
            world, ContentIds.DryingRack, npc.Fragment, npc.Tile, anchor);
        var collector = WorldObjectMutations.SpawnObject(
            world, ContentIds.WaterCollector, npc.Fragment, npc.Tile, anchor);
        var garment = new ItemInstance(ContentIds.LeatherPants) { Wetness = 0.6f };
        var bottle = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 7f };
        bottle.WaterKind = WaterKind.Rain;
        var stone = new ItemInstance(ContentIds.Stone);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.IsLootable(world, rack), Is.True);
            Assert.That(ContainerLootMath.Capacity(world, rack),
                Is.EqualTo(SimBalance.RackCapacity));
            Assert.That(ContainerLootMath.CanAccept(world, rack, new[] { garment }), Is.True);
            Assert.That(ContainerLootMath.CanAccept(world, rack, new[] { stone }), Is.False);
            Assert.That(ContainerLootMath.Capacity(world, collector), Is.EqualTo(1));
            Assert.That(ContainerLootMath.CanAccept(world, collector, new[] { bottle }), Is.True);
            Assert.That(ContainerLootMath.CanAccept(world, collector, new[] { garment }), Is.False);
        });

        npc.Inventory.Items.Add(garment);
        ContainerLootMath.GiveToContainer(world, rack, npc, new[] { garment });
        npc.Inventory.Items.Add(bottle);
        ContainerLootMath.GiveToContainer(world, collector, npc, new[] { bottle });

        var rackCells = new List<(string ItemId, int Count, int SourceIndex)>();
        var collectorCells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, rack, rackCells);
        ContainerLootMath.BuildCells(world, collector, collectorCells);
        var collectorSnapshot = WorldSnapshotExporter.Export(world).Objects.Single(obj =>
            obj.Id.Equals(collector.Id));
        Assert.Multiple(() =>
        {
            Assert.That(rackCells.Select(cell => cell.ItemId),
                Is.EquivalentTo(new[] { ContentIds.LeatherPants }));
            Assert.That(collectorCells.Select(cell => cell.ItemId),
                Is.EquivalentTo(new[] { ContentIds.Bottle }));
            var parked = WaterCollectorMath.FindVessel(world, collector);
            Assert.That(parked, Is.Not.Null);
            Assert.That(parked!.ResourceAmount, Is.EqualTo(0.7f).Within(1e-5f));
            Assert.That(collectorSnapshot.Contents.Single().WaterKind,
                Is.EqualTo(WaterKind.Rain));
            Assert.That(collectorSnapshot.Contents.Single().ResourceAmount,
                Is.EqualTo(7f));
        });

        Assert.That(ContainerLootMath.TryResolve(
            world, collector, 0, ContentIds.Bottle, 1,
            out var collectedBottle, out var bottleSource), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(collectedBottle[0].WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(BottleInventoryMath.Charges(collectedBottle[0]), Is.EqualTo(7));
        });
        ContainerLootMath.TakeFromContainer(
            world, collector, npc, collectedBottle, bottleSource);
        Assert.That(npc.Inventory.Items.Last(), Is.SameAs(collectedBottle[0]));
    }

    [Test]
    public void CollectorLootRoundTripPreservesPartialNonRainBottle()
    {
        var world = TestWorld.CreateWorld(35516);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var anchor = Junction(world, npc);
        var collector = WorldObjectMutations.SpawnObject(
            world, ContentIds.WaterCollector, npc.Fragment, npc.Tile, anchor);
        var raw = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, 4);
        npc.Inventory.Items.Add(raw);

        ContainerLootMath.GiveToContainer(world, collector, npc, new[] { raw });
        var parked = WaterCollectorMath.FindVessel(world, collector);
        Assert.That(parked, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(parked!.ResourceAmount, Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(parked.WaterKind, Is.EqualTo(WaterKind.Raw));
        });

        world.Environment.IsRaining = true;
        new WaterCollectorSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(parked.ResourceAmount, Is.EqualTo(0.4f).Within(1e-5f),
                "Rain must not silently mix into a non-rain bottle.");
            Assert.That(parked.WaterKind, Is.EqualTo(WaterKind.Raw));
        });

        Assert.That(ContainerLootMath.TryResolve(
            world, collector, 0, ContentIds.Bottle, 1,
            out var moving, out var groundSources), Is.True);
        ContainerLootMath.TakeFromContainer(
            world, collector, npc, moving, groundSources);
        Assert.Multiple(() =>
        {
            Assert.That(moving[0].WaterKind, Is.EqualTo(WaterKind.Raw));
            Assert.That(BottleInventoryMath.Charges(moving[0]), Is.EqualTo(4));
        });

        var full = WorldObjectMutations.SpawnObject(
            world, ContentIds.Bottle, collector.Fragment, collector.Tile, anchor);
        full.ResourceAmount = 1f;
        full.WaterKind = WaterKind.Rain;
        Assert.That(ContainerLootMath.TryResolve(
            world, collector, 0, ContentIds.Bottle, 1,
            out var fullBottle, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(fullBottle[0].WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(BottleInventoryMath.Charges(fullBottle[0]),
                Is.EqualTo(SimBalance.BottleCapacity));
        });
    }

    [Test]
    public void CampfireFuelBufferIsThreeFilteredStacksAndNotConstruction()
    {
        var world = TestWorld.CreateWorld(151002);
        var npc = Colonist(world);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile, Junction(world, npc));
        fire.BuildProduct = ContentIds.Campfire;
        fire.BillSticks = SimBalance.CampfireBillSticks;
        fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        var log = new ItemInstance(ContentIds.Log);
        var board = new ItemInstance(ContentIds.Board);
        var fuelStick = new ItemInstance(ContentIds.Stick);
        npc.Inventory.Items.Add(log);
        npc.Inventory.Items.Add(board);
        npc.Inventory.Items.Add(fuelStick);

        Assert.That(ContainerLootMath.CanAccept(
            world, fire, new[] { log, board, fuelStick }), Is.True);
        ContainerLootMath.GiveToContainer(
            world, fire, npc, new[] { log, board, fuelStick });

        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, fire, cells);
        Assert.Multiple(() =>
        {
            Assert.That(cells.Select(cell => cell.ItemId), Is.EquivalentTo(new[]
            {
                ContentIds.Log, ContentIds.Board, ContentIds.Stick
            }));
            Assert.That(ContainerLootMath.Capacity(world, fire), Is.EqualTo(3));
            Assert.That(BuildSiteMath.Delivered(fire, ContentIds.Stick), Is.EqualTo(1),
                "The queued stick must not become a delivered spit component.");
            Assert.That(ContainerLootMath.CanAccept(
                world, fire, new[] { new ItemInstance(ContentIds.Stone) }), Is.False);
        });

        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(
            world, fire, out var ticks), Is.True);
        Assert.That(ticks, Is.EqualTo(ContainerLootMath.FuelTicksPerStick * 4f));
        ContainerLootMath.BuildCells(world, fire, cells);
        Assert.That(cells.Count, Is.EqualTo(2));
    }

    [Test]
    public void BurningCampfireAutomaticallyFeedsNextBufferedWood()
    {
        var world = TestWorld.CreateWorld(151003);
        var npc = Colonist(world);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile, Junction(world, npc));
        fire.ResourceAmount = 0.01f;
        var board = new ItemInstance(ContentIds.Board);
        npc.Inventory.Items.Add(board);
        ContainerLootMath.GiveToContainer(world, fire, npc, new[] { board });

        new FireSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(fire.ResourceAmount, Is.EqualTo(ContainerLootMath.FuelTicksPerStick));
            Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world, fire), Is.False);
        });
    }

    /// <summary>
    /// §151.3 (bug #293): сырое мясо кладут на вертел и снимают готовое тем же
    /// окном обыска. Пределы топлива и крюков независимы.
    /// </summary>
    [Test]
    public void CampfireSpitAcceptsRawMeatAndGivesTheCookedChunkBack()
    {
        var world = TestWorld.CreateWorld(151004);
        var npc = Colonist(world);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile, Junction(world, npc));
        var rawMeat = new ItemInstance(ContentIds.MeatRaw);
        npc.Inventory.Items.Add(rawMeat);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.Capacity(world, fire),
                Is.EqualTo(ContainerLootMath.CampfireFuelCapacity),
                "Без вертела жарочных ячеек нет вовсе.");
            Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { rawMeat }), Is.False,
                "Вешать мясо не на что, пока вертел не собран.");
        });

        // Вертел = полный счёт палок и верёвки (§54.14, стадии 2-4).
        for (var i = 0; i < SimBalance.CampfireBillSticks; i++)
        {
            fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        }

        for (var i = 0; i < SimBalance.CampfireBillRope; i++)
        {
            fire.Contents.Add(new ItemInstance(ContentIds.Rope));
        }

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.Capacity(world, fire),
                Is.EqualTo(ContainerLootMath.CampfireFuelCapacity +
                           ContainerLootMath.CampfireSpitCells));
            Assert.That(ContainerLootMath.CanAccept(world, fire, new[] { rawMeat }), Is.True);
            Assert.That(ContainerLootMath.CanAccept(
                    world, fire, new[] { new ItemInstance(ContentIds.MeatCooked) }), Is.False,
                "Готовое мясо обратно на вертел не вешают.");
        });

        ContainerLootMath.GiveToContainer(world, fire, npc, new[] { rawMeat });
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        ContainerLootMath.BuildCells(world, fire, cells);
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items, Does.Not.Contain(rawMeat));
            Assert.That(cells.Select(cell => cell.ItemId),
                Is.EquivalentTo(new[] { ContentIds.MeatRaw }),
                "Доставленные палки и верёвка — материал стройки, а не содержимое.");
            Assert.That(rawMeat.ResourceAmount, Is.Zero,
                "Прогресс прожарки начинается с нуля и крутится FireSystem.");
            Assert.That(BuildSiteMath.Delivered(fire, BuildSiteMath.MaterialSticks),
                Is.EqualTo(SimBalance.CampfireBillSticks),
                "Мясо не имеет права засчитаться в строительный счёт.");
        });

        // Крюки кончаются раньше ячеек: их предел стережёт приём.
        var overflow = new List<ItemInstance>();
        for (var i = 0; i < SimBalance.CampfireSpitCapacity; i++)
        {
            overflow.Add(new ItemInstance(ContentIds.MeatRaw));
        }

        Assert.That(ContainerLootMath.CanAccept(world, fire, overflow), Is.False);

        // Прожарили — и сняли готовый кусок тем же окном.
        fire.ResourceAmount = 5000f;
        for (var i = 0; i < 400; i++)
        {
            if (BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) > 0) break;
            new FireSystem().Run(world);
        }

        ContainerLootMath.BuildCells(world, fire, cells);
        var cooked = cells.FindIndex(cell => cell.ItemId == ContentIds.MeatCooked);
        Assert.That(cooked, Is.GreaterThanOrEqualTo(0),
            "Готовый кусок обязан быть видимой ячейкой станции.");
        Assert.That(ContainerLootMath.TryResolve(
            world, fire, cooked, ContentIds.MeatCooked, 1,
            out var moving, out var groundSources), Is.True);
        ContainerLootMath.TakeFromContainer(world, fire, npc, moving, groundSources);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Contains(ContentIds.MeatCooked), Is.True);
            Assert.That(BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked), Is.Zero);
            Assert.That(npc.Inventory.Items
                .Single(item => item.DefinitionId == ContentIds.MeatCooked).ResourceAmount,
                Is.Zero, "Служебное число снимается вместе с вещью.");
        });
    }

    /// <summary>§151.3: три полена не имеют права занять место мяса.</summary>
    [Test]
    public void CampfireFuelQueueAndSpitHooksAreIndependentLimits()
    {
        var world = TestWorld.CreateWorld(151005);
        var npc = Colonist(world);
        var fire = WorldObjectMutations.SpawnObject(
            world, ContentIds.Campfire, npc.Fragment, npc.Tile, Junction(world, npc));
        for (var i = 0; i < SimBalance.CampfireBillSticks; i++)
        {
            fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        }

        for (var i = 0; i < SimBalance.CampfireBillRope; i++)
        {
            fire.Contents.Add(new ItemInstance(ContentIds.Rope));
        }

        var fuel = new[]
        {
            new ItemInstance(ContentIds.Stick),
            new ItemInstance(ContentIds.Board),
            new ItemInstance(ContentIds.Log)
        };
        foreach (var item in fuel) npc.Inventory.Items.Add(item);
        ContainerLootMath.GiveToContainer(world, fire, npc, fuel);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.CanAccept(
                    world, fire, new[] { new ItemInstance(ContentIds.MeatRaw) }), Is.True,
                "Полная очередь топлива не должна закрывать вертел.");
            Assert.That(ContainerLootMath.CanAccept(
                    world, fire, new[] { new ItemInstance(ContentIds.Stone) }), Is.False,
                "Камень костру по-прежнему не еда и не дрова.");
        });
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static HexLive.Simulation.Common.JunctionId Junction(
        WorldState world, NPCState npc) =>
        npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
}
