using System.Text.Json;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

public sealed class AgentIncidentBufferTests
{
    [Test]
    public void ConfirmedDeathIsRetainedAcrossFailedDecisionsButGlobalDeathDoesNotBecomeWitnessed()
    {
        var buffer = new AgentIncidentBuffer();
        buffer.Observe(Event(1, "NpcDied"));
        Assert.That(buffer.HasPending, Is.False);
        buffer.Observe(Event(2, "AgentObservedDeath"));
        var pending = buffer.Snapshot();
        buffer.Observe(Event(2, "AgentObservedDeath"));
        Assert.That(buffer.Snapshot().GetRawText(), Is.EqualTo(pending.GetRawText()));
        buffer.Consume(pending);
        Assert.That(buffer.HasPending, Is.False);
    }

    private static JsonElement Event(long seq, string type = "AgentObservedTheft") =>
        JsonSerializer.SerializeToElement(new { events = new[] { new { seq, type, data = "Actor=NPC7 Item=resource.food" } } });

    [Test]
    public void FailedDecisionRetainsPayloadDespiteDuplicateTransportDelivery()
    {
        var buffer = new AgentIncidentBuffer();
        buffer.Observe(Event(10));
        var failedRequest = buffer.Snapshot();
        buffer.Observe(Event(10));
        var retry = buffer.Snapshot();
        Assert.That(retry.GetRawText(), Is.EqualTo(failedRequest.GetRawText()));
        Assert.That(retry.GetProperty("events")[0].GetProperty("data").GetString(), Does.Contain("NPC7"));
        buffer.Consume(retry);
        buffer.Observe(Event(10));
        Assert.That(buffer.HasPending, Is.False);
    }

    [Test]
    public void DurableDecisionConsumesOnlyFactsIncludedInItsRequest()
    {
        var buffer = new AgentIncidentBuffer();
        buffer.Observe(Event(10));
        var request = buffer.Snapshot();
        buffer.Observe(Event(11, "AgentObservedLoot"));
        buffer.Consume(request);
        Assert.That(buffer.Snapshot().GetProperty("events")[0].GetProperty("seq").GetInt64(), Is.EqualTo(11));
    }

    [Test]
    public void OverflowIsExplicitAndNewAttachmentCanRestartSequence()
    {
        var buffer = new AgentIncidentBuffer();
        for (var seq = 1; seq <= 80; seq++) buffer.Observe(Event(seq));
        var request = buffer.Snapshot();
        Assert.That(request.GetProperty("events").GetArrayLength(), Is.EqualTo(64));
        Assert.That(request.GetProperty("dropped").GetInt64(), Is.EqualTo(16));
        buffer.Reset();
        buffer.Observe(Event(1));
        Assert.That(buffer.Snapshot().GetProperty("events")[0].GetProperty("seq").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public void GlobalTheftEventsAreNotReinterpretedAsPersonalObservations()
    {
        var buffer = new AgentIncidentBuffer();
        buffer.Observe(Event(1, "ItemStolen"));
        Assert.That(buffer.HasPending, Is.False);
    }
}
