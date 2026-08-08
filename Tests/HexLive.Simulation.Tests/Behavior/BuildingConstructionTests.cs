using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class BuildingConstructionTests
{
    [Test]
    public void HutBuildsInThreeGroupedStagesWithLeavesLast()
    {
        var site = NewHutSite();

        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks),
            Is.EqualTo(BuildingRules.FrameSticks));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialBoards),
            Is.EqualTo(BuildingRules.FrameBoards));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialLeaves), Is.False);
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialRope), Is.False);

        Deliver(site, ContentIds.Stick, BuildingRules.FrameSticks);
        Deliver(site, ContentIds.Board, BuildingRules.FrameBoards);
        Assert.That(BuildingRules.CompletedStages(
            BuildSiteMath.Delivered(site, ContentIds.Stick),
            BuildSiteMath.Delivered(site, ContentIds.Board),
            BuildSiteMath.Delivered(site, ContentIds.Rope),
            BuildSiteMath.Delivered(site, ContentIds.PalmLeaf)), Is.EqualTo(1));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialSticks),
            Is.EqualTo(BuildingRules.EnclosureSticks));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialBoards),
            Is.EqualTo(BuildingRules.EnclosureBoards));
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialRope),
            Is.EqualTo(BuildingRules.EnclosureRope));
        Assert.That(BuildSiteMath.Needs(site, BuildSiteMath.MaterialLeaves), Is.False,
            "Листья не должны приниматься, пока не закрыты стены и стропила.");

        Deliver(site, ContentIds.Stick, BuildingRules.EnclosureSticks);
        Deliver(site, ContentIds.Board, BuildingRules.EnclosureBoards);
        Deliver(site, ContentIds.Rope, BuildingRules.EnclosureRope);
        Assert.That(BuildSiteMath.Remaining(site, BuildSiteMath.MaterialLeaves),
            Is.EqualTo(BuildingRules.RoofLeaves));
        Assert.That(BuildingRules.CompletedStages(
            BuildSiteMath.Delivered(site, ContentIds.Stick),
            BuildSiteMath.Delivered(site, ContentIds.Board),
            BuildSiteMath.Delivered(site, ContentIds.Rope),
            BuildSiteMath.Delivered(site, ContentIds.PalmLeaf)), Is.EqualTo(2));

        Deliver(site, ContentIds.PalmLeaf, BuildingRules.RoofLeaves);
        Assert.That(BuildSiteMath.IsStocked(site), Is.True);
        Assert.That(BuildingRules.CompletedStages(
            BuildSiteMath.Delivered(site, ContentIds.Stick),
            BuildSiteMath.Delivered(site, ContentIds.Board),
            BuildSiteMath.Delivered(site, ContentIds.Rope),
            BuildSiteMath.Delivered(site, ContentIds.PalmLeaf)), Is.EqualTo(3));
    }

    [Test]
    public void PrototypeStartsWithOneUsableHutTwoCotsAndAColdHearth()
    {
        var world = TestWorld.CreateWorld(12345);
        var huts = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.Hut1Hex).ToArray();
        Assert.That(huts, Has.Length.EqualTo(1));

        var hut = huts[0];
        var cots = world.Entities.Objects.Values
            .Where(obj => obj.DefinitionId == ContentIds.HutBed && obj.Tile.Equals(hut.Tile))
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
