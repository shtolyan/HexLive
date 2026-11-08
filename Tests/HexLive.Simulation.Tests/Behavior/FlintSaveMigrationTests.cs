using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class FlintSaveMigrationTests
{
    private static void RemoveFlint(WorldState world)
    {
        foreach (var item in world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Flint).ToArray())
            WorldObjectMutations.DespawnObject(world, item.Id);
    }
    private static WorldState Load(WorldState source, int version, bool withoutNaturalSeeds = false)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.WriteAtVersion(source, writer, version);
        data.Position = 0;
        var restored = TestWorld.CreateWorld(source.Seed);
        if (withoutNaturalSeeds) RemoveFlint(restored);
        using var reader = new BinaryReader(data);
        WorldSaveSerializer.Read(restored, reader);
        return restored;
    }
    [Test]
    public void LegacyIslandGetsItsNormalFlintSeedsWithoutChangingExistingState()
    {
        var world = TestWorld.CreateWorld();
        var expected = world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Flint)
            .Select(o => (o.Tile, Junction: o.Junctions[0], o.ResourceAmount)).ToArray();
        Assert.That(expected, Has.Length.GreaterThan(0));
        RemoveFlint(world); world.Tick = 1234;
        var ids = world.Entities.Objects.Keys.ToArray(); var people = world.Entities.Npcs.Keys.ToArray();
        var restored = Load(world, 86);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Tick, Is.EqualTo(1234));
            Assert.That(restored.Entities.Npcs.Keys, Is.EquivalentTo(people));
            Assert.That(ids.All(restored.Entities.Objects.ContainsKey), Is.True);
            Assert.That(restored.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Flint)
                .Select(o => (o.Tile, Junction: o.Junctions[0], o.ResourceAmount)), Is.EquivalentTo(expected));
        });
    }
    [Test]
    public void OccupiedSeedIsSkippedWithoutReplacingTheExistingObject()
    {
        var world = TestWorld.CreateWorld();
        var seed = world.Entities.Objects.Values.First(o => o.DefinitionId == ContentIds.Flint);
        var anchor = seed.Junctions[0]; var tile = seed.Tile; var fragment = seed.Fragment;
        RemoveFlint(world);
        var stone = WorldObjectMutations.SpawnObject(world, "resource.stone", fragment, tile, anchor);
        stone.ResourceAmount = 3;
        var restored = Load(world, 86);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Entities.Objects[stone.Id].ResourceAmount, Is.EqualTo(3));
            Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Flint && o.Junctions.Contains(anchor)), Is.False);
            Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Flint), Is.True);
        });
    }
    [Test]
    public void FlintInInventoryPreventsDuplicatingTheIslandsSupply()
    {
        var world = TestWorld.CreateWorld(); RemoveFlint(world);
        world.Entities.Npcs.Values.First().Inventory.Items.Add(new ItemInstance(ContentIds.Flint));
        var restored = Load(world, 86);
        Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Flint), Is.False);
    }
    [Test]
    public void CurrentSaveDoesNotRespawnConsumedFlint()
    {
        var legacy = TestWorld.CreateWorld(); RemoveFlint(legacy);
        var migrated = Load(legacy, 86); RemoveFlint(migrated);
        var restored = Load(migrated, WorldSaveSerializer.BlobVersion);
        Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Flint), Is.False);
    }
    [Test]
    public void ExistingFlintIsNeverDuplicated()
    {
        var world = TestWorld.CreateWorld();
        var expected = world.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Flint).Select(o => o.Id).ToArray();
        var restored = Load(world, 86);
        Assert.That(restored.Entities.Objects.Values.Where(o => o.DefinitionId == ContentIds.Flint).Select(o => o.Id), Is.EquivalentTo(expected));
    }
    [Test]
    public void WorldWithoutNaturalSeedSitesDoesNotInventResourceLocations()
    {
        var world = TestWorld.CreateWorld(); RemoveFlint(world);
        var restored = Load(world, 86, withoutNaturalSeeds: true);
        Assert.That(restored.Entities.Objects.Values.Any(o => o.DefinitionId == ContentIds.Flint), Is.False);
    }
}
