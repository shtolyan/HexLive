using System.Linq;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Persistence;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class OutsiderInitialRelationsTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void SavedNeutralPairMigratesOnlyWhenEntirelyUntouched(bool hadContact)
    {
        var world = TestWorld.CreateWorld();
        var resident = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var outsider = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Outsiders);
        var relation = resident.Social.GetOrCreate(outsider.Id);
        relation.Affinity = 0f;
        relation.LastInteractionTick = hadContact ? 123 : 0;
        outsider.Social.GetOrCreate(resident.Id).Affinity = 0.65f;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Write(world, writer);
        bytes.Position = 0;
        var loaded = TestWorld.CreateWorld();
        using (var reader = new BinaryReader(bytes, System.Text.Encoding.UTF8, true))
            WorldSaveSerializer.Read(loaded, reader);
        Assert.That(loaded.Entities.Npcs[resident.Id].Social.GetOrCreate(outsider.Id).Affinity,
            Is.EqualTo(hadContact ? 0f : -0.50f));
        Assert.That(loaded.Entities.Npcs[outsider.Id].Social.GetOrCreate(resident.Id).Affinity, Is.EqualTo(0.65f));
    }

    [Test]
    public void InitialOutsiderAndEveryCampStartAtMinusFifty()
    {
        var world = TestWorld.CreateWorld();
        var outsider = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Outsiders);
        foreach (var person in world.Entities.Npcs.Values.Where(n => n.Faction != Faction.Outsiders))
        {
            Assert.That(outsider.Social.GetOrCreate(person.Id).Affinity, Is.EqualTo(-0.50f));
            Assert.That(person.Social.GetOrCreate(outsider.Id).Affinity, Is.EqualTo(-0.50f));
        }
    }

    [Test]
    public void NewWaveSeedsHostilityWithoutResettingExistingFriendship()
    {
        var world = TestWorld.CreateWorld();
        var resident = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var oldOutsider = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Outsiders);
        resident.Social.GetOrCreate(oldOutsider.Id).Affinity = 0.75f;
        oldOutsider.Social.GetOrCreate(resident.Id).Affinity = 0.65f;
        world.Tick = Spec72.RaidWaveIntervalDays * EnvironmentSystem.DayLengthTicks;
        new RaidWaveSystem().Run(world);
        var arrival = world.Entities.Npcs[new EntityId(1001)];
        Assert.That(arrival.Social.GetOrCreate(resident.Id).Affinity, Is.EqualTo(-0.50f));
        Assert.That(resident.Social.GetOrCreate(arrival.Id).Affinity, Is.EqualTo(-0.50f));
        Assert.That(resident.Social.GetOrCreate(oldOutsider.Id).Affinity, Is.EqualTo(0.75f));
        Assert.That(oldOutsider.Social.GetOrCreate(resident.Id).Affinity, Is.EqualTo(0.65f));
        Assert.That(arrival.Social.GetOrCreate(oldOutsider.Id).Affinity, Is.Zero);
    }
}
}
