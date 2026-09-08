using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

[Explicit("Uses installed macOS credentials for read-only provider catalogs; never generates text or speech.")]
public sealed class InstalledCatalogSmokeTests
{
    [Test] public async Task GrokCatalogUsesImportedIntegration()
    {
        var secrets = new OperatingSystemSecretStore();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var catalog = new HttpModelAdapter(ModelProviderKind.Grok, "model.masha.grok", async t =>
            await secrets.ReadAsync("model.masha.grok", t) ?? throw new InvalidOperationException("MissingKey"));
        var models = await catalog.ListAsync(stop.Token);
        Assert.That(models.Count, Is.GreaterThan(1));
        TestContext.WriteLine($"Grok catalog: {models.Count} models. No completion request.");
    }
    [Test] public async Task ElevenLabsCatalogUsesImportedIntegration()
    {
        var secrets = new OperatingSystemSecretStore();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var catalog = new ElevenLabsVoiceCatalog(async t =>
            await secrets.ReadAsync("voice.masha.elevenlabs", t) ?? throw new InvalidOperationException("MissingKey"));
        var voices = await catalog.ListAsync(stop.Token);
        Assert.That(voices.Count, Is.GreaterThan(1));
        TestContext.WriteLine($"ElevenLabs catalog: {voices.Count} voices. No synthesis request.");
    }
}
