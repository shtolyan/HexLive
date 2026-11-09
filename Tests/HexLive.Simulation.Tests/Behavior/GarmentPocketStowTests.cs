using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
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
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle)
            {
                ResourceAmount = i + .05f, WaterKind = WaterKind.Boiled,
                LastAddedWaterKind = WaterKind.Rain, Durability = .2f + i * .01f
            });
        var wardrobe = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Wardrobe);
        npc.Movement.IsMoving = false;
        npc.Movement.JunctionPath.Clear();
        npc.Mind.OutfitLocked = false;
        return (world, npc, garment, wardrobe);
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

    [Test]
    public void HangCompletionUsesTheSamePocketTransaction()
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
        new ExecutionSystem().Run(world);
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

