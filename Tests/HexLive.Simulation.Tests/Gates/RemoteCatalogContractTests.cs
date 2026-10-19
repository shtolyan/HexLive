using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{
public sealed class RemoteCatalogContractTests
{
    private static readonly string[] ServerIds = {
        "resource.board", "resource.fiber", "resource.palm_leaf", "tree.palm",
        "station.workbench", "resource.arrow", "underwear.bra_openback_bra_5",
        "food.human_torso_cooked", "plant.palm_sprout"
    };

    private static string CatalogJson(params string[] ids) => JsonSerializer.Serialize(new {
        worldObjects = ids.Select(id => new { id }).ToArray()
    });

    private static ContentCatalog Remote(string json, string[] authoritative = null)
    {
        Assert.That(SimDataFile.ApplyJson(json), Is.True);
        authoritative ??= JsonDocument.Parse(json).RootElement.GetProperty("worldObjects").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new WorldStateFactory().CreateRemoteTopology(new WorldBootstrapDefinition(), authoritative).Content;
    }

    [TearDown]
    public void RestoreCatalogs()
    {
        WorldObjectLibrary.Clear();
        SimDataFile.Require(RepoPaths.SimData);
        DefinitionIdTable.Build(new WorldStateFactory().CreateTopology(new WorldBootstrapDefinition()).Content);
    }

    [Test]
    public void RemoteVocabularyExcludesNewerClientDefaults()
    {
        var remote = Remote(CatalogJson(ServerIds));
        Assert.That(remote.ObjectDefinitions.Keys, Is.EquivalentTo(ServerIds));
        DefinitionIdTable.Build(remote);
        var sorted = ServerIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < sorted.Length; i++)
            Assert.That(DefinitionIdTable.Resolve(i + 1), Is.EqualTo(sorted[i]));
        Assert.That(DefinitionIdTable.IndexOf(HumanMeatCatalog.Head), Is.Zero);
        Assert.That(DefinitionIdTable.IndexOf(HumanMeatCatalog.MaleTorsoCooked), Is.Zero);
    }

    [TestCase("tree.palm")]
    [TestCase("plant.palm_sprout")]
    [TestCase("station.workbench")]
    [TestCase("resource.board")]
    [TestCase("resource.fiber")]
    [TestCase("resource.arrow")]
    [TestCase("underwear.bra_openback_bra_5")]
    [TestCase("food.human_torso_cooked")]
    public void OldServerObjectKeepsIdentityAcrossNewClientKeyframeAndDelta(string id)
    {
        var server = new ContentCatalog();
        foreach (var name in ServerIds) server.ObjectDefinitions[name] = new ObjectDefinition { Id = name };
        var sent = new WorldSnapshot { Tick = 10 };
        sent.Objects.Add(new ObjectSnapshot { Id = new ObjectId(1), DefinitionId = id });
        DefinitionIdTable.Build(server);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
            WorldSnapshotCodec.Write(sent, writer, false);
        var encoder = new SnapshotDeltaEncoder();
        encoder.Encode(sent, false);
        sent.Tick = 11;
        sent.Objects[0].ResourceAmount = 2;
        sent.Objects[0].DefinitionId = id == "resource.board" ? "tree.palm" : "resource.board";
        var delta = encoder.Encode(sent, false);

        DefinitionIdTable.Build(Remote(CatalogJson(ServerIds)));
        buffer.Position = 0;
        var mirror = new WorldSnapshot();
        using (var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true))
            WorldSnapshotCodec.Read(reader, mirror);
        Assert.That(mirror.Objects.Single().DefinitionId, Is.EqualTo(id));
        using var deltaStream = new MemoryStream(delta);
        using var deltaReader = new BinaryReader(deltaStream);
        SnapshotDeltaReader.Apply(deltaReader, mirror, 10);
        Assert.That(mirror.Objects.Single().DefinitionId, Is.EqualTo(sent.Objects[0].DefinitionId));
        Assert.That(mirror.Objects.Single().ResourceAmount, Is.EqualTo(2));
    }

    [Test]
    public void ReconnectingToOlderCatalogDoesNotRetainPreviousServerIds()
    {
        Remote(CatalogJson(ServerIds.Concat(new[] { "test.server_only" }).ToArray()));
        var next = Remote(CatalogJson(ServerIds));
        Assert.That(next.ObjectDefinitions.ContainsKey("test.server_only"), Is.False);
    }

    public static System.Collections.IEnumerable BadVocabularies()
    {
        yield return new TestCaseData((object)null);
        foreach (var ids in new[] { Array.Empty<string>(), new[] { "" }, new[] { " " }, new[] { (string)null },
            new[] { "resource.board", "resource.board" }, new[] { "tree.palm", "resource.board" },
            new[] { "not_applied" }, new[] { new string('x', 1025) } })
            yield return new TestCaseData((object)ids);
    }

    [TestCaseSource(nameof(BadVocabularies))]
    public void BadCatalogCannotPartiallyChangeVocabulary(string[] ids)
    {
        var catalog = new WorldStateFactory().CreateTopology(new WorldBootstrapDefinition()).Content;
        var before = catalog.ObjectDefinitions.Keys.ToArray();
        Assert.That(SimDataFile.RestrictToRemoteObjectCatalog(catalog, ids), Is.False);
        Assert.That(catalog.ObjectDefinitions.Keys, Is.EquivalentTo(before));
        Assert.Throws<InvalidOperationException>(() =>
            new WorldStateFactory().CreateRemoteTopology(new WorldBootstrapDefinition(), ids));
    }

    [Test]
    public void LocalWorldStillUsesItsFullDefaults()
    {
        Remote(CatalogJson(ServerIds));
        var local = new WorldStateFactory().CreateTopology(new WorldBootstrapDefinition());
        Assert.That(local.Content.ObjectDefinitions.ContainsKey(HumanMeatCatalog.Head), Is.True);
        Assert.That(local.Content.ObjectDefinitions.ContainsKey(HumanMeatCatalog.MaleTorsoCooked), Is.True);
    }

    [TestCase(ContentIds.BedLeaf)]
    [TestCase(ContentIds.HutBed)]
    [TestCase(ContentIds.LeatherPants)]
    public void SaveOnlyAliasesCannotReenterRemoteWireVocabulary(string alias)
    {
        Assert.Throws<InvalidOperationException>(() => Remote(CatalogJson(alias)));
    }

    [Test]
    public void BuiltInsOmittedFromJsonStillUseExplicitServerIndices()
    {
        var remote = Remote(CatalogJson("resource.board"), ServerIds.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.That(remote.ObjectDefinitions.Keys, Is.EquivalentTo(ServerIds),
            "The actual server vocabulary includes built-ins absent from its JSON export.");
    }

    private static byte[] EncodeHandshake(Handshake handshake) => Frame.Handshake(handshake);
    private static Handshake DecodeHandshake(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); stream.Position = 1;
        using var reader = new BinaryReader(stream); return Handshake.Read(reader);
    }

    [Test]
    public void ExactVocabularyTravelsInHandshake()
    {
        var sent = new Handshake();
        sent.ObjectDefinitionIds.AddRange(ServerIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.That(DecodeHandshake(EncodeHandshake(sent)).ObjectDefinitionIds, Is.EqualTo(sent.ObjectDefinitionIds));
    }

    [Test]
    public void LegacyProtocolIsRejectedInsteadOfGuessingItsDictionary()
    {
        var bytes = EncodeHandshake(new Handshake());
        BitConverter.GetBytes(26).CopyTo(bytes, 1);
        Assert.Throws<InvalidDataException>(() => DecodeHandshake(bytes));
    }

    [Test]
    public void VocabularyCountIsBoundedBeforeAllocation()
    {
        var bytes = EncodeHandshake(new Handshake());
        BitConverter.GetBytes(DefinitionIdTable.MaxIds + 1).CopyTo(bytes, bytes.Length - 4);
        Assert.Throws<InvalidDataException>(() => DecodeHandshake(bytes));
    }

    [Test]
    public void DuplicateWireIdsAreRejectedByReaderAndWriter()
    {
        var handshake = new Handshake(); handshake.ObjectDefinitionIds.AddRange(new[] { "a", "b" });
        var bytes = EncodeHandshake(handshake); bytes[bytes.Length - 1] = (byte)'a';
        Assert.Throws<InvalidDataException>(() => DecodeHandshake(bytes));
        handshake.ObjectDefinitionIds[1] = "a";
        Assert.Throws<InvalidDataException>(() => EncodeHandshake(handshake));
    }

    [Test]
    public void ServerVocabularySnapshotDoesNotChangeWhenAnotherTableIsBuilt()
    {
        DefinitionIdTable.Build(Remote(CatalogJson(ServerIds)));
        var snapshot = DefinitionIdTable.ExportIds();
        Assert.That(DefinitionIdTable.Matches(snapshot), Is.True);
        DefinitionIdTable.Build(Remote(CatalogJson("tree.palm")));
        Assert.That(DefinitionIdTable.Matches(snapshot), Is.False,
            "Reconnect must refuse a changed table before decoding with stale indices.");
        Assert.That(DefinitionIdTable.Matches(null), Is.False);
        Assert.That(snapshot, Is.EqualTo(ServerIds.OrderBy(x => x, StringComparer.Ordinal)));
    }
}
}
