using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class ConsciousCrawlerCombatTests
{
    private static (WorldState world, NPCState attacker, NPCState target) Pair()
    {
        var world = TestWorld.CreateWorld();
        world.Tick = 100;
        var actors = world.Entities.Npcs.Values.Take(2).ToArray();
        var attacker = actors[0];
        var target = actors[1];
        var from = world.Junctions.Items.Values.First(j => j.Neighbors.Count > 0);
        var to = world.Junctions.Items[from.Neighbors[0]];
        attacker.CurrentJunction = from.Id;
        attacker.Position = from.WorldPosition;
        attacker.Tile = from.Tiles[0];
        target.CurrentJunction = to.Id;
        target.Position = to.WorldPosition;
        target.Tile = to.Tiles[0];
        attacker.Mind.ManualControl = target.Mind.ManualControl = true;
        attacker.Mind.CurrentGoal = GoalType.PlayerAttack;
        attacker.Mind.ManualAttackNpcId = target.Id;
        attacker.Mind.CombatOpponentNpcId = target.Id;
        attacker.IsFighting = true;
        Assert.That(InteractionReach.CanStrike(world, attacker, target), Is.True);
        return (world, attacker, target);
    }

    [TestCase(0f)]
    [TestCase(0.3f)]
    public void ConsciousCrawlerCanBeHitAndAnswer(float legFunction)
    {
        var (world, attacker, target) = Pair();
        target.Body.Parts[BodyPart.LegL] = legFunction;
        target.Body.Parts[BodyPart.LegR] = legFunction;
        Assert.That(target.Body.IsCrawling, Is.True);
        Assert.That(target.IsUnconscious(world.Tick), Is.False);

        new RaidSystem().Run(world);
        Assert.That(target.Mind.CombatOpponentNpcId, Is.EqualTo(attacker.Id),
            "A conscious crawler with usable hands must answer an attack.");

        var combat = new HumanCombatSystem();
        var attackerHit = attacker.HitStampTick;
        var targetHit = target.HitStampTick;
        for (var i = 0; i < 80 &&
             (attacker.HitStampTick == attackerHit || target.HitStampTick == targetHit); i++)
        {
            world.Tick++;
            combat.Run(world);
        }
        Assert.Multiple(() =>
        {
            Assert.That(target.HitStampTick, Is.GreaterThan(targetHit), "The ordered hit must land.");
            Assert.That(attacker.HitStampTick, Is.GreaterThan(attackerHit), "The crawler must hit back.");
        });
    }

    [Test]
    public void ReplySelectionDoesNotDiscardConsciousCrawler()
    {
        var (world, attacker, target) = Pair();
        attacker.Body.Parts[BodyPart.LegL] = 0f;
        HumanCombatPairing.SelectNearestReply(world, target);
        Assert.That(target.Mind.CombatOpponentNpcId, Is.EqualTo(attacker.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SleepingTargetWakesIncludingManualBedSleep(bool inBed)
    {
        var (world, attacker, target) = Pair();
        target.Mind.CurrentGoal = GoalType.Sleep;
        target.Plan.Goal = GoalType.Sleep;
        target.Plan.Status = PlanStatus.Active;
        target.Execution.Status = ExecutionStatus.InProgress;
        target.Execution.CurrentInteraction = InteractionType.Sleep;
        target.Execution.EndTick = int.MaxValue;
        var bed = inBed ? world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.BedBasic) : null;
        if (bed != null)
        {
            target.Plan.TargetObjectId = bed.Id;
            Assert.That(BedSleep.TryEnter(world, target, bed, int.MaxValue, null), Is.True);
            attacker.Position = target.Position;
            attacker.Tile = target.Tile;
        }

        new RaidSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(target.Execution.CurrentInteraction, Is.Null, "Attack must interrupt sleep.");
            if (bed != null)
            {
                Assert.That(bed.IsOccupied, Is.False);
                Assert.That(bed.CurrentUser, Is.Null);
            }
            else
            {
                Assert.That(target.IsFighting, Is.True);
                Assert.That(target.Mind.CombatOpponentNpcId, Is.EqualTo(attacker.Id));
            }
        });
    }

    [Test]
    public void UnconsciousTargetStillEndsTheFightWithoutAHit()
    {
        var (world, attacker, target) = Pair();
        target.Mind.FaintedUntilTick = world.Tick + 100;
        var hit = target.HitStampTick;
        new HumanCombatSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(target.HitStampTick, Is.EqualTo(hit));
            Assert.That(attacker.Mind.CombatOpponentNpcId, Is.Null);
            Assert.That(attacker.IsFighting, Is.False);
        });
    }
}
