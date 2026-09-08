using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class ProfileIsolationTests
{
    [Test]
    public async Task ServerAddressesCanBeSavedBeforeAuthorizationAndEditedWithoutDuplicates()
    {
        var root = Directory.CreateTempSubdirectory("studio-server-list-");
        try
        {
            var store = new StudioConfigurationStore(root.FullName);
            var initial = await store.ReadAsync();
            var player = new ServerProfile(Guid.NewGuid(), "My characters", new Uri("https://example.test/mcp"), "server.player");
            var admin = player with { Id = Guid.NewGuid(), Name = "Administrator", CredentialId = "server.admin" };
            var saved = await store.SaveAsync(initial, new(1, [], [player, admin]));
            var renamed = player with { Name = "Production" };
            await store.SaveAsync(saved, saved.Configuration with
            { Servers = saved.Configuration.Servers.Select(x => x.Id == renamed.Id ? renamed : x).ToArray() });
            var loaded = await new StudioConfigurationStore(root.FullName).ReadAsync();
            Assert.That(loaded.Configuration.Servers, Has.Length.EqualTo(2));
            Assert.That(loaded.Configuration.Servers[0], Is.EqualTo(renamed));
            Assert.That(loaded.Configuration.Servers[1], Is.EqualTo(admin));
        }
        finally { root.Delete(true); }
    }

    [Test]
    public async Task NewNikaDoesNotInheritMashasRoomOrOverwriteExistingIdentity()
    {
        var root = Directory.CreateTempSubdirectory("studio-identities-");
        try
        {
            var masha = new MashaMemoryStore(Path.Combine(root.FullName, "masha"));
            var nikaPath = Path.Combine(root.FullName, "nika");
            var nika = new MashaMemoryStore(nikaPath, new MashaIdentity { Id = "nika", Name = "Ника", Age = 25, Traits = ["спокойная"] });
            var m = await masha.SnapshotAsync(default);
            var n = await nika.SnapshotAsync(default);
            Assert.That(m.CoreMemories.Any(x => x.Key == "origin.strange_room"), Is.True);
            Assert.That(n.CoreMemories.Any(x => x.Key == "origin.strange_room"), Is.False);
            Assert.That(n.Identity.Name, Is.EqualTo("Ника"));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(nikaPath, "SOUL.md")), Does.Contain("Ника").And.Not.Contain("Маша"));
            var reopened = new MashaMemoryStore(nikaPath);
            Assert.That((await reopened.SnapshotAsync(default)).Identity.Name, Is.EqualTo("Ника"));
            Assert.That((await reopened.SnapshotAsync(default)).CoreMemories.Any(x => x.Key == "origin.strange_room"), Is.False);
        }
        finally { root.Delete(true); }
    }

    [Test]
    public async Task DraftsSurviveRestartButCannotRunUntilConfiguredAndConflictsAreRejected()
    {
        var root = Directory.CreateTempSubdirectory("studio-config-");
        try
        {
            var store = new StudioConfigurationStore(root.FullName);
            var initial = await store.ReadAsync();
            var a = new AgentProfile(Guid.NewGuid(), "Маша", Path.Combine(root.FullName, "masha"),
                Guid.Empty, "", 0, new(ModelProviderKind.Codex, "codex", ""));
            var b = a with { Id = Guid.NewGuid(), Name = "Ника", Workspace = Path.Combine(root.FullName, "nika") };
            a = a with { Model = new(ModelProviderKind.Grok, "model.Grok", "grok-fixture"), Voice = new("voice.ElevenLabs", "masha-voice", "voice-model") };
            b = b with { Model = new(ModelProviderKind.DeepSeek, "model.DeepSeek", "deepseek-fixture", "enabled"), Voice = new("voice.SecondAccount", "nika-voice", "other-voice-model") };
            var saved = await store.SaveAsync(initial, new(1, [a, b], []));
            var loaded = await new StudioConfigurationStore(root.FullName).ReadAsync();
            Assert.That(loaded.Configuration.Agents.Select(x => x.Name), Is.EqualTo(new[] { "Маша", "Ника" }));
            Assert.That(loaded.Configuration.Agents[0].Model.Provider, Is.EqualTo(ModelProviderKind.Grok));
            Assert.That(loaded.Configuration.Agents[1].Model.Provider, Is.EqualTo(ModelProviderKind.DeepSeek));
            Assert.That(loaded.Configuration.Agents[0].Voice!.VoiceId, Is.EqualTo("masha-voice"));
            Assert.That(loaded.Configuration.Agents[1].Voice!.VoiceId, Is.EqualTo("nika-voice"));
            Assert.Throws<InvalidDataException>(() => loaded.Configuration.Agents[0].Validate());
            Assert.ThrowsAsync<IOException>(() => store.SaveAsync(initial, new(1, [], [])));
            Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(saved, new(1, [a, b with { Workspace = a.Workspace }], [])));
            Assert.That((await store.ReadAsync()).Revision, Is.EqualTo(saved.Revision));
        }
        finally { root.Delete(true); }
    }

    [Test]
    public void CodexCatalogUsesReportedModelAndReasoningCapabilities()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"data":[
              {"id":"picker-one","model":"model-one","hidden":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},
              {"id":"picker-two","model":"model-two","hidden":true,"supportedReasoningEfforts":[]}
            ],"nextCursor":null}
            """);
        var models = CodexModelCatalog.ParsePage(doc.RootElement);
        Assert.That(models, Has.Count.EqualTo(1));
        Assert.That(models[0].Id, Is.EqualTo("model-one"));
        Assert.That(models[0].ReasoningModes, Is.EqualTo(new[] { "low", "high" }));
    }
}
