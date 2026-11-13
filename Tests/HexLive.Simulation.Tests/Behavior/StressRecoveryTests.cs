using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
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

    [Test]
    public void StressSnapshotNamesThePursuingWolfAndHostilePerson()
    {
        var world = TestWorld.CreateWorld(187);
        var npc = world.Entities.Npcs.Values.First();
        var other = world.Entities.Npcs.Values.First(candidate => candidate.Id != npc.Id);
        npc.Needs.Hunger = npc.Needs.Thirst = .1f;
        npc.Mind.CurrentGoal = GoalType.Flee;
        npc.Perception.Agents.Add(new PerceivedAgent { Id = other.Id, CanSee = true });
        other.Social.GetOrCreate(npc.Id).Affinity = -.5f;
        npc.Perception.Mobs.Add(new PerceivedMob { Id = 11, MobId = MobIds.Dog, Health = 1f, TargetsMe = true });
        npc.Perception.Mobs.Add(new PerceivedMob { Id = 12, MobId = MobIds.Dog, Health = 1f });

        new NeedsDecaySystem().Run(world);
        var stress = WorldSnapshotExporter.Export(world).Npcs.Single(person => person.Id == npc.Id).EffectImpacts;
        Assert.That(stress, Does.Contain("Stress\tThreatened\tNegative\tmob\t11"));
        Assert.That(stress, Does.Contain($"Stress\tThreatened\tNegative\tnpc\t{other.Id.Value}"));
        Assert.That(stress, Does.Not.Contain("Stress\tThreatened\tNegative\tmob\t12"));

        npc.Mind.CurrentGoal = GoalType.None;
        npc.Perception.Agents.Clear();
        npc.Perception.Hostiles.Clear();
        npc.Perception.Mobs.Clear();
        new NeedsDecaySystem().Run(world);
        stress = WorldSnapshotExporter.Export(world).Npcs.Single(person => person.Id == npc.Id).EffectImpacts;
        Assert.That(stress.Any(row => row.StartsWith("Stress\tThreatened\tNegative\t")), Is.False);
    }
}
