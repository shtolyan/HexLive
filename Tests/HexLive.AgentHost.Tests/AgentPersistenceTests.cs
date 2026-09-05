using System.Text.Json;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentPersistenceTests
{
    private string _directory = "";
    [SetUp] public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "agent-persistence-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Test]
    public void OutboxNeverDiscardsUnacknowledgedTurnsWhenFull()
    {
        var path = Path.Combine(_directory, "outbox.json");
        var outbox = new AgentTurnOutbox(path);
        for (var i = 0; i < 32; i++) outbox.Add(new PendingAgentTurn { TurnId = "t" + i });
        Assert.Throws<InvalidOperationException>(() => outbox.Add(new PendingAgentTurn { TurnId = "overflow" }));
        Assert.That(new AgentTurnOutbox(path).Items.Select(x => x.TurnId),
            Is.EqualTo(Enumerable.Range(0, 32).Select(i => "t" + i)));
        outbox.MarkLocalCommitted("t0");
        Assert.That(new AgentTurnOutbox(path).Items[0].LocalCommitted, Is.True);
        outbox.Remove("t0");
        Assert.That(new AgentTurnOutbox(path).Items.Count, Is.EqualTo(31));
    }

    [Test]
    public void StatusConcurrentHeartbeatsAndTurnsAlwaysWriteCompleteJson()
    {
        var path = Path.Combine(_directory, "status.json");
        var status = new AgentHostStatusStore(path);
        Assert.DoesNotThrow(() => Parallel.For(0, 64, i => status.Write(true,
            i % 2 == 0 ? "Thinking" : "Sleeping", 901, true, i % 2 == 0)));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.That(json.RootElement.GetProperty("npcId").GetInt32(), Is.EqualTo(901));
        Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
    }
}
