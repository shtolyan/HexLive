using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[NonParallelizable]
public sealed class WorldCreationTests
{
    private Func<int, WorldBootstrapDefinition>? _previous;
    [SetUp] public void Setup() { _previous = PrototypeWorldDefinitionFactory.Override; PrototypeWorldDefinitionFactory.Override = Tiny; }
    [TearDown] public void TearDown() => PrototypeWorldDefinitionFactory.Override = _previous;
    private static WorldBootstrapDefinition Tiny(int seed)
    {
        var b = new WorldBootstrapDefinition(); b.Simulation.Seed = seed;
        var f = new FragmentBootstrap { Id = 1 }; b.Fragments.Add(f);
        for (var q = -3; q <= 7; q++) for (var r = -3; r <= 3; r++) f.Tiles.Add(new TileBootstrap { Q = q, R = r });
        b.FactionHomes.Add(new FactionHomeBootstrap { Faction = Faction.Colony, TileQ = 0, TileR = 0, StakeCampfireSite = false });
        b.FactionHomes.Add(new FactionHomeBootstrap { Faction = Faction.Colony2, TileQ = 5, TileR = 0, StakeCampfireSite = false });
        return b;
    }
    private static WorldCreationConfig Config()
    {
        var c = new WorldCreationConfig { Name = "Test world", CreatorPlayerId = Guid.NewGuid().ToString("N"), Seed = 7, PopulationLimit = 10 };
        c.Camps.Add(new CampCreationConfig { Faction = Faction.Colony });
        c.Camps.Add(new CampCreationConfig { Faction = Faction.Colony2 });
        c.Characters.Add(new CharacterCreationConfig { Id = 1, Name = "One", Controlled = true, HairColour = "prototype" });
        c.Characters.Add(new CharacterCreationConfig { Id = 2, Name = "Two" });
        return c;
    }
    [Test] public void MixedCampHasIndependentOwnershipAndDistinctFreeSpawnPoints()
    {
        var c = Config(); var definition = WorldCreation.Definition(c);
        Assert.That(WorldCreation.Validate(c, Tiny(7)), Is.Empty);
        var world = new WorldStateFactory().Create(definition);
        Assert.That(world.Entities.Npcs.Count, Is.EqualTo(2));
        var first = world.Entities.Npcs[new EntityId(1)]; var second = world.Entities.Npcs[new EntityId(2)];
        Assert.Multiple(() =>
        {
            Assert.That(PlayerAuthority.IsPlayerOwned(world, first), Is.True);
            Assert.That(PlayerAuthority.IsPlayerOwned(world, second), Is.False);
            Assert.That(first.CurrentJunction, Is.Not.Null);
            Assert.That(first.CurrentJunction, Is.Not.EqualTo(second.CurrentJunction));
            Assert.That(first.WornItems, Is.Empty, "An explicit empty outfit must not be replaced by random scenario clothing.");
            Assert.That(world.Occupancy.JunctionOwner[first.CurrentJunction!.Value], Is.EqualTo(first.Id));
        });
    }
    [Test] public void DisabledCampIsAbsentButEnabledEmptyCampRemains()
    {
        var c = Config(); var enabled = new WorldStateFactory().Create(WorldCreation.Definition(c));
        Assert.That(enabled.FactionHomes.ContainsKey(Faction.Colony2), Is.True);
        c.Camp(Faction.Colony2).Enabled = false;
        var disabled = new WorldStateFactory().Create(WorldCreation.Definition(c));
        Assert.That(disabled.FactionHomes.ContainsKey(Faction.Colony2), Is.False);
    }
    [Test] public void InvalidPopulationOwnershipAndStatsReturnFieldErrors()
    {
        var c = Config(); c.PopulationLimit = 1; c.Characters[0].Controlled = false;
        c.Characters[1].Attributes.Add(new CreationValue { Id = "Strength", Value = float.NaN });
        var errors = WorldCreation.Validate(c, Tiny(7));
        Assert.That(errors.Any(e => e.Code == "capacity"), Is.True);
        Assert.That(errors.Any(e => e.Code == "noControlledCharacter"), Is.True);
        Assert.That(errors.Any(e => e.Path.Contains("attributes") && e.Code == "range"), Is.True);
    }
    [Test] public void SaveRoundTripPreservesWorldRulesAppearanceAndSkills()
    {
        var c = Config(); c.Camps[0].ControlArrivals = true; c.Camps[0].ArrivalIntervalDays = 23;
        c.Characters[0].Skills.Add(new CreationValue { Id = "Athletics", Value = .65f });
        var original = new WorldStateFactory().Create(WorldCreation.Definition(c));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(original, writer);
        stream.Position = 0;
        var restored = new WorldStateFactory().Create(WorldCreation.Definition(c));
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        Assert.Multiple(() =>
        {
            Assert.That(restored.CreationConfig.Camps[0].ArrivalIntervalDays, Is.EqualTo(23));
            Assert.That(restored.Entities.Npcs[new EntityId(1)].HairColour, Is.EqualTo("prototype"));
            Assert.That(restored.Entities.Npcs[new EntityId(1)].Skills.Athletics, Is.EqualTo(.65f));
            Assert.That(restored.CreationConfig.Owns(new NPCState { Id = new EntityId(2001), Faction = Faction.Colony }), Is.True);
        });
    }
    [TestCase(false)]
    [TestCase(true)]
    public void ArrivalsFollowPerWorldIntervalsAndAcquireDurableOwnership(bool controlled)
    {
        var config = Config(); config.Camps[0].ArrivalIntervalDays = 7;
        config.Camps[0].ControlArrivals = controlled; config.Camps[0].PopulationLimit = 3;
        config.Camps[1].ArrivalIntervalDays = 0;
        var world = new WorldStateFactory().Create(WorldCreation.Definition(config));
        world.Tick = 6 * EnvironmentSystem.DayLengthTicks - EnvironmentSystem.DayLengthTicks / 4;
        var arrivals = new ColonyArrivalSystem(); arrivals.Run(world);
        var arriving = world.Entities.Npcs.Values.Single(n => n.Id.Value >= 2000);
        Assert.That(PlayerAuthority.IsPlayerOwned(world, arriving), Is.EqualTo(controlled));
        Assert.That(world.Entities.Npcs.Values.Any(n => n.Faction == Faction.Colony2), Is.False);
        arriving.Faction = Faction.Colony2;
        config.Camps[0].ControlArrivals = !controlled;
        Assert.That(PlayerAuthority.IsPlayerOwned(world, arriving), Is.EqualTo(controlled), "Ownership is not recomputed from current camp/rules.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Write(world, writer);
        stream.Position = 0;
        var restored = new WorldStateFactory().Create(WorldCreation.Definition(config));
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) WorldSaveSerializer.Read(restored, reader);
        Assert.That(PlayerAuthority.IsPlayerOwned(restored, restored.Entities.Npcs[arriving.Id]), Is.EqualTo(controlled));
    }
    [Test] public void BlockedCampFailsWithTheCharacterThatHasNoSpawnPoint()
    {
        PrototypeWorldDefinitionFactory.Override = seed =>
        {
            var definition = Tiny(seed);
            foreach (var tile in definition.Fragments.SelectMany(f => f.Tiles)) tile.Blocked = true;
            return definition;
        };
        var error = Assert.Throws<WorldCreationPlacementException>(() => new WorldStateFactory().Create(WorldCreation.Definition(Config())));
        Assert.That(error!.CharacterId, Is.EqualTo(1));
    }
    [Test] public void ExplicitMashaKeepsSuppliesWithoutReplacingHerLobbyOutfit()
    {
        var config = Config();
        config.Characters.Add(new CharacterCreationConfig { Id = 901, ProfileId = "masha", Name = "Masha", Voice = "masha" });
        config.Characters.Add(new CharacterCreationConfig { Id = 902, ProfileId = "nika", Name = "Nika", Voice = "marta" });
        Assert.That(WorldCreation.Validate(config, Tiny(7)), Is.Empty);
        var world = new WorldStateFactory().Create(WorldCreation.Definition(config));
        Assert.That(world.SpawnedCharacterPresets.Contains("nika"), Is.True);
        Assert.That(NikaCharacterProfile.EnsureSpawned(world), Is.False);
        Assert.That(world.Entities.Npcs[new EntityId(902)].WornItems, Is.Empty);
        var masha = world.Entities.Npcs[new EntityId(901)];
        Assert.That(masha.WornItems, Is.Empty);
        Assert.That(masha.Inventory.Items.Count, Is.EqualTo(2));
        Assert.That(world.SpawnedCharacterPresets.Contains("masha"), Is.True);
        Assert.That(MashaCompanionProfile.EnsureSpawned(world), Is.False);
        Assert.That(masha.WornItems, Is.Empty, "The explicit outfit remains authoritative even if a preset check runs later.");
    }

    [Test] public void HandshakeRoundTripPreservesRecipeAndMultipleAssignments()
    {
        var c = Config(); var handshake = new Handshake { WorldId = Guid.NewGuid().ToString("N"), CreationConfig = WorldCreationCodec.Encode(c) };
        handshake.AssignedNpcIds.AddRange(new[] { 1, 2 });
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) handshake.Write(writer);
        stream.Position = 0; using var reader = new BinaryReader(stream);
        var result = Handshake.Read(reader);
        Assert.That(result.WorldId, Is.EqualTo(handshake.WorldId));
        Assert.That(result.AssignedNpcIds, Is.EqualTo(new[] { 1, 2 }));
        Assert.That(WorldCreationCodec.Decode(result.CreationConfig).Characters.Count, Is.EqualTo(2));
    }
    [Test] public void ConfigParserRejectsExternalEntities()
    {
        Assert.Throws<InvalidDataException>(() => WorldCreationCodec.Decode("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///etc/passwd'>]><WorldCreationConfig>&a;</WorldCreationConfig>"));
    }
    [Test] public void LibraryPreservesLegacyFilesAndRestoresActiveWorld()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hexlive-lobby-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var save = Path.Combine(directory, "old.sav"); File.WriteAllText(save, "old-save");
            var library = new WorldLibrary(save, 7, GameMode.Feud, "catalog", 5);
            var oldId = library.ActiveId;
            Assert.That(File.ReadAllText(save), Is.EqualTo("old-save"));
            Assert.That(File.ReadAllText(library.SavePath(oldId)), Is.EqualTo("old-save"));
            var record = new ServerWorldRecord { Id = Guid.NewGuid().ToString("N"), Name = "Next", Seed = 8 };
            library.Prepare(record, Config(), "catalog2"); library.Activate(record.Id);
            var restarted = new WorldLibrary(save, 99, GameMode.Feud, "ignored", 99);
            Assert.That(restarted.ActiveId, Is.EqualTo(record.Id));
            restarted.Activate(oldId);
            Assert.That(File.ReadAllText(restarted.SavePath(oldId)), Is.EqualTo("old-save"));
            Assert.Throws<InvalidDataException>(() => restarted.DirectoryFor("../../outside"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test]
    public void SupervisorSwitchRestoresWorldAndDoesNotDuplicateCompletedRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hexlive-lobby-supervisor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var originalCatalog = HexLive.Simulation.Content.SimDataFile.ExportJson();
        try
        {
            var baseCatalog = Path.Combine(directory, "base.json"); File.WriteAllText(baseCatalog, originalCatalog);
            var registry = new HexLive.Server.Assets.AssetRegistryStore(Path.Combine(directory, "assets"));
            var catalog = new HexLive.Server.Assets.AssetGarmentCatalog(registry, baseCatalog);
            string customId;
            var creator = "11111111111111111111111111111111";
            using (var supervisor = new WorldSupervisor(7, GameMode.Feud, Path.Combine(directory, "world.sav"), catalog,
                false, false, new HexLive.Server.Llm.LlmHostOptions(), null, default, startPaused: true))
            {
                Assert.That(supervisor.Host.IsPaused, Is.True);
                Assert.That(supervisor.Host.Tick, Is.Zero, "Startup pause must precede the first tick");
                var legacy = supervisor.Library.ActiveId;
                var config = Config(); config.CreatorPlayerId = creator;
                var request = Guid.NewGuid().ToString("N");
                customId = supervisor.CreateLibraryWorld(7, GameMode.Feud, config, request);
                var session = supervisor.CaptureViewerSession();
                Assert.That(session.Assignments!.Reconcile(session.Host, creator, worldLifetime: session.Lifetime, worldGeneration: session.WorldGeneration), Is.EqualTo(new[] { 1 }));
                Assert.That(session.Assignments.Reconcile(session.Host, Guid.NewGuid().ToString("N"), worldLifetime: session.Lifetime, worldGeneration: session.WorldGeneration), Is.Empty);
                var count = supervisor.Library.List().Count;
                Assert.That(supervisor.CreateLibraryWorld(7, GameMode.Feud, config, request), Is.EqualTo(customId));
                Assert.That(supervisor.Library.List().Count, Is.EqualTo(count));
                supervisor.ActivateLibraryWorld(legacy);
                Assert.That(supervisor.CreateLibraryWorld(7, GameMode.Feud, config, request), Is.EqualTo(customId));
                Assert.That(supervisor.Host.WorldId, Is.EqualTo(legacy), "A completed request must not reactivate a world on replay.");
                supervisor.ActivateLibraryWorld(customId);
                Assert.That(supervisor.Host.Read(w => w.Entities.Npcs.Count), Is.EqualTo(2));
                var invalid = new ServerWorldRecord { Id = Guid.NewGuid().ToString("N"), Name = "Corrupt", Seed = 7 };
                supervisor.Library.Prepare(invalid, config, File.ReadAllText(supervisor.Library.CatalogPath(customId)));
                File.WriteAllText(supervisor.Library.SavePath(invalid.Id), "corrupt-save");
                Assert.Throws<InvalidDataException>(() => supervisor.ActivateLibraryWorld(invalid.Id));
                Assert.That(supervisor.Library.ActiveId, Is.EqualTo(customId));
                Assert.That(supervisor.Host.WorldId, Is.EqualTo(customId));
                Assert.That(File.ReadAllText(supervisor.Library.SavePath(invalid.Id)), Is.EqualTo("corrupt-save"));
                // Force the atomic pointer replacement to fail after candidate save, then retry the same request.
                var retry = Guid.NewGuid().ToString("N");
                var retryConfig = Config(); retryConfig.CreatorPlayerId = creator;
                var pointer = Path.Combine(supervisor.Library.Root, "active.json");
                File.Move(pointer, pointer + ".backup"); Directory.CreateDirectory(pointer);
                try
                {
                    Assert.Throws<IOException>(() => supervisor.CreateLibraryWorld(7, GameMode.Feud, retryConfig, retry));
                    Assert.That(supervisor.Host.WorldId, Is.EqualTo(customId));
                    Assert.That(supervisor.Library.ActiveId, Is.EqualTo(customId));
                }
                finally { Directory.Delete(pointer); File.Move(pointer + ".backup", pointer); }
                var prepared = supervisor.Library.List(true).Single(w => w.RequestId == retry);
                Assert.That(supervisor.Library.CreationSucceeded(prepared), Is.False);
                Assert.That(supervisor.CreateLibraryWorld(7, GameMode.Feud, retryConfig, retry), Is.EqualTo(prepared.Id));
                Assert.That(supervisor.Library.List(true).Count(w => w.RequestId == retry), Is.EqualTo(1));
                supervisor.ActivateLibraryWorld(customId);
                supervisor.Save();
            }
            using var restarted = new WorldSupervisor(99, GameMode.Feud, Path.Combine(directory, "world.sav"), catalog,
                false, false, new HexLive.Server.Llm.LlmHostOptions(), null, default);
            Assert.That(restarted.Host.WorldId, Is.EqualTo(customId));
            var restoredSession = restarted.CaptureViewerSession();
            Assert.That(restoredSession.Assignments!.Reconcile(restoredSession.Host, creator), Is.EqualTo(new[] { 1 }));
        }
        finally { HexLive.Simulation.Content.SimDataFile.ApplyJson(originalCatalog); Directory.Delete(directory, true); }
    }

    [TestCase(GameMode.Feud)]
    [TestCase(GameMode.BigIsland)]
    [TestCase(GameMode.HugeIsland)]
    [TestCase(GameMode.Maniac)]
    [TestCase(GameMode.Islands)]
    public void ScenarioDefaultsAreValidAndTopologyMatchesPreview(GameMode mode)
    {
        PrototypeWorldDefinitionFactory.Override = null;
        var definition = PrototypeWorldDefinitionFactory.Create(12345, mode);
        uint staticChecksum = 0;
        var world = new WorldStateFactory().Create(definition, topology => staticChecksum = TopologyChecksum.Compute(topology));
        var config = WorldCreation.Defaults(world); config.Name = "Scenario";
        config.CreatorPlayerId = "11111111111111111111111111111111";
        var errors = WorldCreation.Validate(config, definition);
        Assert.That(errors.Select(e => e.Path + ":" + e.Code), Is.Empty);
        var preview = new WorldStateFactory().CreateTopology(WorldCreation.Definition(config));
        Assert.That(TopologyChecksum.Compute(preview), Is.EqualTo(staticChecksum));
    }

}
