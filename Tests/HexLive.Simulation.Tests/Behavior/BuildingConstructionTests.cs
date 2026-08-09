using System;
using System.Linq;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class BuildingConstructionTests
{
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
        var hut = world.Entities.Objects.Values.Single(obj => obj.DefinitionId == ContentIds.Hut1Hex);
        Assert.That(hut.ArchitectureElements, Has.Count.EqualTo(30));
        var element = hut.ArchitectureElements[7];
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
        Assert.That(loadedHut.ArchitectureElements, Has.Count.EqualTo(30));
        var loadedElement = loadedHut.ArchitectureElements.Single(e => e.ElementId == element.ElementId);
        Assert.That(loadedElement.SlotKey, Is.EqualTo(element.SlotKey));
        Assert.That(loadedElement.Layer, Is.EqualTo(PlacementLayer.Architecture));
        Assert.That(loadedElement.DeliveredBoards, Is.Zero);
        Assert.That(loadedElement.WorkDone, Is.Zero);
        Assert.That(loadedElement.LocalYaw, Is.EqualTo(element.LocalYaw).Within(0.001f));
    }

    [Test]
    public void PrototypeStartsWithOneUsableHutTwoCotsAndAColdHearth()
    {
        var world = TestWorld.CreateWorld(12345);
        var huts = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Hut1Hex).ToArray();
        Assert.That(huts, Has.Length.EqualTo(1));

        var hut = huts[0];
        var symmetry = hut.RotationDegrees % 60f;
        if (symmetry < 0f) symmetry += 60f;
        Assert.That(symmetry, Is.EqualTo(0f).Within(0.001f),
            "Архитектурный pointy-top гекс допускает только шесть поворотов; произвольный yaw снимает стены с рёбер.");
        var cots = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.BedBasic &&
                obj.Variant == ContentIds.HutBedVariant && obj.Tile.Equals(hut.Tile))
            .ToArray();
        Assert.That(cots, Has.Length.EqualTo(2));
        Assert.That(cots.Select(c => c.Junctions[0]).Distinct().Count(), Is.EqualTo(2));

        // Current-save migration must recover even when both legacy cot ids
        // were serialized on the same junction.
        var collapsed = cots[0].Junctions[0];
        cots[1].Junctions.Clear();
        cots[1].Junctions.Add(collapsed);
        BuildingBootstrap.RepairIntegratedCotAnchors(world);
        Assert.That(cots.Select(c => c.Junctions[0]).Distinct().Count(), Is.EqualTo(2));

        var center = HexSpatialMath.TileToWorld(hut.Tile);
        var radians = hut.RotationDegrees * MathF.PI / 180f;
        var right = new Float2(MathF.Sin(radians), -MathF.Cos(radians));
        var sides = cots.Select(cot =>
        {
            var position = world.Junctions.Items[cot.Junctions[0]].WorldPosition;
            var delta = position - center;
            return delta.X * right.X + delta.Y * right.Y;
        }).OrderBy(value => value).ToArray();
        Assert.That(sides[0], Is.LessThan(0f));
        Assert.That(sides[1], Is.GreaterThan(0f));

        var hearths = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Campfire &&
                obj.Variant == BuildingRules.HutHearthVariant && obj.Tile.Equals(hut.Tile))
            .ToArray();
        Assert.That(hearths, Has.Length.EqualTo(1));
        Assert.That(hearths[0].ResourceAmount, Is.Zero,
            "Домашний очаг должен начинаться холодным: топливо остаётся игровым ресурсом.");
        Assert.That(BuildSiteMath.CampfireSpitComplete(hearths[0]), Is.True);
        Assert.That(BuildSiteMath.CampfireRingComplete(hearths[0]), Is.True);
        Assert.That(hearths[0].BlockedJunctions, Has.Count.EqualTo(1),
            "Малый очаг закрывает только свой опорный узел, а не половину комнаты.");

        var tile = world.Tiles.Items[hut.Tile];
        Assert.That(tile.Flags.HasFlag(TileFlags.HasFloor), Is.True);
        Assert.That(tile.Flags.HasFlag(TileFlags.Indoor), Is.True,
            "Indoor появляется только у полностью завершённой листовой крыши.");

        var boundary = tile.Junctions
            .Select(id => world.Junctions.Items[id])
            .Where(junction => junction.Tiles.Count >= 2).ToArray();
        Assert.That(boundary.Count(junction => junction.Door), Is.EqualTo(3));
        Assert.That(boundary.Where(junction => junction.Door).All(junction => !junction.Blocked),
            Is.True);
        Assert.That(boundary.All(junction => junction.Door || junction.Blocked), Is.True);

        // The exported Bay_00 outward normal is building yaw +300°. The three
        // simulation portal junctions must occupy that SAME visible edge and
        // that edge must be the one facing the colony home.
        var visibleDoorRadians = (hut.RotationDegrees + 300f) * MathF.PI / 180f;
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
            "Portal-edge должен совпадать с видимым Bay_00, а не с противоположной гранью.");

        var homeDelta = HexSpatialMath.TileToWorld(world.FactionHomes[Faction.Colony]) - center;
        var homeLength = MathF.Sqrt(homeDelta.X * homeDelta.X + homeDelta.Y * homeDelta.Y);
        Assert.That((homeDelta.X * visibleDoorOutward.X + homeDelta.Y * visibleDoorOutward.Y) /
                    homeLength, Is.GreaterThan(0.85f),
            "Наружная сторона видимой двери должна смотреть к лагерю.");

        foreach (var cot in cots)
        {
            var definition = world.Content.ObjectDefinitions[cot.DefinitionId];
            Assert.That(definition.Tags, Does.Contain(ObjectTags.Bed));
            Assert.That(definition.Interactions.Any(i => i.Type == InteractionType.Sleep), Is.True);

            var home = world.FactionHomes[Faction.Colony];
            var start = StructurePlacement.CenterJunction(world, home);
            Assert.That(start, Is.Not.Null);
            Assert.That(HexPathfinder.FindPath(world, start.Value, cot.Junctions[0]), Is.Not.Empty,
                "Кровать видна, но путь через дверной портал до неё закрыт.");
        }
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

    [Test]
    public void CompletedRoofStopsRainForBodyClothesCarriedAndLooseMaterials()
    {
        var world = TestWorld.CreateWorld(12345);
        var hut = world.Entities.Objects.Values.Single(
            obj => obj.DefinitionId == ContentIds.Hut1Hex);
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
        var hut = world.Entities.Objects.Values.Single(
            obj => obj.DefinitionId == ContentIds.Hut1Hex);
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

    private static WorldObjectState NewHutSite() => new()
    {
        DefinitionId = ContentIds.BuildSite,
        BuildProduct = ContentIds.Hut1Hex,
        BillSticks = BuildingRules.TotalSticks,
        BillBoards = BuildingRules.TotalBoards,
        BillRope = BuildingRules.TotalRope,
        BillLeaves = BuildingRules.TotalLeaves
    };

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
