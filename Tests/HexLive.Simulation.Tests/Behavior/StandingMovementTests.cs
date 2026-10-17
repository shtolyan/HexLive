using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class StandingMovementTests
{
    [TestCase(InteractionType.Rest, InterruptionCause.ThreatReroute)]
    [TestCase(InteractionType.Sleep, InterruptionCause.ThreatReroute)]
    [TestCase(InteractionType.Rest, InterruptionCause.PlayerCommand)]
    [TestCase(InteractionType.Sleep, InterruptionCause.PlayerCommand)]
    [TestCase(InteractionType.Sit, InterruptionCause.ThreatReroute)]
    [TestCase(InteractionType.ConsoleOther, InterruptionCause.ThreatReroute)]
    public void InterruptedLowPoseGetsTheSameUnshortenableRiseWindow(InteractionType pose, InterruptionCause cause)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.CurrentInteraction = pose;
        npc.Execution.Status = ExecutionStatus.InProgress;
        if (pose == InteractionType.ConsoleOther)
        {
            var patient = world.Entities.Npcs.Values.First(n => n != npc);
            patient.Mind.CryingUntilTick = world.Tick + 100;
            npc.Plan.TargetAgentId = patient.Id;
        }
        var expected = world.Tick + (pose == InteractionType.ConsoleOther ? 28 : 20);
        Assert.That(PlanInterruption.TryAbort(world, npc, cause, "standing regression"), Is.True);
        Assert.That(npc.Mind.WakeGraceUntilTick, Is.GreaterThanOrEqualTo(expected));
        Assert.That(npc.Execution.CurrentInteraction, Is.Null);
        Assert.That(npc.Movement.IsMoving, Is.False);
    }

    [Test]
    public void AlreadyArmedMovementCannotTranslateDuringRiseAndResumesWithoutLosingItsPath()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Plan.Status = PlanStatus.Active;
        npc.Plan.TargetJunctionId = world.Junctions.Items.Keys.First(id => id != npc.CurrentJunction);
        new PathfindingSystem().Run(world);
        Assert.That(npc.Movement.IsMoving, Is.True, "Precondition: a real route must be armed.");
        var position = npc.Position;
        var index = npc.Movement.PathIndex;
        var path = npc.Movement.JunctionPath.ToArray();
        npc.Mind.WakeGraceUntilTick = world.Tick + 20;
        var deadline = npc.Mind.WakeGraceUntilTick;
        var movement = new MovementSystem();
        while (world.Tick < deadline)
        {
            npc.Mind.IsRunning = true;
            movement.Run(world);
            Assert.That(npc.Position, Is.EqualTo(position));
            Assert.That(npc.Movement.PathIndex, Is.EqualTo(index));
            Assert.That(npc.Movement.JunctionPath, Is.EqualTo(path));
            Assert.That(npc.Mind.IsRunning, Is.False);
            Assert.That(npc.Mind.WakeGraceUntilTick, Is.EqualTo(deadline), "Polling must not rearm the cooldown.");
            world.Tick++;
        }
        for (var i = 0; i < 16 && npc.Position.Equals(position); i++) { movement.Run(world); world.Tick++; }
        Assert.That(npc.Position, Is.Not.EqualTo(position));
    }

    [Test]
    public void AStandingBodyLandingFromAFallGetsTheFullRiseRecovery()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Body.Parts[BodyPart.LegL] = .1f;
        Assert.That(npc.Body.CanJump, Is.False);
        Assert.That(npc.Body.IsCrawling, Is.False);
        npc.Movement.HopTimer = .1f;
        npc.Movement.HopUp = false;
        npc.Movement.HopFrom = npc.Position;
        npc.Movement.HopTo = npc.Position + new HexLive.Simulation.Common.Float2(.1f, 0);
        npc.Movement.HopTargetTile = npc.Tile;
        new MovementSystem().Run(world);
        Assert.That(npc.Movement.ClimbPauseTimer, Is.GreaterThanOrEqualTo(5f));
    }

    [Test]
    public void RecoveryWithoutAPathExpiresAndAnUrgentOrderCannotClearIt()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Movement.ClimbPauseTimer = 5f;
        Assert.That(PlanInterruption.TryAbort(world, npc, InterruptionCause.PlayerCommand, "new command"), Is.True);
        Assert.That(npc.Movement.ClimbPauseTimer, Is.EqualTo(5f));
        var position = npc.Position;
        var rotation = npc.RotationDegrees;
        for (var i = 0; i < 20; i++) { new MovementSystem().Run(world); world.Tick++; }
        Assert.That(npc.Movement.ClimbPauseTimer, Is.Zero);
        Assert.That(npc.Position, Is.EqualTo(position));
        Assert.That(npc.RotationDegrees, Is.EqualTo(rotation));
    }

    [Test]
    public void PainOrRaidEndingCryingAlsoWaitsAndRepeatedCleanupCannotRearmTheClock()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.CryingUntilTick = world.Tick + 100;
        LyingSpot.EndCrying(world, npc);
        var deadline = npc.Mind.WakeGraceUntilTick;
        Assert.That(deadline, Is.EqualTo(world.Tick + 20));
        world.Tick++;
        LyingSpot.EndCrying(world, npc);
        PlanInterruption.TryAbort(world, npc, InterruptionCause.ThreatReroute, "repeat order during rise");
        Assert.That(npc.Mind.WakeGraceUntilTick, Is.EqualTo(deadline));
    }

    [Test]
    public void CombatStanceCannotGlideOrStartASwingWhileTheBodyRises()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        var opponent = world.Entities.Npcs.Values.First(n => n != actor);
        opponent.Position = actor.Position + new HexLive.Simulation.Common.Float2(.1f, 0);
        opponent.Tile = actor.Tile;
        actor.Mind.CombatOpponentNpcId = opponent.Id;
        actor.Mind.WakeGraceUntilTick = world.Tick + 20;
        actor.StrikeLandsAtTick = world.Tick + 1;
        actor.AttackAnimUntilTick = world.Tick + 10;
        var position = actor.Position;
        new HumanCombatSystem().Run(world);
        Assert.That(actor.Position, Is.EqualTo(position));
        Assert.That(actor.StrikeLandsAtTick, Is.Zero);
        Assert.That(actor.AttackAnimUntilTick, Is.Zero);
        Assert.That(MeleeSwing.TryAdvanceSwing(world, actor, true, out _, out _, out _), Is.False);
    }

    [Test]
    public void OldOrTunedWakeDurationCannotRemoveAnimationTimeOrShortenALongerPause()
    {
        var old = AiBalance.WakeGraceTicks;
        try
        {
            AiBalance.WakeGraceTicks = 0;
            Assert.That(StandUpMath.GraceTicks, Is.EqualTo(20));
            AiBalance.WakeGraceTicks = 40;
            Assert.That(StandUpMath.GraceTicks, Is.EqualTo(40));
        }
        finally { AiBalance.WakeGraceTicks = old; }
    }
}
