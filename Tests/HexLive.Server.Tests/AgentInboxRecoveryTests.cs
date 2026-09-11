using System;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests;

public sealed class AgentInboxRecoveryTests
{
    [Test]
    public void AcceptedUnreadTextSurvivesSleepAndIsAcknowledgedOnTheNewSession()
    {
        var now = DateTimeOffset.UtcNow; var registry = new AgentSessionRegistry(() => now);
        registry.TryAttach(901, "old", 1, "Masha", AgentCapabilities.PlayerText, 15, out var old, out _);
        var key = registry.InboxResumeKey(old.AttachmentId, "old", 1);
        Assert.That(registry.InboxResumeKey(old.AttachmentId, "other", 1), Is.Empty);
        registry.TryEnqueuePlayerText(901, "message", "ru", "Принеси кокосы", out _, "player");
        now = now.AddHours(1); registry.Sweep(null, 1);
        Assert.That(registry.HasAttachment(901), Is.False);
        registry.TryAttach(901, "new", 1, "Masha", AgentCapabilities.PlayerText, 15, out var attached, out _, key);
        Assert.That(registry.TryReadInbox(attached.AttachmentId, "new", 1, 0, 16, out var inbox, out _), Is.True);
        Assert.That(inbox.Messages, Has.Length.EqualTo(1));
        Assert.That(inbox.Messages[0].Text, Is.EqualTo("Принеси кокосы"));
        Assert.That(registry.InboxResumeKey(attached.AttachmentId, "new", 1), Is.EqualTo(key));
        Assert.That(registry.TryAcknowledgeInbox(attached.AttachmentId, "new", 1, inbox.Watermark, out _), Is.True);
        now = now.AddMinutes(1); registry.Sweep(null, 1);
        registry.TryAttach(901, "third", 1, "Masha", AgentCapabilities.PlayerText, 15, out attached, out _, key);
        registry.TryEnqueuePlayerText(901, "message", "ru", "late duplicate", out _, "player");
        registry.TryReadInbox(attached.AttachmentId, "third", 1, 0, 16, out inbox, out _);
        Assert.That(inbox.Messages, Is.Empty, "A lost client ACK must not re-enqueue the handled message after recovery");
    }

    [TestCase("wrong-key", 901, 1)]
    [TestCase("correct-key", 902, 1)]
    [TestCase("correct-key", 901, 2)]
    public void AnotherAttachmentCannotReadTheSuspendedInbox(string supplied, int npc, int generation)
    {
        var now = DateTimeOffset.UtcNow; var registry = new AgentSessionRegistry(() => now);
        registry.TryAttach(901, "old", 1, "Masha", AgentCapabilities.PlayerText, 15, out var old, out _);
        var key = registry.InboxResumeKey(old.AttachmentId, "old", 1);
        registry.TryEnqueuePlayerText(901, "message", "ru", "private fixture", out _, "player");
        now = now.AddMinutes(1); registry.Sweep(null, 1);
        registry.TryAttach(npc, "new", generation, "Masha", AgentCapabilities.PlayerText, 15, out var attached, out _, supplied == "correct-key" ? key : supplied);
        registry.TryReadInbox(attached.AttachmentId, "new", generation, 0, 16, out var inbox, out _);
        Assert.That(inbox.Messages, Is.Empty);
    }

    [Test]
    public void ExplicitStopDoesNotHandTheInboxToTheNextAttachment()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "old", 1, "Masha", AgentCapabilities.PlayerText, 45, out var old, out _);
        var key = registry.InboxResumeKey(old.AttachmentId, "old", 1);
        registry.TryEnqueuePlayerText(901, "message", "ru", "fixture", out _, "player");
        registry.TryDetach(old.AttachmentId, "old", 1, out _, out _);
        registry.TryAttach(901, "new", 1, "Masha", AgentCapabilities.PlayerText, 45, out var attached, out _, key);
        registry.TryReadInbox(attached.AttachmentId, "new", 1, 0, 16, out var inbox, out _);
        Assert.That(inbox.Messages, Is.Empty);
    }
}
