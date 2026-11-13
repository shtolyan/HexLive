using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class AnimalLootTests
{
    private const string CrabRaw = ContentIds.CrabRaw;
    private const string CrabCooked = ContentIds.CrabCooked;
    private const string CrabRoasted = ContentIds.CrabRoasted;
    private const string WolfHide = ContentIds.WolfHide;

    [OneTimeSetUp]
    public void LoadShippedContent() => SimDataFile.Require(RepoPaths.SimData);

    [TestCase(MobIds.Crab, false)]
    [TestCase("rabbit", true)]
    public void CrabGoesFromCarcassToSpitAndMealAsOneWholeItem(string carcassVariant, bool viaContainer)
    {
        var (engine, actor) = Fixture();
        var world = engine.World;
        Butcher(engine, actor, carcassVariant);
        Assert.That(actor.Inventory.Items.Select(item => item.DefinitionId),
            Is.EquivalentTo(new[] { ContentIds.Knife, CrabRaw }));
        Assert.That(world.Entities.Objects.Values.Any(obj => obj.DefinitionId is ContentIds.Hide or WolfHide), Is.False);

        var anchor = actor.CurrentJunction!.Value;
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire,
            actor.Fragment, actor.Tile, anchor);
        SetWorkpoint(world, actor, fire);
        fire.ResourceAmount = 100000f;
        for (var i = 0; i < SimBalance.CampfireBillSticks; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        for (var i = 0; i < SimBalance.CampfireBillRope; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Rope));
        actor.Inventory.Items.Remove(ContentIds.Knife);

        Apply(engine, viaContainer
            ? new TransferContainerCommand(actor.Id, fire.Id, 0, CrabRaw, 1, InventoryTransferDirection.Give)
            : new CraftItemCommand(actor.Id, GoalType.CookMeat));
        Until(engine, () => BuildSiteMath.HangingMeat(fire, ContentIds.MeatRaw) > 0);
        Assert.That(fire.Contents.Count(item => item.DefinitionId == CrabRaw), Is.EqualTo(1));
        Assert.That(actor.Inventory.Items, Is.Empty);
        Until(engine, () => BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked) > 0);
        Assert.That(fire.Contents.Count(item => item.DefinitionId == CrabRoasted), Is.EqualTo(1));

        Apply(engine, new InteractCommand(actor.Id, fire.Id, InteractionType.PickUp));
        Until(engine, () => actor.Inventory.Items.Any(item => item.DefinitionId == CrabRoasted));
        WorldObjectMutations.DespawnObject(world, fire.Id);
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        var stump = WorldObjectMutations.SpawnObject(world, ContentIds.PalmStump,
            actor.Fragment, actor.Tile, anchor);
        Apply(engine, new InteractCommand(actor.Id, stump.Id, InteractionType.Serve, MealServing.InteractionId));
        Until(engine, () => actor.Inventory.Items.Any(item => item.DefinitionId == CrabCooked));
        actor.Needs.Hunger = .8f;
        Apply(engine, new SelfActionCommand(actor.Id, SelfActionKind.EatFromPack));
        Until(engine, () => actor.Inventory.Items.All(item => item.DefinitionId != CrabCooked));
        Assert.That(actor.Needs.Hunger, Is.LessThan(.7f));
    }

    [Test]
    public void WolfProducesOneWholeSkinnedCarcassAndOneIdentifiableHide()
    {
        var (engine, actor) = Fixture();
        Butcher(engine, actor, MobIds.Dog);
        Assert.That(actor.Inventory.Items.Count(item => item.DefinitionId == ContentIds.WolfCarcassRaw),
            Is.EqualTo(1));
        Assert.That(actor.Inventory.Items.Any(item => item.DefinitionId == ContentIds.MeatRaw), Is.False);
        var hides = engine.World.Entities.Objects.Values.Where(obj => obj.DefinitionId is ContentIds.Hide or WolfHide);
        Assert.That(hides.Select(obj => obj.DefinitionId), Is.EqualTo(new[] { WolfHide }));
    }

    [Test]
    public void WolfGoesFromButcheringToThreeServedPortionsAndOneMeal()
    {
        var (engine, actor) = Fixture();
        var world = engine.World;
        Butcher(engine, actor, MobIds.Dog);

        var anchor = actor.CurrentJunction!.Value;
        var fire = WorldObjectMutations.SpawnObject(world, ContentIds.Campfire,
            actor.Fragment, actor.Tile, anchor);
        SetWorkpoint(world, actor, fire);
        fire.ResourceAmount = 100000f;
        for (var i = 0; i < SimBalance.CampfireBillSticks; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Stick));
        for (var i = 0; i < SimBalance.CampfireBillRope; i++)
            fire.Contents.Add(new ItemInstance(ContentIds.Rope));

        var rawSlot = actor.Inventory.Items.FindIndex(item => item.DefinitionId == ContentIds.WolfCarcassRaw);
        Apply(engine, new ManageInventoryCommand(actor.Id,
            new InventoryItemRef(InventoryItemSource.Carried, rawSlot, ContentIds.WolfCarcassRaw), InventoryAction.Cook));
        Until(engine, () => fire.Contents.Any(item => item.DefinitionId == ContentIds.WolfCarcassCooked));
        Apply(engine, new InteractCommand(actor.Id, fire.Id, InteractionType.PickUp));
        Until(engine, () => actor.Inventory.Items.Any(item => item.DefinitionId == ContentIds.WolfCarcassCooked));
        Assert.That(engine.ApplyManualCommand(new SelfActionCommand(actor.Id, SelfActionKind.EatFromPack)).Accepted,
            Is.False, "A roasted carcass must be served first.");

        WorldObjectMutations.DespawnObject(world, fire.Id);
        var stump = WorldObjectMutations.SpawnObject(world, ContentIds.PalmStump,
            actor.Fragment, actor.Tile, anchor);
        var roastSlot = actor.Inventory.Items.FindIndex(item => item.DefinitionId == ContentIds.WolfCarcassCooked);
        Apply(engine, new ManageInventoryCommand(actor.Id,
            new InventoryItemRef(InventoryItemSource.Carried, roastSlot, ContentIds.WolfCarcassCooked), InventoryAction.Serve));
        Until(engine, () => actor.Inventory.Items.Count(item => item.DefinitionId == ContentIds.MeatCooked) == 3);
        Assert.That(stump.IsOccupied, Is.False);
        Assert.That(actor.Inventory.Items.Any(item => item.DefinitionId == ContentIds.WolfCarcassCooked), Is.False);
        var portions = actor.Inventory.Items.Where(item => item.DefinitionId == ContentIds.MeatCooked).ToArray();
        Assert.That(portions.Select(item => item.ResourceAmount), Is.All.EqualTo(300f));

        actor.Needs.Hunger = .8f;
        var portionSlot = actor.Inventory.Items.FindIndex(item => ReferenceEquals(item, portions[1]));
        Apply(engine, new ManageInventoryCommand(actor.Id,
            new InventoryItemRef(InventoryItemSource.Carried, portionSlot, ContentIds.MeatCooked), InventoryAction.Eat));
        Until(engine, () => actor.Plan.Status == PlanStatus.Completed);
        Assert.That(portions[1].ResourceAmount, Is.EqualTo(200f).Within(.002f));
        Assert.That(portions[0].ResourceAmount, Is.EqualTo(300f));
        Assert.That(portions[2].ResourceAmount, Is.EqualTo(300f));
        Assert.That(actor.Needs.Hunger, Is.EqualTo(.5f).Within(.002f));
    }

    [TestCase(ContentIds.Hide, false)]
    [TestCase(WolfHide, false)]
    [TestCase(WolfHide, true)]
    public void HideRecipesRecognizeBothIdentitiesInTheirPhysicalLocations(string hide, bool onGround)
    {
        var (engine, actor) = Fixture();
        var world = engine.World;
        var bench = WorldObjectMutations.SpawnObject(world, ContentIds.Workbench, actor.Fragment,
            actor.Tile, actor.CurrentJunction!.Value);
        SetWorkpoint(world, actor, bench);
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Rope));
        if (onGround)
            WorldObjectMutations.SpawnObject(world, hide, actor.Fragment, actor.Tile, actor.CurrentJunction.Value);
        else
            actor.Inventory.Items.Add(new ItemInstance(hide));
        var option = CraftingOptions.Resolve(world, actor, ContentIds.LeatherFlask);
        Assert.That(option.CanCraft, Is.True, option.BlockReason.ToString());

        Apply(engine, new CraftByOutputCommand(actor.Id, ContentIds.LeatherFlask));
        Until(engine, () => actor.Plan.Status == PlanStatus.Completed);
        Assert.That(actor.Inventory.Items.Count(item => item.DefinitionId == ContentIds.LeatherFlask) +
            world.Entities.Objects.Values.Count(obj => obj.DefinitionId == ContentIds.LeatherFlask && !obj.IsCraftProject),
            Is.EqualTo(1));
        Assert.That(actor.Inventory.Items.Any(item => item.DefinitionId == hide), Is.False);
        Assert.That(world.Entities.Objects.Values.Any(obj => obj.DefinitionId == hide), Is.False);
    }

    private static void SetWorkpoint(WorldState world, NPCState actor, WorldObjectState station)
    {
        var anchor = station.Junctions[0];
        if (station.DefinitionId == ContentIds.Workbench)
        {
            station.CraftJunction = StructurePlacement.WorkbenchJunction(world, station.Tile, anchor, 0f);
        }
        else
        {
            var rim = new List<JunctionId>();
            var definition = world.Content.ObjectDefinitions[station.DefinitionId];
            SpatialQueries.CollectStandableAround(world, anchor, rim, 96,
                InteractionReach.ForObject(definition.ObstacleRadius), station, InteractionReach.RimMode);
            station.CraftJunction = rim.First(id => SpatialQueries.IsJunctionFree(world, id));
        }
        Assert.That(station.CraftJunction.HasValue, Is.True);
        if (actor.CurrentJunction is { } previous) SpatialMutations.FreeJunction(world, previous, actor.Id);
        var workpoint = world.Junctions.Items[station.CraftJunction!.Value];
        actor.CurrentJunction = workpoint.Id;
        actor.Position = workpoint.WorldPosition;
        SpatialMutations.OccupyJunction(world, workpoint.Id, actor.Id);
    }

    private static void Butcher(SimulationEngine engine, NPCState actor, string variant)
    {
        var world = engine.World;
        ExecutionSystem.SpawnCarcass(world, actor.Tile, actor.CurrentJunction!.Value, variant);
        var corpse = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.CarcassAnimal);
        Apply(engine, new InteractCommand(actor.Id, corpse.Id, InteractionType.Butcher));
        Until(engine, () => !world.Entities.Objects.ContainsKey(corpse.Id));
    }

    private static void Apply(SimulationEngine engine, ISimulationCommand command)
    {
        var admission = engine.ApplyManualCommand(command);
        Assert.That(admission.Accepted, Is.True, admission.Reason);
    }

    private static void Until(SimulationEngine engine, Func<bool> predicate)
    {
        for (var tick = 0; tick < 600 && !predicate(); tick++) engine.Step();
        Assert.That(predicate(), Is.True, () => string.Join("\n",
            engine.World.Events.Items.TakeLast(8).Select(e => e.Message)));
    }

    private static (SimulationEngine Engine, NPCState Actor) Fixture()
    {
        var bootstrap = new WorldBootstrapDefinition();
        var fragment = new FragmentBootstrap { Id = 1 };
        fragment.Tiles.Add(new TileBootstrap { Q = 0, R = 0 });
        bootstrap.Fragments.Add(fragment);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        world.Seed = 629209070;
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        var at = world.Tiles.Items[TileCoord.Zero].Junctions
            .Select(id => world.Junctions.Items[id]).Single(node => node.WorldPosition.Equals(Float2.Zero));
        var actor = new NPCState
        {
            Id = new EntityId(1000000026), Faction = Faction.Colony, Tile = TileCoord.Zero,
            CurrentJunction = at.Id, Fragment = at.Fragment, Position = at.WorldPosition,
        };
        actor.Mind.ManualControl = true;
        actor.Inventory.Capacity = 24;
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
        PopulationArrivalMath.AddToWorld(world, actor, at.Id);
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PlanningSystem());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        engine.Register(new FireSystem());
        return (engine, actor);
    }
}
