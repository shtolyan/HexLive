using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PersonalExplorationTests
{
    [Test]
    public void ManualExploreTargetsTheRemainingPersonalGapDespiteSharedFog()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var actor = world.Entities.Npcs.Values.First();
        actor.Needs.Hunger = actor.Needs.Thirst = 0f;
        engine.Commands.Enqueue(new SetManualControlCommand(actor.Id, true));
        engine.Step();
        var candidates = world.Junctions.Items.Values.Where(j =>
            PlanningSystem.IsExploreCandidate(world, actor, j)).ToArray();
        Assert.That(candidates, Is.Not.Empty);
        new PlanningSystem().BuildExplorePlan(world, actor);
        var oldTile = HexSpatialMath.WorldToTile(world.Junctions.Items[actor.Plan.TargetJunctionId!.Value].WorldPosition);
        var gap = candidates.Select(c => HexSpatialMath.WorldToTile(c.WorldPosition))
            .OrderByDescending(t => HexSpatialMath.HexDistance(t, oldTile)).First();
        Assert.That(HexSpatialMath.HexDistance(gap, oldTile), Is.GreaterThan(AiBalance.PerceptionRadiusTiles));
        foreach (var tile in world.Tiles.Items.Keys)
        {
            world.ExploredTiles.Add(tile); // This is somebody else's map.
            if (!tile.Equals(gap)) actor.Memory.RememberSurvey(tile, 1);
        }
        var admission = engine.ApplyManualCommand(new SelfActionCommand(actor.Id, SelfActionKind.Explore));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        var endpoint = world.Junctions.Items[actor.Plan.TargetJunctionId!.Value];
        var endpointTile = HexSpatialMath.WorldToTile(endpoint.WorldPosition);
        Assert.That(HexSpatialMath.HexDistance(endpointTile, gap), Is.LessThanOrEqualTo(AiBalance.PerceptionRadiusTiles),
            "The next observation must cover the one personally unobserved tile, even when shared fog says all is known.");
        Assert.That(endpoint.Tiles.All(t => HexSpatialMath.HexDistance(t, gap) <= AiBalance.PerceptionRadiusTiles), Is.True,
            "Every legal arrival side must observe the remaining gap.");
        Assert.That(PlanningSystem.IsExploreCandidate(world, actor, endpoint), Is.True);
        var output = System.Environment.GetEnvironmentVariable("HEXLIVE_PERSONAL_EXPLORE_DIAGNOSTIC");
        var start = actor.Position;
        var route = new System.Collections.Generic.List<object> { new { x = start.X, y = start.Y } };
        var initialTiles = world.Tiles.Items.Keys.Select(t => new { q = t.Q, r = t.R,
            x = HexSpatialMath.TileToWorld(t).X, y = HexSpatialMath.TileToWorld(t).Y,
            surveyed = actor.Memory.SurveyedTiles.ContainsKey(t) }).ToArray();
        for (var i = 0; i < 1200 && actor.Mind.CurrentGoal == GoalType.Explore; i++)
        {
            engine.Step();
            if (!string.IsNullOrEmpty(output)) route.Add(new { x = actor.Position.X, y = actor.Position.Y });
        }
        Assert.That(actor.CurrentJunction, Is.EqualTo(endpoint.Id));
        Assert.That(actor.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        if (!string.IsNullOrEmpty(output))
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new {
                hexRadius = HexSpatialMath.HexRadius, sensorRadiusTiles = AiBalance.PerceptionRadiusTiles,
                start = new { x = start.X, y = start.Y }, oldTile = new { q = oldTile.Q, r = oldTile.R },
                gap = new { q = gap.Q, r = gap.R }, endpoint = new { x = endpoint.WorldPosition.X, y = endpoint.WorldPosition.Y },
                endpointTile = new { q = actor.Tile.Q, r = actor.Tile.R }, tiles = initialTiles, route }));
    }

    [Test]
    public void SharedJunctionEstimateNeverExceedsWhatAnyArrivalSideActuallySees()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        actor.Mind.ManualControl = true;
        actor.Mind.CurrentGoal = GoalType.None;
        var radius = AiBalance.PerceptionRadiusTiles;
        var sample = (from junction in world.Junctions.Items.Values
                      where junction.Tiles.Count > 1
                      let rounded = HexSpatialMath.WorldToTile(junction.WorldPosition)
                      from gap in world.Tiles.Items.Keys
                      where HexSpatialMath.HexDistance(rounded, gap) <= radius &&
                            junction.Tiles.Any(t => HexSpatialMath.HexDistance(t, gap) > radius)
                      select (junction, gap)).First();
        foreach (var tile in world.Tiles.Items.Keys)
            if (!tile.Equals(sample.gap)) actor.Memory.RememberSurvey(tile, 0);
        var predicted = new PlanningSystem().PersonalSurveyForDestination(world, actor, sample.junction).NewTiles;
        foreach (var arrival in sample.junction.Tiles)
        {
            actor.Memory.SurveyedTiles.Remove(sample.gap);
            actor.Tile = arrival;
            actor.Position = sample.junction.WorldPosition;
            actor.CurrentJunction = sample.junction.Id;
            var before = actor.Memory.SurveyedTiles.Count;
            new PerceptionSystem().Run(world);
            var actualNew = actor.Memory.SurveyedTiles.Count - before;
            Assert.That(predicted, Is.LessThanOrEqualTo(actualNew),
                $"Survey estimate must hold for real sensor tile {arrival}, not a rounded coordinate.");
        }
    }

    [Test]
    public void AutonomousExploreDoesNotReadPersonalSurveyRanking()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var actor = world.Entities.Npcs.Values.First();
        actor.Needs.Hunger = actor.Needs.Thirst = 0f;
        engine.Step();
        var planner = new PlanningSystem();
        planner.BuildExplorePlan(world, actor);
        var initial = actor.Plan.TargetJunctionId;
        Assert.That(initial, Is.Not.Null);
        foreach (var tile in world.Tiles.Items.Keys) actor.Memory.RememberSurvey(tile, 123);
        actor.Plan.Steps.Clear();
        planner.BuildExplorePlan(world, actor);
        Assert.That(actor.Plan.TargetJunctionId, Is.EqualTo(initial));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SurveyComesOnlyFromOwnAwakeObjectSensor(bool sleeping)
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        actor.Mind.ManualControl = true;
        actor.Mind.CurrentGoal = sleeping ? GoalType.Sleep : GoalType.None;
        foreach (var tile in world.Tiles.Items.Keys) world.ExploredTiles.Add(tile);
        new PerceptionSystem().Run(world);
        if (sleeping) Assert.That(actor.Memory.SurveyedTiles, Is.Empty);
        else
        {
            Assert.That(actor.Memory.SurveyedTiles.ContainsKey(actor.Tile), Is.True);
            Assert.That(actor.Memory.SurveyedTiles.Keys.All(t =>
                HexSpatialMath.HexDistance(actor.Tile, t) <= AiBalance.PerceptionRadiusTiles), Is.True);
            Assert.That(actor.Memory.SurveyedTiles.Count, Is.LessThan(world.ExploredTiles.Count));
        }
    }

    [TestCase(76)]
    [TestCase(77)]
    public void PersonalSurveyRoundTripsWithoutBorrowingSharedMap(int version)
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        actor.Memory.RememberSurvey(actor.Tile, 42);
        var other = new TileCoord(actor.Tile.Q + 1, actor.Tile.R);
        world.ExploredTiles.Add(other);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using var reader = new BinaryReader(bytes);
        WorldSaveSerializer.Read(restored, reader);
        var survey = restored.Entities.Npcs[actor.Id].Memory.SurveyedTiles;
        Assert.That(survey.Count, Is.EqualTo(version >= 77 ? 1 : 0));
        if (version >= 77) Assert.That(survey[actor.Tile], Is.EqualTo(42));
        Assert.That(survey.ContainsKey(other), Is.False);
        Assert.That(restored.ExploredTiles.Contains(other), Is.True);
    }

    [Test]
    public void BoundedSurveyEvictsOldestAndRefreshesRevisitedTile()
    {
        var memory = new MemoryState();
        for (var i = 0; i < MemoryState.SurveyCapacity; i++) memory.RememberSurvey(new TileCoord(i, 0), i);
        memory.RememberSurvey(new TileCoord(0, 0), 9000);
        memory.RememberSurvey(new TileCoord(9000, 0), 9001);
        Assert.That(memory.SurveyedTiles.Count, Is.EqualTo(MemoryState.SurveyCapacity));
        Assert.That(memory.SurveyedTiles.ContainsKey(new TileCoord(0, 0)), Is.True);
        Assert.That(memory.SurveyedTiles.ContainsKey(new TileCoord(1, 0)), Is.False);
    }
}
