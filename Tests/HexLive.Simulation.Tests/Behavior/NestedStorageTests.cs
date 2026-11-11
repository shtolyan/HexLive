using System.Collections.Generic;
using System.IO;
using HexLive.Simulation.Persistence;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class NestedStorageTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void GatherWoodUsesAnObservedLogInsideAStoredBackpack(bool hanging)
    {
        var engine = TestWorld.CreateEngine(692);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Needs.Energy = 1f;
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(ContentIds.AxeStone);
        var (container, pockets, backpack) = StoreBackpack(world, npc, hanging);
        var log = new ItemInstance(ContentIds.Log) { OwnerId = npc.Id.Value, Wetness = 0.3f };
        pockets.Add(log);
        pockets.Add(new ItemInstance(ContentIds.Stone));

        new PerceptionSystem().Run(world);
        var sight = npc.Perception.Objects.Single(item => item.Id == container.Id && !item.FromMemory);
        var seenBackpack = sight.StoredItems!.Single();
        Assert.That(seenBackpack.DefinitionId, Is.EqualTo(backpack.DefinitionId));
        Assert.That(seenBackpack.Contents!.First().OwnerId, Is.EqualTo(npc.Id.Value));
        Assert.That(PlanningSystem.HasObjectCandidateForGoal(world, npc, GoalType.GatherWood, container.Id), Is.True);
        npc.Mind.CurrentGoal = GoalType.GatherWood;
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.TakeStoredItem));

        for (var i = 0; i < 200 && npc.Plan.Goal == GoalType.GatherWood &&
             npc.Plan.Status == PlanStatus.Active; i++) engine.Step();
        Assert.That(npc.Inventory.Items.SingleOrDefault(item => item.DefinitionId == ContentIds.Log), Is.SameAs(log),
            $"Goal={npc.Mind.CurrentGoal} Plan={npc.Plan.Status} Move={npc.Movement.Status} " +
            string.Join(" | ", world.Events.Items.Where(e => e.EntityId == npc.Id.Value &&
                e.Type is "PlanBuilt" or "PathSearching" or "PathBlocked" or "GoalInterrupted" or
                    "GoalChanged" or "ManualOrderRejected" or "StoredItemRecovered").TakeLast(12)
                .Select(e => e.Type + ":" + e.Message)));
        Assert.That(npc.Inventory.Items.Any(item => item.DefinitionId == backpack.DefinitionId), Is.False);
        Assert.That(pockets.Single().DefinitionId, Is.EqualTo(ContentIds.Stone));
        Assert.That(world.Entities.Objects.ContainsKey(container.Id), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StorageExecutionRejectsAReplacementAndForeignPocketOwnership(bool hanging)
    {
        var world = TestWorld.CreateWorld(692);
        var npc = Colonist(world);
        var (container, pockets, _) = StoreBackpack(world, npc, hanging);
        var wanted = new ItemInstance(ContentIds.Log);
        pockets.Add(wanted);
        new PerceptionSystem().Run(world);
        var pack = npc.Perception.Objects.Single(item => item.Id == container.Id).StoredItems!.Single();
        var path = new[] { pack, pack.Contents!.Single() };
        pockets.Clear();
        var replacement = new ItemInstance(ContentIds.Log);
        pockets.Add(replacement);
        Assert.That(ContainerLootMath.TryTakeObservedItem(world, npc, container, path), Is.False);
        Assert.That(pockets.Single(), Is.SameAs(replacement));

        var outsider = world.Entities.Npcs.Values.First(other => other.Id != npc.Id);
        outsider.Faction = Faction.Colony2;
        replacement.OwnerId = outsider.Id.Value;
        world.Mode = GameMode.HugeIsland;
        new PerceptionSystem().Run(world);
        pack = npc.Perception.Objects.Single(item => item.Id == container.Id).StoredItems!.Single();
        Assert.That(pack.Contents, Is.Null);
        Assert.That(pockets.Single(), Is.SameAs(replacement));

        replacement.OwnerId = 0;
        new PerceptionSystem().Run(world);
        pack = npc.Perception.Objects.Single(item => item.Id == container.Id).StoredItems!.Single();
        for (var i = 0; i < 16; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        Assert.That(ContainerLootMath.TryTakeObservedItem(world, npc, container, new[] { pack, pack.Contents!.Single() }), Is.False);
        Assert.That(pockets.Single(), Is.SameAs(replacement));
    }

    [TestCase(92)]
    [TestCase(93)]
    public void StoredBackpackSaveRoundTripPreservesNestedPhysicalFields(int version)
    {
        var world = TestWorld.CreateWorld(692);
        var npc = Colonist(world);
        var (container, pockets, backpack) = StoreBackpack(world, npc, hanging: false);
        if (version >= 93)
        {
            var leaf = new ItemInstance(ContentIds.Bottle)
            {
                ResourceAmount = 3.4f, WaterKind = WaterKind.Rain,
                OwnerId = npc.Id.Value, Durability = .45f, Wetness = .3f
            };
            pockets.Add(leaf);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        stream.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(stream)) WorldSaveSerializer.Read(restored, reader);
        var savedPack = restored.Entities.Objects[container.Id].Contents.Single();
        Assert.That(savedPack.DefinitionId, Is.EqualTo(backpack.DefinitionId));
        if (version < 93) Assert.That(savedPack.Contents, Is.Null);
        else
        {
            var saved = savedPack.Contents!.Single();
            Assert.That(saved.ResourceAmount, Is.EqualTo(3.4f));
            Assert.That(saved.OwnerId, Is.EqualTo(npc.Id.Value));
            Assert.That(saved.WaterKind, Is.EqualTo(WaterKind.Rain));
            Assert.That(saved.Durability, Is.EqualTo(.45f));
            Assert.That(saved.Wetness, Is.EqualTo(.3f));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TransferringTheBackpackKeepsItsCargoWithIt(bool hanging)
    {
        var world = TestWorld.CreateWorld(692);
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        var cargo = new ItemInstance(ContentIds.Log);
        var (container, pockets, backpack) = StoreBackpack(world, npc, hanging, cargo);
        Assert.That(pockets.Single(), Is.SameAs(cargo));
        Assert.That(ContainerLootMath.TryResolve(world, container, 0, backpack.DefinitionId, 1,
            out var moving, out var groundSources), Is.True);
        Assert.That(moving.Single().Contents!.Single(), Is.SameAs(cargo));
        ContainerLootMath.TakeFromContainer(world, container, npc, moving, groundSources);
        var carried = npc.Inventory.Items.Single();
        Assert.That(carried.Contents!.Single(), Is.SameAs(cargo));
        Assert.That(InventoryMath.RemoveReference(npc.Inventory.Items, carried), Is.True);
        var onGround = ExecutionSystem.SpawnDroppedItem(world, npc, carried, npc.Tile, Junction(world, npc));
        Assert.That(onGround.Contents.Single(), Is.SameAs(cargo));
        Assert.That(carried.Contents, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ManualWearDropPickupAndTransferCanCreateTheNestedPath(bool hanging)
    {
        var engine = TestWorld.CreateEngine(692);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.CurrentJunction ??= SpatialQueries.FindNearestJunction(world, npc.Position);
        EquipmentMath.Recalculate(world, npc);
        var handsAndCarry = npc.Inventory.Capacity;

        RegisterBackpack(world);
        var pack = new ItemInstance(BackpackId) { OwnerId = npc.Id.Value };
        npc.Inventory.Items.Add(pack);
        Apply(new ManageInventoryCommand(npc.Id, new InventoryItemRef(InventoryItemSource.Carried, 0, BackpackId), InventoryAction.Wear));
        Assert.That(npc.WornItems.Single(), Is.SameAs(pack));
        var tools = new[] { ContentIds.Bottle, ContentIds.AxeStone, ContentIds.PickaxeStone,
            ContentIds.Hammer, ContentIds.Spear, ContentIds.Bow };
        Assert.That(handsAndCarry, Is.LessThanOrEqualTo(tools.Length));
        foreach (var id in tools.Take(handsAndCarry)) npc.Inventory.Items.Add(new ItemInstance(id));
        var log = new ItemInstance(ContentIds.Log);
        npc.Inventory.Items.Add(log);

        Apply(new ManageInventoryCommand(npc.Id, new InventoryItemRef(InventoryItemSource.Worn, 0, BackpackId), InventoryAction.Drop));
        var droppedPack = world.Entities.Objects.Values.Single(item => item.DefinitionId == BackpackId);
        Assert.That(droppedPack.Contents.Single(), Is.SameAs(log));
        Apply(new ManageInventoryCommand(npc.Id, new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.Bottle), InventoryAction.Drop));
        Apply(new InteractCommand(npc.Id, droppedPack.Id, InteractionType.PickUp));
        var index = npc.Inventory.Items.FindIndex(item => item.DefinitionId == BackpackId);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(npc.Inventory.Items[index].Contents!.Single(), Is.SameAs(log));

        var root = CreateStorage(world, npc, hanging);
        Apply(new TransferContainerCommand(npc.Id, root.Id, index, BackpackId, 1, InventoryTransferDirection.Give));
        new PerceptionSystem().Run(world);
        var seen = npc.Perception.Objects.Single(item => item.Id == root.Id).StoredItems!.Single();
        Assert.That(seen.DefinitionId, Is.EqualTo(BackpackId));
        Assert.That(seen.Contents!.Single().DefinitionId, Is.EqualTo(ContentIds.Log));

        void Apply(ISimulationCommand command)
        {
            var admission = ManualCommandExecutor.Apply(world, command);
            Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
            if (npc.Plan.Status != PlanStatus.Active) return;
            for (var i = 0; i < 200 && npc.Plan.Status == PlanStatus.Active; i++) engine.Step();
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed),
                string.Join(" | ", world.Events.Items.Where(e => e.EntityId == npc.Id.Value).TakeLast(12)
                    .Select(e => e.Type + ":" + e.Message)));
        }
    }

    [Test]
    public void ThirstPlanFindsTheFullBottleInsideTheBackpackAndDrinksFromIt()
    {
        var engine = TestWorld.CreateEngine(692);
        var world = engine.World;
        var npc = Colonist(world);
        npc.Inventory.Items.Clear();
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0.95f;
        npc.Mind.IsDehydrated = true;
        npc.Needs.Energy = 1f;
        var full = new ItemInstance(ContentIds.Bottle);
        BottleInventoryMath.SetContents(full, WaterKind.Rain, 4);
        var (root, pockets, _) = StoreBackpack(world, npc, hanging: true);
        pockets.Add(new ItemInstance(ContentIds.Bottle));
        pockets.Add(full);
        new PerceptionSystem().Run(world);
        Assert.That(PlanningSystem.HasObservedStoredItem(world, npc, GoalType.GetWater), Is.True);

        npc.Mind.CurrentGoal = GoalType.Drink;
        npc.Plan.Status = PlanStatus.None;
        new PlanningSystem().Run(world);
        Assert.That(npc.Plan.TargetObjectId, Is.EqualTo(root.Id));
        Assert.That(npc.Plan.Steps.Last().Type, Is.EqualTo(PlanStepType.TakeStoredItem));
        for (var i = 0; i < 200 && npc.Plan.TargetObjectId == root.Id &&
             npc.Plan.Status == PlanStatus.Active; i++) engine.Step();

        Assert.That(npc.Inventory.Items.SingleOrDefault(item => item.DefinitionId == ContentIds.Bottle), Is.SameAs(full),
            string.Join(" | ", world.Events.Items.Where(e => e.EntityId == npc.Id.Value &&
                e.Type is "PlanBuilt" or "GoalInterrupted" or "GoalChanged" or "StoredItemRecovered")
                .TakeLast(12).Select(e => e.Type + ":" + e.Message)));
        for (var i = 0; i < 200 && BottleInventoryMath.Charges(full) == 4; i++) engine.Step();
        Assert.That(BottleInventoryMath.Charges(full), Is.LessThan(4));
        Assert.That(pockets.Count, Is.EqualTo(1));
        Assert.That(BottleInventoryMath.Charges(pockets[0]), Is.Zero);
    }

    private const string BackpackId = "gear.backpack_primal_hunter";

    private static (WorldObjectState Container, List<ItemInstance> Pockets, ItemInstance Backpack)
        StoreBackpack(WorldState world, NPCState npc, bool hanging, ItemInstance initialCargo = null)
    {
        var root = CreateStorage(world, npc, hanging);
        var backpack = new ItemInstance(BackpackId) { OwnerId = npc.Id.Value };
        if (initialCargo != null) backpack.GetOrCreateContents().Add(initialCargo);
        npc.Inventory.Items.Add(backpack);
        Assert.That(ContainerLootMath.CanAccept(world, root, new[] { backpack }), Is.True);
        ContainerLootMath.GiveToContainer(world, root, npc, new[] { backpack });
        var pockets = hanging
            ? world.Entities.Objects.Values.Single(item => item.DefinitionId == BackpackId).Contents
            : backpack.GetOrCreateContents();
        return (root, pockets, backpack);
    }

    private static WorldObjectState CreateStorage(WorldState world, NPCState npc, bool hanging)
    {
        const string chestId = "furniture.storage_chest";
        RegisterBackpack(world);
        if (!world.Content.ObjectDefinitions.ContainsKey(chestId))
            world.Content.ObjectDefinitions[chestId] = new ObjectDefinition
                { Id = chestId, Tags = { "Container" } };
        var root = WorldObjectMutations.SpawnObject(world,
            hanging ? ContentIds.Wardrobe : chestId, npc.Fragment, npc.Tile, Junction(world, npc));
        root.Owner = npc.Id;
        return root;
    }

    private static void RegisterBackpack(WorldState world)
    {
        world.Content.ObjectDefinitions[BackpackId] = new ObjectDefinition
        {
            Id = BackpackId, Layer = WearLayer.Bags, InventoryCapacity = 4,
            Interactions = { new InteractionDefinition { Id = "test.take_backpack", Type = InteractionType.PickUp, DurationTicks = 1 } }
        };
    }

    private static NPCState Colonist(WorldState world) =>
        world.Entities.Npcs.Values.First(npc => npc.Faction == Faction.Colony);

    private static HexLive.Simulation.Common.JunctionId Junction(
        WorldState world, NPCState npc) =>
        npc.CurrentJunction ?? world.Tiles.Items[npc.Tile].Junctions[0];
}
