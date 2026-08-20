using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class StressRecoveryTests
{
    [Test]
    public void FarDangerMemoryDoesNotPinStressDuringCalmWorkday()
    {
        var world = TestWorld.CreateWorld(186);
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Stress = 0.75f;
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Health = 1f;
        npc.Memory.Dangers.Add(new DangerMemory
        {
            Tile = npc.Tile,
            Tick = world.Tick,
        });

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Stress,
                Is.EqualTo(0.75f - SimBalance.StressDownRate).Within(0.000001f));
            Assert.That(npc.Memory.Dangers, Has.Count.EqualTo(1),
                "stress recovery must not erase the routing memory");
        });
    }

    [Test]
    public void ActiveFleeStillRaisesStress()
    {
        var world = TestWorld.CreateWorld(187);
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Stress = 0.5f;
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Health = 1f;
        npc.Mind.CurrentGoal = GoalType.Flee;

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.Needs.Stress,
            Is.EqualTo(0.5f + SimBalance.StressUpRate).Within(0.000001f));
    }
}
