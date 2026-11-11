using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Persistence;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§128.5 r2 / bug #199: remains and wardrobe share container transfer.</summary>
public sealed class ContainerLootTests
{
    [TestCase("gear.backpack_primal_hunter")]
    [TestCase("gear.backpack_primal_hunter_male")]
    public void WardrobeExportsAndTransfersTheHungHunterBackpack(string packId)
    {
        var engine = TestWorld.CreateEngine(693);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Mind.ManualControl = true;
        world.Content.ObjectDefinitions[packId] = new ObjectDefinition
        {
            Id = packId, Layer = WearLayer.Bags, InventoryCapacity = 4
        };
        // Older definitions can expose Wardrobe without a separate Rack tag.
        world.Content.ObjectDefinitions[ContentIds.Wardrobe] = new ObjectDefinition
        {
            Id = ContentIds.Wardrobe, Tags = { ObjectTags.Wardrobe }
        };
        var anchor = Junction(world, npc);
        var wardrobe = WorldObjectMutations.SpawnObject(
            world, ContentIds.Wardrobe, npc.Fragment, npc.Tile, anchor);
        WorldObjectMutations.SpawnObject(world, "clothing.skirt_primal", npc.Fragment, npc.Tile, anchor);
        var pack = WorldObjectMutations.SpawnObject(world, packId, npc.Fragment, npc.Tile, anchor);
        pack.Durability = 0.62f;

        var decoded = new WorldSnapshot();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSnapshotCodec.Write(WorldSnapshotExporter.Export(world), writer, includeDebugDetails: false);
        stream.Position = 0;
        using (var reader = new BinaryReader(stream)) WorldSnapshotCodec.Read(reader, decoded);
        var rows = decoded.Objects.Single(obj => obj.Id == wardrobe.Id).Contents;
        var slot = rows.FindIndex(row => row.ItemDefinitionId == packId);
        Assert.That(slot, Is.GreaterThanOrEqualTo(0));

        var admission = ManualCommandExecutor.Apply(world, new TransferContainerCommand(
            npc.Id, wardrobe.Id, slot, packId, 1, InventoryTransferDirection.Take));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        for (var i = 0; i < 200 && npc.Plan.Status == PlanStatus.Active; i++) engine.Step();
        Assert.That(npc.Inventory.Items.Single(item => item.DefinitionId == packId).Durability, Is.EqualTo(0.62f));
        Assert.That(world.Entities.Objects.ContainsKey(pack.Id), Is.False);
    }

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
        filled.LastAddedWaterKind = WaterKind.Coconut;
        npc.Inventory.Items.Add(empty);
        npc.Inventory.Items.Add(filled);

        ContainerLootMath.GiveToContainer(world, remains, npc, new[] { filled });
        Assert.Multiple(() =>
        {
            Assert.That(npc.Inventory.Items.Single(), Is.SameAs(empty));
            Assert.That(remains.Contents.Single(), Is.SameAs(filled));
            Assert.That(BottleInventoryMath.Charges(filled), Is.EqualTo(4));
            Assert.That(filled.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(filled.LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
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
            Assert.That(filled.LastAddedWaterKind, Is.EqualTo(WaterKind.Coconut));
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
            world, "clothing.skirt_primal", npc.Fragment, npc.Tile, anchor);
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
        Assert.That(cells.Select(cell => cell.ItemId), Does.Contain("clothing.skirt_primal"));
        var slot = cells.FindIndex(cell => cell.ItemId == "clothing.skirt_primal");
        Assert.That(ContainerLootMath.TryResolve(
            world, wardrobe, slot, "clothing.skirt_primal", 1,
            out var moving, out var groundSources), Is.True);
        Assert.That(ContainerLootMath.FitsInLooter(world, npc, moving), Is.True);

        ContainerLootMath.TakeFromContainer(
            world, wardrobe, npc, moving, groundSources);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.ContainsKey(garment.Id), Is.False);
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == "clothing.skirt_primal"), Is.EqualTo(1));
            Assert.That(npc.Inventory.Items.Count(item =>
                item.DefinitionId == ContentIds.Stone), Is.EqualTo(1));
            var carried = npc.Inventory.Items.Single(item =>
                item.DefinitionId == "clothing.skirt_primal");
            Assert.That(carried.Wetness, Is.EqualTo(0.7f));
            Assert.That(carried.Durability, Is.EqualTo(0.6f));
            Assert.That(carried.OwnerId, Is.EqualTo(npc.Id.Value));
        });

        var returning = npc.Inventory.Items.Single(item =>
            item.DefinitionId == "clothing.skirt_primal");
        Assert.That(ContainerLootMath.CanAccept(
            world, wardrobe, new[] { returning }), Is.True);
        ContainerLootMath.GiveToContainer(
            world, wardrobe, npc, new[] { returning });

        var restored = world.Entities.Objects.Values.Single(obj =>
            obj.Id != wardrobe.Id && obj.DefinitionId == "clothing.skirt_primal" &&
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
        var garment = new ItemInstance("clothing.skirt_primal") { Wetness = 0.6f };
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
                Is.EquivalentTo(new[] { "clothing.skirt_primal" }));
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

    [TestCase(4f)]
    [TestCase(4.2f)]
    [TestCase(.05f)]
    public void CollectorLootRoundTripPreservesPartialNonRainBottle(float amount)
    {
        var world = TestWorld.CreateWorld(35516);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 8;
        var anchor = Junction(world, npc);
        var collector = WorldObjectMutations.SpawnObject(
            world, ContentIds.WaterCollector, npc.Fragment, npc.Tile, anchor);
        var raw = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(raw, WaterKind.Raw, amount);
        npc.Inventory.Items.Add(raw);

        ContainerLootMath.GiveToContainer(world, collector, npc, new[] { raw });
        var parked = WaterCollectorMath.FindVessel(world, collector);
        Assert.That(parked, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(parked!.ResourceAmount, Is.EqualTo(amount / SimBalance.BottleCapacity).Within(1e-5f));
            Assert.That(parked.WaterKind, Is.EqualTo(WaterKind.Raw));
        });

        world.Environment.IsRaining = true;
        new WaterCollectorSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(parked.ResourceAmount, Is.EqualTo(amount / SimBalance.BottleCapacity).Within(1e-5f),
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
            Assert.That(moving[0].ResourceAmount, Is.EqualTo(amount).Within(1e-5f));
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
    public void CampfireFuelBufferHasTwentyDefinitionCellsAndIsNotConstruction()
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
            Assert.That(ContainerLootMath.Capacity(world, fire), Is.EqualTo(20));
            Assert.That(BuildSiteMath.Delivered(fire, ContentIds.Stick), Is.EqualTo(1),
                "The queued stick must not become a delivered spit component.");
            Assert.That(ContainerLootMath.CanAccept(
                world, fire, new[] { new ItemInstance(ContentIds.Stone) }), Is.True);
        });

        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(
            world, fire, out var ticks), Is.True);
        Assert.That(ticks, Is.EqualTo(ContainerLootMath.FuelTicksPerStick * 4f));
        ContainerLootMath.BuildCells(world, fire, cells);
        Assert.That(cells.Count, Is.EqualTo(2));
    }

    [TestCase("resource.yucca_seed")]
    [TestCase("resource.stone")]
    [TestCase("tool.bottle")]
    [TestCase("food.meat_cooked")]
    [TestCase("clothing.top_primal")]
    public void ExplicitCampfireDisposalBurnsOneItemAsOneStickWithoutAutonomousWaste(string id)
    {
        var world = TestWorld.CreateWorld(620);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire,
            npc.Fragment, npc.Tile, Junction(world,npc));
        fire.ResourceAmount = 0f;
        var item = new ItemInstance(id) { ResourceAmount = 4.2f, WaterKind = WaterKind.Rain };
        npc.Inventory.Items.Add(item);
        Assert.That(ContainerLootMath.FindCarriedCampfireFuel(world,npc), Is.Null,
            "Autonomous fire tending must not select seeds, clothing, or useful items.");
        Assert.That(ContainerLootMath.CanAccept(world,fire,new[]{item}),Is.True);
        ContainerLootMath.GiveToContainer(world,fire,npc,new[]{item});
        Assert.That(ContainerLootMath.HasQueuedCampfireFuel(world,fire),Is.True);
        Assert.That(fire.ResourceAmount,Is.Zero,"Cold fire must still require ignition.");
        var cells = new List<(string ItemId,int Count,int SourceIndex)>();
        ContainerLootMath.BuildCells(world,fire,cells);
        Assert.That(cells.Single().ItemId,Is.EqualTo(id));
        Assert.That(BuildSiteMath.HangingMeat(fire,ContentIds.MeatCooked),Is.Zero);
        ContainerLootMath.TakeFromContainer(world,fire,npc,new[]{item},new ObjectId[0]);
        Assert.That(item.ResourceAmount,Is.EqualTo(4.2f),"Queued disposal is reversible until consumed.");
        Assert.That(item.LeafDryingStartTick,Is.EqualTo(-1));
        ContainerLootMath.GiveToContainer(world,fire,npc,new[]{item});
        Assert.That(ContainerLootMath.TryConsumeCampfireFuel(world,fire,out var fuel),Is.True);
        Assert.That(fuel,Is.EqualTo(ContainerLootMath.FuelTicksPerStick));
        Assert.That(fire.Contents,Is.Empty);
        Assert.That(npc.Inventory.Items,Is.Empty);
    }

    [TestCase(87)]
    [TestCase(88)]
    [TestCase(89)]
    public void CampfireRoleSaveMigrationPreservesWaterLeafAgeAndReversibleContents(int version)
    {
        var world = TestWorld.CreateWorld(62088);
        var npc = Colonist(world);
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire,
            npc.Fragment,npc.Tile,Junction(world,npc));
        fire.Variant = "planted";
        var bottle = new ItemInstance(ContentIds.Bottle) {
            ResourceAmount = 4.2f, WaterKind = WaterKind.Rain, LastAddedWaterKind = WaterKind.Coconut,
            LeafDryingStartTick = 17, OwnerId = npc.Id.Value, Wetness = .3f,
            CampfireFuelRole = version >= 88 ? CampfireFuelRole.Discard : CampfireFuelRole.None };
        var leaf = new ItemInstance(ContentIds.PalmLeaf) { LeafDryingStartTick = 123,
            CampfireFuelRole = version >= 88 ? CampfireFuelRole.Discard : CampfireFuelRole.None };
        var wood = new ItemInstance(ContentIds.Stick) {
            ResourceAmount = version < 88 ? -151f : 0f,
            CampfireFuelRole = version >= 88 ? CampfireFuelRole.Wood : CampfireFuelRole.None };
        fire.Contents.AddRange(new[] { bottle,leaf,wood });
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data,System.Text.Encoding.UTF8,true))
            WorldSaveSerializer.WriteAtVersion(world,writer,version);
        data.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(data,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Read(restored,reader);
        Assert.That(data.Position,Is.EqualTo(data.Length));
        var restoredFire = restored.Entities.Objects[fire.Id];
        Assert.That(restoredFire.Variant,Is.EqualTo(version >= 89 ? "planted" : string.Empty));
        var storedBottle = restoredFire.Contents.Single(item => item.DefinitionId == ContentIds.Bottle);
        var storedLeaf = restoredFire.Contents.Single(item => item.DefinitionId == ContentIds.PalmLeaf);
        var storedWood = restoredFire.Contents.Single(item => item.DefinitionId == ContentIds.Stick);
        Assert.That(storedBottle.ResourceAmount,Is.EqualTo(4.2f));
        Assert.That(storedBottle.WaterKind,Is.EqualTo(WaterKind.Rain));
        Assert.That(storedBottle.LastAddedWaterKind,Is.EqualTo(WaterKind.Coconut));
        Assert.That(storedBottle.LeafDryingStartTick,Is.EqualTo(17));
        Assert.That(storedBottle.OwnerId,Is.EqualTo(npc.Id.Value));
        Assert.That(storedBottle.Wetness,Is.EqualTo(.3f));
        Assert.That(storedLeaf.LeafDryingStartTick,Is.EqualTo(123));
        Assert.That(storedLeaf.ResourceAmount,Is.Zero);
        Assert.That(storedWood.CampfireFuelRole,Is.EqualTo(CampfireFuelRole.Wood));
        Assert.That(storedWood.ResourceAmount,Is.Zero);
        Assert.That(storedBottle.CampfireFuelRole,Is.EqualTo(version >= 88 ? CampfireFuelRole.Discard : CampfireFuelRole.None));
        using var upgraded = new MemoryStream();
        using (var writer = new BinaryWriter(upgraded,System.Text.Encoding.UTF8,true))
            WorldSaveSerializer.Write(restored,writer);
        upgraded.Position = 0;
        var twice = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(upgraded,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Read(twice,reader);
        Assert.That(upgraded.Position,Is.EqualTo(upgraded.Length));
        var upgradedFire = twice.Entities.Objects[fire.Id];
        Assert.That(upgradedFire.Variant,Is.EqualTo(restoredFire.Variant));
        Assert.That(upgradedFire.Contents.Single(item => item.DefinitionId == ContentIds.Bottle).ResourceAmount,Is.EqualTo(4.2f));
        Assert.That(upgradedFire.Contents.Single(item => item.DefinitionId == ContentIds.Bottle).WaterKind,Is.EqualTo(WaterKind.Rain));
        Assert.That(upgradedFire.Contents.Single(item => item.DefinitionId == ContentIds.Stick).CampfireFuelRole,Is.EqualTo(CampfireFuelRole.Wood));
        Assert.That(upgradedFire.Contents.Single(item => item.DefinitionId == ContentIds.PalmLeaf).LeafDryingStartTick,Is.EqualTo(123));
        if (version >= 88)
        {
            ContainerLootMath.TakeFromContainer(restored,restoredFire,Colonist(restored),new[]{storedBottle},new ObjectId[0]);
            Assert.That(storedBottle.CampfireFuelRole,Is.EqualTo(CampfireFuelRole.None));
            Assert.That(storedBottle.ResourceAmount,Is.EqualTo(4.2f));
            Assert.That(storedBottle.LeafDryingStartTick,Is.EqualTo(17));
            Assert.That(storedBottle.WaterKind,Is.EqualTo(WaterKind.Rain));
        }
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
                    world, fire, new[] { new ItemInstance(ContentIds.MeatCooked) }), Is.True,
                "Готовое мясо принимается в очередь утилизации, не на вертел.");
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

        var fuel = new[] { ContentIds.Stick, ContentIds.Board, ContentIds.Log }
            .Concat(world.Content.ObjectDefinitions.Keys.Where(id =>
                id.StartsWith("resource.", System.StringComparison.Ordinal) ||
                id.StartsWith("tool.", System.StringComparison.Ordinal)))
            .Where(id => id != ContentIds.Stone).Distinct().Take(20)
            .Select(id => new ItemInstance(id)).ToArray();
        Assert.That(fuel, Has.Length.EqualTo(20));
        foreach (var item in fuel) npc.Inventory.Items.Add(item);
        ContainerLootMath.GiveToContainer(world, fire, npc, fuel);

        Assert.Multiple(() =>
        {
            Assert.That(ContainerLootMath.CanAccept(
                    world, fire, new[] { new ItemInstance(ContentIds.MeatRaw) }), Is.True,
                "Полная очередь топлива не должна закрывать вертел.");
            Assert.That(ContainerLootMath.CanAccept(
                    world, fire, new[] { new ItemInstance(ContentIds.Stone) }), Is.False,
                "20 заполненных видов топлива не принимают 21-й, но не занимают крюки вертела.");
        });
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static HexLive.Simulation.Common.JunctionId Junction(
        WorldState world, NPCState npc) =>
        npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
}
