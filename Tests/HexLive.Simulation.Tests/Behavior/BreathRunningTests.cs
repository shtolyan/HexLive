using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§71: autonomous routine travel spends and re-arms Breath.</summary>
public sealed class BreathRunningTests
{
    [Test]
    public void RoutineAutonomousMovement_UsesWalkRecoveryAndRearmsBreath()
    {
        var (world, npc, start, neighbor, far) = RoutineWalker();
        var movement = new MovementSystem();

        npc.Needs.Breath = 0.02f;
        ArmRoute(world, npc, start, neighbor, far);
        movement.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.IsRunning, Is.True,
                "A routine autonomous route must spend the available breath reserve.");
            Assert.That(npc.Needs.Breath, Is.LessThan(0.02f));
        });

        npc.Needs.Breath = 0f;
        npc.Mind.BreathSpent = false;
        ArmRoute(world, npc, start, neighbor, far);
        movement.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.IsRunning, Is.False);
            Assert.That(npc.Mind.BreathSpent, Is.True);
            Assert.That(npc.Needs.Breath,
                Is.EqualTo(SimBalance.BreathWalkRecoverPerTick).Within(0.000001f),
                "A spent runner must recover while continuing the route at a walk.");
        });

        npc.Needs.Breath = SimBalance.BreathReArm -
            SimBalance.BreathWalkRecoverPerTick * 0.5f;
        ArmRoute(world, npc, start, neighbor, far);
        movement.Run(world);
        Assert.That(npc.Mind.BreathSpent, Is.True,
            "Crossing the re-arm line during this walking tick takes effect next tick.");

        ArmRoute(world, npc, start, neighbor, far);
        movement.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.BreathSpent, Is.False);
            Assert.That(npc.Mind.IsRunning, Is.True,
                "Once recovered to BreathReArm, routine travel starts a new burst.");
        });
    }

    [Test]
    public void ManualMovement_DoesNotInventAnAutonomousRoutineRun()
    {
        var (world, npc, start, neighbor, far) = RoutineWalker();
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Needs.Breath = 0.5f;
        ArmRoute(world, npc, start, neighbor, far);

        new MovementSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.IsRunning, Is.False);
            Assert.That(npc.Needs.Breath, Is.GreaterThan(0.5f));
        });
    }

    [Test]
    public void ManualRunOrder_SpendsBreathWhileWalkPacedOrderDoesNot()
    {
        var (world, npc, start, neighbor, far) = RoutineWalker();
        npc.Mind.ManualControl = true;
        npc.Mind.CurrentGoal = GoalType.PlayerOrder;
        npc.Plan.Goal = GoalType.PlayerOrder;
        npc.Needs.Breath = 0.5f;
        npc.Plan.RunRequested = true;
        ArmRoute(world, npc, start, neighbor, far);

        new MovementSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.IsRunning, Is.True,
                "A run-paced move order must select the running gait.");
            Assert.That(npc.Needs.Breath, Is.LessThan(0.5f),
                "Manual running must use the same finite breath reserve as AI running.");
        });

        npc.Plan.RunRequested = false;
        npc.Needs.Breath = 0.5f;
        ArmRoute(world, npc, start, neighbor, far);
        new MovementSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.IsRunning, Is.False,
                "Switching the pace toggle to walk must replace Run with Walk.");
            Assert.That(npc.Needs.Breath, Is.GreaterThan(0.5f));
        });
    }

    /// <summary>§121.11 (bug #294): темп приходит не из жеста, а из настройки
    /// самой девушки, и переживает сохранение вместе с ней.</summary>
    [Test]
    public void ManualRunPace_ComesFromTheCharacterSettingAndSurvivesSave()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values
            .First(candidate => candidate.Faction == Faction.Colony);
        engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, enabled: true));
        engine.Step();
        var start = npc.CurrentJunction!.Value;
        var destination = world.Junctions.Items.Values.First(candidate =>
            !candidate.Blocked && candidate.Tiles.Count > 0 &&
            HexSpatialMath.HexDistance(npc.Tile, candidate.Tiles[0]) >= 3 &&
            HexSpatialMath.HexDistance(npc.Tile, candidate.Tiles[0]) <= 4 &&
            Connectivity.Reachable(world, start, candidate.Id, npc.Body.CanJump));

        Assert.That(npc.Mind.RunByDefault, Is.True,
            "§121.11: a fresh colonist runs unless the player asks her to walk.");

        // Клик темпа не несёт: его берут у неё самой.
        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();

        Assert.That(npc.Plan.RunRequested, Is.True,
            "A pace-less click must inherit the character's own default pace.");

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(
                   buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        buffer.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(
                   buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Entities.Npcs[npc.Id].Plan.RunRequested, Is.True,
                "An unfinished run must resume as a run after loading.");
            Assert.That(loaded.Entities.Npcs[npc.Id].Mind.RunByDefault, Is.True,
                "The pace setting itself must survive the save, not just the plan.");
        });

        // Тумблер «шагом» — состояние, а не приказ: он переписывает темп уже
        // идущего похода и следующего клика, не роняя сам поход.
        engine.Commands.Enqueue(new SetRunByDefaultCommand(npc.Id, run: false));
        engine.Step();
        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.RunByDefault, Is.False);
            Assert.That(npc.Plan.RunRequested, Is.False,
                "Switching to walk must slow the order already under way.");
        });

        engine.Commands.Enqueue(new MoveToCommand(npc.Id, destination.WorldPosition));
        engine.Step();
        Assert.That(npc.Plan.RunRequested, Is.False,
            "A later click must keep inheriting the character's walk setting.");

        using var walkBuffer = new MemoryStream();
        using (var writer = new BinaryWriter(
                   walkBuffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        walkBuffer.Position = 0;
        var walker = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(
                   walkBuffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(walker, reader);
        }

        Assert.That(walker.Entities.Npcs[npc.Id].Mind.RunByDefault, Is.False,
            "A saved walk setting must not come back as the run default.");
    }

    /// <summary>§121.11: один групповой клик — у каждой свой темп. Общий флаг
    /// на группу был бы враньём: настройка принадлежит девушке.</summary>
    [Test]
    public void GroupMove_GivesEveryActorHerOwnDefaultPace()
    {
        var engine = TestWorld.CreateEngine();
        engine.Step();
        var actors = engine.World.Entities.Npcs.Values
            .Where(candidate => candidate.Faction == Faction.Colony)
            .OrderBy(candidate => candidate.Id.Value)
            .Take(2)
            .ToArray();
        foreach (var actor in actors)
        {
            actor.Mind.ManualControl = true;
        }

        actors[0].Mind.RunByDefault = true;
        actors[1].Mind.RunByDefault = false;

        var click = engine.World.Junctions.Items.Values.First(candidate =>
            !candidate.Blocked && candidate.Tiles.Count > 0 &&
            actors[0].CurrentJunction is { } start &&
            HexSpatialMath.HexDistance(actors[0].Tile, candidate.Tiles[0]) >= 3 &&
            Connectivity.Reachable(engine.World, start, candidate.Id));

        engine.Commands.Enqueue(new GroupMoveCommand(
            actors.Select(actor => actor.Id), click.WorldPosition));
        engine.Step();

        Assert.Multiple(() =>
        {
            Assert.That(actors.All(actor =>
                actor.Mind.CurrentGoal == GoalType.PlayerOrder), Is.True,
                "Both actors must have taken the group order.");
            Assert.That(actors[0].Plan.RunRequested, Is.True,
                "The runner must keep her pace through formation assignment.");
            Assert.That(actors[1].Plan.RunRequested, Is.False,
                "The walker must not be dragged into a run by her partner.");
        });
    }

    [Test]
    public void EnduranceExtendsRunTime_WhileAgilityExtendsRunDistance()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values
            .First(candidate => candidate.Faction == Faction.Colony);

        npc.Attributes.Endurance = 0f;
        npc.Attributes.Agility = 0.5f;
        var lowEnduranceDrain = AttributeMath.BreathDrainMult(npc);
        var averageMoveSpeed = AttributeMath.MoveSpeedMult(npc);

        npc.Attributes.Endurance = 1f;
        var highEnduranceDrain = AttributeMath.BreathDrainMult(npc);
        var sameMoveSpeed = AttributeMath.MoveSpeedMult(npc);

        npc.Attributes.Agility = 1f;
        var highAgilityMoveSpeed = AttributeMath.MoveSpeedMult(npc);
        var sameHighEnduranceDrain = AttributeMath.BreathDrainMult(npc);

        Assert.Multiple(() =>
        {
            Assert.That(highEnduranceDrain, Is.LessThan(lowEnduranceDrain),
                "Endurance must buy a longer burst by reducing breath drain.");
            Assert.That(sameMoveSpeed, Is.EqualTo(averageMoveSpeed).Within(0.000001f),
                "Endurance changes duration, not ground speed.");
            Assert.That(highAgilityMoveSpeed, Is.GreaterThan(averageMoveSpeed),
                "Agility covers more ground during the same burst.");
            Assert.That(sameHighEnduranceDrain,
                Is.EqualTo(highEnduranceDrain).Within(0.000001f),
                "Agility must not silently increase the breath reserve.");
        });
    }

    private static (WorldState World, NPCState Npc, JunctionId Start,
        JunctionId Neighbor, JunctionId Far) RoutineWalker()
    {
        var world = TestWorld.CreateWorld(12345);
        var npc = world.Entities.Npcs.Values
            .First(candidate => candidate.Faction == Faction.Colony);
        foreach (var other in world.Entities.Npcs.Values)
        {
            if (other.Id != npc.Id)
            {
                other.CurrentJunction = null;
            }
        }
        world.Mobs.Clear();

        var start = npc.CurrentJunction ??
            SpatialQueries.FindNearestJunction(world, npc.Position)!.Value;
        var startJunction = world.Junctions.Items[start];
        var neighbor = startJunction.Neighbors.First(candidateId =>
            world.Junctions.Items.TryGetValue(candidateId, out var candidate) &&
            !candidate.Blocked &&
            !HexPathfinder.RequiresJump(world, start, candidateId) &&
            HexPathfinder.TryGetDirectedStepTile(world, start, candidateId, out var tile) &&
            !SpatialQueries.IsSwimTile(tile));
        var far = world.Junctions.Items.Values
            .Where(candidate => !candidate.Blocked && candidate.Tiles.Count > 0)
            .OrderByDescending(candidate =>
                HexSpatialMath.Distance(startJunction.WorldPosition, candidate.WorldPosition))
            .First(candidate => HexSpatialMath.Distance(
                startJunction.WorldPosition, candidate.WorldPosition) >
                SimBalance.ArrivalWalkDistance * 2f).Id;

        npc.Mind.ManualControl = false;
        npc.Mind.CurrentGoal = GoalType.GatherWood;
        npc.Plan.Goal = GoalType.GatherWood;
        npc.Plan.TargetJunctionId = far;
        npc.Plan.Status = PlanStatus.Active;
        npc.Mind.SadWalkUntilTick = 0;
        npc.Mind.ConvalescentUntilTick = 0;
        return (world, npc, start, neighbor, far);
    }

    private static void ArmRoute(
        WorldState world, NPCState npc, JunctionId start, JunctionId neighbor, JunctionId far)
    {
        var startPosition = world.Junctions.Items[start].WorldPosition;
        var neighborPosition = world.Junctions.Items[neighbor].WorldPosition;
        npc.CurrentJunction = start;
        npc.Position = startPosition;
        npc.RotationDegrees = HexSpatialMath.AngleDegrees(
            HexSpatialMath.Normalize(neighborPosition - startPosition));
        npc.Movement.JunctionPath.Clear();
        npc.Movement.JunctionPath.Add(start);
        for (var i = 0; i < 16; i++)
        {
            npc.Movement.JunctionPath.Add(neighbor);
            npc.Movement.JunctionPath.Add(start);
        }
        npc.Movement.JunctionPath.Add(far);
        npc.Movement.PathIndex = 1;
        npc.Movement.IsMoving = true;
        npc.Movement.SetStatus(MovementStatus.Moving);
        npc.Movement.PostTurnTimer = 0f;
        npc.Movement.ClimbPauseTimer = 0f;
        npc.Movement.HopTimer = 0f;
        npc.Movement.HopArmed = false;
        npc.Movement.HopPathIndex = -1;
    }
}

}
