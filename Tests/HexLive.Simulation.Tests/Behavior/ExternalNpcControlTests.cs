using System.IO;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class ExternalNpcControlTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AttachmentAndActionReleasePreservePlayersSavedSwitch(bool playerManual)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Mind.ManualControl = playerManual;
        var control = new ExternalNpcControl();
        Assert.That(control.Bind(world, npc), Is.True);
        Assert.That(npc.Mind.ManualControl, Is.True);
        ExternalNpcControl.TryReleaseAction(world, npc);
        Assert.That(npc.Mind.ManualControl, Is.True);
        Assert.That(npc.Mind.PersistedManualControl, Is.EqualTo(playerManual));
        control.Dispose();
        Assert.That(npc.Mind.ManualControl, Is.EqualTo(playerManual));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveAttachedLoadUnattachedPreservesOnlyPlayersSwitch(bool playerManual)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        var id = npc.Id;
        npc.Mind.ManualControl = playerManual;
        new ExternalNpcControl().Bind(world, npc);
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                WorldSaveSerializer.Write(world, writer);
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                WorldSaveSerializer.Read(world, reader);
        }
        var loaded = world.Entities.Npcs[id];
        Assert.That(loaded.Mind.ExternalControl, Is.Null);
        Assert.That(loaded.Mind.ManualControl, Is.EqualTo(playerManual));
    }

    [Test]
    public void TakeoverDoesNotCancelOngoingSelfDefenseDuringVoluntaryApproach()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToArray();
        var npc = pair[0];
        npc.Mind.ExpulsionPhase = 1;
        npc.Mind.ExpulsionTargetNpcId = pair[1].Id;
        npc.Mind.CombatOpponentNpcId = pair[1].Id;
        npc.IsFighting = true;
        new ExternalNpcControl().Bind(world, npc);
        Assert.That(npc.IsFighting, Is.True);
        Assert.That(npc.Mind.CombatOpponentNpcId, Is.EqualTo(pair[1].Id));
    }

    [Test]
    public void TakeoverCancelsVoluntaryApproachBeforeCombat()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToArray();
        var npc = pair[0];
        npc.Mind.ExpulsionPhase = 1;
        npc.Mind.ExpulsionTargetNpcId = pair[1].Id;
        pair[1].Mind.PendingExpulsionFrom = npc.Id;
        new ExternalNpcControl().Bind(world, npc);
        Assert.That(npc.Mind.ExpulsionTargetNpcId, Is.Null);
        Assert.That(pair[1].Mind.PendingExpulsionFrom, Is.Null);
    }

    [Test]
    public void IncidentWitnessMustHaveActiveAttachmentAndPersonallySeeActor()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToArray();
        var observer = pair[0];
        var actor = pair[1];
        observer.Perception.Agents.Clear();
        observer.Perception.Hostiles.Clear();
        var control = new ExternalNpcControl();
        control.Bind(world, observer);
        Assert.That(AgentIncidentNotifications.CanWitness(world, observer, actor), Is.False);
        observer.Perception.Hostiles.Add(new PerceivedAgent { Id = actor.Id, CanSee = true });
        Assert.That(AgentIncidentNotifications.CanWitness(world, observer, actor), Is.True);
        control.Dispose();
        Assert.That(AgentIncidentNotifications.CanWitness(world, observer, actor), Is.False);
    }

    [Test]
    public void LootNotificationDoesNotLeakHiddenVictimAndIsAddressedOnlyToWitness()
    {
        var world = TestWorld.CreateWorld();
        var people = world.Entities.Npcs.Values.Take(3).ToArray();
        var observer = people[0];
        var actor = people[1];
        var victim = people[2];
        new ExternalNpcControl().Bind(world, observer);
        observer.Perception.Agents.Clear();
        observer.Perception.Hostiles.Clear();
        observer.Perception.Hostiles.Add(new PerceivedAgent { Id = actor.Id, CanSee = true });
        AgentIncidentNotifications.Loot(world, actor, victim.Id, "Started");
        Assert.That(world.Events.Items.Any(e => e.Type == "AgentObservedLoot"), Is.False);
        observer.Perception.Agents.Add(new PerceivedAgent { Id = victim.Id, CanSee = true });
        AgentIncidentNotifications.Loot(world, actor, victim.Id, "Started");
        var notification = world.Events.Items.Single(e => e.Type == "AgentObservedLoot");
        Assert.That(notification.EntityId, Is.EqualTo(observer.Id.Value));
        Assert.That(notification.Message, Does.Contain("Actor=NPC" + actor.Id.Value));
        Assert.That(observer.Mind.CurrentGoal, Is.EqualTo(GoalType.None));
    }

    [Test]
    public void GroundTheftRequiresCurrentSourceVisibilityNotRememberedObject()
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Take(2).ToArray();
        var observer = pair[0];
        var actor = pair[1];
        new ExternalNpcControl().Bind(world, observer);
        observer.Perception.Agents.Clear();
        observer.Perception.Hostiles.Clear();
        observer.Perception.Objects.Clear();
        observer.Perception.Agents.Add(new PerceivedAgent { Id = actor.Id, CanSee = true });
        var source = world.Entities.Objects.Values.First();
        var remembered = new PerceivedObject { Id = source.Id, FromMemory = true };
        observer.Perception.Objects.Add(remembered);
        AgentIncidentNotifications.Theft(world, actor, source.DefinitionId, source: source);
        Assert.That(world.Events.Items.Any(e => e.Type == "AgentObservedTheft"), Is.False);
        remembered.FromMemory = false;
        AgentIncidentNotifications.Theft(world, actor, source.DefinitionId, source: source);
        Assert.That(world.Events.Items.Single(e => e.Type == "AgentObservedTheft").EntityId,
            Is.EqualTo(observer.Id.Value));
    }
}
}
