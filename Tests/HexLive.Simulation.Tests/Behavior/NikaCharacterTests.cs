using System.Linq;
using System.IO;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class NikaCharacterTests
{
    [Test]
    public void AuthoredNikaAndMashaSpawnOnceAndKeepIndependentLooks()
    {
        var world = TestWorld.CreateWorld(14902);
        Assert.That(NikaCharacterProfile.EnsureSpawned(world), Is.True);
        Assert.That(NikaCharacterProfile.EnsureSpawned(world), Is.False);
        MashaCompanionProfile.EnsureSpawned(world);
        var nika = world.Entities.Npcs[new EntityId(902)];
        Assert.That(nika.ActorMesh, Is.EqualTo("Marta"));
        Assert.That(nika.Hairstyle, Is.EqualTo("JenniferHair"));
        Assert.That(nika.DisplayName, Is.EqualTo("Ника"));
        Assert.That(nika.WornItems.Select(x => x.DefinitionId), Is.EqualTo(NikaCharacterProfile.Outfit));
        Assert.That(world.Entities.Npcs[new EntityId(901)].ActorMesh, Is.EqualTo("Jana"));
        using var save = new MemoryStream();
        using (var writer = new BinaryWriter(save, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        save.Position = 0;
        var loaded = TestWorld.CreateWorld(14902);
        using (var reader = new BinaryReader(save, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(loaded, reader);
        Assert.That(NikaCharacterProfile.EnsureSpawned(loaded), Is.False);
        Assert.That(loaded.Entities.Npcs[new EntityId(902)].WornItems.Select(x => x.DefinitionId),
            Is.EqualTo(NikaCharacterProfile.Outfit));
        nika.WornItems.Clear();
        Assert.That(NikaCharacterProfile.EnsureSpawned(world), Is.False);
        Assert.That(nika.WornItems, Is.Empty, "Loading must not reset gameplay clothes.");
    }
}
