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
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InvitationOwnsApproachUntilAnswerAndCuesDoNotDependOnDiagnostics(bool consent, bool diagnostics)
    {
        var (world, initiator, listener) = InvitationPair();
        var previousTrace = SimTrace.Enabled;
        SimTrace.Enabled = diagnostics;
        try
        {
            if (!consent) listener.Social.GetOrCreate(initiator.Id).Trust = 0f;
            StartInvitationConversation(world, initiator, listener);
            var approach = initiator.Plan.TargetJunctionId!.Value;
            var listenerJunction = listener.CurrentJunction!.Value;
            Assert.That(approach, Is.Not.EqualTo(listenerJunction));
            Assert.That(SpatialMutations.TryReserveJunction(world, approach, initiator.Id, world.Tick, 1000), Is.True);
            Assert.That(SpatialMutations.TryReserveJunction(world, listenerJunction, listener.Id, world.Tick, 1000), Is.True);

            world.Tick = initiator.Execution.EndTick;
            new ExecutionSystem().Run(world);

            Assert.Multiple(() =>
            {
                Assert.That(listener.Execution.LastSocialCueKind, Is.EqualTo(consent ? "RomanceAccepted" : "RomanceRefused"));
                Assert.That(initiator.Execution.Status, Is.EqualTo(ExecutionStatus.None));
                Assert.That(initiator.Execution.CurrentTalkTopic, Is.Null);
                Assert.That(listener.Execution.CurrentTalkTopic, Is.Null);
                Assert.That(initiator.Plan.Status, Is.EqualTo(consent ? PlanStatus.Active : PlanStatus.Completed));
                Assert.That(listener.Mind.PendingRomanceFrom, Is.EqualTo(consent ? (EntityId?)initiator.Id : null));
                Assert.That(world.Reservations.Junctions.ContainsKey(approach), Is.EqualTo(consent));
                Assert.That(world.Occupancy.JunctionOwner.TryGetValue(approach, out var owner) && owner == initiator.Id, Is.EqualTo(consent));
                Assert.That(world.Reservations.Junctions[listenerJunction].Owner, Is.EqualTo(listener.Id), "never release someone else's reservation");
                Assert.That(world.Events.Items.Any(e => e.Type == "ManualOrderInterrupted"), Is.False, "an answer is not an aborted order");
                Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationStarted"), Is.EqualTo(diagnostics));
                Assert.That(world.Events.Items.Any(e => e.Type == (consent ? "RomanceInvitationAccepted" : "RomanceInvitationDeclined")), Is.EqualTo(diagnostics));
            });
            if (consent)
            {
                Assert.That(initiator.Plan.Steps[^1].Interaction, Is.EqualTo(InteractionType.Romance));
            }
            else
            {
                Assert.That(initiator.Plan.Steps, Is.Empty);
                Assert.That(initiator.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            }
        }
        finally
        {
            SimTrace.Enabled = previousTrace;
        }
    }

    private static (WorldState world, NPCState initiator, NPCState listener) InvitationPair(WorldState existing = null)
    {
        var world = existing ?? TestWorld.CreateWorld(12709);
        world.Tick = 10;
        var initiator = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Male);
        var listener = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Female);
        PreparePair(world, initiator, listener, allies: true);
        initiator.Mind.ManualControl = true;
        listener.Mind.ManualControl = false;
        MakeMutualConsent(initiator, listener);
        return (world, initiator, listener);
    }

    private static void StartInvitationConversation(WorldState world, NPCState initiator, NPCState listener)
    {
        var result = ManualCommandExecutor.Apply(world, new RomancePersonCommand(initiator.Id, listener.Id, false));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), result.Reason);
        Assert.That(initiator.Mind.RomancePartnerNpcId, Is.Null, "an invitation does not own a paired scene");
        Assert.That(listener.Mind.PendingRomanceFrom, Is.EqualTo(initiator.Id));
        ArriveAtPlanTarget(world, initiator);
        new ExecutionSystem().Run(world);
        Assert.That(initiator.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Talk));
        Assert.That(initiator.Execution.EndTick - world.Tick, Is.EqualTo(Spec127.InvitationTalkTicks));
        Assert.That(initiator.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Flirt));
        Assert.That(listener.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.Flirt));
        Assert.That(initiator.Mind.RomanceClipKey, Is.Empty);
    }

    [TestCase(0f, 0f, 0f)]
    [TestCase(.95f, 0f, .95f)]
    [TestCase(.95f, .95f, 0f)]
    [TestCase(-.8f, 0f, 0f)]
    public void RelationshipsDoNotBlockInvitationAndRefusalIsAConversationOutcome(float affinity, float trust, float familiarity)
    {
        var (world, initiator, listener) = InvitationPair();
        var relationship = listener.Social.GetOrCreate(initiator.Id);
        relationship.Affinity = affinity; relationship.Trust = trust; relationship.Familiarity = familiarity;
        StartInvitationConversation(world, initiator, listener);
        world.Tick = initiator.Execution.EndTick - 1;
        new ExecutionSystem().Run(world);
        Assert.That(initiator.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Talk));
        world.Tick++;
        new ExecutionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(initiator.Plan.Status, Is.EqualTo(PlanStatus.Completed));
            Assert.That(initiator.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
            Assert.That(listener.Mind.PendingRomanceFrom, Is.Null);
            Assert.That(initiator.Mind.RomancePartnerNpcId, Is.Null);
            Assert.That(listener.Mind.RomancePartnerNpcId, Is.Null);
            Assert.That(initiator.Execution.CurrentTalkTopic, Is.Null);
            Assert.That(listener.Execution.CurrentTalkTopic, Is.Null);
            Assert.That(listener.Execution.LastSocialCueKind, Is.EqualTo("RomanceRefused"));
            Assert.That(world.Events.Items.Any(e => e.Type == "ManualOrderRejected"), Is.False);
            Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationDeclined"), Is.True);
            Assert.That(relationship.Affinity, Is.EqualTo(affinity), "a polite refusal is not hostility");
            Assert.That(listener.Mind.RomanceCooldownUntilTick, Is.Zero);
        });
    }

    [Test]
    public void GoodRelationshipsAnswerOnlyAfterConversationAndPlaceIsNotChosenBeforeAnswer()
    {
        var (world, initiator, listener) = InvitationPair();
        StartInvitationConversation(world, initiator, listener);
        world.Tick = initiator.Execution.EndTick;
        new ExecutionSystem().Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationAccepted"), Is.True);
            Assert.That(initiator.Plan.Steps[^1].Interaction, Is.EqualTo(InteractionType.Romance));
            Assert.That(initiator.Execution.CurrentInteraction, Is.Null);
            Assert.That(initiator.Mind.RomanceClipKey, Is.Empty);
            Assert.That(listener.Execution.LastSocialCueKind, Is.EqualTo("RomanceAccepted"));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AnswerUsesCurrentRelationshipsRatherThanAdmissionSnapshot(bool initiallyGood)
    {
        var (world, initiator, listener) = InvitationPair();
        listener.Social.GetOrCreate(initiator.Id).Trust = initiallyGood ? .8f : 0f;
        StartInvitationConversation(world, initiator, listener);
        listener.Social.GetOrCreate(initiator.Id).Trust = initiallyGood ? 0f : .8f;
        world.Tick = initiator.Execution.EndTick;
        new ExecutionSystem().Run(world);
        Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationAccepted"), Is.EqualTo(!initiallyGood));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void StopByEitherParticipantClearsWaitingAndHeartTopics(bool stopInitiator)
    {
        var (world, initiator, listener) = InvitationPair();
        StartInvitationConversation(world, initiator, listener);
        var stopped = stopInitiator ? initiator : listener;
        stopped.Mind.ManualControl = true;
        var result = ManualCommandExecutor.Apply(world, new StopCommand(stopped.Id));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), result.Reason);
        new ExecutionSystem().Run(world);
        Assert.That(listener.Mind.PendingRomanceFrom, Is.Null);
        Assert.That(initiator.Execution.CurrentTalkTopic, Is.Null);
        Assert.That(listener.Execution.CurrentTalkTopic, Is.Null);
        Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationAccepted"), Is.False);
        Assert.That(listener.Mind.RomanceCooldownUntilTick, Is.Zero);
    }

    [Test]
    public void InvitationDoesNotStealAnotherListenersClaim()
    {
        var (world, initiator, listener) = InvitationPair();
        var other = world.Entities.Npcs.Values.First(n => n.Id != initiator.Id && n.Id != listener.Id);
        listener.Mind.PendingRomanceFrom = other.Id;
        var result = ManualCommandExecutor.Apply(world, new RomancePersonCommand(initiator.Id, listener.Id, false));
        Assert.That(result.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(result.Reason, Is.EqualTo("TargetUnavailable"));
        Assert.That(listener.Mind.PendingRomanceFrom, Is.EqualTo(other.Id));
    }

    [Test]
    public void RealSchedulerWalksToWaitingListenerThenConversesAndReleasesAfterRefusal()
    {
        var engine = TestWorld.CreateEngine(12709);
        foreach (var npc in engine.World.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl = true;
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Needs.Energy = 1f;
        }
        var (world, initiator, listener) = InvitationPair(engine.World);
        listener.Social.GetOrCreate(initiator.Id).Trust = 0f;
        var start = listener.Position;
        var admitted = ManualCommandExecutor.Apply(world, new RomancePersonCommand(initiator.Id, listener.Id, false));
        Assert.That(admitted.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admitted.Reason);
        var moved = false;
        var talked = false;
        for (var i = 0; i < 600 && !world.Events.Items.Any(e => e.Type == "RomanceInvitationDeclined"); i++)
        {
            engine.Step();
            moved |= initiator.Movement.IsMoving;
            talked |= initiator.Execution.CurrentInteraction == InteractionType.Talk;
            Assert.That(listener.Position, Is.EqualTo(start), "listener waits instead of walking away");
        }
        Assert.That(moved, Is.True, "actual movement systems must run, not a test teleport");
        Assert.That(talked, Is.True);
        Assert.That(world.Events.Items.Any(e => e.Type == "RomanceInvitationDeclined"), Is.True);
        Assert.That(listener.Mind.PendingRomanceFrom, Is.Null);
        Assert.That(initiator.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
    }

    [Test]
    public void CooldownDoesNotBlockConversationButListenerCanDeclineAtItsEnd()
    {
        var (world, initiator, listener) = InvitationPair();
        listener.Mind.RomanceCooldownUntilTick = world.Tick + 5000;
        StartInvitationConversation(world, initiator, listener);
        world.Tick = initiator.Execution.EndTick;
        new ExecutionSystem().Run(world);
        Assert.That(listener.Execution.LastSocialCueKind, Is.EqualTo("RomanceRefused"));
        Assert.That(listener.Mind.RomanceCooldownUntilTick, Is.EqualTo(5010));
    }

    [Test]
    public void EmergencyDuringConversationCancelsWithoutSceneTakeover()
    {
        var (world, initiator, listener) = InvitationPair();
        StartInvitationConversation(world, initiator, listener);
        listener.IsFighting = true;
        new ExecutionSystem().Run(world);
        Assert.That(listener.Mind.PendingRomanceFrom, Is.Null);
        Assert.That(listener.Execution.CurrentTalkTopic, Is.Null);
        Assert.That(initiator.Mind.RomancePartnerNpcId, Is.Null);
        Assert.That(initiator.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(listener.IsFighting, Is.True, "an invitation cannot suppress an emergency");
    }

    [Test]
    public void MissingTargetEndsInvitationWithoutLeavingInitiatorStuck()
    {
        var (world, initiator, listener) = InvitationPair();
        StartInvitationConversation(world, initiator, listener);
        world.Entities.Npcs.Remove(listener.Id);
        new ExecutionSystem().Run(world);
        Assert.That(initiator.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(initiator.Plan.Status, Is.EqualTo(PlanStatus.Completed));
        Assert.That(initiator.Execution.CurrentTalkTopic, Is.Null);
    }

    [Test]
    public void SavingAnInvitationDoesNotLeaveAnUnrestoredListenerWaitingOrKeepHeartTopics()
    {
        var (world, initiator, listener) = InvitationPair();
        StartInvitationConversation(world, initiator, listener);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using (var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        new ExecutionSystem().Run(restored);
        Assert.That(restored.Entities.Npcs[listener.Id].Mind.PendingRomanceFrom, Is.Null);
        Assert.That(restored.Entities.Npcs[initiator.Id].Mind.CurrentGoal, Is.EqualTo(GoalType.None));
        Assert.That(restored.Entities.Npcs[initiator.Id].Execution.CurrentTalkTopic, Is.Null);
        Assert.That(restored.Entities.Npcs[listener.Id].Execution.CurrentTalkTopic, Is.Null);
    }

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
        male.Needs.Stress = 0.8f;
        female.Needs.Stress = 0.6f;
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

        Assert.That(male.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Talk));
        world.Tick = male.Execution.EndTick;
        new ExecutionSystem().Run(world); // answer
        new ExecutionSystem().Run(world); // existing continuation, after agreement

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

        var maleStressAtStart = male.Needs.Stress;
        var femaleStressAtStart = female.Needs.Stress;
        var execution = new ExecutionSystem();
        for (var tick = 0; tick < 100; tick++)
        {
            world.Tick++;
            execution.Run(world);
        }
        Assert.Multiple(() =>
        {
            Assert.That(male.Needs.Stress, Is.LessThan(maleStressAtStart));
            Assert.That(female.Needs.Stress, Is.LessThan(femaleStressAtStart));
            Assert.That(male.Needs.Stress, Is.GreaterThan(0f),
                "Стресс должен снижаться постепенно, а не исчезать при старте.");
        });

        world.Tick = male.Execution.EndTick;
        execution.Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(female.Body.Condition(BodyPart.Pelvis).IntimacySoil, Is.EqualTo(1f));
            Assert.That(male.Needs.Social, Is.EqualTo(0.90f).Within(0.0001f));
            Assert.That(female.Needs.Social, Is.EqualTo(0.90f).Within(0.0001f));
            Assert.That(male.Social.GetOrCreate(female.Id).Affinity,
                Is.EqualTo(0.65f).Within(0.0001f));
            Assert.That(male.Mind.RomancePartnerNpcId, Is.Null);
            Assert.That(female.Execution.Status, Is.EqualTo(ExecutionStatus.None));
            Assert.That(male.Needs.Stress, Is.Zero);
            Assert.That(female.Needs.Stress, Is.Zero);
            Assert.That(male.Journal.PendingType, Is.EqualTo("RomanceCompleted"));
            Assert.That(female.Journal.PendingType, Is.EqualTo("RomanceCompleted"));
            Assert.That(male.Journal.PendingSubjectId, Is.EqualTo(female.Id));
            Assert.That(female.Journal.PendingSubjectId, Is.EqualTo(male.Id));
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
        aggressor.Needs.Stress = 0.8f;
        victim.Needs.Stress = 0.2f;
        var beforePelvis = victim.Body.Parts[BodyPart.Pelvis];
        var beforeTrust = victim.Social.GetOrCreate(aggressor.Id).Trust;

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(aggressor.Id, victim.Id, forced: true));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted),
            admission.Reason);

        ArriveAtPlanTarget(world, aggressor);
        new ExecutionSystem().Run(world);
        Assert.That(aggressor.Mind.RomanceForced, Is.True);

        var aggressorStressAtStart = aggressor.Needs.Stress;
        var victimStressAtStart = victim.Needs.Stress;
        var execution = new ExecutionSystem();
        for (var tick = 0; tick < 100; tick++)
        {
            world.Tick++;
            execution.Run(world);
        }
        Assert.Multiple(() =>
        {
            Assert.That(aggressor.Needs.Stress, Is.LessThan(aggressorStressAtStart));
            Assert.That(victim.Needs.Stress, Is.GreaterThan(victimStressAtStart));
            Assert.That(victim.Needs.Stress, Is.LessThan(1f),
                "Травма должна нарастать постепенно, а не скачком при старте.");
        });

        world.Tick = aggressor.Execution.EndTick;
        execution.Run(world);

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
            Assert.That(aggressor.Needs.Stress, Is.Zero);
            Assert.That(victim.Needs.Stress, Is.EqualTo(1f));
            Assert.That(victim.Journal.PendingType, Is.EqualTo("RomanceForced"));
            Assert.That(victim.Journal.PendingSubjectId, Is.EqualTo(aggressor.Id));
            Assert.That(aggressor.Journal.PendingType, Is.Not.EqualTo("RomanceForced"),
                "Принудительная сцена должна попасть в дневник жертвы, а не нормализоваться записью инициатора.");
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
