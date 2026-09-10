using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentDiagnosticsTests
{
    private string _root = "";
    [SetUp] public void SetUp() => _root = Directory.CreateTempSubdirectory("agent-diagnostics-").FullName;
    [TearDown] public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public void RepeatedPollsAreNotRepeatedFailuresAndSuccessBreaksTheSeries()
    {
        var log = new AgentDiagnostics(_root, "profile"); log.Bind("world", 901);
        for (var i = 0; i < 10; i++) log.Action("first", "interact", "TargetGone");
        log.Action("second", "interact", "TargetGone");
        log.Action("third", "interact", "TargetGone");
        log.Action("fourth", "interact", "PlanCompleted");
        log.Action("fifth", "interact", "TargetGone");
        var rows = Rows();
        Assert.That(rows.Count(r => r.GetProperty("kind").GetString() == "signal.repeated_failure"), Is.EqualTo(1));
        Assert.That(rows.Count(r => r.GetProperty("kind").GetString() == "action.result"), Is.EqualTo(5));
        Assert.That(rows.Select(r => r.GetProperty("sequence").GetInt64()), Is.Ordered);
    }

    [Test]
    public void RotationAndRetentionStayBoundedWithoutTouchingUnrelatedFiles()
    {
        var old = Path.Combine(_root, "events-old.jsonl"); File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        File.WriteAllText(Path.Combine(_root, "unrelated.txt"), "keep");
        var log = new AgentDiagnostics(_root, "profile", maxBytes: 1024, maxFiles: 3);
        for (var i = 0; i < 30; i++) log.Record("model.completed", i.ToString(), elapsedMs: i);
        Assert.That(File.Exists(old), Is.False);
        Assert.That(Directory.GetFiles(_root, "*.jsonl").Length, Is.InRange(1, 3));
        Assert.That(Directory.GetFiles(_root, "*.jsonl").All(f => new FileInfo(f).Length <= 1024), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(_root, "unrelated.txt")), Is.EqualTo("keep"));
        Assert.That(log.ErrorCode, Is.Empty);
    }

    [Test]
    public void DiskFailureIsNonFatalAndClearsAfterRecovery()
    {
        var path = Path.Combine(_root, "blocked"); File.WriteAllText(path, "file");
        var log = new AgentDiagnostics(path, "profile");
        Assert.DoesNotThrow(() => log.Action("turn", "interact", "Completed"));
        Assert.That(log.ErrorCode, Is.EqualTo("DiagnosticsUnavailable"));
        File.Delete(path); log.Record("session.start");
        Assert.That(log.ErrorCode, Is.Empty);
        Assert.That(File.Exists(Path.Combine(path, "events.jsonl")), Is.True);
    }

    [Test]
    public void SnapshotAndMachineCodesCannotLeakPayloads()
    {
        var log = new AgentDiagnostics(_root, "private-profile");
        using var state = JsonDocument.Parse("""{"tick":12,"position":{"x":1,"y":2},"inventoryItems":[],"visibleItems":[],"bodyNeeds":{"energy":{"value":0.2},"stamina":{"value":0.8}},"transcript":"SECRET","token":"SECRET"}""");
        log.Bind("private-world", 901);
        var source = "spec:153:0:" + new string('a', 64);
        log.Record("turn.observed", "private-turn", result: "Bearer SECRET", observation: AgentDiagnosticObservation.From(state.RootElement),
            sourceIds: [source, "Bearer SECRET"], relationship: new(.9f, .8f, .7f, 0f, -.05f, -.05f),
            execution: new("private-plan", "private-step", "private-command", 7, 1, 4, 2, "active", "failed", "Bearer SECRET"),
            usage: new("Grok", "Bearer SECRET", 123, null));
        var text = File.ReadAllText(Path.Combine(_root, "events.jsonl"));
        Assert.That(text, Does.Not.Contain("SECRET").And.Not.Contain("private-"));
        Assert.That(Rows().Last().GetProperty("sourceIds")[0].GetString(), Is.EqualTo(source));
        Assert.That(Rows().Last().GetProperty("relationship").GetProperty("SympathyDelta").GetSingle(), Is.EqualTo(-.05f));
        Assert.That(Rows().Last().GetProperty("observation").GetProperty("Tick").GetInt64(), Is.EqualTo(12));
        Assert.That(Rows().Last().GetProperty("observation").GetProperty("Energy").GetDouble(), Is.EqualTo(.2));
        Assert.That(Rows().Last().GetProperty("observation").GetProperty("Stamina").GetDouble(), Is.EqualTo(.8));
        Assert.That(Rows().Last().GetProperty("execution").GetProperty("CommandSequence").GetInt64(), Is.EqualTo(7));
        Assert.That(Rows().Last().GetProperty("execution").GetProperty("reason").GetString(), Is.EqualTo("redacted"));
        Assert.That(Rows().Last().GetProperty("usage").GetProperty("inputTokens").GetInt64(), Is.EqualTo(123));
        Assert.That(Rows().Last().GetProperty("usage").GetProperty("outputTokens").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    private JsonElement[] Rows() => File.ReadAllLines(Path.Combine(_root, "events.jsonl"))
        .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
}
