using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§81.15 / bug #250: an abuse scene pauses and resumes manual work.</summary>
public sealed class ManualAbuseSuspensionTests
{
    [Test]
    public void ApproachingAbuserFreezesManualRouteWithoutDestroyingIt()
    {
        var world = TestWorld.CreateWorld(250);
        world.TickDeltaTime = 0.25f;
        var actors = world.Entities.Npcs.Values.Take(2).ToArray();
        var mark = actors[0];
        var abuser = actors[1];
        var start = world.Junctions.Items.Keys.First();
        var destination = SpatialQueries.GetPassableNeighbors(world, start).First();

        mark.CurrentJunction = start;
        mark.Tile = world.Junctions.Items[start].Tiles[0];
        mark.Position = world.Junctions.Items[start].WorldPosition;
        mark.Mind.ManualControl = true;
        mark.Mind.CurrentGoal = GoalType.PlayerOrder;
        mark.Plan.Goal = GoalType.PlayerOrder;
        mark.Plan.Status = PlanStatus.Active;
        mark.Plan.TargetJunctionId = destination;
        mark.Movement.JunctionPath.Clear();
        mark.Movement.JunctionPath.Add(destination);
        mark.Movement.PathIndex = 0;
        mark.Movement.IsMoving = true;
        mark.Movement.SetStatus(MovementStatus.Moving);

        abuser.Tile = mark.Tile;
        abuser.Position = mark.Position;
        abuser.Mind.CurrentGoal = GoalType.Abuse;
        abuser.Mind.AbuseTargetNpcId = mark.Id;
        mark.Mind.PendingAbuseFrom = abuser.Id;

        var before = mark.Position;
        new MovementSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(mark.Position, Is.EqualTo(before));
            Assert.That(mark.Plan.Status, Is.EqualTo(PlanStatus.Active));
            Assert.That(mark.Plan.Goal, Is.EqualTo(GoalType.PlayerOrder));
            Assert.That(mark.Movement.JunctionPath, Has.Count.EqualTo(1));
            Assert.That(mark.Movement.IsMoving, Is.True);
        });

        mark.Mind.PendingAbuseFrom = null;
        for (var i = 0; i < 20 && mark.Position.Equals(before); i++)
        {
            new MovementSystem().Run(world);
        }

        Assert.That(mark.Position, Is.Not.EqualTo(before),
            "После сцены должен продолжиться тот же маршрут, без нового приказа.");
    }

    [Test]
    public void SuspendedInteractionKeepsItsRemainingDuration()
    {
        var world = TestWorld.CreateWorld(2501);
        var actors = world.Entities.Npcs.Values.Take(2).ToArray();
        var mark = actors[0];
        var abuser = actors[1];
        mark.Mind.ManualControl = true;
        mark.Mind.CurrentGoal = GoalType.PlayerOrder;
        mark.Plan.Goal = GoalType.PlayerOrder;
        mark.Plan.Status = PlanStatus.Active;
        mark.Execution.Status = ExecutionStatus.InProgress;
        mark.Execution.CurrentInteraction = InteractionType.Harvest;
        mark.Execution.StartTick = 10;
        mark.Execution.EndTick = 30;

        abuser.Tile = mark.Tile;
        abuser.Mind.CurrentGoal = GoalType.Abuse;
        abuser.Execution.Status = ExecutionStatus.InProgress;
        abuser.Execution.CurrentInteraction = InteractionType.Abuse;
        mark.Mind.PendingAbuseFrom = abuser.Id;

        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(mark.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
            Assert.That(mark.Execution.StartTick, Is.EqualTo(11));
            Assert.That(mark.Execution.EndTick, Is.EqualTo(31));
            Assert.That(mark.Plan.Status, Is.EqualTo(PlanStatus.Active));
        });
    }
}
