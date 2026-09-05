using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

public sealed class AgentIntegrationTests
{
    [Test]
    public void AttachmentIsExclusivePresenceAwareAndExpires()
    {
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        var registry = new AgentSessionRegistry(() => now);
        var capabilities = AgentCapabilities.PlayerText | AgentCapabilities.Speech;

        Assert.That(registry.TryAttach(901, "mcp:a", 7, "Маша", capabilities, 5,
            out var first, out var reason), Is.True, reason);
        Assert.That(first.TtlSeconds, Is.EqualTo(15), "TTL должен clamp-иться к 15..120.");
        Assert.That(registry.TryAttach(901, "mcp:b", 7, "Другой", capabilities, 45,
            out _, out reason), Is.False);
        Assert.That(reason, Is.EqualTo("AlreadyAttached"));

        registry.SetViewerPresence("viewer", new[] { 901 }, true);
        var state = registry.StatesFor(new[] { 901 })[0];
        Assert.Multiple(() =>
        {
            Assert.That(state.Attached, Is.True);
            Assert.That(state.PlayerPresent, Is.True);
            Assert.That(state.DisplayName, Is.EqualTo("Маша"));
        });

        now = now.AddSeconds(16);
        registry.Sweep(null, 7);
        Assert.Multiple(() =>
        {
            Assert.That(registry.HasAttachment(901), Is.False);
            Assert.That(registry.StatesFor(new[] { 901 })[0].Attached, Is.False);
        });
    }

    [Test]
    public void InboxIsBoundedReportsGapAndDeduplicatesMessageId()
    {
        var registry = new AgentSessionRegistry();
        Assert.That(registry.TryAttach(901, "mcp:a", 4, "Agent",
            AgentCapabilities.PlayerText, 45, out var attachment, out _), Is.True);

        for (var i = 1; i <= 66; i++)
            Assert.That(registry.TryEnqueuePlayerText(901, "m" + i, "ru", "текст " + i,
                out _), Is.True);
        Assert.That(registry.TryEnqueuePlayerText(901, "m66", "ru", "дубликат", out _), Is.True);

        Assert.That(registry.TryReadInbox(attachment.AttachmentId, "mcp:a", 4, 1, 16,
            out var inbox, out var reason), Is.True, reason);
        Assert.Multiple(() =>
        {
            Assert.That(inbox.Gap, Is.True);
            Assert.That(inbox.Truncated, Is.True);
            Assert.That(inbox.Messages, Has.Length.EqualTo(16));
            Assert.That(inbox.Messages[0].MessageId, Is.EqualTo("m3"));
            Assert.That(inbox.Messages[15].MessageId, Is.EqualTo("m18"));
        });
    }

    [Test]
    public void TurnCommitIsIdempotentAndViewIsProcessLocal()
    {
        var registry = new AgentSessionRegistry();
        Assert.That(registry.TryAttach(901, "mcp:a", 9, "Agent",
            AgentCapabilities.RelationView | AgentCapabilities.Journal, 45,
            out var attachment, out _), Is.True);

        Assert.That(registry.TryCommitTurn(attachment.AttachmentId, "mcp:a", 9, "turn-1",
            "Осматривает берег", "{\"trust\":0.4}", "Новый берег.",
            out var duplicate, out var npcId, out _), Is.True);
        Assert.That(duplicate, Is.False);
        Assert.That(npcId, Is.EqualTo(901));
        Assert.That(registry.TryCommitTurn(attachment.AttachmentId, "mcp:a", 9, "turn-1",
            "не должен заменить", "{}", "дубликат", out duplicate, out _, out _), Is.True);
        Assert.That(duplicate, Is.True);

        var state = registry.StatesFor(new[] { 901 })[0];
        Assert.Multiple(() =>
        {
            Assert.That(state.IntentSummary, Is.EqualTo("Осматривает берег"));
            Assert.That(state.RelationView, Is.EqualTo("{\"trust\":0.4}"));
            Assert.That(state.JournalEntry, Is.EqualTo("Новый берег."));
        });

        registry.Clear();
        state = registry.StatesFor(new[] { 901 })[0];
        Assert.Multiple(() =>
        {
            Assert.That(state.Attached, Is.False);
            Assert.That(state.IntentSummary, Is.Empty);
            Assert.That(state.JournalEntry, Is.Empty);
        });
    }

    [Test]
    public void SpeechUploadRequiresOrderWaveShapeAndChecksum()
    {
        var registry = new AgentSessionRegistry();
        Assert.That(registry.TryAttach(901, "mcp:a", 2, "Agent",
            AgentCapabilities.Speech, 45, out var attachment, out _), Is.True);
        var wav = MakeWave(100);
        var sha = Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant();
        var metadata = new AgentUtteranceMetadata
        {
            UtteranceId = "speech-1",
            TurnId = "turn-1",
            Language = "ru",
            Text = "Привет",
            Emotion = "warm",
            Delivery = AgentSpeechDelivery.PlayerReply,
            Priority = AgentSpeechPriority.Talk,
            TotalBytes = wav.Length,
            DurationMilliseconds = 100,
            Sha256 = sha,
        };

        Assert.That(registry.TryBeginUtterance(attachment.AttachmentId, "mcp:a", 2,
            metadata, out _, out _), Is.True);
        Assert.That(registry.TryAppendUtterance(attachment.AttachmentId, "mcp:a", 2,
            "speech-1", 1, wav, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("ChunkOutOfOrder"));
        Assert.That(registry.TryAppendUtterance(attachment.AttachmentId, "mcp:a", 2,
            "speech-1", 0, wav, out reason), Is.True, reason);
        Assert.That(registry.TryCommitUtterance(attachment.AttachmentId, "mcp:a", 2,
            "speech-1", new string('0', 64), out _, out _, out reason), Is.False);
        Assert.That(reason, Is.EqualTo("ChecksumMismatch"));
        Assert.That(registry.TryCommitUtterance(attachment.AttachmentId, "mcp:a", 2,
            "speech-1", sha, out var duplicate, out var speech, out reason), Is.True, reason);

        Assert.Multiple(() =>
        {
            Assert.That(duplicate, Is.False);
            Assert.That(speech, Is.Not.Null);
            Assert.That(speech!.Bytes, Is.EqualTo(wav));
            Assert.That(registry.StatesFor(new[] { 901 })[0].Phase,
                Is.EqualTo(AgentPhase.Speaking));
        });
        Assert.That(registry.TryCommitUtterance(attachment.AttachmentId, "mcp:a", 2,
            "speech-1", sha, out duplicate, out speech, out reason), Is.True, reason);
        Assert.That(duplicate, Is.True);
        Assert.That(speech, Is.Null);
    }

    [Test]
    public void AgentWireV13FramesRoundTripAndRejectTrailingOrOversizedData()
    {
        var value = new AgentStateFrame
        {
            Revision = 12,
            NpcId = 901,
            Attached = true,
            AttachmentId = "a",
            DisplayName = "Маша",
            Capabilities = AgentCapabilities.PlayerText | AgentCapabilities.Speech,
            Phase = AgentPhase.Thinking,
            IntentSummary = "Смотрит на лагерь",
            RelationView = "{\"trust\":0.5}",
            JournalEntry = "Сегодня услышала голос.",
            PlayerPresent = true,
        };
        var encoded = Payload(AgentWire.AgentState(value));
        var decoded = AgentWire.ReadAgentState(encoded);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.NpcId, Is.EqualTo(901));
            Assert.That(decoded.Phase, Is.EqualTo(AgentPhase.Thinking));
            Assert.That(decoded.PlayerPresent, Is.True);
            Assert.That(decoded.DisplayName, Is.EqualTo("Маша"));
        });

        var trailing = new byte[encoded.Length + 1];
        Buffer.BlockCopy(encoded, 0, trailing, 0, encoded.Length);
        Assert.Throws<InvalidDataException>(() => AgentWire.ReadAgentState(trailing));
        Assert.Throws<InvalidDataException>(() => AgentWire.AgentSpeechChunk("u", 0,
            new byte[AgentWire.MaxChunkBytes + 1]));
        Assert.Throws<InvalidDataException>(() => AgentWire.AgentTextInput(1, 901, "m", "ru",
            new string('я', AgentWire.MaxTextCharacters + 1)));
    }

    [Test]
    public async Task DeepgramBrokerUsesTokenGrantAndRateLimitsPerOwner()
    {
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        var handler = new GrantHandler();
        using var broker = new DeepgramTokenBroker("secret-key", handler, () => now);

        for (var i = 0; i < DeepgramTokenBroker.GrantsPerMinute; i++)
        {
            var grant = await broker.GrantAsync("ws:player", CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(grant.Accepted, Is.True);
                Assert.That(grant.Token, Is.EqualTo("temporary-jwt"));
                Assert.That(grant.ExpiresUtc, Is.EqualTo(now.AddSeconds(60)));
            });
        }
        var limited = await broker.GrantAsync("ws:player", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(limited.Accepted, Is.False);
            Assert.That(limited.Reason, Is.EqualTo("RateLimited"));
            Assert.That(handler.Calls, Is.EqualTo(4));
            Assert.That(handler.LastAuthorizationScheme, Is.EqualTo("Token"));
            Assert.That(handler.LastAuthorizationValue, Is.EqualTo("secret-key"));
            Assert.That(handler.LastBody, Is.EqualTo("{\"ttl_seconds\":60}"));
        });

        now = now.AddMinutes(1);
        Assert.That((await broker.GrantAsync("ws:player", CancellationToken.None)).Accepted, Is.True);
    }

    [Test]
    public void WireRejectsMalformedUtf8BeforeTextReachesInbox()
    {
        var payload = Payload(AgentWire.AgentTextInput(1, 901, "m", "ru", "Привет"));
        payload[9] = 0xff; // invalid UTF-8 in messageId, after two int32 values + string length
        Assert.Throws<InvalidDataException>(() => AgentWire.ReadAgentTextInput(payload));
    }

    [Test]
    public void AttachmentSeparatesCapabilityFromActionLeaseAndRefusesForeignCommands()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "mcp:one", 1, "Agent", AgentCapabilities.PlayerText, 45, out var attachment, out _);
        Assert.That(registry.CanIssueWorldCommands(901, "mcp:one"), Is.False);
        Assert.That(registry.CanIssueWorldCommands(901, "mcp:two"), Is.False);
        registry.TryAttach(901, "mcp:one", 1, "Agent", AgentCapabilities.WorldActions, 45, out _, out _);
        Assert.That(registry.CanIssueWorldCommands(901, "mcp:one"), Is.True);
        registry.TryDetach(attachment.AttachmentId, "mcp:one", 1, out _, out _);
        Assert.That(registry.CanIssueWorldCommands(901, "mcp:two"), Is.True);
    }

    [Test]
    public async Task AnonymousTokenGrantIsRejectedWithoutCallingProvider()
    {
        var handler = new GrantHandler();
        using var broker = new DeepgramTokenBroker("test-key", handler);
        var result = await broker.GrantAsync("", CancellationToken.None);
        Assert.That(result.Accepted, Is.False);
        Assert.That(result.Reason, Is.EqualTo("Unauthorized"));
        Assert.That(handler.Calls, Is.Zero);
    }

    [Test]
    public async Task DeepgramBrokerWithoutServerKeyFailsClosedWithoutNetwork()
    {
        var handler = new GrantHandler();
        using var broker = new DeepgramTokenBroker(string.Empty, handler);
        var grant = await broker.GrantAsync("ws:player", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(broker.Available, Is.False);
            Assert.That(grant.Accepted, Is.False);
            Assert.That(grant.Reason, Is.EqualTo("SttUnavailable"));
            Assert.That(handler.Calls, Is.Zero);
        });
    }

    private static byte[] Payload(byte[] frame)
    {
        var result = new byte[frame.Length - 1];
        Buffer.BlockCopy(frame, 1, result, 0, result.Length);
        return result;
    }

    private static byte[] MakeWave(int durationMilliseconds)
    {
        var samples = 44100 * durationMilliseconds / 1000;
        var dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), dataBytes);
        return wav;
    }

    private sealed class GrantHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastAuthorizationScheme { get; private set; } = string.Empty;
        public string LastAuthorizationValue { get; private set; } = string.Empty;
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme ?? string.Empty;
            LastAuthorizationValue = request.Headers.Authorization?.Parameter ?? string.Empty;
            LastBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"temporary-jwt\",\"expires_in\":60}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}

}
