using System.IO;
using System.Linq;
using HexLive.Simulation.Persistence;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior;
public sealed class WorldLobbyLegacySaveTests
{
    [TestCase(66)] [TestCase(67)] [TestCase(68)] [TestCase(69)] [TestCase(70)] [TestCase(71)]
    public void OldSaveKeepsLegacyHairAndStartupRules(int version)
    {
        var world = TestWorld.CreateWorld();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.WriteAtVersion(world, writer, version);
        stream.Position = 0;
        var restored = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        Assert.That(restored.CreationConfig, Is.Null);
        Assert.That(restored.Entities.Npcs.Values.All(n => string.IsNullOrEmpty(n.HairColour)), Is.True);
        Assert.That(restored.Entities.Npcs.Count, Is.EqualTo(world.Entities.Npcs.Count));
    }
}
