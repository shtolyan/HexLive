using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wildlife;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PortalWorldTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(17)]
    public void LayoutRoomsAreConnectedWithoutConnectingToTheIsland(int seed)
    {
        var world = CreateWorld(seed);
        var tiles = PortalWorld.ApartmentTiles(seed).ToArray();
        var entry = StructurePlacement.CenterJunction(world, PortalWorld.ApartmentCenter).Value;
        var island = StructurePlacement.CenterJunction(world, TileCoord.Zero).Value;
        Assert.Multiple(() =>
        {
            Assert.That(tiles.Distinct().Count(), Is.EqualTo(13));
            Assert.That(tiles.Select(tile => PortalWorld.RoomAt(tile, seed)).Distinct().Count(), Is.EqualTo(3));
            Assert.That(tiles.All(tile => Connectivity.Reachable(world, entry,
                StructurePlacement.CenterJunction(world, tile).Value, true)), Is.True);
            Assert.That(Connectivity.Reachable(world, entry, island, true), Is.False);
            Assert.That(tiles.Select(tile => PortalWorld.RoomAt(tile, seed)).Distinct()
                .Select(room => PortalWorld.WallpaperIndex(seed, 2, room)), Is.EquivalentTo(new[] { 0, 1, 2 }));
        });

        var doorways = tiles.Where(tile => HexSpatialMath.HexDistance(tile, PortalWorld.ApartmentCenter) == 1 &&
            PortalWorld.RoomAt(tile, seed) != PortalApartmentRoom.Living &&
            !PortalWorld.HasWall(PortalWorld.ApartmentCenter, tile, seed)).ToArray();
        Assert.That(doorways.Length, Is.EqualTo(2));
        foreach (var tile in doorways)
        {
            var center = (HexSpatialMath.TileToWorld(PortalWorld.ApartmentCenter) + HexSpatialMath.TileToWorld(tile)) * .5f;
            var seam = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
                .Where(point => point.Tiles.Contains(PortalWorld.ApartmentCenter) && !point.Blocked).ToArray();
            Assert.That(seam, Has.Length.EqualTo(1));
            Assert.That(HexSpatialMath.Distance(seam[0].WorldPosition, center), Is.LessThan(.001f));
        }

        var exit = tiles.First(tile => PortalWorld.TryGetReturnDoorEdge(tile, seed, out _));
        var windows = tiles.Where(tile => PortalWorld.TryGetWindowEdge(tile, seed, exit, out _)).ToArray();
        Assert.That(windows.Select(tile => PortalWorld.RoomAt(tile, seed)).Distinct().Count(), Is.EqualTo(3));
        Assert.That(windows.All(tile => !PortalWorld.IsApartmentFurnitureTile(tile, seed)), Is.True);
    }

    [Test]
    public void SameRotationCanProduceDifferentApartmentShapes()
    {
        Assert.That(PortalWorld.ApartmentTiles(6), Is.Not.EquivalentTo(PortalWorld.ApartmentTiles(0)));
    }

    [Test]
    public void MidnightOpensAtSelectedCampAndReturnConsumesThatNight()
    {
        var world = CreateWorld();
        world.Portal.Camp = Faction.Colony2;
        world.Tick = WorldBalance.DayLengthTicks * 3 / 4 - 1;
        PortalWorld.Update(world);
        Assert.That(world.Portal.ExteriorDoor, Is.Null);

        world.Tick++;
        PortalWorld.Update(world);
        var outside = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var inside = world.Entities.Objects[world.Portal.InteriorDoor.Value];
        Assert.That(outside.Tile, Is.EqualTo(world.FactionHomes[Faction.Colony2]));
        Assert.That(PortalWorld.TryGetReturnDoorEdge(inside.Tile, world.Seed, out var exteriorNeighbor), Is.True);
        Assert.That(PortalWorld.RoomAt(exteriorNeighbor, world.Seed), Is.EqualTo(PortalApartmentRoom.None));
        Assert.That(PortalWorld.HasWall(inside.Tile, exteriorNeighbor, world.Seed), Is.True);
        var npc = AddVisitor(world, outside.Junctions[0], outside.Tile);
        Assert.That(PortalWorld.Travel(world, npc, outside), Is.Null);
        Assert.That(world.Portal.Visitor, Is.EqualTo(npc.Id));
        Assert.That(npc.Tile, Is.Not.EqualTo(inside.Tile));
        Assert.That(Connectivity.Reachable(world, npc.CurrentJunction.Value, inside.Junctions[0], true), Is.True);

        MoveToDoor(world, npc, inside);
        Assert.That(PortalWorld.Travel(world, npc, inside), Is.Null);
        Assert.That(npc.Fragment, Is.Not.EqualTo(PortalWorld.ApartmentFragment));
        Assert.That(world.Entities.Objects.ContainsKey(outside.Id), Is.False);
        Assert.That(world.Entities.Objects.ContainsKey(inside.Id), Is.False);
        PortalWorld.Update(world);
        Assert.That(world.Portal.ExteriorDoor, Is.Null);
        world.Tick += WorldBalance.DayLengthTicks;
        PortalWorld.Update(world);
        Assert.That(world.Portal.ExteriorDoor, Is.Not.Null);
    }

    [TestCase(0)]
    [TestCase(7)]
    public void ApartmentFurnitureUsesAuthoredWallsAndFacesAcrossTheLivingRoom(int seed)
    {
        var world = CreateWorld(seed);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var furniture = world.Entities.Objects.Values.Where(obj => obj.Variant == PortalWorld.ApartmentFurnitureVariant)
            .ToDictionary(obj => obj.DefinitionId);
        Assert.That(furniture.Count, Is.EqualTo(7));
        Assert.That(furniture.Values.Select(obj => obj.Tile).Distinct().Count(), Is.EqualTo(6));

        (WorldObjectState Object, Float2 Position, float Yaw) Find(string definitionId)
        {
            var obj = furniture[definitionId];
            Assert.That(PortalWorld.TryGetApartmentFurniturePlacement(definitionId, obj.Tile, seed,
                out var position, out var yaw), Is.True);
            Assert.That(obj.RotationDegrees, Is.EqualTo(yaw));
            return (obj, position, yaw);
        }

        var bed = Find("furniture.modern_bed");
        var fridge = Find("furniture.modern_fridge");
        var sofa = Find("furniture.modern_sofa");
        var tv = Find("furniture.modern_tv");
        var chair = Find("furniture.modern_chair");
        var table = Find("furniture.modern_table");
        var shelf = Find(PortalWorld.ApartmentShelfDefinitionId);
        Assert.That(PortalWorld.TryGetModernSeatFacing(sofa.Object.DefinitionId, sofa.Yaw, out var sofaFacing), Is.True);
        Assert.That(PortalWorld.TryGetModernSeatFacing(chair.Object.DefinitionId, chair.Yaw, out var chairFacing), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(PortalWorld.RoomAt(bed.Object.Tile, seed), Is.EqualTo(PortalApartmentRoom.Bedroom));
            Assert.That(PortalWorld.RoomAt(fridge.Object.Tile, seed), Is.EqualTo(PortalApartmentRoom.Kitchen));
            Assert.That(PortalWorld.RoomAt(chair.Object.Tile, seed), Is.EqualTo(PortalApartmentRoom.Kitchen));
            Assert.That(PortalWorld.RoomAt(table.Object.Tile, seed), Is.EqualTo(PortalApartmentRoom.Kitchen));
            Assert.That(PortalWorld.RoomAt(shelf.Object.Tile, seed), Is.EqualTo(PortalApartmentRoom.Living));
            Assert.That(chair.Object.Tile, Is.EqualTo(table.Object.Tile));
            Assert.That(HexSpatialMath.Distance(chair.Position, table.Position), Is.InRange(.9f, 1.1f));
            Assert.That(chair.Object.Junctions[0], Is.Not.EqualTo(table.Object.Junctions[0]));
            Assert.That(MathUtil.DeltaAngle(sofaFacing, HexSpatialMath.AngleDegrees(tv.Position - sofa.Position)),
                Is.EqualTo(0f).Within(.01f));
            Assert.That(MathUtil.DeltaAngle(chairFacing, HexSpatialMath.AngleDegrees(table.Position - chair.Position)),
                Is.EqualTo(0f).Within(.01f));
        });

        foreach (var item in new[] { bed, fridge, sofa, shelf, table })
        {
            var center = HexSpatialMath.TileToWorld(item.Object.Tile);
            var wallDirection = HexSpatialMath.Normalize(item.Position - center);
            var outside = HexSpatialMath.WorldToTile(center + wallDirection * (2f * HexSpatialMath.HexApothem));
            Assert.That(PortalWorld.RoomAt(outside, seed), Is.EqualTo(PortalApartmentRoom.None));
            Assert.That(PortalWorld.HasWall(item.Object.Tile, outside, seed), Is.True);
        }
        var tableBack = HexSpatialMath.AngleDegrees(table.Position - HexSpatialMath.TileToWorld(table.Object.Tile));
        Assert.That(System.MathF.Abs(MathUtil.DeltaAngle(tableBack, table.Yaw + 90f)), Is.EqualTo(90f).Within(.01f));
    }

    [Test]
    public void GeneratedTableAndRackLootHaveDistinctPersistentSlots()
    {
        var world = CreateWorld(7);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var table = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == "furniture.modern_table");
        var shelf = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == PortalWorld.ApartmentShelfDefinitionId);
        var restored = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        foreach (var support in new[] { table, shelf })
        {
            var objects = restored.Entities.Objects.Values.Where(obj => obj.SurfaceObjectId == support.Id).ToArray();
            Assert.That(objects.Length, Is.InRange(1, support == table ? 2 : 3));
            Assert.That(objects.Select(obj => obj.SurfaceSlot).Distinct().Count(), Is.EqualTo(objects.Length));
            Assert.That(objects.All(obj => obj.Tile == support.Tile && obj.DefinitionId != ContentIds.BaseballBat &&
                SurfacePlacementMath.TryGetSlot(support.DefinitionId, obj.SurfaceSlot, out _, out _)), Is.True);
        }
    }

    [Test]
    public void RefrigeratorStockIsContainerLootAndCanBeEatenOnTheIsland()
    {
        var original = CreateWorld(7);
        Assert.That(PortalWorld.OpenImmediately(original, Faction.Colony), Is.Null);
        var originalFridge = original.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.ModernFridge);
        var stock = originalFridge.Contents.Select(item => item.DefinitionId).ToArray();
        Assert.That(stock.Length, Is.InRange(2, 4));
        Assert.That(stock.Distinct().Count(), Is.EqualTo(stock.Length));
        Assert.That(stock.All(ModernFoodCatalog.IsFridgeFood), Is.True);
        Assert.That(original.Entities.Objects.Values.Any(obj => ModernFoodCatalog.IsFridgeFood(obj.DefinitionId)), Is.False);

        var world = RoundTrip(original, WorldSaveSerializer.BlobVersion);
        var fridge = world.Entities.Objects[originalFridge.Id];
        Assert.That(fridge.Contents.Select(item => item.DefinitionId), Is.EqualTo(stock));
        Assert.That(ContainerLootMath.IsLootable(world, fridge), Is.True);
        Assert.That(ContainerLootMath.Capacity(world, fridge), Is.EqualTo(6));
        Assert.That(ContainerLootMath.CanAccept(world, fridge, new[] { new ItemInstance(ContentIds.Stone) }), Is.False);

        var outside = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var inside = world.Entities.Objects[world.Portal.InteriorDoor.Value];
        var visitor = AddVisitor(world, outside.Junctions[0], outside.Tile);
        visitor.Mind.ManualControl = true;
        visitor.Inventory.Capacity = 8;
        Assert.That(PortalWorld.Travel(world, visitor, outside), Is.Null);
        MoveToDoor(world, visitor, fridge);
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new PlanningSystem());
        engine.Register(new ExecutionSystem());

        var itemId = stock[0];
        var loot = engine.ApplyManualCommand(new TransferContainerCommand(visitor.Id, fridge.Id, 0,
            itemId, 1, InventoryTransferDirection.Take));
        Assert.That(loot.Accepted, Is.True, loot.Reason);
        for (int tick = 0; tick < 100 && visitor.Inventory.FindFirstFood(world.Content) == null; tick++)
            engine.Step();
        Assert.That(visitor.Inventory.FindFirstFood(world.Content), Is.EqualTo(itemId));
        Assert.That(fridge.Contents.Select(item => item.DefinitionId), Is.EqualTo(stock.Skip(1)));

        MoveToDoor(world, visitor, inside);
        Assert.That(PortalWorld.Travel(world, visitor, inside), Is.Null);
        visitor.Needs.Hunger = .8f;
        var eat = engine.ApplyManualCommand(new SelfActionCommand(visitor.Id, SelfActionKind.EatFromPack));
        Assert.That(eat.Accepted, Is.True, eat.Reason);
        for (int tick = 0; tick < 100 && visitor.Inventory.FindFirstFood(world.Content) != null; tick++)
            engine.Step();
        Assert.That(visitor.Inventory.FindFirstFood(world.Content), Is.Null);
        Assert.That(visitor.Needs.Hunger, Is.LessThan(.7f));
    }

    [Test]
    public void RefrigeratorVariesAcrossVisitsAndAnEmptyOneCanStillBeMoved()
    {
        var world = CreateWorld(6);
        var sampled = new HashSet<string>();
        for (int night = 1; night <= 12; night++)
        {
            world.Tick = night * WorldBalance.DayLengthTicks;
            Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
            var fridge = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.ModernFridge);
            sampled.UnionWith(fridge.Contents.Select(item => item.DefinitionId));
        }
        Assert.That(sampled, Is.EquivalentTo(new[]
        {
            ContentIds.StrawberryYogurt, ContentIds.CheeseWedge, ContentIds.ChocolateBar,
            ContentIds.HamSandwich, ContentIds.Orange, ContentIds.CannedPeaches,
        }));

        var empty = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.ModernFridge);
        var outside = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var visitor = AddVisitor(world, outside.Junctions[0], outside.Tile);
        visitor.Mind.ManualControl = true;
        Assert.That(PortableFurniture.PickupRefusal(world, visitor, empty), Is.EqualTo("Occupied"));
        empty.Contents.Clear();
        Assert.That(ContainerLootMath.IsLootable(world, empty), Is.True);
        Assert.That(PortableFurniture.PickupRefusal(world, visitor, empty), Is.Null);
    }

    [Test]
    public void HouseholdClothingIsAvailableAsLootAndWornByAnApartmentResident()
    {
        var world = CreateWorld();
        var clothing = new[] { "clothing.shorts_classic", "clothing.halter_vamp", "clothing.croptop_fit" };
        Assert.That(clothing.All(id => GarmentLibrary.IsSpawnable(id) && world.Content.ObjectDefinitions.ContainsKey(id)), Is.True);
        var foundLoot = false;
        NPCState resident = null;
        for (var day = 0; day < 12; day++)
        {
            world.Tick = day * WorldBalance.DayLengthTicks + WorldBalance.DayLengthTicks * 3 / 4;
            PortalWorld.Update(world);
            foundLoot |= world.Entities.Objects.Values.Any(obj =>
                obj.Fragment == PortalWorld.ApartmentFragment && clothing.Contains(obj.DefinitionId));
            if (world.Portal.Resident is { } id) resident = world.Entities.Npcs[id];
            if (foundLoot && resident != null) break;
        }
        Assert.That(foundLoot, Is.True);
        Assert.That(resident, Is.Not.Null);
        Assert.That(resident.WornItems.Select(item => item.DefinitionId),
            Does.Contain("clothing.shorts_classic").And.Contain("clothing.halter_vamp"));
        Assert.That(resident.Inventory.Items, Is.Empty);
    }

    [Test]
    public void RandomExteriorAvoidsPredatorRangeAndKeepsAPathToACamp()
    {
        var world = CreateWorld();
        var predatorTile = new TileCoord(4, 0);
        world.Mobs.Add(new MobState { Id = 23, Tile = predatorTile,
            Junction = StructurePlacement.CenterJunction(world, predatorTile).Value });
        var unsafeTiles = PopulationArrivalMath.UnsafeHumanLandingTiles(world);
        Assert.That(unsafeTiles, Does.Contain(predatorTile));
        world.Portal.Mode = PortalSpawnMode.Random;
        world.Tick = WorldBalance.DayLengthTicks * 3 / 4;
        PortalWorld.Update(world);
        var door = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        Assert.That(unsafeTiles, Does.Not.Contain(door.Tile));
        Assert.That(Connectivity.Reachable(world,
            StructurePlacement.CenterJunction(world, world.FactionHomes[Faction.Colony]).Value,
            door.Junctions[0], true), Is.True);
    }

    [Test]
    public void ActiveVisitSurvivesSaveAndReturnCarriesResidentIntoCamp()
    {
        var world = CreateWorld();
        world.Tick = WorldBalance.DayLengthTicks * 3 / 4;
        PortalWorld.Update(world);
        var exterior = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var visitor = AddVisitor(world, exterior.Junctions[0], exterior.Tile);
        Assert.That(PortalWorld.Travel(world, visitor, exterior), Is.Null);

        world.Portal.Mode = PortalSpawnMode.Random;
        var reloaded = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        Assert.That(reloaded.Portal.Mode, Is.EqualTo(PortalSpawnMode.Random));
        Assert.That(reloaded.Portal.Visitor, Is.EqualTo(visitor.Id));
        Assert.That(reloaded.Portal.GeneratedObjects, Is.EquivalentTo(world.Portal.GeneratedObjects));
        visitor = reloaded.Entities.Npcs[visitor.Id];
        var exit = reloaded.Entities.Objects[reloaded.Portal.InteriorDoor.Value];

        var resident = new NPCState
        {
            Id = new EntityId(900),
            ProfileId = PortalWorld.ResidentProfileId,
            Faction = Faction.Castaway,
            Fragment = PortalWorld.ApartmentFragment,
            Tile = visitor.Tile,
            Position = visitor.Position,
            CarriedByNpcId = visitor.Id,
        };
        reloaded.Entities.Npcs.Add(resident.Id, resident);
        reloaded.Portal.Resident = resident.Id;
        visitor.CarriedNpcId = resident.Id;
        MoveToDoor(reloaded, visitor, exit);
        Assert.That(PortalWorld.Travel(reloaded, visitor, exit), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(resident.Faction, Is.EqualTo(visitor.Faction));
            Assert.That(resident.Tile, Is.EqualTo(visitor.Tile));
            Assert.That(resident.Fragment, Is.EqualTo(visitor.Fragment));
            Assert.That(reloaded.Portal.Resident, Is.Null);
            Assert.That(reloaded.Portal.ExteriorDoor, Is.Null);
        });
    }

    [Test]
    public void PreviousSaveKeepsIslandJunctionIdsAndWaitsForTheNextMidnight()
    {
        var previous = CreateWorld(portal: false);
        previous.Tick = WorldBalance.DayLengthTicks * 3 / 4 + 10;
        var center = StructurePlacement.CenterJunction(previous, TileCoord.Zero);
        var migrated = RoundTrip(previous, 90);
        Assert.That(StructurePlacement.CenterJunction(migrated, TileCoord.Zero), Is.EqualTo(center));
        Assert.That(migrated.Fragments.Items[PortalWorld.ApartmentFragment].Tiles.Count, Is.EqualTo(13));
        PortalWorld.Update(migrated);
        Assert.That(migrated.Portal.ExteriorDoor, Is.Null);
        migrated.Tick = WorldBalance.DayLengthTicks + WorldBalance.DayLengthTicks * 3 / 4;
        PortalWorld.Update(migrated);
        Assert.That(migrated.Portal.ExteriorDoor, Is.Not.Null);
    }

    [Test]
    public void OperatorOpeningWorksByDayButCannotReplaceAnActiveVisit()
    {
        var world = CreateWorld();
        world.Portal.Mode = PortalSpawnMode.Disabled;
        Assert.That(PortalWorld.TryParseCamp("1", out var first), Is.True);
        Assert.That(first, Is.EqualTo(Faction.Colony));
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony2), Is.Null);
        PortalWorld.Update(world);
        Assert.That(world.Portal.OperatorOpened, Is.True);
        var doorway = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var visitor = AddVisitor(world, doorway.Junctions[0], doorway.Tile);
        Assert.That(PortalWorld.Travel(world, visitor, doorway), Is.Null);
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.EqualTo("PortalOccupied"));
        Assert.That(world.Portal.ExteriorDoor, Is.EqualTo(doorway.Id));
    }

    [Test]
    public void ManualInteractionRunsThroughTheOrdinaryExecutionSystem()
    {
        var world = CreateWorld();
        Assert.That(PortalWorld.OpenImmediately(world, Faction.Colony), Is.Null);
        var doorway = world.Entities.Objects[world.Portal.ExteriorDoor.Value];
        var visitor = AddVisitor(world, doorway.Junctions[0], doorway.Tile);
        visitor.Mind.ManualControl = true;
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new ExecutionSystem());
        var admission = engine.ApplyManualCommand(new InteractCommand(visitor.Id, doorway.Id,
            InteractionType.TravelPortal, PortalWorld.TravelInteractionId));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        for (var i = 0; i < 10 && world.Portal.Visitor == null; i++) engine.Step();
        Assert.That(world.Portal.Visitor, Is.EqualTo(visitor.Id));
        Assert.That(doorway.IsOccupied, Is.False);
    }

    [Test]
    public void PlacementConsumesOneCampItemAndKeepsItsCondition()
    {
        const string product = "furniture.modern_chair";
        var world = CreateWorld();
        var sourceTile = TileCoord.Zero;
        var floor = new TileCoord(1, 0);
        world.Tiles.Items[floor].Flags |= TileFlags.HasFloor;
        var actor = AddVisitor(world, StructurePlacement.CenterJunction(world, sourceTile).Value, sourceTile);
        actor.Inventory.Items.Add(new ItemInstance(product) { Durability = .35f, OwnerId = actor.Id.Value });
        var permitted = new[] { actor.Faction };
        Assert.That(PortalWorld.PlaceFurniture(world, product, floor, 60f, permitted), Is.Null);
        var placed = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == product);
        Assert.Multiple(() =>
        {
            Assert.That(actor.Inventory.Items, Is.Empty);
            Assert.That(placed.Variant, Is.EqualTo(PortalWorld.PlacedFurnitureVariant));
            Assert.That(placed.Durability, Is.EqualTo(.35f));
            Assert.That(placed.OwnerFaction, Is.EqualTo(actor.Faction));
            Assert.That(PortalWorld.PlaceFurniture(world, product, floor, 60f, permitted), Is.EqualTo("HexOccupied"));
        });
    }

    [Test]
    public void ManualPickupAndExactPlacementMoveOneForeignStoolWithoutLosingCondition()
    {
        const string stool = "chair.basic";
        var world = CreateWorld();
        world.Mode = GameMode.HugeIsland;
        var origin = world.FactionHomes[Faction.Colony2];
        var originPoint = StructurePlacement.CenterJunction(world, origin).Value;
        var old = WorldObjectMutations.SpawnObject(world, stool, world.Junctions.Items[originPoint].Fragment, origin, originPoint);
        old.OwnerFaction = Faction.Colony2;
        old.Durability = .31f;
        old.Wetness = .22f;
        var actor = AddVisitor(world, originPoint, origin);
        actor.Mind.ManualControl = true;
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Register(new PathfindingSystem());
        engine.Register(new MovementSystem());
        engine.Register(new ExecutionSystem());

        old.IsOccupied = true;
        old.CurrentUser = new EntityId(2);
        var pickup = new InteractCommand(actor.Id, old.Id, InteractionType.PickUp, "pickup.chair.basic");
        Assert.That(engine.ApplyManualCommand(pickup).Reason, Is.EqualTo("Occupied"));
        old.IsOccupied = false;
        old.CurrentUser = null;
        var receipt = engine.ApplyManualCommand(pickup);
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        for (var i = 0; i < 80 && world.Entities.Objects.ContainsKey(old.Id); i++) engine.Step();
        Assert.That(world.Entities.Objects.ContainsKey(old.Id), Is.False);
        Assert.That(actor.Inventory.Items.Single().DefinitionId, Is.EqualTo(stool));
        Assert.That(world.Events.Items.Count(item => item.Type == "ItemStolen"), Is.EqualTo(1));

        var target = new TileCoord(-3, 0);
        var center = StructurePlacement.CenterJunction(world, target).Value;
        MoveToPoint(world, actor, center, target);
        var point = world.Tiles.Items[target].Junctions.Select(id => world.Junctions.Items[id]).First(candidate =>
            HexSpatialMath.Distance(candidate.WorldPosition, actor.Position) is > .4f and < .8f &&
            PortableFurniture.FitsInsideTile(stool, target, candidate.WorldPosition));
        var item = new InventoryItemRef(InventoryItemSource.Carried, 0, stool);
        var placement = new PlaceInventoryFurnitureCommand(actor.Id, item, point.Id, target, 118f);
        Assert.That(engine.ApplyManualCommand(new ManageInventoryCommand(actor.Id, item, InventoryAction.Drop)).Reason,
            Is.EqualTo("FurnitureNeedsPlacement"));
        receipt = engine.ApplyManualCommand(placement);
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var placed = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == stool);
        Assert.Multiple(() =>
        {
            Assert.That(actor.Inventory.Items, Is.Empty);
            Assert.That(placed.Junctions.Single(), Is.EqualTo(point.Id));
            Assert.That(placed.RotationDegrees, Is.EqualTo(120f));
            Assert.That(placed.Durability, Is.EqualTo(.31f));
            Assert.That(placed.Wetness, Is.EqualTo(.22f));
            Assert.That(placed.OwnerFaction, Is.EqualTo(actor.Faction));
            Assert.That(StructurePlacement.TileHoldsStructure(world, target), Is.True);
            Assert.That(engine.ApplyManualCommand(placement).Reason, Is.EqualTo("StaleItem"));
        });
    }

    [Test]
    public void IntegratedBedCannotBeTakenIntoInventory()
    {
        var world = CreateWorld();
        var point = StructurePlacement.CenterJunction(world, TileCoord.Zero).Value;
        var actor = AddVisitor(world, point, TileCoord.Zero);
        actor.Mind.ManualControl = true;
        var home = new TileCoord(1, 0);
        var anchor = StructurePlacement.CenterJunction(world, home).Value;
        var bed = WorldObjectMutations.SpawnObject(world, ContentIds.BedBasic, actor.Fragment, home, anchor);
        bed.Variant = ContentIds.HutBedVariant;
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        var receipt = engine.ApplyManualCommand(new InteractCommand(actor.Id, bed.Id, InteractionType.PickUp, "pickup.bed.basic"));
        Assert.That(receipt.Reason, Is.EqualTo("FurnitureIntegrated"));
        Assert.That(world.Entities.Objects.ContainsKey(bed.Id), Is.True);
        Assert.That(actor.Inventory.Items, Is.Empty);
    }

    [Test]
    public void CarriedStandaloneBedPlacesOnFloorWithoutBecomingAnIntegratedCot()
    {
        var world = CreateWorld();
        world.FactionHomes.Remove(Faction.Colony2);
        var tile = world.Tiles.Items[TileCoord.Zero];
        tile.Flags |= TileFlags.HasFloor;
        var target = StructurePlacement.CenterJunction(world, tile.Coord).Value;
        var origin = world.Junctions.Items.Values.First(point => point.Fragment == world.Junctions.Items[target].Fragment && !point.Blocked &&
            HexSpatialMath.Distance(point.WorldPosition, HexSpatialMath.TileToWorld(tile.Coord)) is > 1.3f and < 1.6f);
        var actor = AddVisitor(world, origin.Id, origin.Tiles[0]);
        actor.Mind.ManualControl = true;
        actor.Inventory.Items.Add(new ItemInstance(ContentIds.BedBasic));
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        var reference = new InventoryItemRef(InventoryItemSource.Carried, 0, ContentIds.BedBasic);
        var receipt = engine.ApplyManualCommand(new PlaceInventoryFurnitureCommand(actor.Id, reference, target, tile.Coord, 60f));
        Assert.That(receipt.Accepted, Is.True, receipt.Reason);
        var bed = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.BedBasic);
        Assert.That(PortableFurniture.IsBuiltInBed(bed.DefinitionId, bed.Variant, hasFloor: true), Is.False);
        Assert.That(PortableFurniture.PickupRefusal(world, actor, bed), Is.Null);
        Assert.That(actor.Inventory.Items, Is.Empty);
    }

    private static WorldState CreateWorld(int seed = 0, bool portal = true)
    {
        var bootstrap = new WorldBootstrapDefinition();
        bootstrap.Simulation.Seed = seed;
        bootstrap.Simulation.EnablePortalTopology = portal;
        var island = new FragmentBootstrap { Id = 1 };
        for (var q = -4; q <= 4; q++)
        for (var r = -4; r <= 4; r++)
            if (HexSpatialMath.HexDistance(new TileCoord(q, r), TileCoord.Zero) <= 4)
                island.Tiles.Add(new TileBootstrap { Q = q, R = r });
        bootstrap.Fragments.Add(island);
        var world = new WorldStateFactory().CreateTopology(bootstrap);
        world.FactionHomes[Faction.Colony] = TileCoord.Zero;
        world.FactionHomes[Faction.Colony2] = new TileCoord(3, 0);
        return world;
    }

    private static NPCState AddVisitor(WorldState world, JunctionId at, TileCoord tile)
    {
        var junction = world.Junctions.Items[at];
        var npc = new NPCState
        {
            Id = new EntityId(1), Faction = Faction.Colony, Tile = tile,
            CurrentJunction = at, Fragment = junction.Fragment, Position = junction.WorldPosition,
        };
        PopulationArrivalMath.AddToWorld(world, npc, at);
        return npc;
    }

    private static void MoveToDoor(WorldState world, NPCState npc, WorldObjectState door)
        => MoveToPoint(world, npc, door.Junctions[0], door.Tile);

    private static void MoveToPoint(WorldState world, NPCState npc, JunctionId id, TileCoord tile)
    {
        var point = world.Junctions.Items[id];
        SpatialMutations.FreeJunction(world, npc.CurrentJunction.Value, npc.Id);
        SpatialMutations.MoveEntityToTile(world, npc.Id, npc.Tile, tile);
        SpatialMutations.OccupyJunction(world, point.Id, npc.Id);
        npc.CurrentJunction = point.Id;
        npc.Tile = tile;
        npc.Position = point.WorldPosition;
    }

    private static WorldState RoundTrip(WorldState world, int version)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        WorldSaveSerializer.WriteAtVersion(world, writer, version);
        writer.Flush();
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        var restored = CreateWorld(world.Seed);
        WorldSaveSerializer.Read(restored, reader);
        return restored;
    }
}

