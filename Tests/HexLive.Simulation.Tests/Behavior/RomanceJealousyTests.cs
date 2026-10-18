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

/// <summary>§127.13: a lover who catches the scene sours on both accomplices, as in The Sims.</summary>
public sealed class RomanceJealousyTests
{
    private sealed record Triangle(WorldState World, NPCState Male, NPCState Lover, NPCState Other);

    private static Triangle MakeTriangle(int seed = 12709) =>
        MakeTriangle(TestWorld.CreateWorld(seed));

    private static Triangle MakeTriangle(WorldState world)
    {
        world.Tick = 10;
        var male = world.Entities.Npcs.Values.First(n => n.Sex == GarmentSex.Male);
        var females = world.Entities.Npcs.Values.Where(n => n.Sex == GarmentSex.Female).ToList();
        Assume.That(females.Count, Is.GreaterThanOrEqualTo(2));
        var lover = females[0];
        var other = females[1];
        foreach (var npc in new[] { male, lover, other })
        {
            npc.Faction = Faction.Colony;
            npc.Mind.FaintedUntilTick = 0;
            npc.Mind.RomanceCooldownUntilTick = 0;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Needs.Social = 0.8f;
        }

        Bond(lover, male, lover: true);
        Bond(male, lover, lover: true);
        Bond(lover, other, lover: false);
        return new Triangle(world, male, lover, other);
    }

    private static void Bond(NPCState from, NPCState to, bool lover)
    {
        var rel = from.Social.GetOrCreate(to.Id);
        rel.Affinity = 0.70f;
        rel.Trust = 0.60f;
        rel.Familiarity = 0.70f;
        rel.IsLover = lover;
    }

    private static void LetSee(NPCState witness, params NPCState[] seen)
    {
        witness.Perception.Agents.Clear();
        foreach (var npc in seen)
        {
            witness.Perception.Agents.Add(new PerceivedAgent { Id = npc.Id, Tile = npc.Tile, CanSee = true });
        }
    }

    private static int Caught(WorldState world) =>
        world.Events.Items.Count(e => e.Type == "CaughtCheating");

    [Test]
    public void LoverWhoSeesTheSceneSoursOnBothAccomplices()
    {
        var (world, male, lover, other) = MakeTriangle();
        LetSee(lover, male, other);

        RomanceMath.CatchCheaters(world, male, other, forced: false);

        var toMale = lover.Social.GetOrCreate(male.Id);
        var toOther = lover.Social.GetOrCreate(other.Id);
        Assert.Multiple(() =>
        {
            Assert.That(toMale.IsLover, Is.False, "The Sims: the love marker is lost");
            Assert.That(toMale.Affinity, Is.EqualTo(Spec127.JealousyAffinityCeiling).Within(1e-4f),
                "The Sims 2: 0.70 − 0.50 = 0.20, but the bond must fall below zero");
            Assert.That(toMale.Trust, Is.EqualTo(0.10f).Within(1e-4f));
            Assert.That(toOther.Affinity, Is.EqualTo(Spec127.JealousyAffinityCeiling).Within(1e-4f),
                "the one he cheats with is soured too");
            Assert.That(toOther.Trust, Is.EqualTo(0.60f).Within(1e-4f),
                "she promised nothing, trust in her is untouched");
            Assert.That(lover.Needs.Social, Is.Zero, "The Sims 1: social need depleted");
            Assert.That(lover.Execution.LastSocialCueKind, Is.EqualTo("CaughtCheating"));
            Assert.That(lover.Execution.LastSocialCuePeerId, Is.EqualTo(male.Id));
            Assert.That(male.Social.GetOrCreate(lover.Id).IsLover, Is.True,
                "the mark is one-sided: only the jealous one loses it");
            Assert.That(Caught(world), Is.EqualTo(1));
        });

        var message = world.Events.Items.Single(e => e.Type == "CaughtCheating").Message;
        Assert.That(message, Does.Contain($"NPC{lover.Id.Value}->NPC{male.Id.Value}"));
        Assert.That(message, Does.Contain($"With=NPC{other.Id.Value}"));
        Assert.That(lover.Journal.PendingType, Is.EqualTo("CaughtCheating"));
        Assert.That(lover.Journal.PendingSubjectId, Is.EqualTo(male.Id));

        RomanceMath.CatchCheaters(world, male, other, forced: false);
        Assert.That(Caught(world), Is.EqualTo(1), "once per scene: nobody left to be jealous of");
    }

    [Test]
    public void SecondLoverCatchesHimWithTheFirst()
    {
        var (world, male, lover, other) = MakeTriangle();
        // He has two lovers; the second one walks in on him with the first.
        Bond(other, male, lover: true);
        Bond(other, lover, lover: false);
        LetSee(other, male);

        RomanceMath.CatchCheaters(world, male, lover, forced: false);

        Assert.Multiple(() =>
        {
            Assert.That(other.Social.GetOrCreate(male.Id).IsLover, Is.False);
            Assert.That(other.Social.GetOrCreate(male.Id).Affinity,
                Is.EqualTo(Spec127.JealousyAffinityCeiling).Within(1e-4f));
            Assert.That(other.Social.GetOrCreate(lover.Id).Affinity,
                Is.EqualTo(Spec127.JealousyAffinityCeiling).Within(1e-4f));
            Assert.That(lover.Social.GetOrCreate(male.Id).IsLover, Is.True,
                "the one in the scene is not the witness and keeps her mark");
            Assert.That(Caught(world), Is.EqualTo(1));
        });
    }

    [Test]
    public void WitnessWhoLovesBothParticipantsIsHitOnBothSidesByOneEvent()
    {
        var (world, male, lover, other) = MakeTriangle();
        // Both lovers of the witness are in the scene: she trusted each.
        var witness = lover;
        Bond(witness, other, lover: true);
        LetSee(witness, male, other);

        RomanceMath.CatchCheaters(world, male, other, forced: false);

        Assert.Multiple(() =>
        {
            Assert.That(witness.Social.GetOrCreate(male.Id).IsLover, Is.False);
            Assert.That(witness.Social.GetOrCreate(other.Id).IsLover, Is.False);
            Assert.That(witness.Social.GetOrCreate(male.Id).Trust, Is.EqualTo(0.10f).Within(1e-4f));
            Assert.That(witness.Social.GetOrCreate(other.Id).Trust, Is.EqualTo(0.10f).Within(1e-4f));
            Assert.That(Caught(world), Is.EqualTo(1));
        });
    }

    [Test]
    public void SleepingBlindOrMerelyFriendlyWitnessesAreNotJealous()
    {
        var (world, male, lover, other) = MakeTriangle();

        lover.Perception.Agents.Clear();
        RomanceMath.CatchCheaters(world, male, other, forced: false);
        Assert.That(Caught(world), Is.Zero, "did not see it");

        LetSee(lover, male, other);
        lover.Execution.CurrentInteraction = InteractionType.Sleep;
        RomanceMath.CatchCheaters(world, male, other, forced: false);
        Assert.That(Caught(world), Is.Zero, "The Sims 2: a sleeping lover does not catch it");
        lover.Execution.CurrentInteraction = null;

        lover.Social.GetOrCreate(male.Id).IsLover = false;
        RomanceMath.CatchCheaters(world, male, other, forced: false);
        Assert.Multiple(() =>
        {
            Assert.That(Caught(world), Is.Zero, "friendship without intimacy is not love");
            Assert.That(lover.Social.GetOrCreate(male.Id).Affinity, Is.EqualTo(0.70f));
            Assert.That(lover.Needs.Social, Is.EqualTo(0.8f));
        });
    }

    [Test]
    public void ForcedSceneVictimIsNotACheater()
    {
        var (world, male, lover, other) = MakeTriangle();
        // The witness loves the victim only: nothing to be jealous of.
        Bond(lover, male, lover: false);
        Bond(lover, other, lover: true);
        LetSee(lover, male, other);
        RomanceMath.CatchCheaters(world, male, other, forced: true);
        Assert.That(Caught(world), Is.Zero);
        Assert.That(lover.Social.GetOrCreate(other.Id).IsLover, Is.True);

        // The witness loves the aggressor: only he is soured, the victim is spared.
        Bond(lover, male, lover: true);
        RomanceMath.CatchCheaters(world, male, other, forced: true);
        Assert.Multiple(() =>
        {
            Assert.That(Caught(world), Is.EqualTo(1));
            Assert.That(lover.Social.GetOrCreate(male.Id).IsLover, Is.False);
            Assert.That(lover.Social.GetOrCreate(other.Id).Affinity, Is.EqualTo(0.70f));
            Assert.That(lover.Social.GetOrCreate(other.Id).IsLover, Is.True);
        });
    }

    [Test]
    public void ConsensualSceneMakesLoversAndTheRunningSceneIsWatched()
    {
        var (world, male, lover, other) = MakeTriangle();
        // Fresh pair: the scene between him and the other woman.
        Bond(male, other, lover: false);
        Bond(other, male, lover: false);
        PreparePair(world, male, other);
        male.Mind.ManualControl = true;

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(male.Id, other.Id, forced: false));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);
        MoveTo(world, male, male.Plan.TargetJunctionId!.Value);
        male.Movement.SetStatus(MovementStatus.Arrived);
        var execution = new ExecutionSystem();
        execution.Run(world); // invitation talk
        world.Tick = male.Execution.EndTick;
        execution.Run(world); // answer
        execution.Run(world); // scene starts
        Assert.That(male.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Romance));

        lover.Execution.CurrentInteraction = null;
        LetSee(lover, male, other);
        world.Tick++;
        execution.Run(world);
        Assert.That(Caught(world), Is.EqualTo(1), "the running scene is watched every tick");
        Assert.That(male.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Romance),
            "being caught does not stop the scene");

        world.Tick = male.Execution.EndTick;
        execution.Run(world);
        Assert.Multiple(() =>
        {
            Assert.That(male.Social.GetOrCreate(other.Id).IsLover, Is.True);
            Assert.That(other.Social.GetOrCreate(male.Id).IsLover, Is.True);
        });
    }

    [Test]
    public void LoverMarkSurvivesSaveAndOldSavesHaveNone()
    {
        var (world, male, lover, _) = MakeTriangle();

        var loaded = RoundTrip(world, WorldSaveSerializer.BlobVersion);
        Assert.That(loaded.Entities.Npcs[lover.Id].Social.Relationships[male.Id].IsLover, Is.True);

        var old = RoundTrip(world, 85);
        Assert.That(old.Entities.Npcs[lover.Id].Social.Relationships[male.Id].IsLover, Is.False);
        Assert.That(old.Entities.Npcs[lover.Id].Social.Relationships[male.Id].Affinity,
            Is.EqualTo(0.70f));
    }

    [Test]
    public void JealousLoverWalksOverAndLandsOneFistBlowWithoutAFight()
    {
        var engine = TestWorld.CreateEngine(12709);
        var world = engine.World;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            npc.Mind.ManualControl = true;
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Needs.Energy = 1f;
        }

        var (_, male, lover, other) = MakeTriangle(world);
        Bond(male, other, lover: false);
        Bond(other, male, lover: false);
        PreparePair(world, male, other);
        PlaceNear(world, lover, male);
        var cheaterHealth = male.Health;
        var cheaterToLover = male.Social.GetOrCreate(lover.Id).Affinity;

        var admission = ManualCommandExecutor.Apply(world,
            new RomancePersonCommand(male.Id, other.Id, forced: false));
        Assert.That(admission.Status, Is.EqualTo(ManualCommandAdmissionStatus.Accepted), admission.Reason);

        var sawScene = false;
        var headed = false;
        var fought = false;
        for (var i = 0; i < 2000 && male.HitStampTick == 0; i++)
        {
            engine.Step();
            sawScene |= male.Execution.CurrentInteraction == InteractionType.Romance;
            // She stands by until the scene starts, then her own AI takes over.
            if (sawScene) lover.Mind.ManualControl = false;
            headed |= lover.Mind.CurrentGoal == GoalType.Jealousy;
            fought |= lover.IsFighting || male.IsFighting || other.IsFighting;
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawScene, Is.True, "the scene must actually run");
            Assert.That(Caught(world), Is.EqualTo(1));
            Assert.That(headed, Is.True, "the jealous walk is a real plan");
            Assert.That(male.HitStampTick, Is.GreaterThan(0), "one blow landed on the cheater");
            Assert.That(male.HitWeaponId, Is.Empty, "a bare-handed blow, not her knife");
            Assert.That(male.Health, Is.LessThan(cheaterHealth));
            Assert.That(fought, Is.False, "no fight: nobody is latched into combat");
            Assert.That(male.Execution.CurrentInteraction, Is.Not.EqualTo(InteractionType.Romance),
                "as in the Sims, the slap ends the scene");
            Assert.That(other.Execution.CurrentInteraction, Is.Not.EqualTo(InteractionType.Romance));
            Assert.That(male.Social.GetOrCreate(lover.Id).Affinity,
                Is.EqualTo(cheaterToLover).Within(0.01f),
                "§93.5 is skipped: the blow does not make him hate her (only ordinary drift)");
            Assert.That(lover.Social.GetOrCreate(male.Id).Affinity,
                Is.EqualTo(Spec127.JealousyAffinityCeiling).Within(1e-4f),
                "her side was soured by the jealousy alone, not pushed to −0.50");
            Assert.That(lover.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Jealousy));
            Assert.That(lover.Mind.ForcedMeleeWeaponId, Is.Null, "her own weapon choice comes back");
            Assert.That(male.Social.GetOrCreate(other.Id).IsLover, Is.False,
                "an interrupted scene does not make lovers");
        });
    }

    [Test]
    public void PlayerDrivenWitnessStaysJealousButDoesNotStrike()
    {
        var engine = TestWorld.CreateEngine(12709);
        var world = engine.World;
        var (_, male, lover, other) = MakeTriangle(world);
        PreparePair(world, male, other);
        PlaceNear(world, lover, male);
        lover.Mind.ManualControl = true;
        LetSee(lover, male, other);

        RomanceMath.CatchCheaters(world, male, other, forced: false);

        Assert.Multiple(() =>
        {
            Assert.That(Caught(world), Is.EqualTo(1));
            Assert.That(lover.Mind.CurrentGoal, Is.Not.EqualTo(GoalType.Jealousy),
                "§121.5: the strike is a choice, and a manual body's choices are the player's");
        });
    }

    [Test]
    public void InterruptedJealousWalkReturnsHerOwnWeaponChoice()
    {
        var engine = TestWorld.CreateEngine(12709);
        var world = engine.World;
        var (_, male, lover, other) = MakeTriangle(world);
        PreparePair(world, male, other);
        PlaceNear(world, lover, male);
        lover.Mind.ManualControl = false;
        LetSee(lover, male, other);
        RomanceMath.CatchCheaters(world, male, other, forced: false);
        Assert.That(lover.Mind.CurrentGoal, Is.EqualTo(GoalType.Jealousy));

        lover.Mind.ForcedMeleeWeaponId = string.Empty; // as if mid-swing
        PlanInterruption.TryAbort(world, lover, InterruptionCause.ThreatReroute, "test danger");

        Assert.That(lover.Mind.ForcedMeleeWeaponId, Is.Null);
    }

    private static void PlaceNear(WorldState world, NPCState npc, NPCState anchor)
    {
        var near = SpatialQueries.GetPassableNeighbors(world, anchor.CurrentJunction!.Value)
            .SelectMany(id => SpatialQueries.GetPassableNeighbors(world, id))
            .First(id => SpatialQueries.IsJunctionFree(world, id) &&
                         !world.Entities.Npcs.Values.Any(n => n.CurrentJunction is { } j && j.Equals(id)) &&
                         !id.Equals(anchor.CurrentJunction!.Value));
        npc.Execution.Status = ExecutionStatus.None;
        npc.Execution.CurrentInteraction = null;
        npc.Plan.Status = PlanStatus.None;
        npc.Plan.Steps.Clear();
        npc.Movement.JunctionPath.Clear();
        npc.Movement.IsMoving = false;
        npc.Movement.SetStatus(MovementStatus.Arrived);
        MoveTo(world, npc, near);
    }

    private static WorldState RoundTrip(WorldState source, int version)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        stream.Position = 0;
        var loaded = TestWorld.CreateWorld(source.Seed);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        WorldSaveSerializer.Read(loaded, reader);
        return loaded;
    }

    private static void PreparePair(WorldState world, NPCState male, NPCState female)
    {
        foreach (var npc in new[] { male, female })
        {
            npc.Health = npc.Body.Mean();
            npc.IsFighting = false;
            npc.Mind.CombatOpponentNpcId = null;
            npc.Mind.CryingUntilTick = 0;
            npc.Plan.Goal = GoalType.None;
            npc.Plan.Status = PlanStatus.None;
            npc.Plan.Steps.Clear();
            npc.Plan.CurrentStepIndex = 0;
            npc.Plan.TargetObjectId = null;
            npc.Plan.TargetJunctionId = null;
            npc.Plan.TargetTile = null;
            npc.Plan.TargetAgentId = null;
            npc.Execution.TargetObject = null;
            npc.Execution.StartTick = 0;
            npc.Execution.EndTick = 0;
            npc.Movement.JunctionPath.Clear();
            npc.Movement.IsMoving = false;
            npc.Movement.SetStatus(MovementStatus.Arrived);
            if (npc.CurrentJunction is null)
            {
                MoveTo(world, npc, world.Tiles.Items[npc.Tile].Junctions
                    .First(id => !world.Junctions.Items[id].Blocked &&
                                 SpatialQueries.IsJunctionFree(world, id)));
            }
        }

        MoveTo(world, female, SpatialQueries.GetPassableNeighbors(world, male.CurrentJunction!.Value)
            .First(id => SpatialQueries.IsJunctionFree(world, id)));
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
