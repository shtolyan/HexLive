using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HexLive.Server.Assets;
using NUnit.Framework;

namespace HexLive.Server.Tests.Assets
{

[TestFixture]
public sealed class AssetRegistryStoreTests
{
    private string _root = null!;
    private AssetRegistryStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "hexlive-atomic-assets-" + Guid.NewGuid().ToString("N"));
        _store = new AssetRegistryStore(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task UpdatingSkirtChangesOnlySkirtRecordAndDelta()
    {
        await _store.PublishAsync(Candidate("wear", "skirt.anarchy", "skirt-v1", hasIcon: true));
        await _store.PublishAsync(Candidate("hair", "bob.short", "hair-v1", hasIcon: true));

        var hairPath = Path.Combine(_root, "records", "hair", "bob.short.json");
        var hairBytes = File.ReadAllBytes(hairPath);
        var hairMtime = File.GetLastWriteTimeUtc(hairPath);
        var cursor = _store.RegistryRevision;

        var update = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "skirt-v2-with-new-icon", hasIcon: true));

        Assert.That(update.Record.Revision, Is.EqualTo(2));
        Assert.That(_store.RegistryRevision, Is.EqualTo(cursor + 1));
        Assert.That(File.ReadAllBytes(hairPath), Is.EqualTo(hairBytes));
        Assert.That(File.GetLastWriteTimeUtc(hairPath), Is.EqualTo(hairMtime));

        var delta = _store.GetIndex("StandaloneOSX", "unity6000-content1", cursor);
        Assert.That(delta.IsDelta, Is.True);
        Assert.That(delta.Objects.Select(value => value.Type + "/" + value.Id),
            Is.EqualTo(new[] { "wear/skirt.anarchy" }));
        Assert.That(delta.Objects[0].Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task ActiveWearRecipeTravelsInSimdataAndRetiringRemovesIt()
    {
        var basePath = Path.Combine(_root, "base.json");
        File.WriteAllText(basePath, "{\"garments\":[],\"recipes\":[]}");
        var published = await _store.PublishAsync(Candidate("wear", "clothing.top_primal", "top", hasIcon: true));
        var catalog = new AssetGarmentCatalog(_store, basePath);
        var metadata = new GarmentSimulationMetadata {
            DisplayName = "Hide Wrap", PrototypeId = "clothing.top_primal", Layer = "Underwear", Sex = "Female",
            Covers = new List<string> { "Torso" }, DressDurationTicks = 8,
            Craft = new GarmentCraftMetadata { Inputs = new List<GarmentCraftInput> {
                new() { Id = "resource.hide", Count = 1 }, new() { Id = "resource.rope", Count = 1 } } }
        };
        var record = _store.ReadCurrentRecord("wear", "clothing.top_primal")!;
        await _store.UpdateRecordAsync(record.Type, record.Id, record.Revision, "active", catalog.WithSimulationMetadata(record, metadata));
        var recipe = JsonNode.Parse(catalog.Materialize().Json)!["recipes"]!.AsArray().Single()!;
        Assert.That(recipe["output"]!.GetValue<string>(), Is.EqualTo(record.Id));
        Assert.That(recipe["station"]!.GetValue<string>(), Is.EqualTo("Workbench"));
        Assert.That(recipe["inputs"]!.AsArray().Count, Is.EqualTo(2));
        record = _store.ReadCurrentRecord(record.Type, record.Id)!;
        await _store.UpdateRecordAsync(record.Type, record.Id, record.Revision, "retired", record.Metadata);
        Assert.That(JsonNode.Parse(catalog.Materialize().Json)!["recipes"]!.AsArray(), Is.Empty);
    }

    [Test]
    public void MalformedWearBillsAreRejectedBeforeCatalogActivation()
    {
        var craft = new GarmentCraftMetadata { Inputs = new List<GarmentCraftInput> {
            new() { Id = "resource.hide", Count = 1 }, new() { Id = "resource.hide", Count = 2 } } };
        Assert.Throws<InvalidDataException>(() => craft.Validate());
        craft.Inputs.RemoveAt(1); craft.Inputs[0].Count = 0;
        Assert.Throws<InvalidDataException>(() => craft.Validate());
        craft.Inputs[0].Count = 1; craft.Station = "unknown";
        Assert.Throws<InvalidDataException>(() => craft.Validate());
    }

    [Test]
    public async Task PreparedCatalogMaterializesAllWearRecipesAlongsideBaseNonWearRecipes()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "SimData", "simdata.json"))) repo = repo.Parent;
        Assert.That(repo, Is.Not.Null);
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo!.FullName,
            "Assets", "HexLiveContent", "People", "catalog.json")));
        var rows = source.RootElement.GetProperty("records").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == "wear").ToArray();
        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString()!;
            var candidate = Candidate("wear", id, id, hasIcon: true);
            candidate.Metadata["simulation"] = row.GetProperty("metadata").GetProperty("simulation").Clone();
            await _store.PublishAsync(candidate);
        }
        var basePath = Path.Combine(_root, "base.json");
        File.WriteAllText(basePath, "{\"garments\":[],\"recipes\":[{\"output\":\"resource.rope\"}]}");
        var catalog = new AssetGarmentCatalog(_store, basePath);
        var snapshot = catalog.Materialize(); var json = JsonNode.Parse(snapshot.Json)!;
        Assert.That(rows.Length, Is.EqualTo(82));
        Assert.That(snapshot.SpawnableWear, Is.EqualTo(82));
        Assert.That(snapshot.UnconfiguredWear, Is.Zero);
        Assert.That(json["recipes"]!.AsArray().Count, Is.EqualTo(83));
        Assert.That(json["recipes"]!.AsArray().OfType<JsonObject>().Select(r => r["output"]!.GetValue<string>()),
            Is.EquivalentTo(rows.Select(r => r.GetProperty("id").GetString()).Append("resource.rope")));
        // #448: assert the effective registry catalogue used by the server,
        // including both fitted sexes and colour variants, not only defaults.
        var garments = json["garments"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var garment in garments)
        {
            var sourceRow = rows.Single(r => r.GetProperty("id").GetString() == garment["id"]!.GetValue<string>());
            Assert.That(garment["alternateFitId"]!.GetValue<string>(), Is.EqualTo(
                sourceRow.GetProperty("metadata").GetProperty("simulation").GetProperty("alternateFitId").GetString()),
                "The effective server catalog must preserve the explicitly authored body fit");
        }
        var collars = garments.Where(g => g["id"]!.GetValue<string>().StartsWith("clothing.collar_fur", StringComparison.Ordinal)).ToArray();
        Assert.That(collars.Length, Is.EqualTo(14));
        Assert.That(collars.All(g => g["capacity"]!.GetValue<int>() == 1), Is.True);
        var dresses = garments.Where(g => g["id"]!.GetValue<string>().StartsWith("clothing.dress_primal", StringComparison.Ordinal)).ToArray();
        Assert.That(dresses.All(g => g["capacity"]!.GetValue<int>() == 2), Is.True);
        Assert.That(dresses.Length, Is.EqualTo(14));
        Assert.That(dresses.All(g => g["armor"]!.GetValue<float>() == 0.10f), Is.True,
            "Every fur dress colour and both body fits protect their covered parts by ten percent.");
        var headbands = garments.Where(g => g["id"]!.GetValue<string>().StartsWith("clothing.headband_primal", StringComparison.Ordinal)).ToArray();
        Assert.That(headbands.Length, Is.EqualTo(8));
        Assert.That(headbands.All(g => g["armor"]!.GetValue<float>() == 0.05f), Is.True);
        var skirts = garments.Where(g => g["id"]!.GetValue<string>().StartsWith("clothing.skirt_primal", StringComparison.Ordinal)).ToArray();
        Assert.That(skirts.All(g => g["capacity"]!.GetValue<int>() == 2), Is.True);
        foreach (var recipe in json["recipes"]!.AsArray().OfType<JsonObject>()
                     .Where(r => r["output"]!.GetValue<string>() != "resource.rope"))
        {
            var cloth = recipe["inputs"]!.AsArray().OfType<JsonObject>()
                .SingleOrDefault(input => input["id"]!.GetValue<string>() == "resource.cloth");
            Assert.That(cloth, Is.Not.Null,
                $"{recipe["output"]} must consume the shared cloth material.");
            var dressRecipe = recipe["output"]!.GetValue<string>().StartsWith("clothing.dress_primal", StringComparison.Ordinal);
            Assert.That(cloth!["count"]!.GetValue<int>(), Is.EqualTo(dressRecipe ? 2 : 1));
            if (dressRecipe)
            {
                var inputs = recipe["inputs"]!.AsArray().OfType<JsonObject>()
                    .ToDictionary(i => i["id"]!.GetValue<string>(), i => i["count"]!.GetValue<int>());
                Assert.That(inputs["resource.hide"], Is.EqualTo(6));
                Assert.That(inputs["resource.rope"], Is.EqualTo(2));
            }
        }
    }

    [Test]
    public async Task IconIsAnEntryOfOwningPayloadAndThereIsNoIconObjectType()
    {
        var result = await _store.PublishAsync(
            Candidate("prosthetic", "leg.wood.l", "prosthetic-and-icon", hasIcon: true));
        var resolved = _store.Resolve(
            "prosthetic", "leg.wood.l", "StandaloneOSX", "unity6000-content1");

        Assert.That(ContentIdentity.IsType("icon"), Is.False);
        Assert.That(ContentIdentity.IsType("ui"), Is.False);
        Assert.That(result.Record.Variants.Single().IconAsset, Is.EqualTo("icon"));
        Assert.That(resolved!.Variant!.IconAsset, Is.EqualTo("icon"));
        Assert.That(resolved.Variant.Sha256, Is.EqualTo(result.Record.Variants.Single().Sha256));
    }

    [Test]
    public async Task VoiceAndVisemeSidecarPublishAsOneAtomicRevision()
    {
        var candidate = Candidate("audio", "voice_jana_hungry_0", "wav-v1", hasIcon: false);
        candidate.Variants[0].PayloadType = "file";
        candidate.Variants[0].Attachments.Add(Attachment("vis", "timeline-v1"));

        var result = await _store.PublishAsync(candidate);
        var resolved = _store.Resolve(
            "audio", "voice_jana_hungry_0", "StandaloneOSX", "unity6000-content1");
        var attachment = resolved!.Variant!.Attachments.Single();

        Assert.That(result.Record.Revision, Is.EqualTo(1));
        Assert.That(attachment.Name, Is.EqualTo("vis"));
        Assert.That(_store.TryGetVerifiedBlob(attachment.Sha256, out _, out var size), Is.True);
        Assert.That(size, Is.EqualTo(attachment.Size));
        Assert.That(Directory.Exists(Path.Combine(_root, "history")), Is.False);
    }

    [Test]
    public void CorruptVoiceSidecarCannotAdvanceRecord()
    {
        var candidate = Candidate("audio", "voice_jana_hungry_0", "wav-v1", hasIcon: false);
        candidate.Variants[0].PayloadType = "file";
        var attachment = Attachment("vis", "timeline-v1");
        candidate.Variants[0].Attachments.Add(attachment);
        File.AppendAllText(attachment.StagedPath, "corruption");

        Assert.That(async () => await _store.PublishAsync(candidate),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(_store.RegistryRevision, Is.Zero);
        Assert.That(_store.Resolve(
            "audio", "voice_jana_hungry_0", "StandaloneOSX", "unity6000-content1"), Is.Null);
    }

    [Test]
    public async Task RepeatingIdenticalCandidateIsNoOp()
    {
        var candidate = Candidate("wear", "skirt.anarchy", "same-bytes", hasIcon: true);
        var first = await _store.PublishAsync(candidate);
        var second = await _store.PublishAsync(candidate);

        Assert.That(first.Changed, Is.True);
        Assert.That(second.Changed, Is.False);
        Assert.That(second.Record.Revision, Is.EqualTo(1));
        Assert.That(second.RegistryRevision, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.Combine(_root, "changes")), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task LegacyStableIdWithInternalSpacesRemainsAddressable()
    {
        var published = await _store.PublishAsync(
            Candidate("wear", "FAO Harness Male", "legacy-authored-id", hasIcon: true));

        Assert.That(published.Record.Id, Is.EqualTo("FAO Harness Male"));
        Assert.That(_store.Resolve(
            "wear", "FAO Harness Male", "StandaloneOSX", "unity6000-content1")?.Revision,
            Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(
            _root, "records", "wear", "FAO Harness Male.json")), Is.True);
    }

    [Test]
    public async Task WindowsBootstrapRetainsPublishedMacVariant()
    {
        var mac = Candidate("wear", "skirt.anarchy", "mac-v1", hasIcon: true);
        await _store.PublishAsync(mac);
        var windows = Candidate("wear", "skirt.anarchy", "windows-v1", hasIcon: true);
        windows.Variants[0].Platform = "StandaloneWindows64";

        var published = await _store.PublishAsync(windows, retainCurrentVariants: true);

        Assert.That(published.Record.Revision, Is.EqualTo(2));
        Assert.That(published.Record.Variants.Select(value => value.Platform),
            Is.EqualTo(new[] { "StandaloneOSX", "StandaloneWindows64" }));
        Assert.That(_store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1"), Is.Not.Null);
        Assert.That(_store.Resolve(
            "wear", "skirt.anarchy", "StandaloneWindows64", "unity6000-content1"), Is.Not.Null);
    }

    [Test]
    public void CorruptCandidateCannotCreateCurrentRecord()
    {
        var candidate = Candidate("wear", "skirt.anarchy", "good", hasIcon: true);
        File.AppendAllText(candidate.Variants[0].StagedPath, "corruption");

        Assert.That(async () => await _store.PublishAsync(candidate),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(_store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1"), Is.Null);
        Assert.That(_store.RegistryRevision, Is.Zero);
    }

    [Test]
    public async Task VisualObjectWithoutAuthoredIconUsesAtomicEmojiFallback()
    {
        var candidate = Candidate("wear", "skirt.anarchy", "bytes", hasIcon: false);
        candidate.Metadata["iconFallback"] = JsonSerializer.SerializeToElement("emoji");

        var published = await _store.PublishAsync(candidate);
        var resolved = _store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1");

        Assert.That(published.Record.Revision, Is.EqualTo(1));
        Assert.That(resolved!.Variant!.IconAsset, Is.Null);
        Assert.That(resolved.Metadata["iconFallback"].GetString(), Is.EqualTo("emoji"));
        Assert.That(_store.RegistryRevision, Is.EqualTo(1));
    }

    [Test]
    public async Task RepublishFromSourceUsesNextCursorWithoutKeepingOldPayloads()
    {
        var v1 = Candidate("wear", "skirt.anarchy", "v1", hasIcon: true);
        var first = await _store.PublishAsync(v1);
        await _store.PublishAsync(Candidate("wear", "skirt.anarchy", "v2", hasIcon: true));
        var rollback = await _store.PublishAsync(v1);

        Assert.That(rollback.Record.Revision, Is.EqualTo(3));
        Assert.That(rollback.Record.Variants.Single().Sha256,
            Is.EqualTo(first.Record.Variants.Single().Sha256));
        Assert.That(Directory.Exists(Path.Combine(_root, "history")), Is.False);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "blobs")), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task CurrentRecordKeepsOneVariantForEachSupportedProfile()
    {
        var oldProfile = Candidate("wear", "skirt.anarchy", "old-profile", hasIcon: true);
        await _store.PublishAsync(oldProfile);
        var newProfile = Candidate("wear", "skirt.anarchy", "new-profile", hasIcon: true);
        newProfile.Variants[0].RuntimeProfile = "unity6000-content2";
        await _store.PublishAsync(newProfile);

        var oldPlayer = _store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1");
        var newPlayer = _store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content2");

        Assert.That(oldPlayer!.Revision, Is.EqualTo(2));
        Assert.That(newPlayer!.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task ConcurrentPublishesOfOneIdAreSerialized()
    {
        var a = Candidate("wear", "skirt.anarchy", "parallel-a", hasIcon: true);
        var b = Candidate("wear", "skirt.anarchy", "parallel-b", hasIcon: true);

        var results = await Task.WhenAll(_store.PublishAsync(a), _store.PublishAsync(b));

        Assert.That(results.Select(value => value.Record.Revision).OrderBy(value => value),
            Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(_store.RegistryRevision, Is.EqualTo(2));
        Assert.That(Directory.Exists(Path.Combine(_root, "history")), Is.False);
        Assert.That(Directory.GetFiles(Path.Combine(_root, "blobs")), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RetiredObjectAppearsInDeltaWithoutPayload()
    {
        await _store.PublishAsync(Candidate("wear", "skirt.anarchy", "v1", hasIcon: true));
        var cursor = _store.RegistryRevision;
        var retired = new ContentPublishCandidate
        {
            Type = "wear",
            Id = "skirt.anarchy",
            State = "retired",
            Metadata = Metadata("Anarchy Skirt"),
        };

        await _store.PublishAsync(retired);
        var delta = _store.GetIndex("StandaloneOSX", "unity6000-content1", cursor);

        Assert.That(delta.Objects, Has.Count.EqualTo(1));
        Assert.That(delta.Objects[0].State, Is.EqualTo("retired"));
        Assert.That(delta.Objects[0].Variant, Is.Null);
    }

    [Test]
    public async Task AdminMetadataUpdateRetainsPayloadAndRejectsLostUpdate()
    {
        var published = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "skirt-v1", hasIcon: true));
        var variant = published.Record.Variants.Single();
        var metadata = published.Record.Metadata.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        metadata["simulation"] = JsonSerializer.SerializeToElement(new { warmth = 0.25f });

        var updated = await _store.UpdateRecordAsync(
            "wear", "skirt.anarchy", 1, "active", metadata);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Record.Revision, Is.EqualTo(2));
            Assert.That(updated.Record.Variants.Single().Sha256, Is.EqualTo(variant.Sha256));
            Assert.That(updated.Record.Metadata["simulation"].GetProperty("warmth").GetSingle(),
                Is.EqualTo(0.25f));
        });
        Assert.That(async () => await _store.UpdateRecordAsync(
                "wear", "skirt.anarchy", 1, "active", metadata),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task VisualRepublishPreservesServerOwnedSimulationMetadata()
    {
        var first = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "skirt-v1", hasIcon: true));
        var metadata = first.Record.Metadata.ToDictionary(
            pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        metadata["simulation"] = JsonSerializer.SerializeToElement(new { warmth = 0.4f });
        await _store.UpdateRecordAsync("wear", "skirt.anarchy", 1, "active", metadata);

        var rebuilt = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "skirt-v2", hasIcon: true));

        Assert.That(rebuilt.Record.Revision, Is.EqualTo(3));
        Assert.That(rebuilt.Record.Metadata["simulation"].GetProperty("warmth").GetSingle(),
            Is.EqualTo(0.4f));
    }

    [Test]
    public async Task RetiredObjectIsDiscoverableOnlyByExplicitWorldResolve()
    {
        var active = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "skirt-v1", hasIcon: true));
        await _store.UpdateRecordAsync(
            "wear", "skirt.anarchy", active.Record.Revision, "retired", active.Record.Metadata);

        var index = _store.GetIndex("StandaloneOSX", "unity6000-content1");
        var resolved = _store.ResolveMany(new AssetResolveRequest
        {
            Platform = "StandaloneOSX",
            RuntimeProfile = "unity6000-content1",
            Objects = new List<AssetObjectKey>
            {
                new() { Type = "wear", Id = "skirt.anarchy" },
            },
        });

        Assert.That(index.Objects.Single().State, Is.EqualTo("retired"));
        Assert.That(index.Objects.Single().Variant, Is.Null);
        Assert.That(resolved.Objects.Single().State, Is.EqualTo("legacy"));
        Assert.That(resolved.Objects.Single().Variant!.Sha256,
            Is.EqualTo(active.Record.Variants.Single().Sha256));
    }

    [Test]
    public async Task EffectiveCatalogUsesAtomicWearAndKeepsRetiredDefinitionsForSaves()
    {
        var basePath = Path.Combine(_root, "base-simdata.json");
        File.WriteAllText(basePath, """
        {
          "version": 5,
          "garments": [
            {
              "id": "skirt.old",
              "displayName": "Old Skirt",
              "layer": "Wear",
              "sex": "Female",
              "warmth": 0.1,
              "armor": 0.0,
              "thermalDelta": 0.0,
              "dressDurationTicks": 8,
              "capacity": 1,
              "covers": ["Pelvis"]
            }
          ]
        }
        """);
        var old = await _store.PublishAsync(
            Candidate("wear", "skirt.old", "old-bytes", hasIcon: true));
        var fresh = await _store.PublishAsync(
            Candidate("wear", "skirt.new", "new-bytes", hasIcon: true));
        var catalog = new AssetGarmentCatalog(_store, basePath);

        var before = catalog.Materialize();
        Assert.Multiple(() =>
        {
            Assert.That(before.SpawnableWear, Is.EqualTo(1));
            Assert.That(before.UnconfiguredWear, Is.EqualTo(1));
        });

        var newRecord = _store.ReadCurrentRecord("wear", "skirt.new")!;
        var simulation = new GarmentSimulationMetadata
        {
            DisplayName = "New Skirt",
            PrototypeId = "skirt.new",
            Layer = "Wear",
            Sex = "Female",
            Warmth = 0.35f,
            Armor = 0.05f,
            ThermalDelta = 0.02f,
            DressDurationTicks = 9,
            Capacity = 2,
            Covers = new List<string> { "Pelvis", "LegL", "LegR" },
        };
        await _store.UpdateRecordAsync("wear", "skirt.new", fresh.Record.Revision, "active",
            catalog.WithSimulationMetadata(newRecord, simulation));
        await _store.UpdateRecordAsync(
            "wear", "skirt.old", old.Record.Revision, "retired", old.Record.Metadata);

        var after = catalog.Materialize();
        var garments = JsonNode.Parse(after.Json)!["garments"]!.AsArray();
        var oldJson = garments.Single(value => value!["id"]!.GetValue<string>() == "skirt.old")!;
        var newJson = garments.Single(value => value!["id"]!.GetValue<string>() == "skirt.new")!;

        Assert.Multiple(() =>
        {
            Assert.That(after.SpawnableWear, Is.EqualTo(1));
            Assert.That(after.RetiredWear, Is.EqualTo(1));
            Assert.That(after.UnconfiguredWear, Is.Zero);
            Assert.That(oldJson["retired"]!.GetValue<bool>(), Is.True);
            Assert.That(newJson["warmth"]!.GetValue<float>(), Is.EqualTo(0.35f));
            Assert.That(File.Exists(after.Path), Is.True);
        });
    }

    [Test]
    public async Task StartupFinishesInterruptedOneObjectTransaction()
    {
        var first = await _store.PublishAsync(
            Candidate("wear", "skirt.anarchy", "v1", hasIcon: true));
        var record = new ContentObjectRecord
        {
            Type = first.Record.Type,
            Id = first.Record.Id,
            Revision = 2,
            State = "active",
            Metadata = Metadata("Recovered skirt"),
            Variants = first.Record.Variants,
            PublishedAtUtc = DateTimeOffset.UtcNow,
        };
        var change = new RegistryChange
        {
            RegistryRevision = 2,
            Type = record.Type,
            Id = record.Id,
            ObjectRevision = record.Revision,
            State = record.State,
            PublishedAtUtc = record.PublishedAtUtc,
        };
        var transaction = Path.Combine(_store.StagingPath, ".transaction-crash.json");
        File.WriteAllText(transaction, JsonSerializer.Serialize(new
        {
            record,
            change,
            registryRevision = 2,
        }));

        _store = new AssetRegistryStore(_root);

        Assert.That(File.Exists(transaction), Is.False);
        Assert.That(_store.RegistryRevision, Is.EqualTo(2));
        Assert.That(_store.Resolve(
            "wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1")!.Revision,
            Is.EqualTo(2));
        Assert.That(_store.GetIndex(
            "StandaloneOSX", "unity6000-content1", 1).Objects.Single().Revision,
            Is.EqualTo(2));
    }

    [Test]
    public async Task WindowsIndexNamesTheObjectsItCannotLoad()
    {
        // The door is the §152 shape of bug #242: published, live, and absent
        // from a Windows build. An index that just omits it is indistinguishable
        // from a registry that never had it.
        await _store.PublishAsync(BothPlatforms("wear", "skirt.anarchy", "skirt-v1"));
        await _store.PublishAsync(Candidate("building", "door.wood", "door-v1", hasIcon: true));

        var windows = _store.GetIndex("StandaloneWindows64", "unity6000-content1");
        var mac = _store.GetIndex("StandaloneOSX", "unity6000-content1");

        Assert.That(windows.Objects.Select(value => value.Type + "/" + value.Id),
            Is.EqualTo(new[] { "wear/skirt.anarchy" }));
        Assert.That(windows.PlatformMissing.Select(value => value.Type + "/" + value.Id),
            Is.EqualTo(new[] { "building/door.wood" }));
        Assert.That(mac.Objects, Has.Count.EqualTo(2));
        Assert.That(mac.PlatformMissing, Is.Empty);
    }

    [Test]
    public async Task RetiredObjectIsNotReportedAsAPlatformGap()
    {
        await _store.PublishAsync(Candidate("building", "door.wood", "door-v1", hasIcon: true));
        await _store.PublishAsync(new ContentPublishCandidate
        {
            Type = "building",
            Id = "door.wood",
            State = "retired",
            Metadata = Metadata("door.wood"),
        });

        var windows = _store.GetIndex("StandaloneWindows64", "unity6000-content1");

        Assert.That(windows.PlatformMissing, Is.Empty);
        Assert.That(windows.Objects.Single().State, Is.EqualTo("retired"));
    }

    [Test]
    public async Task CoverageAuditListsEveryPublishedPlatformAndItsGaps()
    {
        await _store.PublishAsync(BothPlatforms("wear", "skirt.anarchy", "skirt-v1"));
        await _store.PublishAsync(Candidate("building", "door.wood", "door-v1", hasIcon: true));
        await _store.PublishAsync(new ContentPublishCandidate
        {
            Type = "hair",
            Id = "bob.short",
            State = "retired",
            Metadata = Metadata("bob.short"),
        });

        var report = _store.AuditCoverage();
        var mac = report.Platforms.Single(value => value.Platform == "StandaloneOSX");
        var windows = report.Platforms.Single(value => value.Platform == "StandaloneWindows64");

        Assert.That(report.ActiveObjects, Is.EqualTo(2));
        Assert.That(report.RetiredObjects, Is.EqualTo(1));
        Assert.That(report.Platforms, Has.Count.EqualTo(2));
        Assert.That(mac.Covered, Is.EqualTo(2));
        Assert.That(mac.Missing, Is.Empty);
        Assert.That(windows.Covered, Is.EqualTo(1));
        Assert.That(windows.Missing.Select(value => value.Type + "/" + value.Id),
            Is.EqualTo(new[] { "building/door.wood" }));
    }

    [Test]
    public async Task CoverageAuditOmitsAPlatformNothingWasEverBuiltFor()
    {
        await _store.PublishAsync(Candidate("wear", "skirt.anarchy", "skirt-v1", hasIcon: true));

        var report = _store.AuditCoverage();

        Assert.That(report.Platforms.Select(value => value.Platform),
            Is.EqualTo(new[] { "StandaloneOSX" }));
        Assert.That(_store.GetIndex("StandaloneWindows64", "unity6000-content1")
            .PlatformMissing.Select(value => value.Type + "/" + value.Id),
            Is.EqualTo(new[] { "wear/skirt.anarchy" }));
    }

    [Test]
    public async Task CoverageCountsRetainedCurrentProfilesWithoutHistoricalFallback()
    {
        // Each supported profile now lives in the single current record.
        var first = BothPlatforms("wear", "skirt.anarchy", "skirt-v1");
        await _store.PublishAsync(first);
        var second = BothPlatforms("wear", "skirt.anarchy", "skirt-v2");
        foreach (var variant in second.Variants)
        {
            variant.RuntimeProfile = "unity6000-content2";
        }

        await _store.PublishAsync(second);

        var oldProfile = _store.GetIndex("StandaloneWindows64", "unity6000-content1");
        var report = _store.AuditCoverage();

        Assert.That(oldProfile.PlatformMissing, Is.Empty);
        Assert.That(oldProfile.Objects.Single().Revision, Is.EqualTo(2));

        Assert.That(report.Platforms.Select(value => value.Platform + "/" + value.RuntimeProfile),
            Is.EqualTo(new[]
            {
                "StandaloneOSX/unity6000-content1",
                "StandaloneOSX/unity6000-content2",
                "StandaloneWindows64/unity6000-content1",
                "StandaloneWindows64/unity6000-content2",
            }));
        Assert.That(report.Platforms
            .Single(value => value.Platform == "StandaloneWindows64" && value.RuntimeProfile == "unity6000-content1").Covered,
            Is.EqualTo(1));
    }

    [Test]
    public async Task SupersededPayloadIsDeletedOnlyAfterItsLastCurrentOwnerChanges()
    {
        var first = await _store.PublishAsync(Candidate("object", "shared.a", "shared", true));
        await _store.PublishAsync(Candidate("object", "shared.b", "shared", true));
        var sha = first.Record.Variants[0].Sha256;
        await _store.PublishAsync(Candidate("object", "shared.a", "new-a", true));
        Assert.That(_store.TryGetVerifiedBlob(sha, out _, out _), Is.True);
        await _store.PublishAsync(Candidate("object", "shared.b", "new-b", true));
        Assert.That(_store.TryGetVerifiedBlob(sha, out _, out _), Is.False);
        Assert.That(_store.Resolve("object", "shared.a", "StandaloneOSX", "unity6000-content1"), Is.Not.Null);
    }

    [Test]
    public async Task ReplacingVoiceRemovesOldAudioAndSidecarTogether()
    {
        var old = Candidate("audio", "voice.single", "audio-old", false);
        old.Variants[0].Attachments.Add(Attachment("vis", "vis-old"));
        await _store.PublishAsync(old);
        var replacement = Candidate("audio", "voice.single", "audio-new", false);
        replacement.Variants[0].Attachments.Add(Attachment("vis", "vis-new"));
        await _store.PublishAsync(replacement);
        Assert.That(_store.TryGetVerifiedBlob(old.Variants[0].Sha256, out _, out _), Is.False);
        Assert.That(_store.TryGetVerifiedBlob(old.Variants[0].Attachments[0].Sha256, out _, out _), Is.False);
        Assert.That(_store.TryGetVerifiedBlob(replacement.Variants[0].Attachments[0].Sha256, out _, out _), Is.True);
    }

    [Test]
    public async Task LegacyCompactionPreservesLatestNativeMetadataAndRetiredSavePayload()
    {
        var (native, web) = await LegacyHistoryFixture();
        var before = File.ReadAllBytes(Path.Combine(_root, "records", "wear", "skirt.anarchy.json"));
        var preview = _store.CompactStorage();
        Assert.That(preview.Applied, Is.False);
        Assert.That(preview.PromotedVariants, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(Path.Combine(_root, "records", "wear", "skirt.anarchy.json")), Is.EqualTo(before));
        Assert.Throws<InvalidDataException>(() => _store.RequireCompactedStorage());

        var applied = _store.CompactStorage(apply: true);
        var mac = _store.Resolve("wear", "skirt.anarchy", "StandaloneOSX", "unity6000-content1")!;
        var browser = _store.Resolve("wear", "skirt.anarchy", "WebGL", "unity6000-content1")!;
        Assert.That(applied.Applied, Is.True);
        Assert.That(mac.Variant!.Sha256, Is.EqualTo(native.Variants[0].Sha256));
        Assert.That(mac.Metadata["displayName"].GetString(), Is.EqualTo("native metadata"));
        Assert.That(browser.Variant!.Sha256, Is.EqualTo(web.Variants[0].Sha256));
        Assert.That(browser.Metadata["displayName"].GetString(), Is.EqualTo("web metadata"));
        Assert.That(mac.Revision, Is.EqualTo(browser.Revision));
        Assert.That(Directory.Exists(Path.Combine(_root, "history")), Is.False);
        Assert.That(_store.GetIndex("StandaloneOSX", "unity6000-content1").PlatformMissing, Is.Empty);
        Assert.That(_store.CompactStorage(apply: true).PromotedVariants, Is.Zero);
        Assert.That(_store.CompactStorage().HistoryRecords, Is.Zero);
        Assert.DoesNotThrow(() => _store.RequireCompactedStorage());

        var record = _store.ReadCurrentRecord("wear", "skirt.anarchy")!;
        await _store.UpdateRecordAsync(record.Type, record.Id, record.Revision, "retired", record.Metadata);
        var legacy = _store.ResolveMany(new AssetResolveRequest { Platform = "StandaloneOSX", RuntimeProfile = "unity6000-content1",
            Objects = new List<AssetObjectKey> { new() { Type = record.Type, Id = record.Id } } });
        Assert.That(legacy.Objects.Single().State, Is.EqualTo("legacy"));
        Assert.That(legacy.Objects.Single().Variant!.Sha256, Is.EqualTo(native.Variants[0].Sha256));
    }

    [Test]
    public async Task CorruptRetainedPayloadRefusesCompactionBeforeAnyPointerChanges()
    {
        var (native, _) = await LegacyHistoryFixture();
        var current = Path.Combine(_root, "records", "wear", "skirt.anarchy.json");
        var before = File.ReadAllBytes(current);
        File.AppendAllText(Path.Combine(_root, "blobs", native.Variants[0].Sha256), "bad");
        Assert.Throws<InvalidDataException>(() => _store.CompactStorage(apply: true));
        Assert.That(File.ReadAllBytes(current), Is.EqualTo(before));
        Assert.That(Directory.Exists(Path.Combine(_root, "history")), Is.True);
    }

    [Test]
    public async Task CompactionRemovesOrphansAndHistoryOnlyIdsWithoutResurrectingThem()
    {
        var (native, _) = await LegacyHistoryFixture();
        var orphan = Candidate("object", "removed.item", "not-current", true);
        File.Copy(orphan.Variants[0].StagedPath, Path.Combine(_root, "blobs", orphan.Variants[0].Sha256));
        var history = Path.Combine(_root, "history", "object", "removed.item");
        Directory.CreateDirectory(history);
        var old = new ContentObjectRecord { Type = orphan.Type, Id = orphan.Id, Revision = 1,
            Variants = new List<ContentObjectVariant> { new() { Platform = "StandaloneOSX", RuntimeProfile = "unity6000-content1",
                Sha256 = orphan.Variants[0].Sha256, Size = orphan.Variants[0].Size } } };
        File.WriteAllText(Path.Combine(history, "1.json"), JsonSerializer.Serialize(old));
        var applied = _store.CompactStorage(apply: true);
        Assert.That(applied.RemovedBlobs, Is.EqualTo(1));
        Assert.That(_store.ReadCurrentRecord(orphan.Type, orphan.Id), Is.Null);
        Assert.That(_store.TryGetVerifiedBlob(orphan.Variants[0].Sha256, out _, out _), Is.False);
        Assert.That(_store.TryGetVerifiedBlob(native.Variants[0].Sha256, out _, out _), Is.True);
    }

    private async Task<(ContentPublishCandidate Native, ContentPublishCandidate Web)> LegacyHistoryFixture()
    {
        var native = Candidate("wear", "skirt.anarchy", "native", true);
        native.Metadata = Metadata("native metadata");
        var old = (await _store.PublishAsync(native)).Record;
        var web = Candidate("wear", "skirt.anarchy", "web", true);
        web.Variants[0].Platform = "WebGL";
        web.Metadata = Metadata("web metadata");
        var current = new ContentObjectRecord { Type = web.Type, Id = web.Id, Revision = 2, Metadata = web.Metadata,
            Variants = new List<ContentObjectVariant> { new() { Platform = "WebGL", RuntimeProfile = "unity6000-content1",
                Sha256 = web.Variants[0].Sha256, Size = web.Variants[0].Size, IconAsset = "icon" } } };
        File.Copy(web.Variants[0].StagedPath, Path.Combine(_root, "blobs", web.Variants[0].Sha256));
        File.WriteAllText(Path.Combine(_root, "records", "wear", "skirt.anarchy.json"), JsonSerializer.Serialize(current));
        var history = Path.Combine(_root, "history", "wear", "skirt.anarchy");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "1.json"), JsonSerializer.Serialize(old));
        File.WriteAllText(Path.Combine(history, "2.json"), JsonSerializer.Serialize(current));
        return (native, web);
    }

    private ContentPublishCandidate BothPlatforms(string type, string id, string payload)
    {
        var candidate = Candidate(type, id, payload, hasIcon: true);
        var windows = Candidate(type, id, payload + "-win", hasIcon: true).Variants[0];
        windows.Platform = "StandaloneWindows64";
        candidate.Variants.Add(windows);
        return candidate;
    }

    private ContentPublishCandidate Candidate(
        string type, string id, string payload, bool hasIcon)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(_store.StagingPath, sha + "-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return new ContentPublishCandidate
        {
            Type = type,
            Id = id,
            Metadata = Metadata(id),
            Variants = new List<ContentPublishVariant>
            {
                new()
                {
                    Platform = "StandaloneOSX",
                    RuntimeProfile = "unity6000-content1",
                    Sha256 = sha,
                    Size = bytes.Length,
                    PayloadType = "assetBundle",
                    EntryAsset = "main",
                    IconAsset = hasIcon ? "icon" : null,
                    StagedPath = path,
                },
            },
        };
    }

    private ContentPublishAttachment Attachment(string name, string payload)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(_store.StagingPath, sha + "-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return new ContentPublishAttachment
        {
            Name = name,
            Sha256 = sha,
            Size = bytes.Length,
            StagedPath = path,
        };
    }

    private static Dictionary<string, JsonElement> Metadata(string displayName) => new()
    {
        ["displayName"] = JsonDocument.Parse(JsonSerializer.Serialize(displayName)).RootElement.Clone(),
    };
}

}
