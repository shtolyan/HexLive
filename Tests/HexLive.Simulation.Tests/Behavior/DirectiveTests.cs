using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Social;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>§167: указания — просьба, согласие, тяга, снятие, сейв.</summary>
public sealed class DirectiveTests
{
    [Test]
    public void AskBuildAcceptedSetsDirectiveAndAnswerBubble()
    {
        var engine = Setup(out var actor, out var target);
        Warm(target, actor, affinity: 0.5f, trust: 0.5f);
        Ask(engine.World, actor, target, TalkTopic.AskBuild);
        var asked = engine.World.Tick;
        StartTalk(engine, actor, target);

        Assert.Multiple(() =>
        {
            Assert.That(target.Mind.Directive, Is.Not.Null);
            Assert.That(target.Mind.Directive!.Kind, Is.EqualTo(DirectiveKind.Build));
            Assert.That(target.Mind.Directive!.FromId, Is.EqualTo(actor.Id));
            Assert.That(target.Mind.Directive!.UntilTick,
                Is.EqualTo(target.Mind.Directive!.IssuedTick + Spec167.DirectiveTicks));
            Assert.That(target.Mind.Directive!.IssuedTick, Is.GreaterThanOrEqualTo(asked));
            Assert.That(actor.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.AskBuild));
            Assert.That(target.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.DirectiveYes));
            Assert.That(target.Social.GetOrCreate(actor.Id).Authority,
                Is.EqualTo(Spec167.AuthorityOnAccept).Within(1e-6f));
            Assert.That(Events(engine.World, "DirectiveAsked", actor), Is.True);
            Assert.That(Events(engine.World, "DirectiveAccepted", target), Is.True);
        });
    }

    [Test]
    public void DislikeRefusesWithoutSpoilingTheAskersAffinity()
    {
        var engine = Setup(out var actor, out var target);
        // Above the talk-level refusal threshold (-0.25) so the talk itself starts.
        Warm(target, actor, affinity: -0.2f, trust: 0f);
        var askerAffinity = actor.Social.GetOrCreate(target.Id).Affinity;
        Ask(engine.World, actor, target, TalkTopic.AskStockFood);
        StartTalk(engine, actor, target);

        Assert.Multiple(() =>
        {
            Assert.That(target.Mind.Directive, Is.Null);
            Assert.That(target.Execution.CurrentTalkTopic, Is.EqualTo(TalkTopic.DirectiveNo));
            Assert.That(EventMessage(engine.World, "DirectiveRefused", target), Does.Contain("Reason=Dislike"));
            Assert.That(actor.Social.GetOrCreate(target.Id).Affinity, Is.EqualTo(askerAffinity));
        });
    }

    [Test]
    public void OwnNeedRefusesByRule()
    {
        var engine = Setup(out var actor, out var target);
        Warm(target, actor, affinity: 0.9f, trust: 0.9f);
        Ask(engine.World, actor, target, TalkTopic.AskStockWater);
        // Hungry, but not starving: the talk starts, the promise is declined.
        StartTalk(engine, actor, target, hungerOfTarget: 0.7f);

        Assert.Multiple(() =>
        {
            Assert.That(target.Mind.Directive, Is.Null);
            Assert.That(EventMessage(engine.World, "DirectiveRefused", target), Does.Contain("Reason=Needed"));
        });
    }

    [Test]
    public void ManualListenerRefusesByRule()
    {
        var engine = Setup(out var actor, out var target, targetManual: true);
        Warm(target, actor, affinity: 0.9f, trust: 0.9f);
        Ask(engine.World, actor, target, TalkTopic.AskBuild);
        StartTalk(engine, actor, target);
        Assert.That(EventMessage(engine.World, "DirectiveRefused", target), Does.Contain("Reason=Manual"));
        Assert.That(target.Mind.Directive, Is.Null);
    }

    [Test]
    public void AgentListenerGetsPendingAndTimeoutDecides()
    {
        var engine = Setup(out var actor, out var target);
        Warm(target, actor, affinity: 0.9f, trust: 0.9f);
        // §160.1: an attached agent vetoes the AI; the ask waits for its answer.
        target.Mind.ExternalControl = new ExternalNpcControl();
        Ask(engine.World, actor, target, TalkTopic.AskStockFood);
        StartTalk(engine, actor, target);
        Assert.Multiple(() =>
        {
            Assert.That(target.Mind.Directive, Is.Null);
            Assert.That(target.Mind.PendingDirective, Is.Not.Null);
            Assert.That(target.Mind.PendingDirective!.Kind, Is.EqualTo(DirectiveKind.StockFood));
            Assert.That(target.Mind.PendingDirective!.FromId, Is.EqualTo(actor.Id));
            Assert.That(Events(engine.World, "DirectivePending", target), Is.True);
            // The listener keeps the shared subject while she is still deciding.
            Assert.That(target.Execution.CurrentTalkTopic, Is.Not.EqualTo(TalkTopic.DirectiveNo));
        });

        // §167.8: the agent answers.
        Assert.That(DirectiveMath.Respond(engine.World, target, actor.Id, DirectiveKind.StockFood, accept: true, out var reason),
            Is.True, reason);
        Assert.That(target.Mind.Directive?.Kind, Is.EqualTo(DirectiveKind.StockFood));
        Assert.That(target.Mind.PendingDirective, Is.Null);

        // A second ask with no answer: the timeout lets the simulation decide.
        target.Mind.Directive = null;
        target.Mind.PendingDirective = new PendingDirective
        {
            Kind = DirectiveKind.StockWater, FromId = actor.Id,
            SinceTick = engine.World.Tick - Spec167.PendingTimeoutTicks
        };
        target.Mind.ExternalControl = null;
        DirectiveMath.Update(engine.World, target);
        Assert.That(EventMessage(engine.World, "DirectiveAccepted", target), Does.Contain("Kind=StockWater"),
            "Timeout falls back to the ordinary willingness decision (affinity 0.9 accepts).");
        Assert.That(target.Mind.PendingDirective, Is.Null);
    }

    [Test]
    public void PullBiasesOnlyTheGoalsOfItsKind()
    {
        var plain = TestWorld.CreateEngine();
        var biased = TestWorld.CreateEngine();
        var plainNpc = plain.World.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        var biasedNpc = biased.World.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        for (var i = 0; i < 8; i++) { plain.Step(); biased.Step(); }
        biasedNpc.Mind.Directive = new Directive
        {
            Kind = DirectiveKind.Build, IssuedTick = biased.World.Tick,
            UntilTick = biased.World.Tick + Spec167.DirectiveTicks
        };
        for (var i = 0; i < 4; i++) { plain.Step(); biased.Step(); }

        Assert.That(biasedNpc.Mind.LastScores.Count, Is.EqualTo(plainNpc.Mind.LastScores.Count).And.GreaterThan(0));
        foreach (var a in plainNpc.Mind.LastScores)
        {
            var b = biasedNpc.Mind.LastScores.Single(s => s.Goal == a.Goal);
            var pull = DirectiveMath.Pull(DirectiveKind.Build, a.Goal);
            if (pull == 0f || b.FinalScore == 0f)
            {
                Assert.That(b.FinalScore, Is.EqualTo(a.FinalScore), a.Goal.ToString());
                Assert.That(b.CommandModifier, Is.EqualTo(pull), a.Goal.ToString());
            }
            else
            {
                Assert.That(b.FinalScore - a.FinalScore, Is.EqualTo(pull).Within(1e-5f), a.Goal.ToString());
            }
        }
    }

    [Test]
    public void DirectiveNeverOverridesSurvival()
    {
        var engine = TestWorld.CreateEngine();
        var npc = engine.World.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        npc.Mind.Directive = new Directive
        {
            Kind = DirectiveKind.StockFood, IssuedTick = 0, UntilTick = Spec167.DirectiveTicks
        };
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked));
        npc.Needs.Hunger = 0.9f;
        for (var i = 0; i < 12 && npc.Mind.CurrentGoal != GoalType.Eat; i++)
        {
            npc.Needs.Hunger = 0.9f;
            engine.Step();
        }

        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.Eat));
    }

    [Test]
    public void StockFoodRestraintHoldsOnlyBelowTheHungerThreshold()
    {
        var engine = TestWorld.CreateEngine();
        var npc = engine.World.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        npc.Mind.Directive = new Directive
        {
            Kind = DirectiveKind.StockFood, IssuedTick = 0, UntilTick = Spec167.DirectiveTicks
        };
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked));
        Calm(npc);
        npc.Needs.Hunger = 0.2f;
        for (var i = 0; i < 8; i++) { npc.Needs.Hunger = 0.2f; engine.Step(); }
        var eat = npc.Mind.LastScores.Single(s => s.Goal == GoalType.Eat);
        Assert.That(eat.FinalScore, Is.EqualTo(0f), "Stock is not eaten while she is not hungry.");
    }

    [Test]
    public void StockReachedAndTimeoutClearTheDirective()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var npc = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).First();
        Assert.That(ColonyQueries.Home(world, Faction.Colony), Is.Not.Null);
        var home = ColonyQueries.Home(world, Faction.Colony)!.Value;
        var spawned = 0;
        foreach (var tile in world.Tiles.Items.Keys.OrderBy(t => t.Q).ThenBy(t => t.R))
        {
            if (spawned >= Spec167.StockFoodTarget) break;
            if (HexSpatialMath.HexDistance(home, tile) > Spec167.StockRadiusTiles) continue;
            if (StructurePlacement.CenterJunction(world, tile) is not { } anchor) continue;
            WorldObjectMutations.SpawnObject(world, ContentIds.MeatCooked,
                world.Junctions.Items[anchor].Fragment, tile, anchor);
            spawned++;
        }
        Assert.That(spawned, Is.EqualTo(Spec167.StockFoodTarget));
        Assert.That(ColonyQueries.CampStock(world, Faction.Colony, DirectiveKind.StockFood),
            Is.GreaterThanOrEqualTo(Spec167.StockFoodTarget));

        var asker = world.Entities.Npcs.Values.OrderBy(n => n.Id.Value).Skip(1).First();
        npc.Mind.Directive = new Directive
        {
            Kind = DirectiveKind.StockFood, FromId = asker.Id,
            IssuedTick = world.Tick, UntilTick = world.Tick + Spec167.DirectiveTicks
        };
        var before = npc.Social.GetOrCreate(asker.Id).Authority;
        Assert.That(DirectiveMath.Update(world, npc), Is.EqualTo(DirectiveKind.None));
        Assert.Multiple(() =>
        {
            Assert.That(npc.Mind.Directive, Is.Null);
            Assert.That(EventMessage(world, "DirectiveDone", npc), Does.Contain("Reason=StockReached"));
            Assert.That(npc.Social.GetOrCreate(asker.Id).Authority - before,
                Is.EqualTo(Spec167.AuthorityOnDone).Within(1e-6f));
        });

        npc.Mind.Directive = new Directive
        {
            Kind = DirectiveKind.Build, FromId = asker.Id,
            IssuedTick = world.Tick - 10, UntilTick = world.Tick - 1
        };
        Assert.That(DirectiveMath.Update(world, npc), Is.EqualTo(DirectiveKind.None));
        Assert.That(EventMessage(world, "DirectiveExpired", npc), Does.Contain("Reason=TimedOut"));
    }

    [Test]
    public void SaveKeepsDirectiveAndAuthorityAndOldSaveHasNone()
    {
        var engine = Setup(out var actor, out var target);
        Warm(target, actor, affinity: 0.5f, trust: 0.5f);
        Ask(engine.World, actor, target, TalkTopic.AskStockWater);
        StartTalk(engine, actor, target);
        Assert.That(target.Mind.Directive, Is.Not.Null);
        var expected = target.Mind.Directive!;

        var loaded = Reload(engine.World, WorldSaveSerializer.BlobVersion).World.Entities.Npcs[target.Id];
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Mind.Directive, Is.Not.Null);
            Assert.That(loaded.Mind.Directive!.Kind, Is.EqualTo(expected.Kind));
            Assert.That(loaded.Mind.Directive!.FromId, Is.EqualTo(expected.FromId));
            Assert.That(loaded.Mind.Directive!.IssuedTick, Is.EqualTo(expected.IssuedTick));
            Assert.That(loaded.Mind.Directive!.UntilTick, Is.EqualTo(expected.UntilTick));
            Assert.That(loaded.Social.GetOrCreate(actor.Id).Authority,
                Is.EqualTo(target.Social.GetOrCreate(actor.Id).Authority));
        });

        var old = Reload(engine.World, 77).World.Entities.Npcs[target.Id];
        Assert.That(old.Mind.Directive, Is.Null);
        Assert.That(old.Social.GetOrCreate(actor.Id).Authority, Is.EqualTo(0f));
    }

    [Test]
    public void ShoutSkipsManualAndCapsResponders()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girls = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value).ToArray();
        Assert.That(girls.Length, Is.GreaterThanOrEqualTo(3));
        var asker = girls[0];
        asker.Mind.ManualControl = true;
        var manualHearer = girls[1];
        manualHearer.Mind.ManualControl = true;
        foreach (var g in girls) { Calm(g); Warm(g, asker, affinity: 0.9f, trust: 0.9f); }
        engine.Step();

        var accepted = DirectiveMath.Shout(world, asker, DirectiveKind.Build);
        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.LessThanOrEqualTo(Spec167.MaxShoutResponders));
            Assert.That(accepted, Is.GreaterThan(0));
            Assert.That(manualHearer.Mind.Directive, Is.Null);
            Assert.That(EventMessage(world, "DirectiveRefused", manualHearer), Does.Contain("Reason=Manual"));
            Assert.That(Events(world, "DirectiveShout", asker), Is.True);
            Assert.That(girls.Count(g => g.Mind.Directive?.Kind == DirectiveKind.Build), Is.EqualTo(accepted));
        });
    }

    [Test]
    public void SetDirectiveAssignsOwnGirlsWithoutManualControl()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var girls = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value).Take(2).Select(n => n.Id).ToArray();
        var admission = ManualCommandExecutor.Apply(world, new SetDirectiveCommand(girls, DirectiveKind.StockWater));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        foreach (var id in girls)
        {
            var npc = world.Entities.Npcs[id];
            Assert.That(npc.Mind.ManualControl, Is.False);
            Assert.That(npc.Mind.Directive?.Kind, Is.EqualTo(DirectiveKind.StockWater));
            Assert.That(npc.Mind.Directive!.FromId, Is.Null);
        }

        var bad = ManualCommandExecutor.Apply(world, new SetDirectiveCommand(girls, (DirectiveKind)99));
        Assert.That(bad.Status, Is.EqualTo(ManualCommandAdmissionStatus.Rejected));
        Assert.That(bad.Reason, Is.EqualTo("InvalidKind"));
    }

    [Test]
    public void AskTopicsPassTheCommandWhitelist()
    {
        foreach (var topic in new[] { TalkTopic.AskBuild, TalkTopic.AskStockFood, TalkTopic.AskStockWater, TalkTopic.AskFirewood })
            Assert.That(TalkTopicRequest.IsAllowed(topic), Is.True, topic.ToString());
        Assert.That(TalkTopicRequest.IsAllowed(TalkTopic.DirectiveYes), Is.False);
        Assert.That(TalkTopicRequest.IsAllowed(TalkTopic.DirectiveNo), Is.False);
    }

    // ── helpers (shape of RequestedTalkTopicTests) ──────────────────────

    private static void Calm(NPCState npc)
    {
        npc.Needs.Hunger = 0f; npc.Needs.Thirst = 0f; npc.Needs.Energy = 1f; npc.Needs.ThermalComfort = 0f;
    }

    private static void Warm(NPCState who, NPCState towards, float affinity, float trust)
    {
        var rel = who.Social.GetOrCreate(towards.Id);
        rel.Affinity = affinity;
        rel.Trust = trust;
    }

    private static bool Events(WorldState world, string type, NPCState npc) =>
        world.Events.Items.Any(e => e.Type == type && e.EntityId == npc.Id.Value);

    private static string EventMessage(WorldState world, string type, NPCState npc) =>
        world.Events.Items.LastOrDefault(e => e.Type == type && e.EntityId == npc.Id.Value)?.Message ?? string.Empty;

    private static SimulationEngine Setup(out NPCState actor, out NPCState target, bool targetManual = false)
    {
        var engine = TestWorld.CreateEngine();
        var pair = engine.World.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).OrderBy(n => n.Id.Value).Take(2).ToArray();
        actor = pair[0]; target = pair[1];
        foreach (var npc in pair) { Calm(npc); engine.Commands.Enqueue(new SetManualControlCommand(npc.Id, true)); }
        engine.Step();
        var world = engine.World;
        var id = SpatialQueries.GetPassableNeighbors(world, actor.CurrentJunction!.Value).First(j => SpatialQueries.IsJunctionFree(world, j));
        var junction = world.Junctions.Items[id];
        if (target.CurrentJunction is { } old) { SpatialMutations.FreeJunction(world, old, target.Id); SpatialMutations.ReleaseJunctionReservation(world, old, target.Id); }
        var oldTile = target.Tile;
        target.Tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : target.Tile;
        target.Fragment = junction.Fragment; target.Position = junction.WorldPosition; target.CurrentJunction = id;
        SpatialMutations.MoveEntityToTile(world, target.Id, oldTile, target.Tile);
        SpatialMutations.OccupyJunction(world, id, target.Id);
        if (!targetManual)
        {
            target.Mind.ManualControl = false;
        }
        return engine;
    }

    private static void Ask(WorldState world, NPCState actor, NPCState target, TalkTopic topic)
    {
        var admission = ManualCommandExecutor.Apply(world, new TalkToCommand(actor.Id, target.Id, topic));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
    }

    private static void StartTalk(SimulationEngine engine, NPCState actor, NPCState target, float hungerOfTarget = 0f)
    {
        for (var i = 0; i < 120 && actor.Execution.CurrentInteraction != InteractionType.Talk; i++)
        {
            Calm(actor); Calm(target);
            target.Needs.Hunger = hungerOfTarget;
            engine.Step();
        }
        Assert.That(actor.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Talk));
        Assert.That(actor.Execution.Status, Is.EqualTo(ExecutionStatus.InProgress));
    }

    private static SimulationEngine Reload(WorldState world, int version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.WriteAtVersion(world, writer, version);
        buffer.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(loaded, reader);
        var clock = new SimulationClock(); clock.Resume();
        var engine = new SimulationEngine(loaded, new SimulationSettings(), clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        return engine;
    }
}
