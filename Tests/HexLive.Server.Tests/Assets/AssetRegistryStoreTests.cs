using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task IconIsAnEntryOfOwningPayloadAndThereIsNoIconObjectType()
    {
        var result = await _store.PublishAsync(
            Candidate("prosthetic", "leg.wood.l", "prosthetic-and-icon", hasIcon: true));
        var resolved = _store.Resolve(
            "prosthetic", "leg.wood.l", "StandaloneOSX", "unity6000-content1");

        Assert.That(ContentIdentity.IsType("icon"), Is.False);
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
        Assert.That(Directory.GetFiles(Path.Combine(
            _root, "history", "audio", "voice_jana_hungry_0")), Has.Length.EqualTo(1));
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
    public void VisualObjectWithoutOwnedIconIsRejected()
    {
        var candidate = Candidate("wear", "skirt.anarchy", "bytes", hasIcon: false);

        Assert.That(async () => await _store.PublishAsync(candidate),
            Throws.TypeOf<ArgumentException>());
        Assert.That(_store.RegistryRevision, Is.Zero);
    }

    [Test]
    public async Task RollbackUsesNextRevisionAndReusesOldBlob()
    {
        var v1 = Candidate("wear", "skirt.anarchy", "v1", hasIcon: true);
        var first = await _store.PublishAsync(v1);
        await _store.PublishAsync(Candidate("wear", "skirt.anarchy", "v2", hasIcon: true));
        var rollback = await _store.PublishAsync(v1);

        Assert.That(rollback.Record.Revision, Is.EqualTo(3));
        Assert.That(rollback.Record.Variants.Single().Sha256,
            Is.EqualTo(first.Record.Variants.Single().Sha256));
        Assert.That(_store.ReadHistory("wear", "skirt.anarchy").Select(value => value.Revision),
            Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public async Task OldRuntimeProfileGetsNewestCompatibleHistoryRevision()
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

        Assert.That(oldPlayer!.Revision, Is.EqualTo(1));
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
        Assert.That(_store.ReadHistory("wear", "skirt.anarchy"), Has.Count.EqualTo(2));
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
