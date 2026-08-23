using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>§127.9–12: command boundary, paired scene and authoritative outcomes.</summary>
public sealed class RomanceInteractionTests
{
    [Test]
    public void PlaybackStartsAtPointOneEndsAtOneAndClimaxRunsAtHalfSpeed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Spec127.DurationTicks, Is.EqualTo(1_334));
            Assert.That(Spec127.ClimaxTicks, Is.EqualTo(64));
            Assert.That(Spec127.MinPlaybackSpeed, Is.EqualTo(0.1f));
            Assert.That(Spec127.MaxPlaybackSpeed, Is.EqualTo(1f));
            Assert.That(Spec127.ClimaxPlaybackSpeed, Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void RomanceStateCooldownAndStainSurviveSaveLoad()
    {
        var world = TestWorld.CreateWorld(12756);
        var subject = world.Entities.Npcs.Values.First();
        var partner = world.Entities.Npcs.Values.First(n => n.Id != subject.Id);
        subject.Mind.RomancePartnerNpcId = partner.Id;
        subject.Mind.RomanceLeaderNpcId = subject.Id;
        subject.Mind.RomanceClipKey = "Standing_Mission_Loop0";
        subject.Mind.RomanceForced = true;
        subject.Mind.RomanceAnchorX = 12.5f;
        subject.Mind.RomanceAnchorY = -4.25f;
        subject.Mind.RomanceFacingDegrees = 240f;
        subject.Mind.RomanceCooldownUntilTick = 9191;
        subject.Body.Condition(BodyPart.Pelvis).IntimacySoil = 0.62f;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream,
                   System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Write(world, writer);
        }

        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(stream,
                   System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSaveSerializer.Read(loaded, reader);
        }

        var restored = loaded.Entities.Npcs[subject.Id];
        Assert.Multiple(() =>
        {
            Assert.That(restored.Mind.RomancePartnerNpcId, Is.EqualTo(partner.Id));
            Assert.That(restored.Mind.RomanceLeaderNpcId, Is.EqualTo(subject.Id));
            Assert.That(restored.Mind.RomanceClipKey, Is.EqualTo("Standing_Mission_Loop0"));
            Assert.That(restored.Mind.RomanceForced, Is.True);
            Assert.That(restored.Mind.RomanceAnchorX, Is.EqualTo(12.5f));
            Assert.That(restored.Mind.RomanceAnchorY, Is.EqualTo(-4.25f));
            Assert.That(restored.Mind.RomanceFacingDegrees, Is.EqualTo(240f));
            Assert.That(restored.Mind.RomanceCooldownUntilTick, Is.EqualTo(9191));
            Assert.That(restored.Body.Condition(BodyPart.Pelvis).IntimacySoil,
                Is.EqualTo(0.62f));
        });
    }

    [Test]
    public void ConsensualManualOrderDropsAllGenitalBlockingClothesAndLeavesWashableStain()
    {
        var world = TestWorld.CreateWorld(12709);
        var male = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Male);
        var female = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        PreparePair(world, male, female, allies: true);
        MakeMutualConsent(male, female);
        male.Mind.ManualControl = true;
        male.Needs.Social = 0.05f;
        female.Needs.Social = 0.05f;
        male.WornItems.Add(new ItemInstance("FCO Pants Male"));
        male.WornItems.Add(new ItemInstance("FCO Legs Straps Male"));
        female.WornItems.Add(new ItemInstance("underwear.thong_anarchy"));

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(male.Id, female.Id, forced: false));

        Assert.Multiple(() =>
        {
            Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
                admission.Reason);
            Assert.That(male.Mind.CurrentGoal, Is.EqualTo(GoalType.Romance));
            Assert.That(female.Mind.PendingRomanceFrom, Is.EqualTo(male.Id));
        });

        new ManualOrderSystem().Run(world);
        Assert.That(male.Mind.CurrentGoal, Is.EqualTo(GoalType.Romance),
            "Ручной sweep не должен уничтожить только что принятый приказ §127.");

        ArriveAtPlanTarget(world, male);
        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(male.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Romance));
            Assert.That(female.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Romance));
            Assert.That(male.Execution.EndTick - male.Execution.StartTick,
                Is.EqualTo(Spec127.DurationTicks));
            Assert.That(male.WornItems.Any(i => RomanceMath.CoversPelvis(world, i)), Is.False);
            Assert.That(male.WornItems.Any(i =>
                RomanceMath.MustRemoveForRomance(world, male, i)), Is.False);
            Assert.That(female.WornItems.Any(i => RomanceMath.CoversPelvis(world, i)), Is.False);
            Assert.That(world.Entities.Objects.Values.Any(o =>
                o.DefinitionId == "FCO Legs Straps Male"), Is.True,
                "Боковые набедренники должны лежать на земле, а не оставаться на мужчине.");
        });

        world.Tick = male.Execution.EndTick;
        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(female.Body.Condition(BodyPart.Pelvis).IntimacySoil, Is.EqualTo(1f));
            Assert.That(male.Needs.Social, Is.EqualTo(0.90f).Within(0.0001f));
            Assert.That(female.Needs.Social, Is.EqualTo(0.90f).Within(0.0001f));
            Assert.That(male.Social.GetOrCreate(female.Id).Affinity,
                Is.EqualTo(0.65f).Within(0.0001f));
            Assert.That(male.Mind.RomancePartnerNpcId, Is.Null);
            Assert.That(female.Execution.Status, Is.EqualTo(ExecutionStatus.None));
        });

        WoundMath.WashBloodSoil(female, 0.25f);
        Assert.That(female.Body.Condition(BodyPart.Pelvis).IntimacySoil,
            Is.EqualTo(0.75f).Within(0.0001f));
    }

    [Test]
    public void ForcedCompletionDamagesPelvisRuinsTrustAndLeavesVictimCrying()
    {
        var world = TestWorld.CreateWorld(12712);
        var aggressor = world.Entities.Npcs.Values.First(n =>
            n.Sex == GarmentSex.Male && n.Traits.Has(TraitKind.Abuser));
        var victim = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        PreparePair(world, aggressor, victim, allies: false);
        aggressor.Mind.ManualControl = true;
        aggressor.Needs.Social = 0f;
        victim.Needs.Social = 0.9f;
        var beforePelvis = victim.Body.Parts[BodyPart.Pelvis];
        var beforeTrust = victim.Social.GetOrCreate(aggressor.Id).Trust;

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(aggressor.Id, victim.Id, forced: true));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            admission.Reason);

        ArriveAtPlanTarget(world, aggressor);
        new ExecutionSystem().Run(world);
        Assert.That(aggressor.Mind.RomanceForced, Is.True);

        world.Tick = aggressor.Execution.EndTick;
        new ExecutionSystem().Run(world);

        var damage = beforePelvis - victim.Body.Parts[BodyPart.Pelvis];
        Assert.Multiple(() =>
        {
            Assert.That(damage, Is.InRange(Spec127.ForcedPelvisDamageMin,
                Spec127.ForcedPelvisDamageMax));
            Assert.That(victim.Social.GetOrCreate(aggressor.Id).Trust,
                Is.LessThan(beforeTrust));
            Assert.That(victim.Needs.Social, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(victim.Mind.CryingUntilTick,
                Is.EqualTo(world.Tick + Spec127.VictimCryingTicks));
            Assert.That(victim.Body.Condition(BodyPart.Pelvis).IntimacySoil,
                Is.EqualTo(1f));
        });
    }

    [Test]
    public void CombatInterruptsForcedPairAndVictimRemainsCrying()
    {
        var world = TestWorld.CreateWorld(12713);
        var aggressor = world.Entities.Npcs.Values.First(n =>
            n.Sex == GarmentSex.Male && n.Traits.Has(TraitKind.Abuser));
        var victim = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        PreparePair(world, aggressor, victim, allies: false);
        aggressor.Mind.ManualControl = true;

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(aggressor.Id, victim.Id, forced: true));
        Assert.That(admission.Accepted, Is.True, admission.Reason);
        ArriveAtPlanTarget(world, aggressor);
        new ExecutionSystem().Run(world);

        aggressor.IsFighting = true;
        aggressor.Mind.CombatOpponentNpcId = victim.Id;
        new ExecutionSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(aggressor.Execution.CurrentInteraction, Is.Null);
            Assert.That(victim.Execution.CurrentInteraction, Is.Null);
            Assert.That(victim.Mind.CryingUntilTick,
                Is.GreaterThanOrEqualTo(world.Tick + Spec127.VictimCryingTicks));
        });
    }

    private static void PreparePair(WorldState world, NPCState male, NPCState female,
        bool allies)
    {
        male.Faction = Faction.Colony;
        female.Faction = allies ? Faction.Colony : Faction.Outsiders;
        Reset(male);
        Reset(female);
        EnsureStandingJunction(world, male);
        EnsureStandingJunction(world, female);
        PlaceOnFreeNeighbor(world, female, male);
    }

    private static void Reset(NPCState npc)
    {
        npc.Health = npc.Body.Mean();
        npc.IsFighting = false;
        npc.Mind.CombatOpponentNpcId = null;
        npc.Mind.FaintedUntilTick = 0;
        npc.Mind.CryingUntilTick = 0;
        npc.Mind.RomanceCooldownUntilTick = 0;
        npc.Plan.Goal = GoalType.None;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.Steps.Clear();
        npc.Plan.CurrentStepIndex = 0;
        npc.Plan.TargetObjectId = null;
        npc.Plan.TargetJunctionId = null;
        npc.Plan.TargetTile = null;
        npc.Plan.TargetAgentId = null;
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Execution.TargetObject = null;
        npc.Execution.StartTick = 0;
        npc.Execution.EndTick = 0;
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);
    }

    private static void MakeMutualConsent(NPCState a, NPCState b)
    {
        Set(a.Social.GetOrCreate(b.Id));
        Set(b.Social.GetOrCreate(a.Id));

        static void Set(RelationshipData relationship)
        {
            relationship.Affinity = 0.55f;
            relationship.Trust = 0.35f;
            relationship.Familiarity = 0.55f;
        }
    }

    private static void PlaceOnFreeNeighbor(WorldState world, NPCState person,
        NPCState anchor)
    {
        var destinationId = SpatialQueries.GetPassableNeighbors(
                world, anchor.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id));
        MoveTo(world, person, destinationId);
    }

    private static void EnsureStandingJunction(WorldState world, NPCState npc)
    {
        if (npc.CurrentJunction is not null) return;
        var destinationId = world.Tiles.Items[npc.Tile].Junctions
            .First(id => !world.Junctions.Items[id].Blocked &&
                         SpatialQueries.IsJunctionFree(world, id));
        MoveTo(world, npc, destinationId);
    }

    private static void ArriveAtPlanTarget(WorldState world, NPCState npc)
    {
        Assert.That(npc.Plan.TargetJunctionId, Is.Not.Null);
        MoveTo(world, npc, npc.Plan.TargetJunctionId!.Value);
        npc.Movement.SetStatus(MovementStatus.Arrived);
    }

    private static void MoveTo(WorldState world, NPCState npc, JunctionId destinationId)
    {
        var destination = world.Junctions.Items[destinationId];
        if (npc.CurrentJunction is { } previous && !previous.Equals(destinationId))
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }

        var previousTile = npc.Tile;
        npc.Tile = destination.Tiles.Count > 0 ? destination.Tiles[0] : npc.Tile;
        npc.Fragment = destination.Fragment;
        npc.Position = destination.WorldPosition;
        npc.CurrentJunction = destinationId;
        SpatialMutations.MoveEntityToTile(world, npc.Id, previousTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, destinationId, npc.Id);
    }
}

}
