using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§54.11 r2: designer-facing 4 h bed / 8 h ground sleep contract.</summary>
public sealed class SleepRecoveryTests
{
    private const float SlowTicksPerGameHour = 1000f / 16f;

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
            Assert.That(SimBalance.GroundSleepEnergy, Is.Zero,
                "Ground interaction must not add a hidden second recovery stream.");
            Assert.That(SimBalance.BedEnergy, Is.Zero,
                "Bed interaction must not add a hidden second recovery stream.");
            Assert.That(SimBalance.SleepEnergyFireBonus, Is.Zero,
                "A nearby fire must not silently shorten the hour contract.");
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
