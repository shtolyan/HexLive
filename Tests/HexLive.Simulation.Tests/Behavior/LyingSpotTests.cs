using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§113 r2: full-body ground placement on the 37-node sub-grid.</summary>
public sealed class LyingSpotTests
{
    private static NPCState Girl(WorldState world) => world.Entities.Npcs.Values.First();

    private static void MoveOtherNpcsAway(WorldState world, NPCState girl)
    {
        foreach (var other in world.Entities.Npcs.Values.Where(n => !n.Id.Equals(girl.Id)))
        {
            other.Tile = new TileCoord(girl.Tile.Q + 4, girl.Tile.R);
        }
    }

    private static WorldObjectState SpawnAtCentre(
        WorldState world, NPCState girl, string definitionId)
    {
        var centre = StructurePlacement.CenterJunction(world, girl.Tile);
        Assert.That(centre, Is.Not.Null);
        return WorldObjectMutations.SpawnObject(
            world, definitionId, girl.Fragment, girl.Tile, centre.Value);
    }

    private static float ClearanceFromObject(
        WorldObjectState thing, WorldState world, NPCState girl)
    {
        Assert.That(LyingSpot.TryAnchor(world, thing, out var anchor), Is.True);
        var radius = LyingSpot.SolidRadius(world, thing);
        var d = anchor - girl.Position;
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        var outsideAlong = System.MathF.Max(
            System.MathF.Abs(d.X * forward.X + d.Y * forward.Y) -
            LyingSpot.BodyHalfLength, 0f);
        var outsideSide = System.MathF.Max(
            System.MathF.Abs(d.X * lateral.X + d.Y * lateral.Y) -
            LyingSpot.BodyHalfWidth, 0f);
        return System.MathF.Sqrt(outsideAlong * outsideAlong + outsideSide * outsideSide) - radius;
    }

    [Test]
    public void EmptyHex_UsesTheNearestInteriorGridNode()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var before = girl.Position;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);

        var nearest = HexPointLayout.GetInteriorTemplates()
            .Select(t => HexSpatialMath.TileToWorld(girl.Tile) + t.Offset)
            .Min(p => HexSpatialMath.Distance(p, before));
        Assert.That(HexSpatialMath.Distance(girl.Position, before),
            Is.EqualTo(nearest).Within(0.001f));
        Assert.That(girl.ClaimedJunctions.Count, Is.GreaterThanOrEqualTo(3),
            "A ground body must reserve at least the centre/head/feet support nodes.");
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "LieDownSpot" && e.Message.Contains("Fit=Clear")), Is.True);
    }

    [Test]
    public void BedSleepEntry_OwnsPoseInteractionAndClaimAsOneInvariant()
    {
        var world = TestWorld.CreateWorld(12345);
        var girl = Girl(world);
        var hut = world.Entities.Objects.Values.Single(o =>
            o.DefinitionId == ContentIds.Hut1Hex);
        var bed = world.Entities.Objects.Values.First(o =>
            o.DefinitionId == ContentIds.BedBasic &&
            o.Variant == ContentIds.HutBedVariant);
        Assert.That(LyingSpot.TryAnchor(world, bed, out var routeAnchor), Is.True);

        var expectedCentre = BuildingRules.HutBedVisualPosition(
            HexSpatialMath.TileToWorld(bed.Tile),
            hut.RotationDegrees,
            routeAnchor);
        var wakeJunction = girl.CurrentJunction;
        var endTick = world.Tick + 321;

        Assert.That(BedSleep.TryEnter(
            world, girl, bed, endTick, wakeJunction), Is.True);

        Assert.That(girl.Position, Is.EqualTo(expectedCentre),
            "The junction is only the route anchor; the sleeper belongs at the rendered point.");
        Assert.That(HexSpatialMath.Distance(girl.Position, routeAnchor),
            Is.GreaterThan(0.30f),
            "HutTest reproduces the old attach rejection: its authored centre is over the 0.30-wu guard from the node.");

        var bodyRadians = girl.RotationDegrees * System.MathF.PI / 180f;
        var bedRadians = bed.RotationDegrees * System.MathF.PI / 180f;
        var simulatedHead = new Float2(
            -System.MathF.Cos(bodyRadians), -System.MathF.Sin(bodyRadians));
        var renderedHead = new Float2(
            System.MathF.Sin(bedRadians), -System.MathF.Cos(bedRadians));
        Assert.That(simulatedHead.X, Is.EqualTo(renderedHead.X).Within(0.0001f));
        Assert.That(simulatedHead.Y, Is.EqualTo(renderedHead.Y).Within(0.0001f));
        Assert.That(girl.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
        Assert.That(girl.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep));
        Assert.That(girl.Execution.TargetObject, Is.EqualTo(bed.Id));
        Assert.That(girl.Execution.StartTick, Is.EqualTo(world.Tick));
        Assert.That(girl.Execution.EndTick, Is.EqualTo(endTick));
        Assert.That(girl.CurrentJunction, Is.EqualTo(wakeJunction));
        Assert.That(girl.Movement.IsMoving, Is.False);
        Assert.That(bed.IsOccupied, Is.True);
        Assert.That(bed.CurrentUser, Is.EqualTo(girl.Id));
    }

    [Test]
    public void BedSleepEntry_RejectsASecondSleeperWithoutPartialMutation()
    {
        var world = TestWorld.CreateWorld(12345);
        var girls = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToArray();
        var bed = world.Entities.Objects.Values.First(o =>
            o.DefinitionId == ContentIds.BedBasic &&
            o.Variant == ContentIds.HutBedVariant);
        var secondPosition = girls[1].Position;

        Assert.That(BedSleep.TryEnter(
            world, girls[0], bed, world.Tick + 100, girls[0].CurrentJunction), Is.True);
        Assert.That(BedSleep.TryEnter(
            world, girls[1], bed, world.Tick + 100, girls[1].CurrentJunction), Is.False);

        Assert.That(girls[1].Position, Is.EqualTo(secondPosition));
        Assert.That(girls[1].Execution.CurrentInteraction, Is.Null);
        Assert.That(bed.CurrentUser, Is.EqualTo(girls[0].Id));
    }

    [Test]
    public void CryingBesideAFreeBed_UsesTheBedAndReleasesItOnRise()
    {
        var world = TestWorld.CreateWorld(12345);
        var girls = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToArray();
        var girl = girls[0];
        MoveOtherNpcsAway(world, girl);
        var bed = world.Entities.Objects.Values.First(o =>
            o.DefinitionId == ContentIds.BedBasic && o.Variant == ContentIds.HutBedVariant);

        // Make this bed the deterministic first choice; no unrelated bed may
        // accidentally satisfy the assertion just because it is a little nearer.
        foreach (var otherBed in world.Entities.Objects.Values.Where(
                     o => ContentIds.IsBed(o.DefinitionId)))
        {
            otherBed.IsOccupied = !otherBed.Id.Equals(bed.Id);
            otherBed.CurrentUser = otherBed.Id.Equals(bed.Id) ? null : girls[1].Id;
            if (!otherBed.Id.Equals(bed.Id))
            {
                otherBed.Owner = girls[1].Id;
            }
        }
        bed.Owner = girl.Id;

        Assert.That(LyingSpot.TryAnchor(world, bed, out var anchor), Is.True);
        var definition = world.Content.ObjectDefinitions[bed.DefinitionId];
        var reach = InteractionReach.ForObject(definition.ObstacleRadius);
        var stand = world.Junctions.Items.Values
            .Where(j => !j.Blocked && !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
                SpatialQueries.IsJunctionFree(world, j.Id))
            .Where(j => !bed.BlockedJunctions.Contains(j.Id) && !j.Id.Equals(bed.Junctions[0]))
            .Where(j => girls.Skip(1).All(other =>
                HexSpatialMath.Distance(other.Position, j.WorldPosition) >= 0.20f))
            .Where(j => HexSpatialMath.Distance(j.WorldPosition, anchor) <= reach)
            .Where(j => SpatialQueries.CanTouchAcross(
                world, j.Id, bed.Junctions[0], reach, bed, InteractionReach.RimMode))
            .OrderBy(j => HexSpatialMath.Distance(j.WorldPosition, anchor))
            .ThenBy(j => j.Id.Value)
            .First();
        girl.Tile = stand.Tiles.Contains(bed.Tile) ? bed.Tile : stand.Tiles[0];
        girl.Position = stand.WorldPosition;
        girl.CurrentJunction = stand.Id;

        var cryingUntil = world.Tick + 240;
        Assert.That(ExecutionSystem.TryLieDownForCrying(
            world, girl, cryingUntil), Is.True);
        girl.Mind.CryingUntilTick = cryingUntil;

        Assert.That(girl.Execution.TargetObject, Is.EqualTo(bed.Id));
        Assert.That(girl.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep));
        Assert.That(girl.CurrentJunction, Is.EqualTo(stand.Id));
        var hut = world.Entities.Objects.Values.Single(o =>
            o.DefinitionId == ContentIds.Hut1Hex && o.Tile.Equals(bed.Tile));
        var visualCentre = BuildingRules.HutBedVisualPosition(
            HexSpatialMath.TileToWorld(bed.Tile), hut.RotationDegrees, anchor);
        Assert.That(girl.Position, Is.EqualTo(visualCentre));
        Assert.That(bed.IsOccupied, Is.True);
        Assert.That(bed.CurrentUser, Is.EqualTo(girl.Id));
        Assert.That(world.Junctions.Items[stand.Id].Blocked, Is.False);
        Assert.That(SpatialQueries.IsAllWaterJunction(world, stand.Id), Is.False);
        Assert.That(!world.Occupancy.JunctionOwner.TryGetValue(stand.Id, out var standOwner) ||
            standOwner is null || standOwner == girl.Id, Is.True);
        Assert.That(girls.Skip(1).All(other =>
            HexSpatialMath.Distance(other.Position, stand.WorldPosition) >= 0.20f), Is.True);

        LyingSpot.EndCrying(world, girl);

        Assert.That(girl.Mind.CryingUntilTick, Is.Zero);
        Assert.That(bed.IsOccupied, Is.False);
        Assert.That(bed.CurrentUser, Is.Null);
        Assert.That(girl.Position, Is.EqualTo(stand.WorldPosition),
            "Rising must return to the same legal furniture side instead of getting up inside it.");
    }

    [Test]
    public void OccupiedNearbyBeds_DoNotReplaceSafeGroundForACollapse()
    {
        var world = TestWorld.CreateWorld(12345);
        var girls = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToArray();
        var girl = girls[0];
        MoveOtherNpcsAway(world, girl);
        foreach (var bed in world.Entities.Objects.Values.Where(
                     o => ContentIds.IsBed(o.DefinitionId)))
        {
            bed.IsOccupied = true;
            bed.CurrentUser = girls[1].Id;
        }

        Assert.That(ExecutionSystem.TryLieDownForCollapse(
            world, girl, world.Tick + 80), Is.True);
        Assert.That(girl.Execution.TargetObject, Is.Null);
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        Assert.That(LyingSpot.BodyClear(
            world, girl, girl.Tile, girl.Position, forward, lateral), Is.True);
        Assert.That(girl.ClaimedJunctions.Count, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Campfire_NoPartOfTheBodyEntersTheFire()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var fire = SpawnAtCentre(world, girl, ContentIds.Campfire);
        var tile = girl.Tile;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);

        Assert.That(girl.Tile, Is.EqualTo(tile), "Choosing a pose is not movement to another tile.");
        Assert.That(ClearanceFromObject(fire, world, girl), Is.GreaterThanOrEqualTo(-0.001f));
    }

    [Test]
    public void Boulder_NoPartOfTheBodyEntersItsSolidRadius()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var boulder = SpawnAtCentre(world, girl, "rock.boulder");

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);
        Assert.That(ClearanceFromObject(boulder, world, girl), Is.GreaterThanOrEqualTo(-0.001f));
    }

    [Test]
    public void CliffEdge_MovesTheBodyToAWhollySupportedNode()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var eastCoord = new TileCoord(girl.Tile.Q + 1, girl.Tile.R);
        Assert.That(world.Tiles.Items.TryGetValue(eastCoord, out var east), Is.True);
        east.Elevation = world.Tiles.Items[girl.Tile].Elevation - 2;

        var eastMost = HexPointLayout.GetInteriorTemplates()
            .OrderByDescending(t => t.Offset.X)
            .ThenBy(t => t.Slot)
            .First();
        girl.Position = HexSpatialMath.TileToWorld(girl.Tile) + eastMost.Offset;
        girl.RotationDegrees = 0f;
        var unsafeX = girl.Position.X;

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.True);
        var radians = girl.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        Assert.That(LyingSpot.BodyClear(
            world, girl, girl.Tile, girl.Position, forward, lateral), Is.True,
            "The selected pose must keep the whole body on level support; a rim node is " +
            "valid when the solver turns the body parallel to the cliff.");
    }

    [Test]
    public void TwoBodies_AreSeparatedWithoutSharedHeadingOrBerthSlots()
    {
        var world = TestWorld.CreateWorld();
        var all = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToList();
        var first = all[0];
        var second = all[1];
        foreach (var other in all.Skip(2))
        {
            other.Tile = new TileCoord(first.Tile.Q + 4, first.Tile.R);
        }

        second.Tile = first.Tile;
        first.Mind.FaintedUntilTick = world.Tick + 100;
        second.Mind.FaintedUntilTick = world.Tick + 100;
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, first), Is.True);
        Assert.That(ExecutionSystem.TryLieDownOnGround(world, second), Is.True);

        var radians = second.RotationDegrees * (System.MathF.PI / 180f);
        var forward = new Float2(System.MathF.Cos(radians), System.MathF.Sin(radians));
        var lateral = new Float2(-forward.Y, forward.X);
        Assert.That(LyingSpot.BodyClear(
            world, second, second.Tile, second.Position, forward, lateral), Is.True);
        Assert.That(HexSpatialMath.Distance(first.Position, second.Position), Is.GreaterThan(0.1f));
    }

    [Test]
    public void FullyBlockedHex_ReturnsNoSpaceInsteadOfStacking()
    {
        var world = TestWorld.CreateWorld();
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var before = girl.Position;
        foreach (var junctionId in world.Tiles.Items[girl.Tile].Junctions)
        {
            world.Junctions.Items[junctionId].Blocked = true;
        }

        Assert.That(ExecutionSystem.TryLieDownOnGround(world, girl), Is.False);
        Assert.That(girl.Position, Is.EqualTo(before));
        Assert.That(world.Events.Items.Any(e =>
            e.Type == "LieDownSpot" && e.Message.Contains("Fit=NoSpace")), Is.True);
    }

    [Test]
    public void CryingWithNoLegalSurface_DoesNotArmALyingStateAtTheOldPoint()
    {
        var world = TestWorld.CreateWorld(12345);
        var girls = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).ToArray();
        var girl = girls[0];
        MoveOtherNpcsAway(world, girl);
        foreach (var bed in world.Entities.Objects.Values.Where(
                     o => ContentIds.IsBed(o.DefinitionId)))
        {
            bed.IsOccupied = true;
            bed.CurrentUser = girls[1].Id;
        }
        foreach (var junctionId in world.Tiles.Items[girl.Tile].Junctions)
        {
            world.Junctions.Items[junctionId].Blocked = true;
        }

        girl.Needs.Energy = 0.5f;
        girl.Needs.Hunger = 0.2f;
        girl.Needs.Blood = 1f;
        girl.Needs.Stamina = 0f;
        girl.Needs.Stress = 1f;
        var before = girl.Position;

        new NeedsDecaySystem().Run(world);

        Assert.That(girl.Mind.CryingUntilTick, Is.Zero,
            "The view must not enter a lying state when no collision-free pose exists.");
        Assert.That(girl.Position, Is.EqualTo(before));
    }

    [Test]
    public void LegacyBedAnchor_WakesOntoNearestFreeStandingJunction()
    {
        var world = TestWorld.CreateWorld(12345);
        var girl = Girl(world);
        MoveOtherNpcsAway(world, girl);
        var bed = world.Entities.Objects.Values.First(o =>
            o.DefinitionId == ContentIds.BedBasic && o.Variant == ContentIds.HutBedVariant);
        var anchor = bed.Junctions[0];
        girl.Tile = bed.Tile;
        girl.Position = world.Junctions.Items[anchor].WorldPosition;
        girl.Plan.TargetJunctionId = anchor; // old save: bed centre, not approach point

        Assert.That(LyingSpot.TryStandAfterObjectSleep(world, girl, bed), Is.True);
        Assert.That(girl.Position, Is.Not.EqualTo(world.Junctions.Items[anchor].WorldPosition));
        Assert.That(world.Tiles.Items[bed.Tile].Junctions.Any(id =>
            world.Junctions.Items[id].WorldPosition.Equals(girl.Position) &&
            !world.Junctions.Items[id].Blocked), Is.True);
    }

    [Test]
    public void TwoBedSleepers_WakeOntoDifferentStandingPoints()
    {
        var world = TestWorld.CreateWorld(12345);
        var girls = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).Take(2).ToArray();
        var beds = world.Entities.Objects.Values.Where(o =>
                o.DefinitionId == ContentIds.BedBasic && o.Variant == ContentIds.HutBedVariant)
            .OrderBy(o => o.Id.Value).Take(2).ToArray();
        Assert.That(beds, Has.Length.EqualTo(2));
        for (var i = 0; i < 2; i++)
        {
            girls[i].Tile = beds[i].Tile;
            girls[i].Position = world.Junctions.Items[beds[i].Junctions[0]].WorldPosition;
            girls[i].Plan.TargetJunctionId = beds[i].Junctions[0];
        }

        Assert.That(LyingSpot.TryStandAfterObjectSleep(world, girls[0], beds[0]), Is.True);
        Assert.That(LyingSpot.TryStandAfterObjectSleep(world, girls[1], beds[1]), Is.True);
        Assert.That(HexSpatialMath.Distance(girls[0].Position, girls[1].Position),
            Is.GreaterThan(0.20f));
    }

    [Test]
    public void SleepAuctionIsClosedWhenNoBedOrFullBodyGroundSpotExists()
    {
        var world = TestWorld.CreateWorld(12345);
        var girl = Girl(world);
        var stand = world.Junctions.Items.Values.First(j =>
            !j.Blocked && j.Tiles.Count > 0);
        girl.CurrentJunction = stand.Id;
        girl.Tile = stand.Tiles[0];
        girl.Position = stand.WorldPosition;
        girl.Perception.Objects.Clear();
        girl.Needs.Energy = 0f;
        girl.Needs.Hunger = 0.1f;
        girl.Needs.Thirst = 0.1f;
        foreach (var junction in world.Junctions.Items.Keys)
        {
            world.Occupancy.JunctionOwner[junction] =
                new EntityId(int.MaxValue - 600);
        }

        Assert.That(PlanningSystem.HasSleepSurface(world, girl), Is.False);
        new DecisionSystem().Run(world);
        Assert.That(girl.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Sleep),
            "Sleep без физической поверхности не должен выигрывать и падать в NoGroundSpot.");
    }
}

}
