using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class CodexDecisionTests
{
    [TestCase("health=1; unconscious=false; moving=true", false)]
    [TestCase("health=1; unconscious=true; moving=false", true)]
    [TestCase("health=1; unconscious=false; memory=unconscious=true", false)]
    public void BodyGateUsesCurrentFlagNotHistoricalMention(string summary, bool expected)
    {
        using var state = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new { stateSummary = summary }));
        Assert.That(AgentHostRuntime.IsUnconscious(state.RootElement), Is.EqualTo(expected));
    }

    [Test]
    public void VoiceIdentityIsStableAndDoesNotDependOnTranscript()
    {
        using var a = System.Text.Json.JsonDocument.Parse("""
            {"messages":[{"seq":12,"messageId":"voice-1","text":"hello"}]}
            """);
        using var b = System.Text.Json.JsonDocument.Parse("""
            {"messages":[{"seq":12,"messageId":"voice-1","text":"different"}]}
            """);
        Assert.That(AgentHostRuntime.VoiceTurnId("attachment", a.RootElement),
            Is.EqualTo(AgentHostRuntime.VoiceTurnId("attachment", b.RootElement)));
        Assert.That(AgentHostRuntime.VoiceTurnId("other", a.RootElement),
            Is.Not.EqualTo(AgentHostRuntime.VoiceTurnId("attachment", a.RootElement)));
    }

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
