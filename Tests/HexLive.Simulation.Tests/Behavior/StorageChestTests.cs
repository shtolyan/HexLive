using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class StorageChestTests
{
    [OneTimeSetUp]
    public void LoadShippedContent() => SimDataFile.Require(RepoPaths.SimData);

    [Test]
    public void HandCraftConsumesItsBillAndCreatesOnePortableChest()
    {
        var (engine, actor) = Create();
        var option = CraftingOptions.Resolve(engine.World, actor, ContentIds.StorageChest);
        Assert.That(option.Ingredients.Select(i => (i.DefinitionId, i.Required)),
            Is.EquivalentTo(new[] { (ContentIds.Board, 6), (ContentIds.Stick, 4), (ContentIds.Rope, 2) }));
        foreach (var ingredient in option.Ingredients)
            for (var i = 0; i < ingredient.Required; i++)
                actor.Inventory.Items.Add(new ItemInstance(ingredient.DefinitionId));

        Assert.That(CraftingOptions.Resolve(engine.World, actor, ContentIds.StorageChest).CanCraft, Is.True);
        Apply(engine, new CraftByOutputCommand(actor.Id, ContentIds.StorageChest));
        Finish(engine, actor);
        Assert.That(actor.Inventory.Items.Single().DefinitionId, Is.EqualTo(ContentIds.StorageChest));
    }

    [Test]
    public void EmptyChestsPlaceOnSeparateBarePointsOnOneHex()
    {
        var (engine, actor) = Create();
        var world = engine.World;
        var tile = world.Tiles.Items[TileCoord.Zero];
        Assert.That(tile.Flags.HasFlag(TileFlags.HasFloor), Is.False);
        var north = Point(world, 0, 2);
        var south = Point(world, 0, -2);
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.StorageChest) { Durability = .45f });
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.StorageChest));
        var placement = new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.StorageChest);

        Apply(engine, new PlaceInventoryFurnitureCommand(actor.Id, placement, north, TileCoord.Zero, 118f));
        var first = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.StorageChest);
        Assert.That(first.Durability, Is.EqualTo(.45f));
        Assert.That(first.RotationDegrees, Is.EqualTo(120f));
        Assert.That(engine.ApplyManualCommand(new PlaceInventoryFurnitureCommand(
            actor.Id, placement, north, TileCoord.Zero, 0f)).Reason, Is.EqualTo("PlacementBlocked"));

        Apply(engine, new PlaceInventoryFurnitureCommand(actor.Id, placement, south, TileCoord.Zero, 0f));
        Assert.That(world.Entities.Objects.Values.Where(obj => obj.DefinitionId == ContentIds.StorageChest)
            .Select(obj => obj.Junctions.Single()), Is.EquivalentTo(new[] { north, south }));
        Assert.That(actor.Inventory.Items, Is.Empty);
    }

    [Test]
    public void PhysicalContainerOrdersKeepThreeBackpacksAndPersistTheLid()
    {
        var (engine, actor) = Create();
        var world = engine.World;
        var chest = WorldObjectMutations.SpawnObject(world, ContentIds.StorageChest, actor.Fragment,
            TileCoord.Zero, Point(world, 0, 2));
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.False);
        Assert.That(ContainerLootMath.IsLootable(world, chest), Is.True);
        Assert.That(ContainerLootMath.Capacity(world, chest), Is.EqualTo(3));
        const string pack = "gear.backpack_riot";
        Assert.That(world.Content.ObjectDefinitions[pack].InventoryCapacity, Is.GreaterThan(0));
        var packs = Enumerable.Range(1, 4).Select(i => new ItemInstance(pack) { Durability = .10f * i }).ToArray();
        actor.Inventory.Items.AddRange(packs);
        for (var i = 0; i < 3; i++)
        {
            Apply(engine, new TransferContainerCommand(actor.Id, chest.Id, 0, pack, 1, InventoryTransferDirection.Give));
            Finish(engine, actor);
        }

        Assert.That(chest.Contents, Is.EqualTo(packs.Take(3)));
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.True);
        Assert.That(ContainerLootMath.CanAccept(world, chest, new[] { packs[3] }), Is.False);
        Assert.That(PortableFurniture.PickupRefusal(world, actor, chest), Is.EqualTo("Occupied"));

        Apply(engine, new InteractCommand(actor.Id, chest.Id, InteractionType.SwitchChestLid,
            StorageChest.CloseInteractionId));
        Finish(engine, actor);
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.False);
        Apply(engine, new TransferContainerCommand(actor.Id, chest.Id, 1, pack, 1, InventoryTransferDirection.Take));
        Finish(engine, actor);
        Assert.That(actor.Inventory.Items.Last(), Is.SameAs(packs[1]));
        Assert.That(chest.Contents, Is.EqualTo(new[] { packs[0], packs[2] }));
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.True);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WorldSaveSerializer.Write(world, writer);
        writer.Flush();
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var restored = Create().Engine.World;
        WorldSaveSerializer.Read(restored, reader);
        var restoredChest = restored.Entities.Objects[chest.Id];
        Assert.That(StorageChest.IsOpen(restoredChest.Variant), Is.True);
        Assert.That(restoredChest.Contents.Select(item => item.Durability), Is.EqualTo(new[] { .10f, .30f }));
    }

    private static void Apply(SimulationEngine engine, ISimulationCommand command)
    {
        var admission = engine.ApplyManualCommand(command);
        Assert.That(admission.Accepted, Is.True, admission.Reason);
    }

    private static void Finish(SimulationEngine engine, NPCState actor)
    {
        for (var tick = 0; tick < 220 && actor.Plan.Status == PlanStatus.Active; tick++)
            engine.Step();
        Assert.That(actor.Plan.Status, Is.EqualTo(PlanStatus.Completed), () =>
            string.Join("\n", engine.World.Events.Items.Where(e => e.EntityId == actor.Id.Value)
                .TakeLast(10).Select(e => e.Message)));
    }

    private static JunctionId Point(WorldState world, int q, int r)
    {
        var offset = HexPointLayout.ToLocalOffset(new AxialPoint(q, r));
        return world.Tiles.Items[TileCoord.Zero].Junctions
            .Single(id => HexSpatialMath.Distance(world.Junctions.Items[id].WorldPosition, offset) < .001f);
    }

    private static (SimulationEngine Engine, NPCState Actor) Create()
    {
        var bootstrap = new WorldBootstrapDefinition();
        var island = new FragmentBootstrap { Id = 1 };
        island.Tiles.Add(new TileBootstrap { Q = 0, R = 0 });
        bootstrap.Fragments.Add(island);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        var at = Point(world, 0, 0);
        var junction = world.Junctions.Items[at];
        var actor = new NPCState
        {
            Id = new EntityId(1), Faction = Faction.Colony, Tile = TileCoord.Zero,
            CurrentJunction = at, Fragment = junction.Fragment, Position = junction.WorldPosition,
        };
        actor.Mind.ManualControl = true;
        actor.Inventory.Capacity = 24;
        PopulationArrivalMath.AddToWorld(world, actor, at);
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());
        return (engine, actor);
    }
}
