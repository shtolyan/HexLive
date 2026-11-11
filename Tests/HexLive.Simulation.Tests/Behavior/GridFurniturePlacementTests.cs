using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// §120.13: одно правило размещения мебели — точка сетки, авторский след,
/// шесть поворотов; на земле и на полу одинаково. Поставить, передвинуть
/// (в том числе уже построенное) и снять площадку.
/// </summary>
public sealed class GridFurniturePlacementTests
{
    private static (SimulationEngine engine, WorldState world) Create()
    {
        var engine = TestWorld.CreateEngine();
        return (engine, engine.World);
    }

    /// <summary>Свободная точка сетки НЕ в центре гекса, рядом с лагерем:
    /// именно то, что прежнее правило §66 запрещало.</summary>
    private static Junction FreeOffCentreAnchor(
        WorldState world, string catalogId, int yawStep = 0, HashSet<JunctionId>? avoid = null)
    {
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, npc.Tile) is >= 1 and <= 4)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, npc.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            var centre = HexSpatialMath.TileToWorld(tile);
            foreach (var id in world.Tiles.Items[tile].Junctions)
            {
                var junction = world.Junctions.Items[id];
                if (avoid != null && avoid.Contains(id)) continue;
                var distance = HexSpatialMath.Distance(junction.WorldPosition, centre);
                if (distance < 0.3f || distance > 0.8f) continue;
                if (FurniturePlacement.Validate(world, catalogId, junction, yawStep) == null) return junction;
            }
        }

        Assert.Fail($"No free off-centre anchor for {catalogId}");
        return null!;
    }

    private static JunctionKey Key(Junction junction) => FurniturePlacement.KeyOf(junction.WorldPosition);

    [TestCase(ContentIds.BedBasic)]
    [TestCase(ContentIds.Wardrobe)]
    [TestCase(ContentIds.DryingRack)]
    [TestCase(ContentIds.Campfire)]
    [TestCase(ContentIds.FurnitureHearth)]
    [TestCase(ContentIds.Workbench)]
    [TestCase(ContentIds.WaterCollector)]
    public void EveryCatalogPieceStakesOnAnyFreeGridPointOnBareGround(string catalogId)
    {
        var (engine, world) = Create();
        var anchor = FreeOffCentreAnchor(world, catalogId, yawStep: 2);
        var key = Key(anchor);
        var tile = FurniturePlacement.TileOf(anchor);
        Assert.That(world.Tiles.Items[tile].Flags.HasFlag(TileFlags.HasFloor), Is.False,
            "The point must be bare ground: floor is not a prerequisite any more.");

        var admission = engine.ApplyManualCommand(new PlaceFurnitureCommand(catalogId, key.XKey, key.YKey, 2));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var site = FurniturePlacement.FindAt(world, catalogId, key);
        Assert.That(site, Is.Not.Null);
        Assert.That(site!.DefinitionId, Is.EqualTo(ContentIds.BuildSite));
        Assert.That(site.Junctions[0], Is.EqualTo(anchor.Id));
        Assert.That(FurniturePlacement.YawStepOf(site.RotationDegrees), Is.EqualTo(2));
        Assert.That(site.BillLogs + site.BillSticks + site.BillStones + site.BillLeaves + site.BillRope +
                    site.BillBoards, Is.GreaterThan(0), "A staked piece carries its ordinary bill.");
    }

    [Test]
    public void SeveralPiecesShareOneHexButNeverOnePoint()
    {
        var (engine, world) = Create();
        var first = FreeOffCentreAnchor(world, ContentIds.FurnitureHearth);
        var firstKey = Key(first);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.FurnitureHearth, firstKey.XKey, firstKey.YKey, 0)).Accepted, Is.True);

        // Тот же якорь занят.
        var again = engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.FurnitureHearth, firstKey.XKey, firstKey.YKey, 0));
        Assert.That(again.Accepted, Is.False);
        Assert.That(again.Reason, Is.EqualTo("PlacementOccupied"));

        // Вторая — в том же гексе, на другой свободной точке.
        var tile = FurniturePlacement.TileOf(first);
        var second = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
            .First(j => !j.Id.Equals(first.Id) &&
                        FurniturePlacement.Validate(world, ContentIds.FurnitureHearth, j, 0) == null);
        var secondKey = Key(second);
        var admission = engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.FurnitureHearth, secondKey.XKey, secondKey.YKey, 0));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects.Values.Count(o =>
            BuildSiteMath.IsSite(o) && o.Tile.Equals(tile) && o.BuildProduct == ContentIds.Campfire),
            Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void FootprintsOfTwoBedsMayNotOverlap()
    {
        var (engine, world) = Create();
        var anchor = FreeOffCentreAnchor(world, ContentIds.BedBasic);
        var key = Key(anchor);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, key.XKey, key.YKey, 0)).Accepted, Is.True);

        // Соседняя точка лежит внутри следа первой кровати (14 узлов).
        var inside = FurniturePlacement.FootprintKeys(ContentIds.BedBasic, key, 0).First(k => !k.Equals(key));
        var admission = engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, inside.XKey, inside.YKey, 0));

        // Причина — «занято» либо «заблокировано» (сдвинутый след мог упереться
        // ещё и в дерево); важно, что вторая кровать в первую не встаёт.
        Assert.That(admission.Accepted, Is.False);
        Assert.That(admission.Reason, Is.AnyOf("PlacementOccupied", "PlacementBlocked"));
        Assert.That(world.Entities.Objects.Values.Count(o => o.BuildProduct == ContentIds.BedBasic &&
            o.Junctions.Count > 0 && FurniturePlacement.FootprintKeys(ContentIds.BedBasic, key, 0)
                .Contains(FurniturePlacement.KeyOf(world.Junctions.Items[o.Junctions[0]].WorldPosition))),
            Is.EqualTo(1));
    }

    [Test]
    public void StakedSiteMovesAndRotatesKeepingItsDeliveredMaterials()
    {
        var (engine, world) = Create();
        var from = FreeOffCentreAnchor(world, ContentIds.BedBasic);
        var fromKey = Key(from);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, fromKey.XKey, fromKey.YKey, 0)).Accepted, Is.True);
        var site = FurniturePlacement.FindAt(world, ContentIds.BedBasic, fromKey)!;
        site.Contents.Add(new ItemInstance(ContentIds.Log));
        var id = site.Id;

        var to = FreeOffCentreAnchor(world, ContentIds.BedBasic, yawStep: 3,
            avoid: new HashSet<JunctionId> { from.Id });
        var toKey = Key(to);
        var admission = engine.ApplyManualCommand(new MoveFurnitureCommand(
            ContentIds.BedBasic, fromKey.XKey, fromKey.YKey, toKey.XKey, toKey.YKey, 3));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects.ContainsKey(id), Is.True, "Moving keeps the same object.");
        Assert.That(site.Junctions[0], Is.EqualTo(to.Id));
        Assert.That(site.Tile, Is.EqualTo(FurniturePlacement.TileOf(to)));
        Assert.That(FurniturePlacement.YawStepOf(site.RotationDegrees), Is.EqualTo(3));
        Assert.That(site.Contents.Count, Is.EqualTo(1), "Delivered material travels with the site.");
        Assert.That(world.Caches.ObjectsByTile[site.Tile], Does.Contain(id));
        Assert.That(FurniturePlacement.FindAt(world, ContentIds.BedBasic, fromKey), Is.Null);
    }

    [Test]
    public void FinishedBedMovesWithItsBlockingFootprint()
    {
        var (engine, world) = Create();
        var from = FreeOffCentreAnchor(world, ContentIds.BedBasic);
        var fromKey = Key(from);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, fromKey.XKey, fromKey.YKey, 0)).Accepted, Is.True);
        var site = FurniturePlacement.FindAt(world, ContentIds.BedBasic, fromKey)!;
        var bed = ExecutionSystem.RaiseFurnitureSite(world, site, from.Fragment, from.Id);
        Assert.That(bed.DefinitionId, Is.EqualTo(ContentIds.BedBasic));
        var oldBlocked = bed.BlockedJunctions.ToArray();
        Assert.That(oldBlocked, Is.Not.Empty);

        var to = world.Junctions.Items.Values
            .Where(j => HexSpatialMath.Distance(j.WorldPosition, from.WorldPosition) is > 3f and < 7f)
            .OrderBy(j => j.Id.Value)
            .First(j => FurniturePlacement.Validate(world, ContentIds.BedBasic, j, 1, bed) == null);
        var toKey = Key(to);
        var admission = engine.ApplyManualCommand(new MoveFurnitureCommand(
            ContentIds.BedBasic, fromKey.XKey, fromKey.YKey, toKey.XKey, toKey.YKey, 1));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(bed.Junctions[0], Is.EqualTo(to.Id));
        Assert.That(oldBlocked.All(j => !world.Junctions.Items[j].Blocked), Is.True,
            "The old footprint is released.");
        var expected = FurniturePlacement.FootprintKeys(ContentIds.BedBasic, toKey, 1)
            .Select(k => FurniturePlacement.TryFindJunction(world, k, out var j) ? j.Id : default).ToHashSet();
        Assert.That(bed.BlockedJunctions.ToHashSet().SetEquals(expected), Is.True,
            "The new footprint is exactly the authored one at the new anchor and yaw.");
        Assert.That(bed.BlockedJunctions.All(j => world.Junctions.Items[j].Blocked), Is.True);
    }

    [Test]
    public void MovingOntoAnotherPieceIsRejectedAndChangesNothing()
    {
        var (engine, world) = Create();
        var a = FreeOffCentreAnchor(world, ContentIds.Wardrobe);
        var aKey = Key(a);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.Wardrobe, aKey.XKey, aKey.YKey, 0)).Accepted, Is.True);
        var b = FreeOffCentreAnchor(world, ContentIds.Wardrobe);
        var bKey = Key(b);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.Wardrobe, bKey.XKey, bKey.YKey, 0)).Accepted, Is.True);

        var admission = engine.ApplyManualCommand(new MoveFurnitureCommand(
            ContentIds.Wardrobe, bKey.XKey, bKey.YKey, aKey.XKey, aKey.YKey, 0));

        Assert.That(admission.Accepted, Is.False);
        Assert.That(admission.Reason, Is.EqualTo("PlacementOccupied"));
        Assert.That(FurniturePlacement.FindAt(world, ContentIds.Wardrobe, bKey), Is.Not.Null);
    }

    [Test]
    public void RemovingASiteLeavesItsMaterialsOnTheGroundAndRefusesFinishedFurniture()
    {
        var (engine, world) = Create();
        var anchor = FreeOffCentreAnchor(world, ContentIds.BedBasic);
        var key = Key(anchor);
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, key.XKey, key.YKey, 0)).Accepted, Is.True);
        var site = FurniturePlacement.FindAt(world, ContentIds.BedBasic, key)!;
        site.Contents.Add(new ItemInstance(ContentIds.Log));
        var logsBefore = world.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.Log);

        var admission = engine.ApplyManualCommand(new RemoveFurnitureCommand(ContentIds.BedBasic, key.XKey, key.YKey));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        Assert.That(world.Entities.Objects.ContainsKey(site.Id), Is.False);
        Assert.That(world.Entities.Objects.Values.Count(o => o.DefinitionId == ContentIds.Log),
            Is.EqualTo(logsBefore + 1), "The delivered log is back on the ground.");

        // Готовую мебель режим стройки не уничтожает.
        Assert.That(engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.BedBasic, key.XKey, key.YKey, 0)).Accepted, Is.True);
        var second = FurniturePlacement.FindAt(world, ContentIds.BedBasic, key)!;
        ExecutionSystem.RaiseFurnitureSite(world, second, anchor.Fragment, anchor.Id);
        var refused = engine.ApplyManualCommand(new RemoveFurnitureCommand(ContentIds.BedBasic, key.XKey, key.YKey));
        Assert.That(refused.Accepted, Is.False);
        Assert.That(refused.Reason, Is.EqualTo("FurnitureBuilt"));
    }

    [Test]
    public void SeedIsPlantedOnTheSamePointRule()
    {
        var (engine, world) = Create();
        var anchor = FreeOffCentreAnchor(world, ContentIds.YuccaSeed);
        var key = Key(anchor);

        var admission = engine.ApplyManualCommand(new PlaceFurnitureCommand(
            ContentIds.YuccaSeed, key.XKey, key.YKey, 0));

        Assert.That(admission.Accepted, Is.True, admission.Reason);
        var site = FurniturePlacement.FindAt(world, ContentIds.YuccaSeed, key);
        Assert.That(site, Is.Not.Null);
        Assert.That(site!.BuildProduct, Is.EqualTo(ContentIds.YuccaSeed));
    }

    [Test]
    public void LooseCoconutIsNotFurnitureButAPlantingSiteIs()
    {
        Assert.That(FurniturePlacement.CatalogId(ContentIds.Coconut, "", "", false), Is.Null);
        Assert.That(FurniturePlacement.CatalogId(ContentIds.BuildSite, ContentIds.Coconut, "", false),
            Is.EqualTo(ContentIds.Coconut));
        Assert.That(FurniturePlacement.CatalogId(ContentIds.Campfire, "", BuildingRules.HutHearthVariant, false),
            Is.EqualTo(ContentIds.FurnitureHearth));
        Assert.That(FurniturePlacement.CatalogId(ContentIds.BedBasic, "", ContentIds.HutBedVariant, false), Is.Null);
        Assert.That(FurniturePlacement.CatalogId("architecture.wall.wood", "", "", true), Is.Null);
    }
}
