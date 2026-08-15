using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class CriticalNeedPriorityTests
{
    [Test]
    public void AvailableFoodResponse_CannotBeInterruptedByHigherPeacetimeChore()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.IsStarving = true;
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GetFood, 2.1f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GatherWood, 2.6f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.Explore, 0.33f, 0f));
        npc.Mind.LastScores.Add(Score(GoalType.Idle, 0.06f, 0f));

        DecisionSystem.SuppressPeacetimeDuringCriticalNeeds(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.GetFood).FinalScore, Is.EqualTo(2.1f));
            Assert.That(Find(npc, GoalType.GatherWood).FinalScore, Is.Zero);
            Assert.That(Find(npc, GoalType.Explore).FinalScore, Is.Zero);
            Assert.That(Find(npc, GoalType.Idle).FinalScore, Is.GreaterThan(0f));
        });
    }

    [Test]
    public void EmergencyToolChain_RemainsAvailableWhenItIsTheFoodWaterAnswer()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.IsDehydrated = true;
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GetWater, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GatherStone, 2.0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.CraftKnife, 2.0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.BuildRaft, 3.0f, 0f));

        DecisionSystem.SuppressPeacetimeDuringCriticalNeeds(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.GatherStone).FinalScore, Is.EqualTo(2.0f));
            Assert.That(Find(npc, GoalType.CraftKnife).FinalScore, Is.EqualTo(2.0f));
            Assert.That(Find(npc, GoalType.BuildRaft).FinalScore, Is.Zero);
        });
    }

    [Test]
    public void NoAvailableEmergencyResponse_LeavesExploreAsDiscoveryFallback()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.IsStarving = true;
        npc.Mind.IsDehydrated = true;
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GetFood, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GetWater, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.Explore, 0.33f, 0f));

        DecisionSystem.SuppressPeacetimeDuringCriticalNeeds(npc);

        Assert.That(Find(npc, GoalType.Explore).FinalScore, Is.EqualTo(0.33f));
    }

    [Test]
    public void NoAvailableEmergencyResponse_StopsUnrelatedChores()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.IsDehydrated = true;
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GetWater, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GatherTools, 2.6f, 0f));
        npc.Mind.LastScores.Add(Score(GoalType.Explore, 0.33f, 0f));
        npc.Mind.LastScores.Add(Score(GoalType.Idle, 0.06f, 0f));

        DecisionSystem.SuppressPeacetimeDuringCriticalNeeds(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.GatherTools).FinalScore, Is.Zero);
            Assert.That(Find(npc, GoalType.Explore).FinalScore, Is.EqualTo(0.33f));
            Assert.That(Find(npc, GoalType.Idle).FinalScore, Is.GreaterThan(0f));
        });
    }

    [Test]
    public void BleederWithoutKnownTreatment_ExploresInsteadOfIdling()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.CraftBandage, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GatherHerb, 0f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.GatherTools, 2.6f, 0f));
        npc.Mind.LastScores.Add(Score(GoalType.Explore, 0.33f, 0f));

        DecisionSystem.SuppressPeacetimeDuringBleeding(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.GatherTools).FinalScore, Is.Zero);
            Assert.That(Find(npc, GoalType.Explore).FinalScore, Is.EqualTo(0.33f));
        });
    }

    [Test]
    public void BleederWithKnownHerbs_DoesNotWanderAwayFromTreatment()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GatherHerb, 1.7f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.Explore, 0.33f, 0f));

        DecisionSystem.SuppressPeacetimeDuringBleeding(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.GatherHerb).FinalScore, Is.EqualTo(1.7f));
            Assert.That(Find(npc, GoalType.Explore).FinalScore, Is.Zero);
        });
    }

    [Test]
    public void BleedingSwimmerKeepsDryLandingPrerequisite()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.CraftBandage, 1.7f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.ReachSafeGround, 2.1f, 1f));

        DecisionSystem.SuppressPeacetimeDuringBleeding(npc);

        Assert.That(Find(npc, GoalType.ReachSafeGround).FinalScore,
            Is.EqualTo(2.1f));
    }

    [Test]
    public void CriticalSwimmerMustReachDryGroundBeforeFetchingWater()
    {
        var npc = TestWorld.CreateWorld().Entities.Npcs.Values.First();
        npc.Mind.IsDehydrated = true;
        npc.Mind.LastScores.Clear();
        npc.Mind.LastScores.Add(Score(GoalType.GetWater, 2.15f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.ReachSafeGround, 2.10f, 1f));
        npc.Mind.LastScores.Add(Score(GoalType.Idle, 0.15f, 0f));

        DecisionSystem.SuppressPeacetimeDuringCriticalNeeds(npc);

        Assert.Multiple(() =>
        {
            Assert.That(Find(npc, GoalType.ReachSafeGround).FinalScore, Is.EqualTo(2.10f));
            Assert.That(Find(npc, GoalType.GetWater).FinalScore, Is.Zero,
                "The dry landing is a prerequisite, not a competing utility bid.");
            Assert.That(Find(npc, GoalType.Idle).FinalScore, Is.GreaterThan(0f));
        });
    }

    [Test]
    public void DehydratedActiveLaundryCannotOverrideItsOwnDryRouteSafety()
    {
        var world = TestWorld.CreateWorld(104729);
        var npc = world.Entities.Npcs.Values.First(n => n.WornItems.Count > 0);
        foreach (var garment in npc.WornItems)
        {
            garment.Dirtiness = 1f;
        }

        npc.Needs.Blood = 1f;
        npc.Needs.Thirst = 1f;
        npc.Mind.IsDehydrated = true;
        npc.Mind.CurrentGoal = GoalType.WashClothes;
        npc.Plan.Goal = GoalType.WashClothes;
        npc.Plan.Status = PlanStatus.Active;
        npc.Execution.Status = ExecutionStatus.None;

        new DecisionSystem().Run(world);

        Assert.That(Find(npc, GoalType.WashClothes).FinalScore, Is.Zero,
            "An active wash must yield when safe routing forbids renewed water travel.");
    }

    private static GoalScore Score(GoalType goal, float final, float emergency) => new()
    {
        Goal = goal,
        EmergencyModifier = emergency,
        FinalScore = final
    };

    private static GoalScore Find(HexLive.Simulation.Agents.NPCState npc, GoalType goal) =>
        npc.Mind.LastScores.Single(score => score.Goal == goal);
}

}
