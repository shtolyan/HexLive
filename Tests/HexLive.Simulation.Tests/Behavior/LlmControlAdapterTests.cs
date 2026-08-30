using System.Globalization;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

public sealed class LlmControlAdapterTests
{
    [Test]
    public void Translator_MapsEveryCompleteDecisionWithoutApplyingIt()
    {
        var npcId = new EntityId(7);

        var none = Translate(new LlmDecision(LlmCommandKind.None), npcId);
        var stop = (StopCommand)Translate(new LlmDecision(LlmCommandKind.Stop), npcId);
        var move = (MoveToCommand)Translate(new LlmDecision(
            LlmCommandKind.MoveTo,
            targetPosition: new Float2(3.5f, -2f)), npcId);
        var interact = (InteractCommand)Translate(new LlmDecision(
            LlmCommandKind.Interact,
            targetObjectId: new ObjectId(11),
            interaction: InteractionType.Harvest), npcId);
        var attackNpc = (AttackNpcCommand)Translate(new LlmDecision(
            LlmCommandKind.AttackNpc,
            targetNpcId: new EntityId(12)), npcId);
        var attackMob = (AttackMobCommand)Translate(new LlmDecision(
            LlmCommandKind.AttackMob,
            targetMobId: 13), npcId);
        var manual = (SetManualControlCommand)Translate(new LlmDecision(
            LlmCommandKind.SetManualControl,
            manualControlEnabled: true), npcId);

        Assert.Multiple(() =>
        {
            Assert.That(none, Is.Null);
            Assert.That(stop.Npc, Is.EqualTo(npcId));
            Assert.That(move.Npc, Is.EqualTo(npcId));
            Assert.That(move.WorldPosition, Is.EqualTo(new Float2(3.5f, -2f)));
            // §121.11 (#294): темп — постоянная настройка NPC; null значит
            // «не навязывать» — сим возьмёт её собственный тумблер.
            Assert.That(move.Run, Is.Null);
            Assert.That(interact.Npc, Is.EqualTo(npcId));
            Assert.That(interact.Target, Is.EqualTo(new ObjectId(11)));
            Assert.That(interact.Interaction, Is.EqualTo(InteractionType.Harvest));
            Assert.That(attackNpc.Npc, Is.EqualTo(npcId));
            Assert.That(attackNpc.Target, Is.EqualTo(new EntityId(12)));
            Assert.That(attackMob.Npc, Is.EqualTo(npcId));
            Assert.That(attackMob.MobId, Is.EqualTo(13));
            Assert.That(manual.Npc, Is.EqualTo(npcId));
            Assert.That(manual.Enabled, Is.True);
        });
    }

    [TestCase(LlmCommandKind.MoveTo, "TargetPosition")]
    [TestCase(LlmCommandKind.Interact, "TargetObjectId")]
    [TestCase(LlmCommandKind.AttackNpc, "TargetNpcId")]
    [TestCase(LlmCommandKind.AttackMob, "TargetMobId")]
    [TestCase(LlmCommandKind.SetManualControl, "ManualControlEnabled")]
    public void Translator_ReturnsReasonForMissingPayload(
        LlmCommandKind kind, string expectedField)
    {
        var translated = LlmCommandTranslator.TryTranslate(
            new LlmDecision(kind),
            new EntityId(7),
            out var command,
            out var errorReason);

        Assert.Multiple(() =>
        {
            Assert.That(translated, Is.False);
            Assert.That(command, Is.Null);
            Assert.That(errorReason, Does.Contain(expectedField));
        });
    }

    [Test]
    public void Translator_RequiresInteractionAlongsideObjectTarget()
    {
        var translated = LlmCommandTranslator.TryTranslate(
            new LlmDecision(
                LlmCommandKind.Interact,
                targetObjectId: new ObjectId(11)),
            new EntityId(7),
            out var command,
            out var errorReason);

        Assert.Multiple(() =>
        {
            Assert.That(translated, Is.False);
            Assert.That(command, Is.Null);
            Assert.That(errorReason, Does.Contain("Interaction"));
        });
    }

    [Test]
    public void Translator_RejectsUnknownCommandKind()
    {
        var translated = LlmCommandTranslator.TryTranslate(
            new LlmDecision((LlmCommandKind)999),
            new EntityId(7),
            out var command,
            out var errorReason);

        Assert.Multiple(() =>
        {
            Assert.That(translated, Is.False);
            Assert.That(command, Is.Null);
            Assert.That(errorReason, Does.Contain("Unsupported"));
            Assert.That(errorReason, Does.Contain("999"));
        });
    }

    [Test]
    public void Translator_RejectsUnknownInteraction()
    {
        var translated = LlmCommandTranslator.TryTranslate(
            new LlmDecision(
                LlmCommandKind.Interact,
                targetObjectId: new ObjectId(11),
                interaction: (InteractionType)999),
            new EntityId(7),
            out var command,
            out var errorReason);

        Assert.Multiple(() =>
        {
            Assert.That(translated, Is.False);
            Assert.That(command, Is.Null);
            Assert.That(errorReason, Does.Contain("Interaction"));
            Assert.That(errorReason, Does.Contain("999"));
        });
    }

    [Test]
    public void Translator_RejectsInvalidTargetsWithoutWorldAccess()
    {
        AssertRejected(
            new LlmDecision(
                LlmCommandKind.MoveTo,
                targetPosition: new Float2(float.NaN, 2f)),
            "finite TargetPosition");
        AssertRejected(
            new LlmDecision(
                LlmCommandKind.Interact,
                targetObjectId: new ObjectId(0),
                interaction: InteractionType.Harvest),
            "positive TargetObjectId");
        AssertRejected(
            new LlmDecision(
                LlmCommandKind.AttackNpc,
                targetNpcId: new EntityId(-1)),
            "positive TargetNpcId");
        AssertRejected(
            new LlmDecision(
                LlmCommandKind.AttackNpc,
                targetNpcId: new EntityId(7)),
            "acting NPC");
        AssertRejected(
            new LlmDecision(
                LlmCommandKind.AttackMob,
                targetMobId: 0),
            "positive TargetMobId");
    }

    [Test]
    public void ContextBuilder_UsesNpcSnapshotsDeterministicallyWithoutReorderingThem()
    {
        var world = new WorldState { Tick = 321 };
        var npc = new NPCState
        {
            Id = new EntityId(7),
            Position = new Float2(1.25f, -4.5f),
            Health = 0.75f
        };
        npc.Mind.CurrentGoal = GoalType.GetFood;
        npc.Needs.Hunger = 0.8f;
        npc.Needs.Thirst = 0.3f;
        npc.Needs.Energy = 0.6f;
        npc.Perception.LastUpdatedTick = 320;
        npc.Perception.Environment.Temperature = 27.5f;
        npc.Perception.Objects.Add(PerceivedObject(9, "tree.palm"));
        npc.Perception.Objects.Add(PerceivedObject(2, "food.coconut"));
        npc.Perception.Hostiles.Add(new PerceivedAgent
        {
            Id = new EntityId(12),
            Distance = 2.25f,
            IsReachable = true
        });
        npc.Memory.KnownObjects.Add(new ObjectId(8), new ObjectMemory
        {
            Id = new ObjectId(8),
            DefinitionId = "water.pond",
            Tile = new TileCoord(3, -1),
            LastSeenTick = 200
        });
        npc.Memory.KnownObjects.Add(new ObjectId(3), new ObjectMemory
        {
            Id = new ObjectId(3),
            DefinitionId = "campfire",
            Tile = new TileCoord(0, 1),
            LastSeenTick = 300,
            IsPermanent = true
        });
        npc.Memory.KnownAgents.Add(new EntityId(4), new AgentMemory
        {
            Id = new EntityId(4),
            Tile = new TileCoord(1, 2),
            LastSeenTick = 310,
            Suffering = 0.5f,
            AidKind = AidKind.Treat
        });
        npc.Memory.Dangers.Add(new DangerMemory
        {
            Tile = new TileCoord(-2, 4),
            Tick = 250
        });

        var previousCulture = CultureInfo.CurrentCulture;
        LlmDecisionContext context;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            context = LlmDecisionContextBuilder.Build(world, npc);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        var repeated = LlmDecisionContextBuilder.Build(world, npc);

        Assert.Multiple(() =>
        {
            Assert.That(context.NpcId, Is.EqualTo(npc.Id));
            Assert.That(context.Tick, Is.EqualTo(321));
            Assert.That(context.Position, Is.EqualTo(npc.Position));
            Assert.That(context.StateSummary, Does.Contain("health=0.75"));
            Assert.That(context.StateSummary, Does.Contain("goal=GetFood"));
            Assert.That(context.StateSummary, Does.Contain("hunger=0.8"));
            Assert.That(context.PerceptionSummary, Does.Contain("temperature=27.5"));
            Assert.That(context.PerceptionSummary, Does.Contain("hostiles=[{id=12"));
            Assert.That(context.MemorySummary, Does.Contain("definition=campfire"));
            Assert.That(context.MemorySummary, Does.Contain("aid=Treat"));
            Assert.That(context.MemorySummary, Does.Contain("dangers=[{tile=-2,4, tick=250}]"));
            Assert.That(context.StateSummary, Is.EqualTo(repeated.StateSummary));
            Assert.That(context.PerceptionSummary, Is.EqualTo(repeated.PerceptionSummary));
            Assert.That(context.MemorySummary, Is.EqualTo(repeated.MemorySummary));
            Assert.That(
                context.PerceptionSummary.IndexOf("{id=2"),
                Is.LessThan(context.PerceptionSummary.IndexOf("{id=9")));
            Assert.That(
                context.MemorySummary.IndexOf("{id=3"),
                Is.LessThan(context.MemorySummary.IndexOf("{id=8")));
            Assert.That(npc.Perception.Objects[0].Id, Is.EqualTo(new ObjectId(9)),
                "Building a context must not sort the live perception list in place.");
        });
    }

    private static ISimulationCommand Translate(LlmDecision decision, EntityId npcId)
    {
        var translated = LlmCommandTranslator.TryTranslate(
            decision, npcId, out var command, out var errorReason);

        Assert.That(translated, Is.True, errorReason);
        Assert.That(errorReason, Is.Empty);
        return command;
    }

    private static void AssertRejected(LlmDecision decision, string expectedReason)
    {
        var translated = LlmCommandTranslator.TryTranslate(
            decision, new EntityId(7), out var command, out var errorReason);

        Assert.Multiple(() =>
        {
            Assert.That(translated, Is.False);
            Assert.That(command, Is.Null);
            Assert.That(errorReason, Does.Contain(expectedReason));
        });
    }

    private static PerceivedObject PerceivedObject(int id, string definitionId)
    {
        var result = new PerceivedObject
        {
            Id = new ObjectId(id),
            DefinitionId = definitionId,
            Distance = id / 10f,
            IsReachable = true
        };
        result.AvailableInteractions.Add(InteractionType.PickUp);
        return result;
    }
}

}
