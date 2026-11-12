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
/// §120.14: строитель идёт к своей детали дома, а не к якорю здания;
/// §120.13 r2 (#721): пока игрок расставляет, лагерь не берёт новых строек;
/// #723/#724: след стола и верстака — три ряда «4, 3, 4».
/// </summary>
public sealed class BuildWorkPointTests
{
    private static (SimulationEngine engine, WorldState world, WorldObjectState site) StakeHouse()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, npc.Tile) is >= 2 and <= 6)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, npc.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (!engine.ApplyManualCommand(new PlaceBuildingPlanCommand(tile, 0f)).Accepted) continue;
            var site = world.Entities.Objects.Values.Single(o =>
                BuildSiteMath.IsSite(o) && o.BuildProduct == ContentIds.HutPlan);
            return (engine, world, site);
        }

        Assert.Fail("No place for a plan house near the camp.");
        return default;
    }

    [Test]
    public void EveryModuleOfAHouseHasItsOwnPlaceAwayFromTheBuildingAnchor()
    {
        var (_, world, site) = StakeHouse();
        var anchor = world.Junctions.Items[site.Junctions[0]].WorldPosition;
        var positions = BuildingRules.Elements(world, site)
            .Select(element => BuildWorkPoint.ModulePosition(world, site, element)).ToArray();

        Assert.That(positions.Length, Is.GreaterThan(10));
        Assert.That(positions.Max(p => HexSpatialMath.Distance(p, anchor)), Is.GreaterThan(1.2f),
            "Walls stand on the hex edge, a hex radius away from the anchor every builder used to share.");
        Assert.That(positions.Select(p => ((int)System.MathF.Round(p.X * 100f), (int)System.MathF.Round(p.Y * 100f)))
            .Distinct().Count(), Is.GreaterThan(positions.Length / 2));
    }

    [Test]
    public void BuildersOnOppositeSidesWalkToDifferentPieces()
    {
        var (_, world, site) = StakeHouse();
        var anchor = world.Junctions.Items[site.Junctions[0]].WorldPosition;
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var west = new Float2(anchor.X - 4f, anchor.Y);
        var east = new Float2(anchor.X + 4f, anchor.Y);

        Assert.That(BuildWorkPoint.TryNearestPiece(world, site, west, out var westPiece), Is.True);
        Assert.That(BuildWorkPoint.TryNearestPiece(world, site, east, out var eastPiece), Is.True);
        Assert.That(westPiece.X, Is.LessThan(anchor.X));
        Assert.That(eastPiece.X, Is.GreaterThan(anchor.X));

        // Место стояния — у детали, а не в центре дома.
        Assert.That(BuildWorkPoint.TryReserve(world, npc, site, 48, out var stand), Is.True);
        Assert.That(BuildWorkPoint.TryNearestPiece(world, site, npc.Position, out var mine), Is.True);
        var standPosition = world.Junctions.Items[stand].WorldPosition;
        Assert.That(HexSpatialMath.Distance(standPosition, mine), Is.LessThanOrEqualTo(BuildWorkPoint.MaxWorkDistance));
        Assert.That(world.Junctions.Items[stand].Blocked, Is.False);
        Assert.That(HexSpatialMath.Distance(standPosition, mine),
            Is.LessThan(HexSpatialMath.Distance(anchor, mine) + 0.01f),
            "She stands closer to her piece than the building anchor is.");
    }

    [Test]
    public void TheDeliveredUnitGoesIntoThePieceTheBuilderStandsAt()
    {
        var (_, world, site) = StakeHouse();
        var anchor = world.Junctions.Items[site.Junctions[0]].WorldPosition;
        var from = new Float2(anchor.X + 4f, anchor.Y + 1f);
        var before = BuildingRules.Elements(world, site).ToDictionary(e => e.SlotKey, e => e.DeliveredSticks);

        Assert.That(BuildingRules.AssignDeliveredMaterial(world, site, ContentIds.Stick, staged: false, near: from),
            Is.True);

        var changed = BuildingRules.Elements(world, site).Single(e => e.DeliveredSticks != before[e.SlotKey]);
        var nearest = BuildingRules.Elements(world, site)
            .Where(e => e.Buildable && !e.Complete && before[e.SlotKey] < e.RequiredSticks)
            .OrderBy(e => HexSpatialMath.Distance(BuildWorkPoint.ModulePosition(world, site, e), from))
            .ThenBy(e => e.SlotKey, System.StringComparer.Ordinal).First();
        Assert.That(changed.SlotKey, Is.EqualTo(nearest.SlotKey));
    }

    [Test]
    public void TheCampTakesNoNewBuildsWhileItsPlayerIsStillDesigning()
    {
        var (engine, world, site) = StakeHouse();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        Assert.That(DecisionSystem.IsOurSite(world, npc, site), Is.True);

        Assert.That(engine.ApplyManualCommand(new SetConstructionHoldCommand(true)).Accepted, Is.True);
        Assert.That(ConstructionHold.Active(world, Faction.Colony), Is.True);
        Assert.That(DecisionSystem.IsOurSite(world, npc, site), Is.False, "Staked, but she is still designing.");
        Assert.That(DecisionSystem.FindBuildSite(npc, world), Is.Null);

        Assert.That(engine.ApplyManualCommand(new SetConstructionHoldCommand(false)).Accepted, Is.True);
        Assert.That(ConstructionHold.Active(world, Faction.Colony), Is.False);
        Assert.That(DecisionSystem.IsOurSite(world, npc, site), Is.True, "«Готово» releases the builders.");
    }

    [Test]
    public void AForgottenHoldExpiresByItself()
    {
        var (engine, world, _) = StakeHouse();
        Assert.That(engine.ApplyManualCommand(new SetConstructionHoldCommand(true)).Accepted, Is.True);
        world.ConstructionHoldUntil[Faction.Colony] = world.Tick - 1;
        Assert.That(ConstructionHold.Active(world, Faction.Colony), Is.False,
            "A client that vanished must not leave the camp without builders.");
    }

    [TestCase(ContentIds.Workbench)]
    [TestCase("furniture.modern_table")]
    public void TableAndWorkbenchOccupyThreeRowsOfFourThreeFour(string definitionId)
    {
        var offsets = BlueprintFurnitureFootprints.LocalOffsets(definitionId);
        Assert.That(offsets.Count, Is.EqualTo(11));
        Assert.That(offsets.Count(k => k.XKey == -1), Is.EqualTo(4));
        Assert.That(offsets.Count(k => k.XKey == 0), Is.EqualTo(3));
        Assert.That(offsets.Count(k => k.XKey == 1), Is.EqualTo(4));
        var centroid = BlueprintFurnitureFootprints.CentroidOffset(definitionId, 0);
        Assert.That(System.MathF.Abs(centroid.X) + System.MathF.Abs(centroid.Y), Is.LessThan(0.001f),
            "The anchor is the middle of the footprint.");
    }
}
