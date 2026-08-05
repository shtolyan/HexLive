using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§115: требование, ответ и исход драки за лагерь.</summary>
public sealed class CampExpulsionTests
{
    private static (WorldState world, NPCState colonist, NPCState outsider) Pair()
    {
        var world = TestWorld.CreateWorld();
        var colonist = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var outsider = world.Entities.Npcs.Values.First(n => n.Faction != Faction.Colony);
        return (world, colonist, outsider);
    }

    private static void Teleport(WorldState world, NPCState npc, TileCoord tile)
    {
        var junctionId = world.Tiles.Items[tile].Junctions[0];
        var junction = world.Junctions.Items[junctionId];
        npc.Tile = tile;
        npc.CurrentJunction = junctionId;
        npc.Position = junction.WorldPosition;
        npc.Movement.IsMoving = false;
    }

    private static void PutAtCamp(WorldState world, NPCState owner, NPCState intruder)
    {
        Assert.That(world.FactionHomes.TryGetValue(owner.Faction, out var camp), Is.True);
        Teleport(world, owner, camp);
        Teleport(world, intruder, camp);
        owner.Mind.CurrentGoal = GoalType.None;
        intruder.Mind.CurrentGoal = GoalType.None;
        owner.Mind.GoalLock = null;
        intruder.Mind.GoalLock = null;
        owner.IsFighting = false;
        intruder.IsFighting = false;
    }

    [Test]
    public void DetectionIsSymmetricForBothCamps()
    {
        var (colonyWorld, colonist, outsider) = Pair();
        PutAtCamp(colonyWorld, colonist, outsider);

        new CampExpulsionSystem().Run(colonyWorld);

        var colonyOwner = colonyWorld.Entities.Npcs.Values
            .FirstOrDefault(n => n.Faction == Faction.Colony &&
                                 n.Mind.ExpulsionTargetNpcId == outsider.Id);
        Assert.That(colonyOwner, Is.Not.Null,
            "Колония должна гнать чужака так же, как чужак — колонистку.");

        var (outsiderWorld, visitingColonist, campOwner) = Pair();
        PutAtCamp(outsiderWorld, campOwner, visitingColonist);

        new CampExpulsionSystem().Run(outsiderWorld);

        Assert.That(campOwner.Mind.CurrentGoal, Is.EqualTo(GoalType.Expel));
        Assert.That(campOwner.Mind.ExpulsionTargetNpcId, Is.EqualTo(visitingColonist.Id));
        Assert.That(visitingColonist.Mind.PendingExpulsionFrom, Is.EqualTo(campOwner.Id));
    }

    [Test]
    public void BelowHalfHealthAgreesAndFleesWithoutStartingFight()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        intruder.Health = 0.49f;
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        owner.Mind.ExpulsionPhase = 1;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick - Spec82.TerritoryResponseDelayTicks;
        intruder.Mind.CurrentGoal = GoalType.Expel;

        CampExpulsionSystem.AdvanceDemand(world, owner, intruder);

        Assert.That(intruder.Mind.CurrentGoal, Is.EqualTo(GoalType.Flee));
        Assert.That(intruder.Plan.Status, Is.EqualTo(PlanStatus.Active));
        Assert.That(owner.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(owner.Mind.SceneBlowsPlanned, Is.Zero,
            "Мирное согласие не должно даже объявлять удары.");
        Assert.That(intruder.Mind.CombatOpponentNpcId, Is.Null);
    }

    [Test]
    public void ExactlyHalfHealthRefusesAndStartsThreeBlowFight()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        intruder.Health = 0.5f;
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        owner.Mind.ExpulsionPhase = 1;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick - Spec82.TerritoryResponseDelayTicks;
        intruder.Mind.CurrentGoal = GoalType.Expel;

        CampExpulsionSystem.AdvanceDemand(world, owner, intruder);

        Assert.That(owner.Mind.ExpulsionPhase, Is.EqualTo(2));
        Assert.That(owner.Mind.SceneBlowsPlanned, Is.EqualTo(3));
        Assert.That(owner.Mind.CombatOpponentNpcId, Is.EqualTo(intruder.Id));
        Assert.That(intruder.Mind.CombatOpponentNpcId, Is.EqualTo(owner.Id));
    }

    [Test]
    public void LowHealthWithoutAHomeRouteRefusesInsteadOfFalseAgreement()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        intruder.Health = 0.2f;
        world.FactionHomes.Remove(intruder.Faction);
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        owner.Mind.ExpulsionPhase = 1;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick - Spec82.TerritoryResponseDelayTicks;
        intruder.Mind.CurrentGoal = GoalType.Expel;

        CampExpulsionSystem.AdvanceDemand(world, owner, intruder);

        Assert.That(owner.Mind.ExpulsionPhase, Is.EqualTo(2));
        Assert.That(intruder.Mind.CurrentGoal, Is.EqualTo(GoalType.Expel));
    }

    [TestCase(0.9f, 0.8f, true, TestName = "MoreDamageToIntruder_Expels")]
    [TestCase(0.8f, 0.8f, true, TestName = "NonZeroTie_FavorsCamp")]
    [TestCase(0.7f, 0.9f, false, TestName = "MoreDamageToOwner_IntruderStays")]
    [TestCase(1.0f, 1.0f, false, TestName = "ZeroDamage_DoesNotInventWin")]
    public void FightOutcomeUsesDamageReceived(float ownerHealth, float intruderHealth,
        bool expectedFlee)
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        intruder.Mind.CurrentGoal = GoalType.Expel;
        owner.Mind.SceneStartHealth = 1f;
        intruder.Mind.SceneStartHealth = 1f;
        owner.Health = ownerHealth;
        intruder.Health = intruderHealth;
        FightScene.Begin(world, owner, intruder, weaponId: null, blows: 3, spacingClips: 1.2f);
        owner.Mind.AbuseBlows = 3;
        owner.Mind.SceneLastBlowRestTick = world.Tick;
        owner.Mind.ExpulsionPhase = 2;
        intruder.Mind.CombatOpponentNpcId = owner.Id;

        CampExpulsionSystem.AdvanceFight(world, owner, intruder);

        if (expectedFlee)
        {
            Assert.That(intruder.Mind.CurrentGoal, Is.EqualTo(GoalType.Flee));
        }
        else
        {
            Assert.That(intruder.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(intruder.Mind.ExpulsionProtectedUntilTick,
                Is.EqualTo(world.Tick + Spec82.TerritoryCooldownTicks));
        }

        Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null);
        Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null);
        Assert.That(owner.Mind.CombatOpponentNpcId, Is.Null);
        Assert.That(intruder.Mind.CombatOpponentNpcId, Is.Null);
    }
}

}
