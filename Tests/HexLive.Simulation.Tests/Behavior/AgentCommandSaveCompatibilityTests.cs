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
