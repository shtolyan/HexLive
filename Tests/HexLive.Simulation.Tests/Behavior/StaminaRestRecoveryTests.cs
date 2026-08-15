using System.Linq;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class StaminaRestRecoveryTests
{
    [Test]
    public void SittingNeverLowersStaminaWhenDynamicCeilingFalls()
    {
        var world = TestWorld.CreateWorld(152);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.CurrentInteraction = InteractionType.Sit;
        npc.Needs.Hunger = 1f;
        npc.Needs.Energy = 0f;
        npc.Needs.Comfort = 0f;
        npc.Needs.Stamina = 0.8f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Stamina, Is.EqualTo(before).Within(0.000001f),
            "a positive rest tick must not clamp accumulated stamina downward");
    }

    [Test]
    public void SittingBelowCeilingStillGainsCanonicalRestRate()
    {
        var world = TestWorld.CreateWorld(1521);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.CurrentInteraction = InteractionType.Sit;
        npc.Needs.Hunger = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Stamina = 0.2f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Stamina - before,
            Is.EqualTo(SimBalance.StaminaRestGain).Within(0.000001f));
    }
}
