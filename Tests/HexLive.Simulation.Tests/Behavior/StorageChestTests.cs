using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
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
    public void ChestIsABuildMenuFurnitureWithTheCollectorFootprintAndNoHandCraft()
    {
        Assert.That(BuildCatalogDefinition.TryGet(ContentIds.StorageChest, out var entry), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(entry.Mode, Is.EqualTo(BuildCatalogMode.Furniture));
            Assert.That(entry.PlacementKind, Is.EqualTo(BuildCatalogPlacementKind.Furniture));
            Assert.That(entry.CategoryId, Is.EqualTo(BuildCatalogCategories.Storage));
            Assert.That(entry.RequiresCompletedFloor, Is.False);
            Assert.That(RecipeCatalog.ByOutput.ContainsKey(ContentIds.StorageChest), Is.False);
            Assert.That(Runtime.Blueprints.BlueprintFurnitureFootprints.LocalOffsets(ContentIds.StorageChest),
                Is.EqualTo(Runtime.Blueprints.BlueprintFurnitureFootprints.LocalOffsets(ContentIds.WaterCollector)));
            // #732: three backpacks' worth of cells, measured from the shipped packs.
            foreach (var pack in new[] { "gear.backpack_riot", "gear.backpack_osiris" })
                Assert.That(Create().Engine.World.Content.ObjectDefinitions[pack].InventoryCapacity,
                    Is.EqualTo(StorageChest.BackpackCells));
            Assert.That(StorageChest.Capacity, Is.EqualTo(3 * StorageChest.BackpackCells));
            // 40 % of the 1.046 wu water collector, between knee and waist.
            Assert.That(StorageChest.ModelHeight, Is.EqualTo(.4f * 1.046f).Within(.01f));
        });
    }

    [Test]
    public void BuildMenuOrderRaisesAnEmptyClosedChestFromSticksBoardsAndRope()
    {
        var engine = TestWorld.CreateEngine(732);
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        Apply(engine, new SetManualControlCommand(npc.Id, true));
        npc.Needs.Hunger = npc.Needs.Thirst = 0f;
        npc.Needs.Stamina = npc.Needs.Energy = 1f;
        npc.Inventory.Items.Clear();
        npc.Inventory.Capacity = 24;
        world.Mobs.Clear();
        engine.Step();

        var anchor = world.Junctions.Items.Values.First(j => j.Tiles.Count == 1 &&
            j.Id != npc.CurrentJunction && j.Fragment == npc.Fragment &&
            HexSpatialMath.HexDistance(npc.Tile, j.Tiles[0]) <= 2 &&
            SpatialQueries.IsJunctionFree(world, j.Id) &&
            FurniturePlacement.Validate(world, ContentIds.StorageChest, j, 1) is null &&
            Connectivity.Reachable(world, npc.CurrentJunction!.Value, j.Id));
        var key = FurniturePlacement.KeyOf(anchor.WorldPosition);
        // The exact command the build menu (HutLayoutDesigner) sends.
        Apply(engine, new PlaceFurnitureCommand(ContentIds.StorageChest, key.XKey, key.YKey, 1));
        var site = FurniturePlacement.FindAt(world, ContentIds.StorageChest, key);
        Assert.That(site, Is.Not.Null);
        Assert.That(site.DefinitionId, Is.EqualTo(ContentIds.BuildSite));
        Assert.That(BuildSiteMath.NeedsHammer(world, site), Is.False);
        Assert.That(new[] { ContentIds.Stick, ContentIds.Board, ContentIds.Rope }
                .Select(id => BuildSiteMath.Bill(site, id)),
            Is.EqualTo(new[] { StorageChest.BillSticks, StorageChest.BillBoards, StorageChest.BillRope }));
        Assert.That(BuildSiteMath.Bill(site, ContentIds.Log) + BuildSiteMath.Bill(site, ContentIds.Stone) +
                    BuildSiteMath.Bill(site, ContentIds.PalmLeaf), Is.Zero);

        var materials = Enumerable.Repeat(ContentIds.Stick, StorageChest.BillSticks)
            .Concat(Enumerable.Repeat(ContentIds.Board, StorageChest.BillBoards))
            .Concat(Enumerable.Repeat(ContentIds.Rope, StorageChest.BillRope))
            .Select(id => new ItemInstance(id)).ToArray();
        npc.Inventory.Items.AddRange(materials);
        for (var order = 0; order < 30 && world.Entities.Objects.ContainsKey(site.Id); order++)
        {
            Apply(engine, new InteractCommand(npc.Id, site.Id, InteractionType.Build));
            for (var i = 0; i < 700 && world.Entities.Objects.ContainsKey(site.Id) &&
                            npc.Plan.Status == PlanStatus.Active; i++) engine.Step();
            engine.Step();
        }

        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False, "the site was never raised");
        var chest = FurniturePlacement.FindAt(world, ContentIds.StorageChest, key);
        Assert.That(chest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(chest.DefinitionId, Is.EqualTo(ContentIds.StorageChest));
            Assert.That(materials.Any(item => InventoryMath.ContainsReference(npc.Inventory.Items, item)), Is.False);
            // §120.10: the bill is consumed; a finished chest is not a pile of sticks.
            Assert.That(chest.Contents, Is.Empty);
            Assert.That(StorageChest.IsOpen(chest.Variant), Is.False);
            Assert.That(chest.OwnerFaction, Is.EqualTo(npc.Faction));
            Assert.That(chest.RotationDegrees, Is.EqualTo(60f));
            Assert.That(ContainerLootMath.IsLootable(world, chest), Is.True);
            Assert.That(ContainerLootMath.Capacity(world, chest), Is.EqualTo(StorageChest.Capacity));
            Assert.That(chest.BlockedJunctions.Select(id => FurniturePlacement.KeyOf(world.Junctions.Items[id].WorldPosition)),
                Is.EquivalentTo(FurniturePlacement.FootprintKeys(ContentIds.StorageChest, key, 1)));
        });
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
        Assert.That(ContainerLootMath.Capacity(world, chest), Is.EqualTo(StorageChest.Capacity));
        const string pack = "gear.backpack_riot";
        Assert.That(world.Content.ObjectDefinitions[pack].InventoryCapacity, Is.EqualTo(StorageChest.BackpackCells));
        // #732: three backpacks' worth — every cell holds one non-stackable pack.
        var packs = Enumerable.Range(1, StorageChest.Capacity + 1)
            .Select(i => new ItemInstance(pack) { Durability = .01f * i }).ToArray();
        actor.Inventory.Capacity = packs.Length + 2;
        actor.Inventory.Items.AddRange(packs);
        for (var i = 0; i < StorageChest.Capacity; i++)
        {
            Apply(engine, new TransferContainerCommand(actor.Id, chest.Id, 0, pack, 1, InventoryTransferDirection.Give));
            Finish(engine, actor);
        }

        Assert.That(chest.Contents, Is.EqualTo(packs.Take(StorageChest.Capacity)));
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.True);
        var extra = packs[StorageChest.Capacity];
        Assert.That(ContainerLootMath.CanAccept(world, chest, new[] { extra }), Is.False);
        // A 28th pack is refused at admission or left in hand by the transfer.
        engine.ApplyManualCommand(new TransferContainerCommand(
            actor.Id, chest.Id, 0, pack, 1, InventoryTransferDirection.Give));
        for (var tick = 0; tick < 220 && actor.Plan.Status == PlanStatus.Active; tick++) engine.Step();
        Assert.That(chest.Contents.Count, Is.EqualTo(StorageChest.Capacity));
        Assert.That(InventoryMath.ContainsReference(actor.Inventory.Items, extra), Is.True);
        InventoryMath.RemoveReference(actor.Inventory.Items, extra);
        Assert.That(PortableFurniture.PickupRefusal(world, actor, chest), Is.EqualTo("Occupied"));

        Apply(engine, new InteractCommand(actor.Id, chest.Id, InteractionType.SwitchChestLid,
            StorageChest.CloseInteractionId));
        Finish(engine, actor);
        Assert.That(StorageChest.IsOpen(chest.Variant), Is.False);
        Apply(engine, new TransferContainerCommand(actor.Id, chest.Id, 1, pack, 1, InventoryTransferDirection.Take));
        Finish(engine, actor);
        Assert.That(actor.Inventory.Items.Last(), Is.SameAs(packs[1]));
        var kept = packs.Take(StorageChest.Capacity).Where((_, index) => index != 1).ToArray();
        Assert.That(chest.Contents, Is.EqualTo(kept));
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
        Assert.That(restoredChest.Contents.Select(item => item.Durability),
            Is.EqualTo(kept.Select(item => item.Durability)));
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
