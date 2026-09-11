using System.IO;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class AgentCommandSaveCompatibilityTests
{
    [TestCase(75)]
    [TestCase(76)]
    [TestCase(77)]
    public void RestTargetRoundTripsOnlyInTheVersionThatSupportsIt(int version)
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        var ledger = new AgentCommandLedger { HighestSequence = 1, ActiveSequence = 1,
            RestNeed = "Energy", RestTarget = .8f };
        ledger.Receipts.Add(new() { Sequence = 1, Id = "rest", Fingerprint = new string('a', 64),
            Outcome = "accepted", Reason = "Accepted" });
        world.AgentCommands[actor.Id.Value] = ledger;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, version);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using var reader = new BinaryReader(bytes);
        WorldSaveSerializer.Read(restored, reader);
        var saved = restored.AgentCommands[actor.Id.Value];
        Assert.That(saved.ActiveSequence, Is.EqualTo(1));
        Assert.That(saved.Receipts.Single().Id, Is.EqualTo("rest"));
        Assert.That(saved.RestNeed, Is.EqualTo(version >= 76 ? "Energy" : ""));
        Assert.That(saved.RestTarget, Is.EqualTo(version >= 76 ? .8f : 0f));
    }

    [Test]
    public void Version74KeepsAllProduceOriginsAndStartsWithoutCommandReceipts()
    {
        var world = TestWorld.CreateWorld();
        var actor = world.Entities.Npcs.Values.First();
        var origins = new[] { ProduceOrigin.Natural, ProduceOrigin.Gathered, ProduceOrigin.Unknown };
        for (var i = 0; i < origins.Length; i++)
            world.Entities.RegisterObject(new WorldObjectState { Id = new ObjectId(990001 + i),
                DefinitionId = ContentIds.Coconut, Tile = actor.Tile, ProduceOrigin = origins[i] });
        world.AgentCommands[actor.Id.Value] = new AgentCommandLedger { HighestSequence = 12 };
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(world, writer, 74);
        bytes.Position = 0;
        var restored = TestWorld.CreateWorld(world.Seed);
        using var reader = new BinaryReader(bytes);
        WorldSaveSerializer.Read(restored, reader);
        Assert.That(restored.AgentCommands, Is.Empty);
        for (var i = 0; i < origins.Length; i++)
            Assert.That(restored.Entities.Objects[new ObjectId(990001 + i)].ProduceOrigin, Is.EqualTo(origins[i]));
    }
}
