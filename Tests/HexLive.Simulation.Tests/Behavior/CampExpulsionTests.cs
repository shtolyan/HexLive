using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
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
        // Most tests below exercise scene phases rather than the new §117
        // voluntary-risk gate. Stage an owner who can safely issue the demand;
        // dedicated odds tests replace this loadout explicitly.
        owner.Inventory.Items.Clear();
        owner.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));
        intruder.Inventory.Items.Clear();
        owner.EquippedArmor = intruder.EquippedArmor = 0f;
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
    public void OrdinaryAuctionCannotStealAnActiveExpulsionScene()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);

        // Give the auction plenty of ordinary work to prefer.  The scene owns
        // this interval and must be ended by CampExpulsionSystem, not by scores.
        owner.Needs.Hunger = 0.7f;
        owner.Needs.Thirst = 0.7f;
        new DecisionSystem().Run(world);

        Assert.That(owner.Mind.CurrentGoal, Is.EqualTo(GoalType.Expel));
        Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.EqualTo(intruder.Id));
    }

    [Test]
    public void CriticalOwnerDoesNotStartTerritorialScene()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        owner.Mind.IsStarving = true;

        CampExpulsionSystem.BeginChallenge(world, owner, intruder);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Expel));
            Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null,
                "A survival emergency keeps ownership of the actor.");
            Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null);
        });
    }

    [Test]
    public void VoluntaryChallengeWeighsHealthArmorAndWeaponOnBothSides()
    {
        var (world, owner, intruder) = Pair();
        owner.Inventory.Items.Clear();
        intruder.Inventory.Items.Clear();
        owner.Health = intruder.Health = 1f;
        owner.EquippedArmor = intruder.EquippedArmor = 0f;
        owner.Attributes.Strength = intruder.Attributes.Strength = 0.5f;
        owner.Skills.Combat = intruder.Skills.Combat = 0f;

        Assert.That(CampExpulsionSystem.HasSafeChallengeOdds(world, owner, intruder),
            Is.True, "Равные здоровые безоружные стороны не меняют прежнее поведение.");

        intruder.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));
        Assert.That(CampExpulsionSystem.HasSafeChallengeOdds(world, owner, intruder),
            Is.False, "Безоружная не должна сама начинать бой против мачете.");

        owner.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));
        Assert.That(CampExpulsionSystem.HasSafeChallengeOdds(world, owner, intruder),
            Is.True, "Одинаковое оружие должно вернуть равный расклад.");

        owner.Health = 0.6f;
        Assert.That(CampExpulsionSystem.HasSafeChallengeOdds(world, owner, intruder),
            Is.False, "Текущее здоровье обеих сторон должно участвовать в решении.");

        owner.Health = 1f;
        intruder.EquippedArmor = 0.25f;
        Assert.That(CampExpulsionSystem.HasSafeChallengeOdds(world, owner, intruder),
            Is.False, "Броня чужака должна удерживать более слабую хозяйку от атаки.");
    }

    [Test]
    public void WorseOddsDoNotClaimIntruderOrStartFight()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        owner.Inventory.Items.Clear();
        intruder.Inventory.Items.Clear();
        intruder.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));

        CampExpulsionSystem.BeginChallenge(world, owner, intruder);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Expel));
            Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null);
            Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null);
            Assert.That(owner.Mind.SceneBlowsPlanned, Is.Zero);
        });
    }

    [Test]
    public void OddsAreRecheckedImmediatelyBeforeRefusalBecomesFight()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        owner.Inventory.Items.Clear();
        intruder.Inventory.Items.Clear();
        owner.Attributes.Strength = intruder.Attributes.Strength = 0.5f;
        owner.Skills.Combat = intruder.Skills.Combat = 0f;
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        owner.Mind.ExpulsionPhase = 1;
        owner.Mind.ExpulsionPhaseStartedTick = world.Tick - Spec82.TerritoryResponseDelayTicks;
        intruder.Mind.CurrentGoal = GoalType.Expel;

        intruder.Inventory.Items.Add(new ItemInstance(GearCatalog.Machete));
        CampExpulsionSystem.AdvanceDemand(world, owner, intruder);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null);
            Assert.That(owner.Mind.SceneBlowsPlanned, Is.Zero,
                "После ухудшения расклада драка не должна даже объявлять удары.");
            Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null);
        });
    }

    [Test]
    public void ScenePhaseDoesNotRebuildACompletedExpelPlanEveryMediumTick()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        CampExpulsionSystem.BeginChallenge(world, owner, intruder);
        owner.Mind.ExpulsionPhase = 1;
        owner.Plan.Goal = GoalType.Expel;
        owner.Plan.Status = PlanStatus.Completed;
        owner.Plan.CurrentStepIndex = 37; // sentinel: Planning resets/rebuilds this state
        owner.Plan.Steps.Clear();
        owner.Plan.Steps.Add(new PlanStep { Type = PlanStepType.Wait });

        new PlanningSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(owner.Plan.CurrentStepIndex, Is.EqualTo(37));
            Assert.That(owner.Plan.Steps, Has.Count.EqualTo(1));
        });
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
                Is.EqualTo(world.Tick + (ownerHealth < intruderHealth
                    ? Spec82.TerritoryDefeatCooldownTicks
                    : Spec82.TerritoryCooldownTicks)));
        }

        Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null);
        Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null);
        Assert.That(owner.Mind.CombatOpponentNpcId, Is.Null);
        Assert.That(intruder.Mind.CombatOpponentNpcId, Is.Null);
    }

    [Test]
    public void InjuredOwnerDoesNotStartAnotherSerialExpulsionFight()
    {
        var (world, owner, intruder) = Pair();
        PutAtCamp(world, owner, intruder);
        owner.Body.Parts[BodyPart.Head] =
            Spec82.TerritoryChallengeWorstPartHealth - 0.01f;

        CampExpulsionSystem.BeginChallenge(world, owner, intruder);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Mind.ExpulsionTargetNpcId, Is.Null);
            Assert.That(owner.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Expel));
            Assert.That(intruder.Mind.PendingExpulsionFrom, Is.Null,
                "После проигранного боя раненая должна лечиться, а не немедленно реваншироваться.");
        });
    }
}

}
