using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class ActionItemBindingTests
{
    [TearDown]
    public void RestoreCatalog() { GearCatalog.ResetToDefaults(); SimDataFile.Require(System.IO.Path.Combine(RepoPaths.Root, "SimData", "simdata.json")); }

    [Test]
    public void HydrationDisplaysAndSpendsTheBoundBottleAfterInventoryReorder()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var selected = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 3, WaterKind = WaterKind.Rain };
        var other = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 7, WaterKind = WaterKind.Raw };
        npc.Inventory.Items.Add(selected);
        npc.Execution.CurrentInteraction = InteractionType.HydrateOther;
        AidSupply.Bind(world, npc, AidKind.Hydrate);
        npc.Inventory.Items.Insert(0, other);
        var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id == npc.Id);
        Assert.That(snapshot.HeldItemId, Is.EqualTo(ContentIds.Bottle));
        Assert.That(ActionItemResolver.HeldBottle(npc), Is.SameAs(selected));
        Assert.That(AidSupply.TrySpend(world, npc, AidKind.Hydrate, out _), Is.True);
        Assert.That(selected.ResourceAmount, Is.EqualTo(2));
        Assert.That(other.ResourceAmount, Is.EqualTo(7));
    }

    [Test]
    public void RemovedBottleIsNotReplacedByAnotherIdenticalBottle()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var selected = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 3, WaterKind = WaterKind.Rain };
        npc.Inventory.Items.Add(selected);
        npc.Execution.CurrentInteraction = InteractionType.HydrateOther;
        AidSupply.Bind(world, npc, AidKind.Hydrate);
        npc.Inventory.Items.Clear();
        var replacement = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 7, WaterKind = WaterKind.Raw };
        npc.Inventory.Items.Add(replacement);
        Assert.That(ActionItemResolver.HeldId(world, npc), Is.Empty);
        Assert.That(AidSupply.TrySpend(world, npc, AidKind.Hydrate, out _), Is.False);
        Assert.That(replacement.ResourceAmount, Is.EqualTo(7));
    }

    [Test]
    public void EmptyBottleDoesNotHideTheCoconutActuallyUsedForAid()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        var coconut = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        npc.Inventory.Items.Add(coconut);
        npc.Execution.CurrentInteraction = InteractionType.HydrateOther;
        AidSupply.Bind(world, npc, AidKind.Hydrate);
        Assert.That(ActionItemResolver.HeldId(world, npc), Is.EqualTo(ContentIds.CoconutPierced));
        Assert.That(ActionItemResolver.HeldBottle(npc), Is.Null);
        Assert.That(AidSupply.TrySpend(world, npc, AidKind.Hydrate, out _), Is.True);
        Assert.That(coconut.ResourceAmount, Is.EqualTo(3));
    }

    [Test]
    public void FeedingKeepsTheSelectedFoodWhenBetterFoodArrives()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var selected = new ItemInstance(ContentIds.CoconutOpen);
        npc.Inventory.Items.Add(selected);
        npc.Execution.CurrentInteraction = InteractionType.FeedOther;
        AidSupply.Bind(world, npc, AidKind.Feed);
        npc.Inventory.Items.Insert(0, new ItemInstance("food.meat_cooked"));
        Assert.That(ActionItemResolver.HeldInstance(world, npc), Is.SameAs(selected));
        Assert.That(AidSupply.TrySpend(world, npc, AidKind.Feed, out var spend), Is.True);
        Assert.That(spend.Item, Is.EqualTo(ContentIds.CoconutOpen));
        Assert.That(npc.Inventory.Items.Single().DefinitionId, Is.EqualTo("food.meat_cooked"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InventoryActingHandMatchesPhysicalSupply(bool leftOnly)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        if (leftOnly) npc.Body.Sever(BodyPart.ArmR);
        var selected = new ItemInstance(ContentIds.Bottle) { ResourceAmount = 3, WaterKind = WaterKind.Rain };
        npc.Inventory.Items.Add(selected);
        npc.Execution.CurrentInteraction = InteractionType.HydrateOther;
        AidSupply.Bind(world, npc, AidKind.Hydrate);
        var layout = InventoryLayoutBuilder.Build(world, npc);
        var hand = layout.Containers.Single(c => c.Id == (leftOnly ? "hand:left" : "hand:right"));
        Assert.That(hand.Slots.Single().ItemDefinitionId, Is.EqualTo(ContentIds.Bottle));
        Assert.That(hand.Slots.Single().SourceIndex, Is.EqualTo(0));
        Assert.That(layout.Containers.SelectMany(c => c.Slots).Sum(s => s.StackCount), Is.EqualTo(1));
    }

    [Test]
    public void DrawingHolsteredToolKeepsTypedSlotAndDoesNotDuplicateTheItem()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.WornItems.Clear();
        npc.WornItems.Add("legHolster_2204");
        var tool = new ItemInstance("tool.axe_stone");
        npc.Inventory.Items.Add(tool);
        EquipmentMath.RecalculateCapacity(world, npc);
        npc.Execution.CurrentInteraction = InteractionType.Harvest;
        npc.Execution.ActionTool = tool;
        npc.Execution.ActionItemsBound = true;
        var layout = InventoryLayoutBuilder.Build(world, npc);
        var hand = layout.Containers.Single(c => c.Id == "hand:right").Slots[0];
        var holster = layout.Containers.Single(c => c.Kind == InventoryContainerKind.Holster);
        Assert.That(hand.ItemDefinitionId, Is.EqualTo(tool.DefinitionId));
        Assert.That(hand.AcceptedItemDefinitionId, Is.Empty);
        Assert.That(holster.Slots.Single(s => s.AcceptedItemDefinitionId == tool.DefinitionId).StackCount, Is.Zero);
        Assert.That(layout.Containers.SelectMany(c => c.Slots).Sum(s => s.StackCount), Is.EqualTo(1));
        npc.Inventory.Items.Clear();
        Assert.That(ActionItemResolver.HeldId(world, npc), Is.Empty);
    }

    [Test]
    public void ChangingActionClearsPreviousBindings()
    {
        var state = new NPCExecutionState { CurrentInteraction = InteractionType.FillVessel };
        state.ActionItemsBound = true;
        state.ActionSupply = new ItemInstance(ContentIds.CoconutPierced);
        state.ActionTool = new ItemInstance(GearCatalog.Knife);
        state.VesselSources.Add(state.ActionSupply);
        state.CurrentInteraction = null;
        Assert.That(state.ActionSupply, Is.Null);
        Assert.That(state.ActionTool, Is.Null);
        Assert.That(state.VesselSources, Is.Empty);
        Assert.That(state.ActionItemsBound, Is.False);
    }

    [Test]
    public void OneHandCannotPourTwoVesselsButCanStillDrink()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Bottle));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 });
        Assert.That(VesselTransferMath.CanFillBottle(npc), Is.True);
        npc.Body.Sever(BodyPart.ArmL);
        Assert.That(VesselTransferMath.CanFillBottle(npc), Is.False);
        Assert.That(AidSupply.SelectItem(world, npc, AidKind.Hydrate)?.DefinitionId, Is.EqualTo(ContentIds.CoconutPierced));
    }

    [Test]
    public void PourBindsSourcesAndCannotConsumeAnInsertedReplacement()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        var source = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        npc.Inventory.Items.Add(bottle);
        npc.Inventory.Items.Add(source);
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.FillVessel });
        npc.Plan.Status = PlanStatus.Active;
        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.FillVessel));
        Assert.That(npc.Execution.ActionSupply, Is.SameAs(source));
        var layout = InventoryLayoutBuilder.Build(world, npc);
        Assert.That(layout.Containers.Single(c => c.Id == "hand:right").Slots[0].SourceIndex, Is.EqualTo(0));
        Assert.That(layout.Containers.Single(c => c.Id == "hand:left").Slots[0].SourceIndex, Is.EqualTo(1));
        npc.Inventory.Items.RemoveAt(1);
        var replacement = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        npc.Inventory.Items.Add(replacement);
        world.Tick++;
        execution.Run(world);
        Assert.That(npc.Execution.CurrentInteraction, Is.Null);
        Assert.That(bottle.ResourceAmount, Is.Zero);
        Assert.That(replacement.ResourceAmount, Is.EqualTo(4));
    }

    [Test]
    public void PourChangesSourcePhaseWithoutSwitchingToUnreservedCoconuts()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        var bottle = new ItemInstance(ContentIds.Bottle);
        var first = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        var second = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        npc.Inventory.Items.AddRange(new[] { bottle, first, second });
        npc.Plan.Steps.Clear();
        npc.Plan.Steps.Add(new PlanStep { Type = PlanStepType.FillVessel });
        npc.Plan.Status = PlanStatus.Active;
        var execution = new ExecutionSystem();
        execution.Run(world);
        Assert.That(npc.Execution.ActionSupply, Is.SameAs(first));
        var added = new ItemInstance(ContentIds.CoconutPierced) { ResourceAmount = 4 };
        npc.Inventory.Items.Insert(0, added);
        world.Tick += SimBalance.FillVesselDurationTicks / 2;
        execution.Run(world);
        Assert.That(npc.Execution.ActionSupply, Is.SameAs(second));
        world.Tick += SimBalance.FillVesselDurationTicks;
        execution.Run(world);
        Assert.That(bottle.ResourceAmount, Is.EqualTo(8));
        Assert.That(first.ResourceAmount + second.ResourceAmount, Is.Zero);
        Assert.That(added.ResourceAmount, Is.EqualTo(4));
    }

    [Test]
    public void NewToolUsesCapabilityAndSpeedWithoutAnIdWhitelist()
    {
        TestWorld.CreateWorld();
        const string id = "test.action_tool";
        GearCatalog.Override(new GearStats { Id = id, Capabilities = GearCapability.Saw, HarvestSpeedMult = 3 });
        var tools = new[] { new ItemInstance(GearCatalog.Axe), new ItemInstance(id) };
        Assert.That(GearCatalog.BestToolFor(tools, GearCapability.Saw), Is.SameAs(tools[1]));
        Assert.That(GearCatalog.BestSpeedMultFor(tools, GearCapability.Saw), Is.EqualTo(3));
        Assert.That(ActionRequirements.HasTool(tools.Select(t => t.DefinitionId), new[] { GearCapability.Saw }, 1), Is.True);
        Assert.That(ActionRequirements.HasTool(tools.Select(t => t.DefinitionId), new[] { GearCapability.Saw }, 0), Is.False);
    }
}
