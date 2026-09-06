using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class CodexDecisionTests
{
    [Test]
    public void SubscriptionProcessDoesNotInheritSecretsOrApiBilling()
    {
        var start = CodexDecisionRunner.CreateStartInfo("codex", Path.GetTempPath());
        Assert.That(start.UseShellExecute, Is.False);
        Assert.That(start.RedirectStandardInput, Is.True);
        Assert.That(start.Environment.Keys, Is.SubsetOf(new[]
            { "HOME", "USER", "LOGNAME", "PATH", "TMPDIR", "RUST_LOG" }));
        Assert.That(start.Environment.ContainsKey("OPENAI_API_KEY"), Is.False);
        Assert.That(start.Environment.ContainsKey("HEXLIVE_MCP_TOKEN"), Is.False);
    }

    [Test]
    public void CodexDecisionUsesSameStrictValidatorAsGrok()
    {
        const string json = """
        {"speech":"","emotion":"curious","action":null,"reaction":"None",
        "intentSummary":"Отдыхаю.","memoryUpserts":[],"journalText":""}
        """;
        Assert.That(AgentProviders.ParseDecision(json, "heartbeat").IntentSummary, Is.EqualTo("Отдыхаю."));
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(
            json.Replace("\"action\":null", "\"action\":{\"tool\":\"reset_world\",\"arguments\":{}}"), "heartbeat"));
        Assert.Throws<InvalidDataException>(() => AgentProviders.ParseDecision(
            json.Replace("\"speech\":\"\"", "\"unknown\":true,\"speech\":\"\""), "heartbeat"));
    }
}
