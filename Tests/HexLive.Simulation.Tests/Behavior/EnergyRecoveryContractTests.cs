using System.Linq;
using System.Reflection;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§42 / bug #150: Energy is recovered by sleep, never awake rest.</summary>
public sealed class EnergyRecoveryContractTests
{
    [Test]
    public void GroundChairAndStumpHaveNoEnergyRecoveryData()
    {
        var world = TestWorld.CreateWorld(42150);
        var chair = world.Content.ObjectDefinitions["chair.basic"];
        var stump = world.Content.ObjectDefinitions["stump.palm"];

        Assert.Multiple(() =>
        {
            Assert.That(chair.Interactions.Single(i => i.Type == InteractionType.Sit)
                .Effects.EnergyDelta, Is.Zero);
            Assert.That(stump.Interactions.Single(i => i.Type == InteractionType.Sit)
                .Effects.EnergyDelta, Is.Zero);
            Assert.That(SimData.BalanceValues().Keys,
                Does.Not.Contain("SimBalance.GroundSitEnergy"));
            Assert.That(SimData.BalanceValues().Keys,
                Does.Not.Contain("SimBalance.ChairEnergy"));
        });
    }

    [Test]
    public void AdrenalineKeepsAlertWindowWithoutManufacturingEnergy()
    {
        var world = TestWorld.CreateWorld(42151);
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Energy = 0.01f;

        DamageReactionSystemHelpers.GrantAdrenaline(world, npc, 0.2f, "test");

        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.AdrenalineUntilTick, Is.GreaterThan(world.Tick));
            Assert.That(npc.Needs.Energy, Is.EqualTo(0.01f).Within(0.000001f));
            Assert.That(SimData.BalanceValues().Keys,
                Does.Not.Contain("SimBalance.AdrenalineEnergyFloor"));
        });
    }

    [Test]
    public void PositiveInteractionEnergyIsAcceptedOnlyWhileSleeping()
    {
        var apply = typeof(ExecutionSystem).GetMethod(
            "ApplyEffectsScaled", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(apply, Is.Not.Null);

        var npc = new NPCState();
        var effects = new InteractionEffects { EnergyDelta = 0.4f };

        npc.Needs.Energy = 0.2f;
        npc.Execution.CurrentInteraction = InteractionType.Sit;
        apply.Invoke(null, new object[] { npc, effects, 1f });
        Assert.That(npc.Needs.Energy, Is.EqualTo(0.2f).Within(0.000001f),
            "Legacy/external sit data must not restore Energy.");

        npc.Execution.CurrentInteraction = InteractionType.Sleep;
        apply.Invoke(null, new object[] { npc, effects, 1f });
        Assert.That(npc.Needs.Energy, Is.EqualTo(0.6f).Within(0.000001f),
            "Sleep remains the sole interaction allowed to restore Energy.");
    }
}

}
