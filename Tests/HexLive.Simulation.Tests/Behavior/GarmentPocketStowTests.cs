using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class GarmentPocketStowTests
{
    private const string Coat = "test.pocket_coat";
    private const string Trinket = "item.photo_frame";

    private static (WorldState world, NPCState npc, ItemInstance garment, WorldObjectState wardrobe) Setup()
    {
        var engine = TestWorld.CreateEngine(12345);
        for (var i = 0; i < 4; i++) engine.Step();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "test setup");
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        world.Content.ObjectDefinitions[Coat] = new ObjectDefinition
        {
            Id = Coat, Layer = WearLayer.Wear, InventoryCapacity = 4,
            Interactions = { new InteractionDefinition { Id = "pickup.testcoat", Type = InteractionType.PickUp } }
        };
        var garment = new ItemInstance(Coat) { OwnerId = npc.Id.Value, Durability = .73f };
        npc.WornItems.Add(garment);
        EquipmentMath.Recalculate(world, npc);
        for (var i = 0; i < npc.Inventory.Capacity; i++)
            npc.Inventory.Items.Add(new ItemInstance(Trinket)
            {
                ResourceAmount = i + .05f, Durability = .2f + i * .01f
            });
        var wardrobe = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Wardrobe);
        npc.Movement.IsMoving = false;
        npc.Movement.JunctionPath.Clear();
        npc.Mind.OutfitLocked = false;
        return (world, npc, garment, wardrobe);
    }

    [Test]
    public void DoffingKeepsTheMacheteAndBottleAndPacksAWholeLogCell()
    {
        var (world, npc, garment, wardrobe) = Setup();
        npc.Inventory.Items.Clear();
        var blade = new ItemInstance(GearCatalog.Machete) { Durability = .71f };
        var bottle = new ItemInstance(ContentIds.Bottle)
        {
            ResourceAmount = 3, WaterKind = WaterKind.Boiled, LastAddedWaterKind = WaterKind.Rain
        };
        npc.Inventory.Items.Add(blade);
        npc.Inventory.Items.Add(bottle);
        for (var i = 2; i < 4; i++)
            npc.Inventory.Items.Add(new ItemInstance(Trinket));
        for (var i = 4; i < npc.Inventory.Capacity - 2; i++)
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        var logs = Enumerable.Range(0, InventoryState.StackSizeFor(ContentIds.Log))
            .Select(i => new ItemInstance(ContentIds.Log) { Durability = .41f + i * .01f }).ToArray();
        npc.Inventory.Items.AddRange(logs);
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Stone));
        Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(npc.Inventory.Capacity));
        Assert.That(InventoryLayoutBuilder.TryCollectOwnedContents(world, npc, 0, out var pockets), Is.True);
        Assert.That(pockets.Any(item => ReferenceEquals(item, blade)), Is.True);
        Assert.That(pockets.Any(item => ReferenceEquals(item, bottle)), Is.True);

        var before = world.Entities.Objects.Count;
        var laid = ExecutionSystem.StowWornGarmentWithContents(world, npc, garment, wardrobe.Id);
        Assert.That(laid, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(before + 1), "Only the garment appears.");
            Assert.That(npc.Inventory.Items.Any(item => ReferenceEquals(item, blade)), Is.True);
            Assert.That(npc.Inventory.Items.Any(item => ReferenceEquals(item, bottle)), Is.True);
            Assert.That(bottle.ResourceAmount, Is.EqualTo(3));
            Assert.That(bottle.WaterKind, Is.EqualTo(WaterKind.Boiled));
            Assert.That(logs.All(log => laid.Contents.Any(item => ReferenceEquals(item, log))), Is.True);
            Assert.That(npc.Inventory.Items.Any(item => item.DefinitionId == ContentIds.Log), Is.False);
            Assert.That(npc.Inventory.UsedSlots, Is.EqualTo(npc.Inventory.Capacity));
        });
    }

    [Test]
    public void DoffingWithoutRoomForProtectedItemsLeavesBothStoresUnchanged()
    {
        var (world, npc, garment, wardrobe) = Setup();
        npc.Inventory.Items.Clear();
        for (var i = 0; i < npc.Inventory.Capacity; i++)
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle) { ResourceAmount = i });
        var before = npc.Inventory.Items.ToArray();
        var objects = world.Entities.Objects.Count;

        Assert.That(ExecutionSystem.StowWornGarmentWithContents(world, npc, garment, wardrobe.Id), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(garment));
            Assert.That(npc.Inventory.Items.Count, Is.EqualTo(before.Length));
            Assert.That(npc.Inventory.Items.Zip(before, ReferenceEquals).All(same => same), Is.True);
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HeatWithoutAStationUsesCarriedSpaceOrKeepsTheCoatOn(bool hasSpace)
    {
        var (world, npc, coat, _) = Setup();
        RemoveClothingStations(world);
        world.Content.ObjectDefinitions[Coat].Interactions.Add(new InteractionDefinition
        {
            Type = InteractionType.Dress, Effects = { WarmthDelta = .5f }
        });
        if (hasSpace) npc.Inventory.Items.Clear();
        npc.Needs.ThermalDiscomfort = .9f;
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Status = PlanStatus.None;
        var objects = world.Entities.Objects.Count;

        new PlanningSystem().Run(world);
        if (hasSpace)
        {
            Assert.That(npc.Plan.Steps.Single().TargetObject, Is.Null);
            var execution = new ExecutionSystem();
            execution.Run(world);
            world.Tick = npc.Execution.EndTick;
            execution.Run(world);
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(npc.Inventory.Items.Single(), Is.SameAs(coat));
            Assert.That(npc.WornItems, Is.Empty);
        }
        else
        {
            Assert.That(npc.Plan.Status, Is.EqualTo(PlanStatus.Failed));
            Assert.That(npc.WornItems.Single(), Is.SameAs(coat));
        }
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HeatRechecksWardrobeSpaceAtTheEndOfTheAnimation(bool wardrobeFills)
    {
        var (world, npc, coat, wardrobe) = Setup();
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Goal = GoalType.Undress;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetItemDefinitionId = Coat;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.UndressItem, TargetObject = wardrobe.Id });
        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
        if (wardrobeFills)
            for (var i = 0; i < Spec133.WardrobeCapacity; i++)
                WorldObjectMutations.SpawnObject(world, Coat, wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        var objects = world.Entities.Objects.Count;
        var carried = npc.Inventory.Items.ToArray();

        world.Tick = npc.Execution.EndTick;
        execution.Run(world);
        if (wardrobeFills)
        {
            Assert.That(npc.WornItems.Single(), Is.SameAs(coat));
            Assert.That(npc.Inventory.Items.Zip(carried, ReferenceEquals).All(same => same), Is.True);
            Assert.That(npc.Inventory.Items.Count, Is.EqualTo(carried.Length));
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));
        }
        else
        {
            var hung = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == Coat);
            Assert.That(npc.WornItems, Is.Empty);
            Assert.That(hung.Junctions[0], Is.EqualTo(wardrobe.Junctions[0]));
            Assert.That(hung.Contents.Count, Is.EqualTo(4));
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects + 1));
        }
        Assert.That(npc.Execution.HeldGarment, Is.Null);
    }

    [Test]
    public void AutonomousWearWithoutAStationKeepsTheCurrentCoatWhenItsReplacementCannotFit()
    {
        var (world, npc, coat, _) = Setup();
        RemoveClothingStations(world);
        world.Content.ObjectDefinitions[Coat].Covers.Add(BodyPart.Torso);
        var replacement = new ObjectDefinition { Id = "test.replacement_coat", Layer = WearLayer.Wear };
        replacement.Covers.Add(BodyPart.Torso);
        world.Content.ObjectDefinitions[replacement.Id] = replacement;
        var candidate = WorldObjectMutations.SpawnObject(world, replacement.Id, npc.Fragment, npc.Tile,
            npc.CurrentJunction.Value);
        npc.Plan.Goal = GoalType.Dress;
        var carried = npc.Inventory.Items.ToArray();
        var objects = world.Entities.Objects.Count;

        Assert.That(ExecutionSystem.CompleteDress(world, npc, candidate, replacement,
            new InteractionDefinition { Type = InteractionType.Dress }, ""), Is.False);
        Assert.That(world.Entities.Objects[candidate.Id], Is.SameAs(candidate));
        Assert.That(npc.WornItems.Single(), Is.SameAs(coat));
        Assert.That(npc.Inventory.Items.Zip(carried, ReferenceEquals).All(same => same), Is.True);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(carried.Length));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));
    }

    private static void RemoveClothingStations(WorldState world)
    {
        foreach (var station in world.Entities.Objects.Values
                     .Where(obj => obj.DefinitionId is ContentIds.Wardrobe or ContentIds.DryingRack).ToArray())
            WorldObjectMutations.DespawnObject(world, station.Id);
    }

    [Test]
    public void WardrobeReceivesExactlyTheSamePhysicalPocketInstances_Bug640()
    {
        var (world, npc, garment, wardrobe) = Setup();
        InventoryLayoutBuilder.TryCollectOwnedContents(world, npc, 0, out var pockets);
        Assert.That(pockets.Count, Is.EqualTo(4));
        var otherItems = npc.Inventory.Items.Where(item => !pockets.Any(p => ReferenceEquals(p, item))).ToArray();
        var objectsBefore = world.Entities.Objects.Count;
        var laid = ExecutionSystem.StowWornGarmentWithContents(world, npc, garment, wardrobe.Id);
        Assert.Multiple(() =>
        {
            Assert.That(laid, Is.Not.Null);
            Assert.That(laid.Contents.Count, Is.EqualTo(pockets.Count));
            Assert.That(pockets.All(p => laid.Contents.Any(i => ReferenceEquals(i, p))), Is.True);
            Assert.That(otherItems.All(p => npc.Inventory.Items.Any(i => ReferenceEquals(i, p))), Is.True);
            Assert.That(npc.Inventory.Items.Any(p => pockets.Any(i => ReferenceEquals(i, p))), Is.False);
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objectsBefore + 1), "No loose resource litter");
            Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(npc.Inventory.Capacity));
            Assert.That(laid.Owner, Is.EqualTo(npc.Id));
        });
    }

    [Test]
    public void MidUndressSlowTickSaveAndAbortPreserveTheAuthoritativeStores()
    {
        var (world, npc, garment, wardrobe) = Setup();
        npc.Mind.CurrentGoal = GoalType.Undress;
        npc.Plan.Goal = GoalType.Undress;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetItemDefinitionId = Coat;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.UndressItem, TargetObject = wardrobe.Id });
        npc.Execution.Status = ExecutionStatus.None;
        var execution = new ExecutionSystem();
        execution.Run(world);
        world.Tick = (npc.Execution.StartTick + npc.Execution.EndTick) / 2;
        execution.Run(world);
        Assert.That(npc.Execution.HeldGarment, Is.SameAs(garment));
        var count = npc.Inventory.Items.Count;
        var objects = world.Entities.Objects.Count;
        new NeedsDecaySystem().Run(world);
        Assert.That(npc.WornItems.Contains(garment), Is.True);
        Assert.That(npc.Inventory.Items.Count, Is.EqualTo(count));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));

        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var loaded = TestWorld.CreateWorld(world.Seed);
        loaded.Content.ObjectDefinitions[Coat] = world.Content.ObjectDefinitions[Coat];
        using (var reader = new BinaryReader(bytes)) WorldSaveSerializer.Read(loaded, reader);
        var restored = loaded.Entities.Npcs[npc.Id];
        Assert.That(restored.WornItems.Any(i => i.DefinitionId == Coat), Is.True);
        Assert.That(restored.Inventory.Items.Count, Is.EqualTo(count));
        Assert.That(restored.Inventory.Items.Select(i => i.ResourceAmount),
            Is.EqualTo(npc.Inventory.Items.Select(i => i.ResourceAmount)));

        PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "test cancel");
        Assert.That(npc.WornItems.Count(i => ReferenceEquals(i, garment)), Is.EqualTo(1));
        Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects), "Visual alias must not spawn a duplicate");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HangCompletionUsesTheSamePocketTransaction(bool rackFull)
    {
        var (world, npc, garment, wardrobe) = Setup();
        garment.Wetness = .9f;
        InventoryLayoutBuilder.TryCollectOwnedContents(world, npc, 0, out var pockets);
        var stand = StowMath.FindUndressSpot(world, npc).Value.Stand;
        npc.CurrentJunction = stand;
        npc.Position = world.Junctions.Items[stand].WorldPosition;
        npc.Tile = world.Junctions.Items[stand].Tiles[0];
        npc.Mind.CurrentGoal = GoalType.DryClothes;
        npc.Plan.Goal = GoalType.DryClothes;
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetObjectId = wardrobe.Id;
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Interact, TargetObject = wardrobe.Id, Interaction = InteractionType.Hang });
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Hang;
        npc.Execution.TargetObject = wardrobe.Id;
        npc.Execution.EndTick = world.Tick;
        wardrobe.IsOccupied = true;
        wardrobe.CurrentUser = npc.Id;
        if (rackFull)
            for (var i = 0; i < Spec133.WardrobeCapacity; i++)
                WorldObjectMutations.SpawnObject(world, Coat, wardrobe.Fragment, wardrobe.Tile, wardrobe.Junctions[0]);
        var objects = world.Entities.Objects.Count;
        new ExecutionSystem().Run(world);
        if (rackFull)
        {
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objects));
            Assert.That(npc.WornItems.Single(), Is.SameAs(garment));
            Assert.That(wardrobe.IsOccupied, Is.False);
            return;
        }
        var hung = world.Entities.Objects.Values.Single(o => o.DefinitionId == Coat);
        Assert.That(pockets.All(p => hung.Contents.Any(i => ReferenceEquals(i, p))), Is.True);
        Assert.That(npc.WornItems.Any(i => ReferenceEquals(i, garment)), Is.False);
        Assert.That(npc.Inventory.UsedSlots, Is.LessThanOrEqualTo(npc.Inventory.Capacity));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PickingUpAStoredGarmentTransfersAllContentsOrLeavesWholeBundleUntouched(bool full)
    {
        var (world, npc, garment, wardrobe) = Setup();
        var laid = ExecutionSystem.StowWornGarmentWithContents(world, npc, garment, wardrobe.Id);
        var contents = laid.Contents.ToArray();
        npc.Inventory.Items.Clear();
        if (full)
            for (var i = 0; i < npc.Inventory.Capacity; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        npc.Plan.Goal = GoalType.PlayerOrder; npc.Mind.ManualControl = true;
        var objectCount = world.Entities.Objects.Count;
        var definition = world.Content.ObjectDefinitions[laid.DefinitionId];
        var completed = ExecutionSystem.ApplyInteractionCompletion(world, npc, laid, definition,
            definition.Interactions.First(i => i.Type == InteractionType.PickUp), "");
        Assert.That(completed, Is.EqualTo(!full));
        if (full)
        {
            Assert.That(world.Entities.Objects.ContainsKey(laid.Id), Is.True);
            Assert.That(laid.Contents.Count, Is.EqualTo(contents.Length));
            Assert.That(contents.All(item => laid.Contents.Any(x => ReferenceEquals(x, item))), Is.True);
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objectCount));
        }
        else
        {
            Assert.That(world.Entities.Objects.ContainsKey(laid.Id), Is.False);
            Assert.That(contents.All(item => npc.Inventory.Items.Any(x => ReferenceEquals(x, item))), Is.True);
            Assert.That(contents.Select(item => item.ResourceAmount), Is.EqualTo(new[] { .05f, 1.05f, 2.05f, 3.05f }));
            Assert.That(world.Entities.Objects.Count, Is.EqualTo(objectCount - 1));
        }
    }

    [Test]
    public void ExistingAiCanFindResourcesInsideTheStowedGarment()
    {
        var (world, npc, garment, wardrobe) = Setup();
        npc.Inventory.Items.Clear();
        for (var i = 0; i < 4; i++) npc.Inventory.Items.Add(new ItemInstance(ContentIds.Rope));
        var laid = ExecutionSystem.StowWornGarmentWithContents(world, npc, garment, wardrobe.Id);
        Assert.That(laid.Contents.Any(i => i.DefinitionId == ContentIds.Rope), Is.True);
        npc.Perception.Objects.Clear();
        var seen = new PerceivedObject { Id = laid.Id, DefinitionId = Coat, Tile = laid.Tile, IsReachable = true };
        seen.AvailableInteractions.Add(InteractionType.PickUp);
        npc.Perception.Objects.Add(seen);
        Assert.That(CraftProjectMath.TryFindUsableCompletedOutput(world, npc, GoalType.CraftRope, out var source), Is.True);
        Assert.That(source, Is.SameAs(laid));
    }
}
}

