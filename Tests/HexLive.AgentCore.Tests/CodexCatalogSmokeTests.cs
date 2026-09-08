using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class CodexCatalogSmokeTests
{
    [Test, Explicit("Requires an explicitly supplied local Codex executable and existing ChatGPT login. No generation.")]
    public async Task OfficialCatalogCanBeReadWithoutStartingAModelTurn()
    {
        var executable = Environment.GetEnvironmentVariable("HEXLIVE_CODEX_SMOKE_EXECUTABLE");
        Assert.That(executable, Is.Not.Null.And.Not.Empty);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var models = await new CodexModelCatalog(executable!).ListAsync(stop.Token);
        Assert.That(models, Is.Not.Empty);
        Assert.That(models.All(x => !string.IsNullOrWhiteSpace(x.Id)), Is.True);
        TestContext.WriteLine($"Official picker models: {models.Count}; no turn/start was sent.");
    }
}
