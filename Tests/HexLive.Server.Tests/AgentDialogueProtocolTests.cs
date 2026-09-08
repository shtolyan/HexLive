using System;
using System.IO;
using System.Linq;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests;

public sealed class AgentDialogueProtocolTests
{
    [Test]
    public void InboxKeepsServerSuppliedIdentityAndDeduplicatesPerSpeaker()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "mcp:a", 1, "Маша", AgentCapabilities.PlayerText, 45, out var attached, out _);
        registry.TryEnqueuePlayerText(901, "same", "ru", "Я другой игрок", out _, "alice");
        registry.TryEnqueuePlayerText(901, "same", "ru", "Привет", out _, "bob");
        registry.TryEnqueuePlayerText(901, "same", "ru", "Повтор", out _, "alice");
        Assert.That(registry.TryReadInbox(attached.AttachmentId, "mcp:a", 1, 0, 16, out var inbox, out _), Is.True);
        Assert.That(inbox.Messages.Select(m => m.SenderId), Is.EqualTo(new[] { "alice", "bob" }));
    }

    [Test]
    public void EachAuthenticatedViewerReceivesOnlyItsOwnRelationshipView()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "mcp:a", 1, "Маша", AgentCapabilities.PlayerText, 45, out var attached, out _);
        const string alice = "{\"speakerId\":\"alice\",\"trust\":1,\"affinity\":0.9,\"voiceName\":\"Любимый\"}";
        const string bob = "{\"speakerId\":\"bob\",\"trust\":0,\"affinity\":-0.5,\"voiceName\":\"Голос\"}";
        registry.TryCommitTurn(attached.AttachmentId, "mcp:a", 1, "a", "", alice, "", out _, out _, out _);
        registry.TryCommitTurn(attached.AttachmentId, "mcp:a", 1, "b", "", bob, "", out _, out _, out _);
        Assert.That(registry.StatesFor(new[] { 901 }, "alice")[0].RelationView, Is.EqualTo(alice));
        Assert.That(registry.StatesFor(new[] { 901 }, "bob")[0].RelationView, Is.EqualTo(bob));
        Assert.That(registry.StatesFor(new[] { 901 }, "charlie")[0].RelationView, Is.Empty);
    }

    [Test]
    public void MultipleConnectionsKeepSpeakerPresentUntilLastDisconnect()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "mcp:a", 1, "Маша", AgentCapabilities.PlayerText, 45, out var attached, out _);
        registry.SetViewerPresence("tab1", new[] { 901 }, true, "alice");
        registry.SetViewerPresence("tab2", new[] { 901 }, true, "alice");
        registry.SetViewerPresence("tab1", new[] { 901 }, false);
        registry.TryHeartbeat(attached.AttachmentId, "mcp:a", 1, out var present, out _);
        Assert.That(present.PresentSpeakerIds, Is.EqualTo(new[] { "alice" }));
        registry.SetViewerPresence("tab2", new[] { 901 }, false);
        registry.TryHeartbeat(attached.AttachmentId, "mcp:a", 1, out var absent, out _);
        Assert.That(absent.PresentSpeakerIds, Is.Empty);
    }

    [TestCase(240)] [TestCase(241)] [TestCase(600)]
    public void LongSpeechRoundTripsWithoutChangingInputOrIntentLimits(int length)
    {
        var value = new AgentSpeechBeginFrame {
            UtteranceId = "u", TurnId = "t", Language = "ru", Text = new string('я', length), Emotion = "warm",
            DurationMilliseconds = 60000, TotalBytes = 6 * 1024 * 1024, Sha256 = new string('a', 64)
        };
        var encoded = AgentWire.AgentSpeechBegin(value);
        var decoded = AgentWire.ReadAgentSpeechBegin(encoded.Skip(1).ToArray());
        Assert.That(decoded.Text, Is.EqualTo(value.Text));
        Assert.That(decoded.DurationMilliseconds, Is.EqualTo(60000));
        Assert.That(AgentWire.MaxTextCharacters, Is.EqualTo(240));
        value.Text = new string('я', 601);
        Assert.Throws<InvalidDataException>(() => AgentWire.AgentSpeechBegin(value));
    }
}
