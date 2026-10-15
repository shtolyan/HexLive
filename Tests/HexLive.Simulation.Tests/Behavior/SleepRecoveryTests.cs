using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§54.11 r2: designer-facing 4 h bed / 8 h ground sleep contract.</summary>
public sealed class SleepRecoveryTests
{
    private const float SlowTicksPerGameHour = 1000f / 16f;

    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    public void BedComfortMatchesActualStumpExecutionOverEqualTime(int slowInterval)
    {
        var bedWorld=TestWorld.CreateWorld(54013);var stumpWorld=TestWorld.CreateWorld(54013);
        var bedNpc=bedWorld.Entities.Npcs.Values.First();var stumpNpc=stumpWorld.Entities.Npcs.Values.First();
        var bed=bedWorld.Entities.Objects.Values.First(o=>o.DefinitionId==ContentIds.BedBasic);
        var seat=stumpWorld.Junctions.Items.Values.First(j=>j.Fragment==stumpNpc.Fragment && !j.Blocked);
        stumpNpc.CurrentJunction=seat.Id;stumpNpc.Position=seat.WorldPosition;stumpNpc.Tile=seat.Tiles[0];
        var stump=WorldObjectMutations.SpawnObject(stumpWorld,ContentIds.PalmStump,stumpNpc.Fragment,
            stumpNpc.Tile,stumpNpc.CurrentJunction!.Value);
        Prepare(bedWorld,bedNpc,bed,InteractionType.Sleep,GoalType.Sleep,100);
        Prepare(stumpWorld,stumpNpc,stump,InteractionType.Sit,GoalType.Sit,70);
        var decay=new NeedsDecaySystem();var execution=new ExecutionSystem();
        for(var tick=1;tick<=64;tick++)
        {
            bedWorld.Tick=stumpWorld.Tick=tick;
            execution.Run(bedWorld);execution.Run(stumpWorld);
            if(tick%slowInterval==0) {decay.Run(bedWorld);decay.Run(stumpWorld);}
        }
        Assert.That(bedNpc.Execution.CurrentInteraction,Is.EqualTo(InteractionType.Sleep));
        Assert.That(stumpNpc.Execution.CurrentInteraction,Is.EqualTo(InteractionType.Sit));
        Assert.That(bedNpc.Needs.Comfort,Is.GreaterThan(.2f));
        Assert.That(bedNpc.Needs.Comfort,Is.EqualTo(stumpNpc.Needs.Comfort).Within(.00001f));

        void Prepare(WorldState world,NPCState npc,WorldObjectState target,InteractionType action,GoalType goal,int duration)
        {
            world.SlowIntervalTicks=slowInterval;world.Environment.Phase=DayPhase.Night;world.Environment.IsRaining=false;
            foreach(var fire in world.Entities.Objects.Values.Where(o=>o.DefinitionId=="campfire.spot")) fire.ResourceAmount=0;
            PrepareNeeds(npc);npc.WornItems.Clear();npc.Needs.Comfort=.2f;
            npc.Mind.CurrentGoal=goal;npc.Mind.WakeGraceUntilTick=0;npc.Mind.AdrenalineUntilTick=0;
            npc.Plan.Status=PlanStatus.Active;npc.Plan.Goal=goal;npc.Plan.Steps.Clear();
            npc.Plan.TargetObjectId=target.Id;
            npc.Execution.Status=ExecutionStatus.InProgress;npc.Execution.CurrentInteraction=action;
            npc.Execution.TargetObject=target.Id;npc.Execution.StartTick=0;npc.Execution.EndTick=duration;
            target.IsOccupied=true;target.CurrentUser=npc.Id;
        }
    }

    [Test]
    public void BedComfortReadsCurrentStumpTuningAndCapsAtFull()
    {
        var world=TestWorld.CreateWorld(54014);var npc=world.Entities.Npcs.Values.First();
        PrepareSleeping(npc,.5f);npc.WornItems.Clear();npc.Needs.Comfort=.2f;
        npc.Execution.TargetObject=world.Entities.Objects.Values.First(o=>o.DefinitionId==ContentIds.BedBasic).Id;
        world.Environment.Phase=DayPhase.Night;world.Environment.IsRaining=false;
        foreach(var fire in world.Entities.Objects.Values.Where(o=>o.DefinitionId=="campfire.spot")) fire.ResourceAmount=0;
        var sit=world.Content.ObjectDefinitions[ContentIds.PalmStump].Interactions.Single(i=>i.Type==InteractionType.Sit);
        var oldDelta=sit.Effects.ComfortDelta;var oldDuration=sit.DurationTicks;
        try
        {
            sit.Effects.ComfortDelta=.3f;sit.DurationTicks=60;
            var before=npc.Needs.Comfort;new NeedsDecaySystem().Run(world);
            Assert.That(npc.Needs.Comfort-before,Is.EqualTo(.3f/60*world.SlowIntervalTicks-SimBalance.ComfortRate).Within(.00001f));
            npc.Needs.Comfort=.999f;new NeedsDecaySystem().Run(world);Assert.That(npc.Needs.Comfort,Is.EqualTo(1f));
        }
        finally {sit.Effects.ComfortDelta=oldDelta;sit.DurationTicks=oldDuration;}
    }

    [Test]
    public void ZeroToFullTakesFourHoursInBedAndEightOnGround()
    {
        var groundWorld = TestWorld.CreateWorld(54011);
        var groundSleeper = groundWorld.Entities.Npcs.Values.First();
        PrepareSleeping(groundSleeper, energy: 0.5f);

        var groundBefore = groundSleeper.Needs.Energy;
        new NeedsDecaySystem().Run(groundWorld);
        var groundPerSlowTick = groundSleeper.Needs.Energy - groundBefore;

        var bedWorld = TestWorld.CreateWorld(54011);
        var bedSleeper = bedWorld.Entities.Npcs.Values.First();
        PrepareSleeping(bedSleeper, energy: 0.5f);
        var bed = bedWorld.Entities.Objects.Values.First(
            obj => obj.DefinitionId == ContentIds.BedBasic);
        bedSleeper.Execution.TargetObject = bed.Id;

        var bedBefore = bedSleeper.Needs.Energy;
        new NeedsDecaySystem().Run(bedWorld);
        var bedPerSlowTick = bedSleeper.Needs.Energy - bedBefore;

        Assert.Multiple(() =>
        {
            Assert.That(SimBalance.SleepEnergyFireBonus, Is.Zero,
                "A nearby fire must not silently shorten the hour contract.");
            Assert.That(groundWorld.Content.ObjectDefinitions[ContentIds.BedBasic]
                    .Interactions.Single(i => i.Type == InteractionType.Sleep)
                    .Effects.EnergyDelta,
                Is.Zero,
                "The timed bed interaction must not own a second recovery stream.");
            Assert.That(groundPerSlowTick, Is.EqualTo(0.002f).Within(0.000001f));
            Assert.That(bedPerSlowTick, Is.EqualTo(0.004f).Within(0.000001f));
            Assert.That(1f / groundPerSlowTick / SlowTicksPerGameHour,
                Is.EqualTo(8f).Within(0.001f));
            Assert.That(1f / bedPerSlowTick / SlowTicksPerGameHour,
                Is.EqualTo(4f).Within(0.001f));
        });
    }

    [Test]
    public void SleepingMetabolismIsOneTenthOfAwakeMetabolism()
    {
        var awakeWorld = TestWorld.CreateWorld(54012);
        var awake = awakeWorld.Entities.Npcs.Values.First();
        PrepareNeeds(awake);
        awake.Execution.CurrentInteraction = null;
        var awakeHunger = awake.Needs.Hunger;
        var awakeThirst = awake.Needs.Thirst;
        new NeedsDecaySystem().Run(awakeWorld);

        var sleepWorld = TestWorld.CreateWorld(54012);
        var sleeper = sleepWorld.Entities.Npcs.Values.First();
        PrepareNeeds(sleeper);
        sleeper.Execution.CurrentInteraction = InteractionType.Sleep;
        var sleepHunger = sleeper.Needs.Hunger;
        var sleepThirst = sleeper.Needs.Thirst;
        new NeedsDecaySystem().Run(sleepWorld);

        var awakeHungerDelta = awake.Needs.Hunger - awakeHunger;
        var awakeThirstDelta = awake.Needs.Thirst - awakeThirst;
        var sleepHungerDelta = sleeper.Needs.Hunger - sleepHunger;
        var sleepThirstDelta = sleeper.Needs.Thirst - sleepThirst;

        Assert.Multiple(() =>
        {
            Assert.That(Spec85.SleepMetabolismFactor, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(sleepHungerDelta / awakeHungerDelta,
                Is.EqualTo(0.1f).Within(0.001f));
            Assert.That(sleepThirstDelta / awakeThirstDelta,
                Is.EqualTo(0.1f).Within(0.001f));
        });
    }

    private static void PrepareSleeping(NPCState npc, float energy)
    {
        PrepareNeeds(npc);
        npc.Needs.Energy = energy;
        npc.Execution.CurrentInteraction = InteractionType.Sleep;
        npc.Execution.TargetObject = null;
    }

    private static void PrepareNeeds(NPCState npc)
    {
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Needs.Energy = 0.8f;
        npc.Needs.ThermalComfort = 0f;
        npc.Needs.ThermalDiscomfort = 0f;
    }
}

}
