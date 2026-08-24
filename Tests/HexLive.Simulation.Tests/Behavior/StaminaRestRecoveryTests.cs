using System.Linq;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
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

    [Test]
    public void IdleRestRecoversStaminaAndReportsRestInsteadOfWork()
    {
        var world = TestWorld.CreateWorld(220);
        var npc = world.Entities.Npcs.Values.First();
        npc.Execution.Status = ExecutionStatus.InProgress;
        npc.Execution.CurrentInteraction = InteractionType.Rest;
        npc.Needs.Hunger = 0f;
        npc.Needs.Energy = 1f;
        npc.Needs.Comfort = 1f;
        npc.Needs.Stamina = 0.2f;
        var before = npc.Needs.Stamina;

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Stamina - before,
                Is.EqualTo(SimBalance.StaminaRestGain).Within(0.000001f));
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stamina &&
                    impact.Kind == EffectKind.Resting &&
                    impact.Direction == EffectImpactDirection.Positive),
                Is.True);
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stamina &&
                    impact.Kind == EffectKind.Working),
                Is.False);
        });
    }
}
