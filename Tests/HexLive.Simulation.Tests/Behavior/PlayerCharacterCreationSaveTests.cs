using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PlayerCharacterCreationSaveTests
{
    [Test]
    public void LandingIsDeterministicAndAllocatedIdsAreNeverReusedAfterRemoval()
    {
        var first = TestWorld.CreateWorld(); var second = TestWorld.CreateWorld();
        var request = new CharacterCreationConfig { Body = "Marta", Skin = "Marta", Name = "Player", Camp = Faction.Colony };
        var owner = new string('1', 32); var requestId = new string('2', 32);
        // A pre-existing occupant of the reserved band must be skipped deterministically.
        foreach (var world in new[] { first, second })
            world.Entities.RegisterObject(new WorldObjectState { Id = new ObjectId(PlayerCharacterCreation.FirstNpcId), DefinitionId = ContentIds.Coconut });
        Assert.That(PlayerCharacterCreation.TryCreate(first, owner, requestId, request, out var a), Is.Empty);
        Assert.That(PlayerCharacterCreation.TryCreate(second, owner, requestId, request, out var b), Is.Empty);
        var npc = first.Entities.Npcs[new EntityId(a.NpcId)]; var other = second.Entities.Npcs[new EntityId(b.NpcId)];
        Assert.That(a.NpcId, Is.EqualTo(PlayerCharacterCreation.FirstNpcId + 1));
        Assert.That((npc.Id, npc.Tile, npc.Position, npc.CurrentJunction), Is.EqualTo((other.Id, other.Tile, other.Position, other.CurrentJunction)));
        first.Entities.Npcs.Remove(npc.Id); // Includes the case where the corpse was already cleaned up.
        Assert.That(PlayerCharacterCreation.TryCreate(first, owner, new string('3', 32), request, out var next), Is.Empty);
        Assert.That(next.NpcId, Is.GreaterThan(a.NpcId));
        Assert.That(PlayerCharacterCreation.TryCreate(first, owner, requestId, request, out var repeated), Is.Empty);
        Assert.That(repeated.NpcId, Is.EqualTo(a.NpcId));
    }

    [TestCase(82)]
    [TestCase(83)]
    public void CreationReceiptsAreVersionedPrivateSaveState(int version)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        // Use an actual NPC so restoration also exercises the ownership projection.
        var oldId = npc.Id; world.Entities.Npcs.Remove(oldId);
        npc.Id = new EntityId(PlayerCharacterCreation.FirstNpcId); world.Entities.Npcs[npc.Id] = npc;
        var receipt = new PlayerCharacterCreationReceipt { PlayerId = new string('1', 32), RequestId = new string('2', 32),
            NpcId = npc.Id.Value, Name = "Свой герой", Camp = Faction.Colony };
        world.PlayerCharacterCreations.Add(receipt);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.WriteAtVersion(world, writer, version);
        var restored = TestWorld.CreateWorld(); bytes.Position = 0;
        using var reader = new BinaryReader(bytes); WorldSaveSerializer.Read(restored, reader);
        if (version == 82) Assert.That(restored.PlayerCharacterCreations, Is.Empty);
        else
        {
            var saved = restored.PlayerCharacterCreations.Single();
            Assert.That((saved.PlayerId, saved.RequestId, saved.NpcId, saved.Name, saved.Camp),
                Is.EqualTo((receipt.PlayerId, receipt.RequestId, receipt.NpcId, receipt.Name, receipt.Camp)));
            Assert.That(restored.PlayerControlledNpcs, Does.Contain(npc.Id.Value));
        }
        Assert.That(typeof(HexLive.Simulation.Debug.WorldSnapshot).GetProperties().Select(p => p.Name),
            Does.Not.Contain(nameof(world.PlayerCharacterCreations)));
    }
}
