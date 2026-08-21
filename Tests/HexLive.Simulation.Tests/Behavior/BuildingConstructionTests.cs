using System;
using System.Linq;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class BuildingConstructionTests
{
    [TestCase(ContentIds.BedBasic, "", 14)]
    [TestCase(ContentIds.Campfire, BuildingRules.HutHearthVariant, 1)]
    [TestCase(ContentIds.Wardrobe, "", 3)]
    public void ConstructorRaisedIndoorFurnitureUsesAuthoredFootprint(
        string definitionId, string variant, int expectedBlocked)
    {
        var world = TestWorld.CreateWorld(12345);
        var source = world.Entities.Objects.Values.First(obj =>
            obj.DefinitionId == definitionId &&
            (definitionId != ContentIds.Campfire || obj.Variant == variant));
        var tile = source.Tile;
        var anchor = source.Junctions[0];
        var yaw = source.RotationDegrees;
        WorldObjectMutations.DespawnObject(world, source.Id);

        var raised = WorldObjectMutations.SpawnObject(
            world, definitionId, source.Fragment, tile, anchor);
        raised.Variant = variant;
        raised.RotationDegrees = yaw;
        ExecutionSystem.ApplyIndoorFurnitureFootprint(world, raised);

        Assert.That(raised.BlockedJunctions, Has.Count.EqualTo(expectedBlocked));
        Assert.That(raised.BlockedJunctions.All(id => world.Junctions.Items[id].Blocked), Is.True);
        Assert.That(raised.BlockedJunctions.Any(id => world.Junctions.Items[id].Door), Is.False,
            "Authoring footprint не имеет права перекрывать portal-junction.");
    }

    // §120.3 r2: стартовый дом прототипа — конструкторное plan-здание
    // (чертёж Hut1Hex из реестра §120.8); hut_1hex остался только как путь
    // совместимости старых сейвов и проверяется отдельным legacy-тестом.
    private static WorldObjectState StartHut(WorldState world) =>
        world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.HutPlan &&
            string.IsNullOrEmpty(obj.BuildProduct));

    [Test]
    public void CanonicalHutFurnitureBlocksGeometryAndKeepsDoorApproachesOpen()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = StartHut(world);
        var furniture = world.Caches.ObjectsByTile[hut.Tile]
            .Select(id => world.Entities.Objects[id]).ToArray();
        var beds = furniture.Where(obj => obj.DefinitionId == ContentIds.BedBasic).ToArray();
        var hearth = furniture.Single(obj => obj.DefinitionId == ContentIds.Campfire &&
            obj.Variant == BuildingRules.HutHearthVariant);
        var wardrobe = furniture.Single(obj => obj.DefinitionId == ContentIds.Wardrobe);

        Assert.That(beds, Has.Length.EqualTo(2));
        Assert.That(beds.All(bed => bed.BlockedJunctions.Count == 14), Is.True);
        Assert.That(hearth.BlockedJunctions, Has.Count.EqualTo(1));
        Assert.That(wardrobe.BlockedJunctions, Has.Count.EqualTo(3));
        Assert.That(furniture.SelectMany(obj => obj.BlockedJunctions)
            .Any(id => world.Junctions.Items[id].Door), Is.False);

        var start = StructurePlacement.CenterJunction(world, world.FactionHomes[Faction.Colony]);
        Assert.That(start, Is.Not.Null);
        foreach (var target in beds.Append(hearth).Append(wardrobe))
        {
            Assert.That(Connectivity.ReachableBeside(
                    world, start.Value, target.Junctions[0], owner: target),
                Is.True, $"Дверной коридор не ведёт к {target.DefinitionId}.");
        }
    }

    [Test]
    public void CatalogPublishesExactlyOneBedType()
    {
        var world = TestWorld.CreateWorld(12345);
        var beds = world.Content.ObjectDefinitions.Values
            .Where(definition => definition.Tags.Contains(ObjectTags.Bed))
            .Select(definition => definition.Id)
            .ToArray();

        Assert.That(beds, Is.EqualTo(new[] { ContentIds.BedBasic }));
    }

    [TestCase(ContentIds.BedLeaf, "")]
    [TestCase(ContentIds.HutBed, ContentIds.HutBedVariant)]
    public void LegacyBedIdsLoadAsTheCanonicalBed(string legacyId, string expectedVariant)
    {
        var world = TestWorld.CreateWorld(12345);
        var bed = world.Entities.Objects.Values.First(obj => obj.DefinitionId == ContentIds.BedBasic);
        bed.DefinitionId = legacyId;
        bed.Variant = string.Empty;
        bed.RotationDegrees = 31f;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var migrated = loaded.Entities.Objects[bed.Id];
        Assert.That(migrated.DefinitionId, Is.EqualTo(ContentIds.BedBasic));
        Assert.That(migrated.Variant, Is.EqualTo(expectedVariant));
        Assert.That(migrated.RotationDegrees, Is.EqualTo(60f).Within(0.001f));
    }

    [Test]
    public void HutBuildsAsIndependentModulesAndRoofUnlocksAtHalfSupports()
    {
        var site = NewHutSite();
        BuildingRules.EnsureHutElements(site);

        Assert.That(site.ArchitectureElements, Has.Count.EqualTo(30));
        Assert.That(site.ArchitectureElements.Select(element => element.ElementId).Distinct().Count(),
            Is.EqualTo(30));
        Assert.That(site.ArchitectureElements.Select(element => element.SlotKey).Distinct().Count(),
            Is.EqualTo(30));
        Assert.That(site.ArchitectureElements.All(element =>
            element.Layer == PlacementLayer.Architecture), Is.True);

        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks),
            Is.EqualTo(BuildingRules.TotalSticks));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialBoards),
            Is.EqualTo(BuildingRules.TotalBoards));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialLeaves), Is.False);
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialRope),
            Is.EqualTo(BuildingRules.TotalRope));

        var previousSupports = 0;
        while (!BuildingRules.RoofUnlocked(site.Id.Value,
                   BuildSiteMath.Delivered(site, ContentIds.Stick), 0, 0))
        {
            Deliver(site, ContentIds.Stick, 1);
            var supports = BuildingRules.CompletedSupports(site.Id.Value,
                BuildSiteMath.Delivered(site, ContentIds.Stick), 0, 0);
            Assert.That(supports, Is.GreaterThanOrEqualTo(previousSupports));
            previousSupports = supports;
            if (supports < BuildingRules.RequiredSupportsForRoof(BuildingRules.SupportCount))
                Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialLeaves), Is.False);
        }

        Assert.That(previousSupports, Is.EqualTo(3));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialLeaves),
            Is.EqualTo(BuildingRules.RoofLeaves));

        Deliver(site, ContentIds.Stick,
            BuildingRules.TotalSticks - BuildSiteMath.Delivered(site, ContentIds.Stick));
        Deliver(site, ContentIds.Board, BuildingRules.TotalBoards);
        Deliver(site, ContentIds.Rope, BuildingRules.TotalRope);
        Deliver(site, ContentIds.PalmLeaf, BuildingRules.RoofLeaves);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);

        var elements = BuildingRules.ResolveHutElements(site.Id.Value,
            BuildingRules.TotalSticks, BuildingRules.TotalBoards,
            BuildingRules.TotalRope, BuildingRules.TotalLeaves);
        Assert.That(elements, Has.Count.EqualTo(30));
        Assert.That(elements.All(element => element.Complete), Is.True);
        Assert.That(elements.Count(element => element.Kind == BuildingElementKind.Roof), Is.EqualTo(6));
    }

    [Test]
    public void FloorCompletesBeforeWallsAndRoof()
    {
        var site = NewHutSite();
        BuildingRules.EnsureHutElements(site);
        Assert.That(BuildingRules.FloorComplete(site), Is.False);

        foreach (var element in site.ArchitectureElements
                     .Where(element => element.DefinitionId == "architecture.floor.board"))
        {
            element.DeliveredBoards = element.RequiredBoards;
            element.WorkDone = element.WorkRequired;
        }

        Assert.That(BuildingRules.FloorComplete(site), Is.True);
        Assert.That(site.ArchitectureElements
            .Where(element => element.DefinitionId == "architecture.roof.palm")
            .All(element => !element.Complete), Is.True,
            "Трава должна исчезнуть после настила, не дожидаясь крыши.");
    }

    [Test]
    public void ArchitectureUsesItsOwnPlacementLayerAndRejectsOnlyDuplicateSlots()
    {
        var site = NewHutSite();
        BuildingRules.EnsureHutElements(site);
        var existing = site.ArchitectureElements[0];
        var duplicate = existing.Clone();
        duplicate.ElementId = 999;

        Assert.That(ArchitecturePlacementRules.CanPlace(site, duplicate), Is.False);
        Assert.That(ArchitecturePlacementRules.ConflictsWithFurniture(existing), Is.False,
            "Кровать и архитектура могут занимать один гекс: их overlap-правила независимы.");

        duplicate.SlotKey = "custom.slot";
        Assert.That(ArchitecturePlacementRules.CanPlace(site, duplicate), Is.True);
    }

    [Test]
    public void ArchitectureElementsSurviveSaveLoadAsIndependentState()
    {
        const int seed = 12345;
        var world = TestWorld.CreateWorld(seed);
        var hut = StartHut(world);
        var pieces = BuildingRules.ArchitectureObjects(world, hut).ToArray();
        Assert.That(hut.ArchitectureElements, Is.Empty,
            "Footprint aggregate must not contain selectable/renderable LEGO state.");
        // §120.3 r2: число модулей — свойство ЧЕРТЕЖА (у Hut1Hex 36: контур
        // идёт секциями по 0.5 wu), а не константа канонического кита.
        var moduleCount = Runtime.Blueprints.BlueprintBuildingPlan.Modules(
            world.PlayerBlueprints[hut.BlueprintId]).Count;
        Assert.That(pieces, Has.Length.EqualTo(moduleCount));
        Assert.That(pieces.Select(piece => piece.Id).Distinct().Count(), Is.EqualTo(moduleCount));
        var piece = pieces[7];
        var element = piece.ArchitectureElements.Single();
        var canonicalYaw = element.LocalYaw;
        element.DeliveredBoards = 0;
        element.WorkDone = 0;
        element.LocalYaw += 7f;

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);

        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);

        var loadedHut = loaded.Entities.Objects[hut.Id];
        Assert.That(loadedHut.ArchitectureElements, Is.Empty);
        var loadedPieces = BuildingRules.ArchitectureObjects(loaded, loadedHut).ToArray();
        Assert.That(loadedPieces, Has.Length.EqualTo(moduleCount));
        Assert.That(loaded.Entities.Objects.ContainsKey(piece.Id), Is.True,
            "Each LEGO piece keeps its own ObjectId through save/load.");
        var loadedPiece = loaded.Entities.Objects[piece.Id];
        Assert.That(loadedPiece.ArchitectureOwnerId, Is.EqualTo(loadedHut.Id));
        var loadedElement = loadedPiece.ArchitectureElements.Single();
        Assert.That(loadedElement.SlotKey, Is.EqualTo(element.SlotKey));
        Assert.That(loadedElement.Layer, Is.EqualTo(PlacementLayer.Architecture));
        Assert.That(loadedElement.DeliveredBoards, Is.Zero);
        Assert.That(loadedElement.WorkDone, Is.Zero);
        Assert.That(loadedElement.LocalYaw, Is.EqualTo(canonicalYaw).Within(0.001f),
            "Blueprint geometry is catalog data; even a current-version broken save must not preserve drifted slots.");
    }

    [Test]
    public void RaisedPlayerHouseKeepsItsExactBlueprint_Bug188()
    {
        var world = TestWorld.CreateWorld(18801);
        var draft = BuiltInBuildingBlueprints.Hut1Hex();
        var blueprintId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[blueprintId] = draft;
        var tile = FindPlanTile(world, draft);
        var site = BuildingBootstrap.CreatePlayerBlueprintSite(
            world, tile, rotationDegrees: 0f, blueprintId);
        Assert.That(site, Is.Not.Null);

        var bill = BlueprintBuildingPlan.Bill(BlueprintBuildingPlan.Modules(draft));
        Deliver(site, ContentIds.Stick, bill.Sticks);
        Deliver(site, ContentIds.Board, bill.Boards);
        Deliver(site, ContentIds.Rope, bill.Rope);
        Deliver(site, ContentIds.PalmLeaf, bill.Leaves);
        BuildingRules.SyncHutElements(world, site);
        Assert.That(BuildingRules.Elements(world, site).All(element => element.Complete), Is.True,
            "Распределение материалов обязано читать чертёж площадки, а не committed fallback.");

        var raised = ExecutionSystem.RaiseFurnitureSite(
            world, site, site.Fragment, site.Junctions[0]);
        Assert.That(raised, Is.Not.Null);
        Assert.That(raised.BlueprintId, Is.EqualTo(blueprintId),
            "build.site → building.hut_plan не имеет права терять instance BlueprintId.");
        Assert.That(BuildingRules.PlanFor(world, raised), Is.SameAs(draft));
        Assert.That(BuildingRules.Elements(world, raised).Select(element => element.SlotKey),
            Is.EquivalentTo(BlueprintBuildingPlan.Modules(draft).Select(module => module.Key)));

        var furnitureProducts = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.BuildSite &&
                          BuildingBootstrap.FootprintTiles(world, raised).Contains(obj.Tile))
            .Select(obj => obj.BuildProduct).Where(product => !string.IsNullOrEmpty(product))
            .ToArray();
        Assert.That(furnitureProducts, Has.Length.EqualTo(draft.Furniture.Count),
            "Мебель должна стейкаться из того же one-hex blueprint, что и стены.");
    }

    [Test]
    public void LoadRepairsCompletedHouseWhoseShellAndFurnitureUsedDifferentPlans_Bug188()
    {
        const int seed = 18802;
        var world = TestWorld.CreateWorld(seed);
        var stale = BuiltInBuildingBlueprints.Hut1Hex();
        var staleId = world.NextPlayerBlueprintId++;
        world.PlayerBlueprints[staleId] = stale;
        var tile = FindPlanTile(world, CommittedBuildingPlans.PlayerHut);
        var site = BuildingBootstrap.CreatePlayerBlueprintSite(world, tile, 0f, staleId);
        Assert.That(site, Is.Not.Null);

        // Exact old failure: the site had already raised its 36 one-hex pieces,
        // then the replacement owner lost BlueprintId before CompleteHut.  Its
        // furniture therefore came from the 51-module committed three-hex plan.
        site.BlueprintId = 0;
        var broken = ExecutionSystem.RaiseFurnitureSite(
            world, site, site.Fragment, site.Junctions[0]);
        Assert.That(BuildingRules.Elements(world, broken).ToArray(), Has.Length.EqualTo(36));
        Assert.That(BlueprintBuildingPlan.Modules(BuildingRules.PlanFor(world, broken)),
            Has.Count.EqualTo(51));

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(seed);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);

        var repaired = loaded.Entities.Objects[broken.Id];
        var expected = BlueprintBuildingPlan.Modules(BuildingRules.PlanFor(loaded, repaired));
        Assert.That(BuildingRules.Elements(loaded, repaired).Select(element => element.SlotKey),
            Is.EquivalentTo(expected.Select(module => module.Key)),
            "Load repair обязан добавить отсутствующие half-hex sectors и убрать старые bays.");
        Assert.That(BuildingRules.Elements(loaded, repaired).All(element => element.Complete), Is.True);
        Assert.That(BuildingBootstrap.FootprintTiles(loaded, repaired), Has.Count.EqualTo(3));
    }

    [Test]
    public void PrototypeStartsWithOneUsableHutTwoCotsAndAColdHearth()
    {
        var world = TestWorld.CreateWorld(12345);
        // §120.3 r2: стартовый дом — конструкторное plan-здание с чертежом
        // Hut1Hex из реестра §120.8, а не авторский FBX-кит hut_1hex.
        var huts = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.HutPlan &&
                          string.IsNullOrEmpty(obj.BuildProduct)).ToArray();
        Assert.That(huts, Has.Length.EqualTo(1));

        var hut = huts[0];
        Assert.That(hut.BlueprintId, Is.Not.Zero,
            "Стартовый дом обязан ссылаться на свой чертёж в реестре мира.");
        Assert.That(world.PlayerBlueprints.ContainsKey(hut.BlueprintId), Is.True);
        var symmetry = hut.RotationDegrees % 60f;
        if (symmetry < 0f) symmetry += 60f;
        Assert.That(symmetry % 60f, Is.EqualTo(0f).Within(0.001f),
            "Архитектурный pointy-top гекс допускает только шесть поворотов.");

        // Мебель чертежа поднята при рождении — недостроенных площадок нет.
        Assert.That(world.Entities.Objects.Values.Any(obj =>
                obj.DefinitionId == ContentIds.BuildSite && obj.Tile.Equals(hut.Tile)),
            Is.False, "Стартовый дом рождается обжитым: площадки мебели подняты.");

        var cots = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.BedBasic && obj.Tile.Equals(hut.Tile))
            .ToArray();
        Assert.That(cots, Has.Length.EqualTo(2));
        Assert.That(cots.Select(c => c.Junctions[0]).Distinct().Count(), Is.EqualTo(2));

        var hearths = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Campfire &&
                obj.Variant == BuildingRules.HutHearthVariant && obj.Tile.Equals(hut.Tile))
            .ToArray();
        Assert.That(hearths, Has.Length.EqualTo(1));
        Assert.That(hearths[0].ResourceAmount, Is.Zero,
            "Домашний очаг должен начинаться холодным: топливо остаётся игровым ресурсом.");

        // Голова спящей ближе к огню, чем ноги (§66) — ориентация кроватей
        // авторская из чертежа и повторяет прежний кит (+180°/+240°).
        var hearthPosition = world.Junctions.Items[hearths[0].Junctions[0]].WorldPosition;
        foreach (var cot in cots)
        {
            var bedPosition = world.Junctions.Items[cot.Junctions[0]].WorldPosition;
            var heading = cot.RotationDegrees * MathF.PI / 180f;
            var headDirection = new Float2(MathF.Sin(heading), -MathF.Cos(heading));
            var towardFire = hearthPosition - bedPosition;
            Assert.That(headDirection.X * towardFire.X + headDirection.Y * towardFire.Y,
                Is.GreaterThan(0f), "Unity sleep-head обязан быть ближе к очагу, чем ноги.");
        }

        Assert.That(world.Entities.Objects.Values.Count(obj =>
                obj.DefinitionId == ContentIds.Wardrobe && obj.Tile.Equals(hut.Tile)),
            Is.EqualTo(1));

        var tile = world.Tiles.Items[hut.Tile];
        Assert.That(tile.Flags.HasFlag(TileFlags.HasFloor), Is.True);
        Assert.That(tile.Flags.HasFlag(TileFlags.Indoor), Is.True,
            "Indoor появляется только у полностью завершённой листовой крыши.");

        // Контур запечатан посекционно (§120.6): каждый пограничный junction —
        // либо единственный дверной портал, либо закрыт стеной.
        var boundary = tile.Junctions
            .Select(id => world.Junctions.Items[id])
            .Where(junction => junction.Tiles.Count >= 2).ToArray();
        Assert.That(boundary.Count(junction => junction.Door), Is.EqualTo(1));
        Assert.That(boundary.Where(junction => junction.Door).All(junction => !junction.Blocked),
            Is.True);
        Assert.That(boundary.All(junction => junction.Door || junction.Blocked), Is.True);
        Assert.That(hut.BlockedJunctions, Is.Empty,
            "Footprint aggregate must not own pathfinding: individual LEGO pieces do.");

        // Дверь смотрит наружу через видимый проём, а не через чужую грань.
        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var visibleDoorRadians = BuildingRules.DoorOutwardYaw(world, hut) * MathF.PI / 180f;
        var visibleDoorOutward = new Float2(
            MathF.Cos(visibleDoorRadians), MathF.Sin(visibleDoorRadians));
        var doorJunctions = boundary.Where(junction => junction.Door).ToArray();
        var doorCenter = new Float2(
            doorJunctions.Average(junction => junction.WorldPosition.X),
            doorJunctions.Average(junction => junction.WorldPosition.Y));
        var portalDelta = doorCenter - center;
        var portalLength = MathF.Sqrt(portalDelta.X * portalDelta.X + portalDelta.Y * portalDelta.Y);
        Assert.That((portalDelta.X * visibleDoorOutward.X + portalDelta.Y * visibleDoorOutward.Y) /
                    portalLength, Is.GreaterThan(0.95f),
            "Portal-edge должен совпадать с видимым дверным проёмом.");
    }

    [Test]
    public void LegacyHut1HexKitStillCompletesWithIntegratedCots()
    {
        // Путь совместимости старых сейвов: канонический FBX-кит hut_1hex
        // по-прежнему достраивается со встроенными кроватями и чинит их якоря.
        var world = TestWorld.CreateWorld(12345);
        var spot = world.Tiles.Items.Keys
            .Where(coord => BuildingBootstrap.CanPlaceHut(world, coord))
            .OrderBy(coord => coord.Q).ThenBy(coord => coord.R).First();
        var anchor = StructurePlacement.CenterJunction(world, spot).Value;
        var hut = WorldObjectMutations.SpawnObject(
            world, ContentIds.Hut1Hex, world.Junctions.Items[anchor].Fragment, spot, anchor);
        BuildingRules.EnsureHutElements(world, hut, completed: true);
        hut.RotationDegrees = 360f;
        BuildingBootstrap.CompleteHut(world, hut);

        var cots = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant && obj.Tile.Equals(spot))
            .ToArray();
        Assert.That(cots, Has.Length.EqualTo(2));
        Assert.That(cots.Select(c => c.Junctions[0]).Distinct().Count(), Is.EqualTo(2));

        // Repair переживает сейв, где оба легаси-якоря слиплись в один узел.
        var collapsed = cots[0].Junctions[0];
        cots[1].Junctions.Clear();
        cots[1].Junctions.Add(collapsed);
        BuildingBootstrap.RepairIntegratedCotAnchors(world);
        Assert.That(cots.Select(c => c.Junctions[0]).Distinct().Count(), Is.EqualTo(2));
    }
    [Test]
    public void FreshPrototypeHutSeedsSixDistinctDeterministicWardrobeGarments()
    {
        var first = TestWorld.CreateWorld(12345);
        var second = TestWorld.CreateWorld(12345);
        var firstWardrobe = first.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.Wardrobe);
        var secondWardrobe = second.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.Wardrobe);

        var firstGarments = first.Entities.Objects.Values
            .Where(obj => obj.Tile.Equals(firstWardrobe.Tile) &&
                          obj.Junctions.Count == 1 &&
                          obj.Junctions[0].Equals(firstWardrobe.Junctions[0]) &&
                          first.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                          definition.Tags.Contains("Clothing"))
            .OrderBy(obj => obj.Id.Value)
            .ToArray();
        var secondGarments = second.Entities.Objects.Values
            .Where(obj => obj.Tile.Equals(secondWardrobe.Tile) &&
                          obj.Junctions.Count == 1 &&
                          obj.Junctions[0].Equals(secondWardrobe.Junctions[0]) &&
                          second.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                          definition.Tags.Contains("Clothing"))
            .OrderBy(obj => obj.Id.Value)
            .ToArray();

        Assert.That(firstGarments, Has.Length.EqualTo(6));
        Assert.That(firstGarments.Select(obj => obj.DefinitionId).Distinct().Count(), Is.EqualTo(6));
        Assert.That(firstGarments.All(obj => obj.BlockedJunctions.Count == 0), Is.True,
            "Стартовая одежда висит на гардеробе и не запирает проход в хижине.");
        Assert.That(secondGarments.Select(obj => obj.DefinitionId),
            Is.EqualTo(firstGarments.Select(obj => obj.DefinitionId)),
            "Одна и та же seed-новая игра должна давать один и тот же набор вещей.");
    }

    [Test]
    public void FreshPrototypeWardrobeAlwaysIncludesPantsBootsAndProtectiveClothing()
    {
        for (var seed = 1; seed <= 100; seed++)
        {
            var world = TestWorld.CreateWorld(seed);
            var wardrobe = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.Wardrobe);
            var garments = world.Entities.Objects.Values
                .Where(obj => obj.Tile.Equals(wardrobe.Tile) &&
                              obj.Junctions.Count == 1 &&
                              obj.Junctions[0].Equals(wardrobe.Junctions[0]) &&
                              world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
                              definition.Tags.Contains("Clothing"))
                .ToArray();

            var footwearCount = 0;
            var bootCount = 0;
            var pantsCount = 0;
            var protectiveCount = 0;
            foreach (var objectState in garments)
            {
                var garment = GarmentLibrary.Active.Single(candidate => candidate.Id == objectState.DefinitionId);
                Assert.That(BuildingBootstrap.IsStarterWardrobeGarment(garment, out var isFootwear), Is.True,
                    $"{objectState.DefinitionId} is outside the starter wardrobe policy.");
                if (isFootwear) footwearCount++;
                if (BuildingBootstrap.IsStarterWardrobeBoots(garment)) bootCount++;
                if (BuildingBootstrap.IsStarterWardrobePants(garment)) pantsCount++;
                if (BuildingBootstrap.IsStarterWardrobeProtective(garment)) protectiveCount++;
            }

            Assert.Multiple(() =>
            {
                Assert.That(garments, Has.Length.EqualTo(6));
                Assert.That(footwearCount, Is.EqualTo(1),
                    "The starter wardrobe must contain exactly one footwear item.");
                Assert.That(bootCount, Is.EqualTo(1),
                    "The guaranteed footwear must be actual boots, not heels or sandals.");
                Assert.That(pantsCount, Is.GreaterThanOrEqualTo(1),
                    "The starter wardrobe must contain real four-pocket pants, not only a skirt.");
                Assert.That(protectiveCount, Is.GreaterThanOrEqualTo(1),
                    "The starter wardrobe must contain one non-leg piece with armor >= 0.10.");
            });
        }
    }

    [Test]
    public void WardrobeStorageCategoriesRequireTheWholeWearSlotShape()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GarmentStorageCategories.CategoryFor("clothing.sandals_summer1"),
                Is.EqualTo(GarmentCategory.Footwear));
            Assert.That(GarmentStorageCategories.CategoryFor("clothing.gloves_cindy"),
                Is.EqualTo(GarmentCategory.Gloves));
            Assert.That(GarmentStorageCategories.CategoryFor("clothing.outfit_reiko"),
                Is.EqualTo(GarmentCategory.Outfit),
                "Цельный наряд не является обувью из-за наличия ступней.");
            Assert.That(GarmentStorageCategories.CategoryFor("clothing.armguards_fighter"),
                Is.EqualTo(GarmentCategory.Armwear),
                "Наручи не являются перчатками.");
        });
    }

    [Test]
    public void EveryDefaultGarmentHasAnExplicitSemanticCategory()
    {
        Assert.That(GarmentLibrary.Defaults.All(garment =>
            garment.Category != GarmentCategory.Unclassified), Is.True);
    }

    [Test]
    public void DoorApiClosesOnePortalReopensItAndPersistsTheState()
    {
        var world = TestWorld.CreateWorld(12345);
        var door = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == "architecture.door.wood");
        Assert.That(door.Junctions, Has.Count.EqualTo(1));
        var portalId = door.Junctions[0];
        var topology = world.TopologyVersion;
        var doorState = world.DoorStateVersion;

        // §129: закрытая дверь — поведение, не топология. Портал остаётся
        // проходимым для графа (Blocked=false), меняется только DoorStateVersion.
        Assert.That(BuildingDoorRules.TryClose(world, door.Id), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(door.IsDoorOpen, Is.False);
            Assert.That(world.Junctions.Items[portalId].Door, Is.True);
            Assert.That(world.Junctions.Items[portalId].Blocked, Is.False);
            Assert.That(world.TopologyVersion, Is.EqualTo(topology),
                "Створка не имеет права дёргать топологию (§129).");
            Assert.That(world.DoorStateVersion, Is.EqualTo(doorState + 1));
            Assert.That(DoorTopology.IsClosedDoorPortal(world, portalId), Is.True);
        });

        using var blob = new MemoryStream();
        using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Write(world, writer);
        blob.Position = 0;
        var loaded = TestWorld.CreateWorld(12345);
        using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, leaveOpen: true))
            WorldSaveSerializer.Read(loaded, reader);
        var loadedDoor = loaded.Entities.Objects[door.Id];
        Assert.Multiple(() =>
        {
            Assert.That(loadedDoor.IsDoorOpen, Is.False);
            Assert.That(loadedDoor.Junctions, Has.Count.EqualTo(1));
            Assert.That(loaded.Junctions.Items[loadedDoor.Junctions[0]].Blocked, Is.False,
                "RepairHutTopology на загрузке обязан оставить портал проходимым (§129).");
            Assert.That(DoorTopology.IsClosedDoorPortal(
                loaded, loadedDoor.Junctions[0]), Is.True);
        });

        Assert.That(BuildingDoorRules.TryOpen(loaded, loadedDoor.Id), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(loadedDoor.IsDoorOpen, Is.True);
            Assert.That(loaded.Junctions.Items[loadedDoor.Junctions[0]].Door, Is.True);
            Assert.That(loaded.Junctions.Items[loadedDoor.Junctions[0]].Blocked, Is.False);
            Assert.That(DoorTopology.IsClosedDoorPortal(
                loaded, loadedDoor.Junctions[0]), Is.False);
        });
    }

    [TestCase(0f, 0f)]
    [TestCase(29f, 0f)]
    [TestCase(31f, 60f)]
    [TestCase(89f, 60f)]
    [TestCase(91f, 120f)]
    [TestCase(181f, 180f)]
    [TestCase(329f, 300f)]
    [TestCase(331f, 0f)]
    public void HutYawSnapsToPointyTopHexSymmetries(float requested, float expected)
    {
        Assert.That(StructurePlacement.QuantizeHexSymmetryYaw(requested),
            Is.EqualTo(expected).Within(0.001f));
        Assert.That(StructurePlacement.QuantizeHexYaw(requested),
            Is.EqualTo(expected).Within(0.001f),
            "Мебель должна использовать те же шесть поворотов, что и гекс.");
    }

    [TestCase(0f)]
    [TestCase(60f)]
    [TestCase(120f)]
    [TestCase(180f)]
    [TestCase(240f)]
    [TestCase(300f)]
    public void DoorElementSelectsItsOwnPortalAtEveryHexRotation(float yaw)
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = StartHut(world);
        hut.RotationDegrees = yaw;
        BuildingBootstrap.RepairPlanTopology(world, hut);

        var tile = world.Tiles.Items[hut.Tile];
        var portals = tile.Junctions.Select(id => world.Junctions.Items[id])
            .Where(junction => junction.Door).ToArray();
        Assert.That(portals, Has.Length.EqualTo(1),
            "§120: door bay has exactly one junction portal; its other edge nodes stay blocked.");

        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var portalCenter = portals[0].WorldPosition;
        var portalDelta = HexSpatialMath.Normalize(portalCenter - center);
        var doorRadians = BuildingRules.DoorOutwardYaw(world, hut) * MathF.PI / 180f;
        var doorDirection = new Float2(MathF.Cos(doorRadians), MathF.Sin(doorRadians));
        Assert.That(portalDelta.X * doorDirection.X + portalDelta.Y * doorDirection.Y,
            Is.GreaterThan(0.95f),
            $"door.0 при yaw={yaw}° обязан открывать junction своей видимой грани.");
    }

    [Test]
    public void CompletedRoofStopsRainForBodyClothesCarriedAndLooseMaterials()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = StartHut(world);
        var outdoor = FindDryOutdoorTile(world, hut.Tile);
        var npc = world.Entities.Npcs.Values.First();
        npc.Tile = hut.Tile;
        npc.BodyWetness = 0f;
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        npc.WornItems.Add(new ItemInstance(ContentIds.Coat));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.Board));

        var insideBoard = SpawnPickup(world, ContentIds.Board, hut.Tile);
        var outsideBoard = SpawnPickup(world, ContentIds.Board, outdoor);
        world.Environment.IsRaining = true;

        new MoistureSystem().Run(world);

        Assert.That(npc.BodyWetness, Is.Zero);
        Assert.That(npc.WornItems.Single().Wetness, Is.Zero);
        Assert.That(npc.Inventory.Items.Single().Wetness, Is.Zero);
        Assert.That(insideBoard.Wetness, Is.Zero);
        Assert.That(outsideBoard.Wetness, Is.EqualTo(1f));

        npc.Tile = outdoor;
        new MoistureSystem().Run(world);
        Assert.That(npc.BodyWetness, Is.EqualTo(1f));
        Assert.That(npc.WornItems.Single().Wetness, Is.EqualTo(1f));
        Assert.That(npc.Inventory.Items.Single().Wetness, Is.EqualTo(1f));
    }

    [Test]
    public void IndoorHearthIgnoresRainAndBurnsHalfAsFastAsBestOutdoorFire()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = StartHut(world);
        var hearth = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.Campfire &&
            obj.Variant == BuildingRules.HutHearthVariant);
        var outdoor = new WorldObjectState
        {
            DefinitionId = ContentIds.Campfire,
            Tile = FindDryOutdoorTile(world, hut.Tile)
        };
        Deliver(outdoor, ContentIds.Stone, SimBalance.CampfireBillStones);

        world.Environment.IsRaining = false;
        var bestOutdoorBurn = FireSystem.FuelBurnPerSlowTick(world, outdoor);
        var indoorDryBurn = FireSystem.FuelBurnPerSlowTick(world, hearth);
        world.Environment.IsRaining = true;
        var indoorRainBurn = FireSystem.FuelBurnPerSlowTick(world, hearth);
        var outdoorRainBurn = FireSystem.FuelBurnPerSlowTick(world, outdoor);

        Assert.That(indoorDryBurn,
            Is.EqualTo(bestOutdoorBurn * SimBalance.IndoorFireBurnMultiplier).Within(0.0001f));
        Assert.That(indoorRainBurn, Is.EqualTo(indoorDryBurn).Within(0.0001f));
        Assert.That(outdoorRainBurn, Is.EqualTo(bestOutdoorBurn * 4f).Within(0.0001f));
    }

    [Test]
    public void IndoorHearthHeatFadesAcrossConnectedRoomAndNeverWarmsTheStreet()
    {
        var world = TestWorld.CreateWorld(12345);
        foreach (var fire in world.Entities.Objects.Values.Where(obj =>
                     obj.DefinitionId == ContentIds.Campfire))
        {
            fire.ResourceAmount = 0f;
        }

        var hearth = world.Entities.Objects.Values.Single(obj =>
            obj.DefinitionId == ContentIds.Campfire &&
            obj.Variant == BuildingRules.HutHearthVariant);
        hearth.ResourceAmount = 100f;

        var own = hearth.Tile;
        var adjacent = new TileCoord(own.Q + HexDirection.East.DQ, own.R + HexDirection.East.DR);
        var outer = new TileCoord(own.Q + 2 * HexDirection.East.DQ, own.R + 2 * HexDirection.East.DR);
        var beyond = new TileCoord(own.Q + 3 * HexDirection.East.DQ, own.R + 3 * HexDirection.East.DR);
        var street = new TileCoord(
            own.Q + HexDirection.SouthEast.DQ,
            own.R + HexDirection.SouthEast.DR);
        SetIndoor(world, own, true);
        SetIndoor(world, adjacent, true);
        SetIndoor(world, outer, true);
        SetIndoor(world, beyond, true);
        SetIndoor(world, street, false);

        Assert.That(TemperatureSystem.NearbyFireWarmth(world, own, out var onFire),
            Is.EqualTo(SimBalance.FireWarmthRange1).Within(0.0001f));
        Assert.That(onFire, Is.True);
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, adjacent, out _),
            Is.EqualTo(SimBalance.FireWarmthRange1 * 0.75f).Within(0.0001f));
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, outer, out _),
            Is.EqualTo(SimBalance.FireWarmthRange1 * 0.50f).Within(0.0001f));
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, beyond, out _), Is.Zero);
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, street, out _), Is.Zero,
            "An indoor hearth must not warm an adjacent outdoor tile.");

        SetIndoor(world, adjacent, false);
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, outer, out _), Is.Zero,
            "Heat must not jump across an outdoor gap between roofed tiles.");
    }

    [Test]
    public void OutdoorFireKeepsItsLegacyTwoRingHeatProfile()
    {
        var world = TestWorld.CreateWorld(12345);
        foreach (var candidate in world.Entities.Objects.Values.Where(obj =>
                     obj.DefinitionId == ContentIds.Campfire))
        {
            candidate.ResourceAmount = 0f;
        }

        var hut = StartHut(world);
        var fire = SpawnPickup(
            world, ContentIds.Campfire, FindDryOutdoorTile(world, hut.Tile));
        fire.ResourceAmount = 100f;

        var own = fire.Tile;
        var adjacent = new TileCoord(own.Q + HexDirection.East.DQ, own.R + HexDirection.East.DR);
        var outer = new TileCoord(own.Q + 2 * HexDirection.East.DQ, own.R + 2 * HexDirection.East.DR);
        SetIndoor(world, own, false);
        SetIndoor(world, adjacent, false);
        SetIndoor(world, outer, false);

        Assert.That(TemperatureSystem.NearbyFireWarmth(world, own, out var onFire),
            Is.EqualTo(SimBalance.FireWarmthRange1).Within(0.0001f));
        Assert.That(onFire, Is.True);
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, adjacent, out _),
            Is.EqualTo(SimBalance.FireWarmthRange1).Within(0.0001f));
        Assert.That(TemperatureSystem.NearbyFireWarmth(world, outer, out _),
            Is.EqualTo(SimBalance.FireWarmthRange2).Within(0.0001f));
    }

    private static WorldObjectState NewHutSite() => new()
    {
        DefinitionId = ContentIds.BuildSite,
        BuildProduct = ContentIds.Hut1Hex,
        BillSticks = BuildingRules.TotalSticks,
        BillBoards = BuildingRules.TotalBoards,
        BillRope = BuildingRules.TotalRope,
        BillLeaves = BuildingRules.TotalLeaves
    };

    private static TileCoord FindPlanTile(WorldState world, BuildingBlueprintDraft plan) =>
        world.Tiles.Items.Keys
            .OrderBy(tile => tile.Q).ThenBy(tile => tile.R)
            .First(tile => BuildingBootstrap.FootprintTiles(plan, tile, 0f)
                .All(footprint => BuildingBootstrap.CanPlaceHut(world, footprint)) &&
                StructurePlacement.CenterJunction(world, tile) is not null);

    private static void SetIndoor(WorldState world, TileCoord coord, bool indoor)
    {
        if (!world.Tiles.Items.TryGetValue(coord, out var tile))
        {
            tile = new Tile { Coord = coord, Flags = TileFlags.Walkable };
            world.Tiles.Items[coord] = tile;
        }

        tile.Flags = indoor
            ? tile.Flags | TileFlags.Indoor
            : tile.Flags & ~TileFlags.Indoor;
    }

    private static void Deliver(WorldObjectState site, string material, int count)
    {
        for (var i = 0; i < count; i++) site.Contents.Add(new ItemInstance(material));
    }

    private static TileCoord FindDryOutdoorTile(WorldState world, TileCoord hutTile)
    {
        return world.Tiles.Items
            .Where(pair => pair.Value.Flags.HasFlag(TileFlags.Walkable) &&
                !pair.Value.Flags.HasFlag(TileFlags.Water) &&
                !pair.Value.Flags.HasFlag(TileFlags.Indoor) &&
                HexSpatialMath.HexDistance(pair.Key, hutTile) >= 3 &&
                StructurePlacement.CenterJunction(world, pair.Key) is not null)
            .OrderBy(pair => pair.Key.Q)
            .ThenBy(pair => pair.Key.R)
            .Select(pair => pair.Key)
            .First();
    }

    private static WorldObjectState SpawnPickup(WorldState world, string definitionId, TileCoord tile)
    {
        var junction = StructurePlacement.CenterJunction(world, tile);
        Assert.That(junction, Is.Not.Null);
        return WorldObjectMutations.SpawnObject(
            world, definitionId, world.Junctions.Items[junction.Value].Fragment, tile, junction.Value);
    }
}

}
