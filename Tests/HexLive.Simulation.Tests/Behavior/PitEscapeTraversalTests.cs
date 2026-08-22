using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PitEscapeTraversalTests
{
    [Test]
    public void InjuredManualColonistCanClimbSingleStep_Bug191()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        npc.CurrentJunction = SpatialQueries.FindNearestJunction(world, npc.Position);
        var start = npc.CurrentJunction;
        Assert.That(start, Is.Not.Null);

        // Other bodies must not turn the one-step traversal contract into an
        // actor-avoidance test.
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (!other.Id.Equals(npc.Id)) other.CurrentJunction = null;
        }
        world.Mobs.Clear();

        npc.Movement.JunctionPath.Clear();
        npc.Body.Parts[BodyPart.LegL] = 0.74f;
        npc.Body.Parts[BodyPart.LegR] = 0.74f;
        npc.Mind.ManualControl = true;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;

        // The production world fixture contains an elevation shelf that is
        // reachable only when the upward step is enabled.
        var target = world.Junctions.Items.Values
            .Where(j => !j.Blocked && j.Tiles.Count > 0 &&
                Connectivity.Reachable(world, start!.Value, j.Id, canJump: true) &&
                !Connectivity.Reachable(world, start.Value, j.Id, canJump: false))
            .OrderBy(j => j.Id.Value)
            .First();
        Assert.That(HexPathfinder.FindPath(
            world, start!.Value, target.Id, null, true, canJump: true),
            Has.Count.GreaterThan(0));
        Assert.That(HexPathfinder.FindPath(
            world, start.Value, target.Id, null, true, canJump: false), Is.Empty);

        var admission = ManualCommandExecutor.Apply(
            world, new MoveToCommand(npc.Id, target.WorldPosition));
        new PathfindingSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Body.CanJump, Is.False,
                "Fixture must retain the exact ordinary-jump rejection from the report.");
            Assert.That(PlanningSystem.CanUseCriticalTraversal(npc), Is.True,
                "Both injured legs still support an emergency scramble.");
            Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                admission.Reason);
            Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder));
            Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(target.Id));
            Assert.That(npc.Movement.JunctionPath, Has.Count.GreaterThan(0),
                "Pathfinding must keep the same emergency permission as command admission.");
        });
    }
}
