using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[NonParallelizable]
public sealed class PlayerOnlyWorldPolicyTests
{
    private string _directory = "";
    [SetUp] public void Setup() => Directory.CreateDirectory(_directory = Path.Combine(Path.GetTempPath(), "hexlive-player-only-" + Guid.NewGuid().ToString("N")));
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);
    private WorldHost Host() => new(12345, GameMode.Feud, Path.Combine(_directory, "world.sav"), RepoFile("SimData/simdata.json"), false);
    private static string RepoFile(string path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HexLive.Simulation.Standalone.csproj"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, path);
    }
    private static CharacterCreationConfig Character() => new() { Name = "Игрок", Body = "Marta", Skin = "Rita", Hair = "none", Camp = Faction.Colony };

    [Test]
    public void MigrationPreservesPlayerBodyAndAllowsNextRealCreation()
    {
        using var host = Host();
        host.Read(w =>
        {
            Assert.That(PlayerCharacterCreation.TryCreate(w, new string('1', 32), new string('2', 32), Character(), out var receipt), Is.Empty);
            var body = w.Entities.Npcs[new EntityId(receipt.NpcId)];
            var generated = w.Entities.Npcs.Keys.First(id => id.Value != receipt.NpcId);
            body.Social.GetOrCreate(generated).Trust = .5f;
            var tick = w.Tick;
            var objects = w.Entities.Objects.Count;
            var removed = PlayerOnlyWorldPolicy.Apply(w, new[] { receipt.NpcId });
            Assert.That(removed, Does.Contain(generated.Value));
            Assert.That(w.Entities.Npcs.Values.Single(), Is.SameAs(body));
            Assert.That(body.Social.Relationships, Is.Empty);
            Assert.That(w.PlayerCharacterCreations.Single(), Is.SameAs(receipt));
            Assert.That(w.Entities.Objects.Count, Is.EqualTo(objects));
            Assert.That(w.Tick, Is.EqualTo(tick));
            Assert.That(w.CreationConfig.PlayersOnly, Is.True);
            Assert.That(w.CreationConfig.Characters, Is.Empty);
            Assert.That(w.CreationConfig.Camps.All(c => c.ArrivalIntervalDays == 0), Is.True);
            Assert.That(PlayerCharacterCreation.TryCreate(w, new string('3', 32), new string('4', 32), Character(), out _), Is.Empty);
            Assert.That(w.Entities.Npcs.Count, Is.EqualTo(2));
            return true;
        });
    }

    [Test]
    public void AssignedBodyWithoutCreationReceiptStopsBeforeMutation()
    {
        using var host = Host();
        host.Read(w =>
        {
            var count = w.Entities.Npcs.Count;
            var id = w.Entities.Npcs.Keys.First().Value;
            Assert.Throws<InvalidOperationException>(() => PlayerOnlyWorldPolicy.Apply(w, new[] { id }));
            Assert.That(w.Entities.Npcs.Count, Is.EqualTo(count));
            Assert.That(w.CreationConfig, Is.Null);
            return true;
        });
    }

    [Test]
    public void SaveRestoreKeepsPolicyAndBlocksEveryAutomaticHumanSpawner()
    {
        using (var host = Host()) { host.Read(w => PlayerOnlyWorldPolicy.Apply(w)); host.Save(); }
        using var restored = Host();
        restored.Read(w =>
        {
            Assert.That(w.CreationConfig.PlayersOnly, Is.True);
            Assert.That(w.Entities.Npcs, Is.Empty);
            // Even if a later operator changes arrival schedules, the world policy remains authoritative.
            foreach (var camp in w.CreationConfig.Camps) camp.ArrivalIntervalDays = 1;
            w.CreationConfig.SeaRaidIntervalDays = 1;
            w.CreationConfig.ShipwreckSurvivorEnabled = true;
            w.Tick = 10_000_000;
            new ColonyArrivalSystem().Run(w);
            new RaidWaveSystem().Run(w);
            new ShipwreckSurvivorSystem().Run(w);
            Assert.That(MashaCompanionProfile.EnsureSpawned(w), Is.False);
            Assert.That(NikaCharacterProfile.EnsureSpawned(w), Is.False);
            Assert.That(w.Entities.Npcs, Is.Empty);
            var result = AdminWorldCommands.Execute(w, new AdminCommand { Kind = "spawn_npc", DefinitionId = "colonist", Target = "Colony", TileQ = 0, TileR = 0 });
            Assert.That(result.Accepted, Is.False);
            return true;
        });
    }

    [Test]
    public void PlayersOnlyDefinitionHasNoAuthoredHumansButKeepsCampHomes()
    {
        using var host = Host();
        host.Read(w =>
        {
            var config = WorldCreation.Defaults(w);
            config.PlayersOnly = true;
            var definition = WorldCreation.Definition(config);
            Assert.That(definition.Npcs, Is.Empty);
            Assert.That(definition.FactionHomes.Count, Is.EqualTo(w.FactionHomes.Count));
            return true;
        });
    }
}
