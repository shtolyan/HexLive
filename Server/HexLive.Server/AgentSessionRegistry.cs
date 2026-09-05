using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HexLive.Simulation.Common;
using HexLive.Simulation.Wire;

namespace HexLive.Server
{

/// <summary>
/// §160 process-local rendezvous between authenticated /watch viewers and MCP
/// sessions. It owns no personality or durable journal and never toggles the
/// simulation's manual-control bit merely because an agent is attached.
/// </summary>
public sealed class AgentSessionRegistry
{
    public const int DefaultTtlSeconds = 45;
    public const int HeartbeatSeconds = 10;
    public const int MaxInboxMessages = 64;
    public const int MaxReadMessages = 16;
    public const int MaxCommittedUtterances = 20;

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Attachment> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _attachmentByNpc = new();
    private readonly Dictionary<int, long> _stateRevisionByNpc = new();
    private readonly Dictionary<string, HashSet<int>> _viewerNpcs = new(StringComparer.Ordinal);
    private readonly List<CommittedAgentUtterance> _utterances = new();
    private long _revision;
    private long _messageSequence;
    private long _speechSequence;

    public AgentSessionRegistry(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private sealed class Attachment
    {
        public string Id = string.Empty;
        public string Owner = string.Empty;
        public int WorldGeneration;
        public int NpcId;
        public string DisplayName = string.Empty;
        public AgentCapabilities Capabilities;
        public AgentPhase Phase = AgentPhase.Ready;
        public string TurnId = string.Empty;
        public string IntentSummary = string.Empty;
        public string RelationView = string.Empty;
        public string JournalEntry = string.Empty;
        public DateTimeOffset LastSeen;
        public int TtlSeconds;
        public readonly List<AgentInboxMessage> Inbox = new();
        public readonly Queue<string> MessageIds = new();
        public readonly HashSet<string> MessageIdSet = new(StringComparer.Ordinal);
        public readonly Queue<string> TurnIds = new();
        public readonly HashSet<string> TurnIdSet = new(StringComparer.Ordinal);
        public readonly Queue<string> UtteranceIds = new();
        public readonly HashSet<string> UtteranceIdSet = new(StringComparer.Ordinal);
        public PendingAgentUtterance? Pending;
    }

    private sealed class PendingAgentUtterance
    {
        public AgentUtteranceMetadata Metadata = null!;
        public MemoryStream Bytes = new();
        public int NextChunkIndex;
        public DateTimeOffset StartedUtc;
    }

    public bool TryAttach(int npcId, string owner, int worldGeneration,
        string displayName, AgentCapabilities capabilities, int ttlSeconds,
        out AgentAttachmentSnapshot snapshot, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (_attachmentByNpc.TryGetValue(npcId, out var existingId) &&
                _byId.TryGetValue(existingId, out var existing))
            {
                if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    snapshot = default;
                    reason = "AlreadyAttached";
                    return false;
                }

                existing.DisplayName = displayName;
                existing.Capabilities = capabilities;
                existing.TtlSeconds = Math.Clamp(ttlSeconds, 15, 120);
                existing.LastSeen = _now();
                Touch(existing.NpcId);
                snapshot = Snapshot(existing);
                reason = string.Empty;
                return true;
            }

            var attachment = new Attachment
            {
                Id = Guid.NewGuid().ToString("N"),
                Owner = owner,
                WorldGeneration = worldGeneration,
                NpcId = npcId,
                DisplayName = displayName,
                Capabilities = capabilities,
                LastSeen = _now(),
                TtlSeconds = Math.Clamp(ttlSeconds, 15, 120),
            };
            _byId.Add(attachment.Id, attachment);
            _attachmentByNpc[npcId] = attachment.Id;
            Touch(npcId);
            snapshot = Snapshot(attachment);
            reason = string.Empty;
            return true;
        }
    }

    public bool TryHeartbeat(string attachmentId, string owner, int worldGeneration,
        out AgentAttachmentSnapshot snapshot, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                snapshot = default;
                return false;
            }

            attachment.LastSeen = _now();
            snapshot = Snapshot(attachment);
            return true;
        }
    }

    public bool TryReadInbox(string attachmentId, string owner, int worldGeneration,
        long sinceSequence, int limit, out AgentInboxRead result, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                result = default;
                return false;
            }

            attachment.LastSeen = _now();
            limit = Math.Clamp(limit, 1, MaxReadMessages);
            var oldest = attachment.Inbox.Count == 0
                ? _messageSequence + 1
                : attachment.Inbox[0].Sequence;
            var gap = sinceSequence > 0 && sinceSequence < oldest - 1;
            var rows = attachment.Inbox.Where(x => x.Sequence > sinceSequence).Take(limit).ToArray();
            var watermark = rows.Length == 0 ? Math.Max(sinceSequence, oldest - 1) : rows[^1].Sequence;
            var truncated = attachment.Inbox.Any(x => x.Sequence > watermark);
            result = new AgentInboxRead(rows, watermark, gap, truncated);
            return true;
        }
    }

    public bool TryPublishPhase(string attachmentId, string owner, int worldGeneration,
        string turnId, AgentPhase phase, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
                return false;
            attachment.LastSeen = _now();
            attachment.TurnId = turnId;
            attachment.Phase = phase;
            Touch(attachment.NpcId);
            return true;
        }
    }

    public bool TryCommitTurn(string attachmentId, string owner, int worldGeneration,
        string turnId, string intentSummary, string relationView, string journalEntry,
        out bool duplicate, out int npcId, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                duplicate = false;
                npcId = 0;
                return false;
            }

            attachment.LastSeen = _now();
            npcId = attachment.NpcId;
            duplicate = attachment.TurnIdSet.Contains(turnId);
            if (duplicate) return true;

            Remember(attachment.TurnIdSet, attachment.TurnIds, turnId, 128);
            attachment.TurnId = turnId;
            attachment.IntentSummary = intentSummary;
            attachment.RelationView = relationView;
            attachment.JournalEntry = journalEntry;
            attachment.Phase = AgentPhase.Ready;
            Touch(attachment.NpcId);
            return true;
        }
    }

    public bool TryBeginUtterance(string attachmentId, string owner, int worldGeneration,
        AgentUtteranceMetadata metadata, out bool duplicate, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                duplicate = false;
                return false;
            }

            duplicate = attachment.UtteranceIdSet.Contains(metadata.UtteranceId);
            if (duplicate) return true;
            if ((attachment.Capabilities & AgentCapabilities.Speech) == 0)
            {
                reason = "CapabilityUnavailable";
                return false;
            }
            if (attachment.Pending != null)
            {
                reason = "UploadInProgress";
                return false;
            }

            attachment.LastSeen = _now();
            attachment.Pending = new PendingAgentUtterance
            {
                Metadata = metadata,
                Bytes = new MemoryStream(Math.Max(0, metadata.TotalBytes)),
                StartedUtc = _now(),
            };
            reason = string.Empty;
            return true;
        }
    }

    public bool TryAppendUtterance(string attachmentId, string owner, int worldGeneration,
        string utteranceId, int chunkIndex, byte[] chunk, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
                return false;
            var pending = attachment.Pending;
            if (pending == null || !string.Equals(pending.Metadata.UtteranceId, utteranceId,
                    StringComparison.Ordinal))
            {
                reason = "NoUpload";
                return false;
            }
            if (chunkIndex != pending.NextChunkIndex)
            {
                reason = "ChunkOutOfOrder";
                return false;
            }
            if (chunk.Length > AgentWire.MaxChunkBytes ||
                pending.Bytes.Length + chunk.Length > pending.Metadata.TotalBytes ||
                pending.Bytes.Length + chunk.Length > AgentWire.MaxAudioBytes)
            {
                reason = "ChunkTooLarge";
                return false;
            }

            pending.Bytes.Write(chunk, 0, chunk.Length);
            pending.NextChunkIndex++;
            attachment.LastSeen = _now();
            reason = string.Empty;
            return true;
        }
    }

    public bool TryCommitUtterance(string attachmentId, string owner, int worldGeneration,
        string utteranceId, string sha256, out bool duplicate,
        out CommittedAgentUtterance? utterance, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                duplicate = false;
                utterance = null;
                return false;
            }

            duplicate = attachment.UtteranceIdSet.Contains(utteranceId);
            if (duplicate)
            {
                utterance = null;
                return true;
            }
            var pending = attachment.Pending;
            if (pending == null || !string.Equals(pending.Metadata.UtteranceId, utteranceId,
                    StringComparison.Ordinal))
            {
                utterance = null;
                reason = "NoUpload";
                return false;
            }

            var bytes = pending.Bytes.ToArray();
            if (bytes.Length != pending.Metadata.TotalBytes)
            {
                utterance = null;
                reason = "SizeMismatch";
                return false;
            }
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!ValidSha(sha256) ||
                !string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual, pending.Metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                utterance = null;
                reason = "ChecksumMismatch";
                return false;
            }
            // A zero-byte utterance is the text-only degradation path: if the
            // agent's TTS provider fails, the exact reply still reaches Unity
            // and is rendered as a subtitle without fabricating audio.
            if (bytes.Length == 0 && pending.Metadata.DurationMilliseconds == 0)
            {
                reason = string.Empty;
            }
            else if (!WavePcm16Mono44100.TryValidate(bytes, out var durationMs, out reason) ||
                     Math.Abs(durationMs - pending.Metadata.DurationMilliseconds) > 100)
            {
                utterance = null;
                reason = reason.Length == 0 ? "DurationMismatch" : reason;
                return false;
            }

            Remember(attachment.UtteranceIdSet, attachment.UtteranceIds, utteranceId, 64);
            utterance = new CommittedAgentUtterance(
                ++_speechSequence, attachment.NpcId, pending.Metadata, bytes);
            _utterances.Add(utterance);
            while (_utterances.Count > MaxCommittedUtterances) _utterances.RemoveAt(0);
            pending.Bytes.Dispose();
            attachment.Pending = null;
            attachment.LastSeen = _now();
            attachment.Phase = AgentPhase.Speaking;
            Touch(attachment.NpcId);
            reason = string.Empty;
            return true;
        }
    }

    public bool TryDetach(string attachmentId, string owner, int worldGeneration,
        out int npcId, out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, worldGeneration);
            if (!TryOwned(attachmentId, owner, worldGeneration, out var attachment, out reason))
            {
                npcId = 0;
                return false;
            }
            npcId = attachment.NpcId;
            RemoveLocked(attachment);
            return true;
        }
    }

    public List<int> DetachOwnedBy(string owner)
    {
        lock (_gate)
        {
            var removed = _byId.Values.Where(x => string.Equals(x.Owner, owner,
                StringComparison.Ordinal)).Select(x => x.NpcId).ToList();
            foreach (var npcId in removed.ToArray())
            {
                if (_attachmentByNpc.TryGetValue(npcId, out var id) && _byId.TryGetValue(id, out var item))
                    RemoveLocked(item);
            }
            return removed;
        }
    }

    public bool HasAttachment(int npcId)
    {
        lock (_gate)
        {
            SweepLocked(null, int.MaxValue);
            return _attachmentByNpc.ContainsKey(npcId);
        }
    }

    public bool TryEnqueuePlayerText(int npcId, string messageId, string language, string text,
        out string reason)
    {
        lock (_gate)
        {
            SweepLocked(null, int.MaxValue);
            if (!_attachmentByNpc.TryGetValue(npcId, out var id) || !_byId.TryGetValue(id, out var attachment))
            {
                reason = "AgentDetached";
                return false;
            }
            if ((attachment.Capabilities & AgentCapabilities.PlayerText) == 0)
            {
                reason = "CapabilityUnavailable";
                return false;
            }
            if (attachment.MessageIdSet.Contains(messageId))
            {
                reason = string.Empty;
                return true;
            }

            Remember(attachment.MessageIdSet, attachment.MessageIds, messageId, MaxInboxMessages);
            attachment.Inbox.Add(new AgentInboxMessage(
                ++_messageSequence, messageId, language, text, _now()));
            while (attachment.Inbox.Count > MaxInboxMessages) attachment.Inbox.RemoveAt(0);
            reason = string.Empty;
            return true;
        }
    }

    public bool CanIssueWorldCommands(int npcId, string owner)
    {
        lock (_gate)
        {
            SweepLocked(null, int.MaxValue);
            return !_attachmentByNpc.TryGetValue(npcId, out var id) ||
                _byId.TryGetValue(id, out var attachment) && attachment.Owner == owner &&
                (attachment.Capabilities & AgentCapabilities.WorldActions) != 0;
        }
    }

    public void SetViewerPresence(string viewerId, IEnumerable<int> npcIds, bool present)
    {
        lock (_gate)
        {
            var before = PresenceSetLocked();
            if (present) _viewerNpcs[viewerId] = new HashSet<int>(npcIds);
            else _viewerNpcs.Remove(viewerId);
            var after = PresenceSetLocked();
            before.SymmetricExceptWith(after);
            foreach (var changed in before) Touch(changed);
        }
    }

    public long LatestSpeechSequence
    {
        get { lock (_gate) return _speechSequence; }
    }

    public AgentStateFrame[] StatesFor(IEnumerable<int> npcIds)
    {
        lock (_gate)
        {
            SweepLocked(null, int.MaxValue);
            var result = new List<AgentStateFrame>();
            foreach (var npcId in npcIds.Distinct().OrderBy(x => x))
            {
                var revision = _stateRevisionByNpc.TryGetValue(npcId, out var value) ? value : 0;
                if (_attachmentByNpc.TryGetValue(npcId, out var id) && _byId.TryGetValue(id, out var attachment))
                {
                    var snapshot = Snapshot(attachment);
                    result.Add(snapshot.ToWire(revision));
                }
                else
                {
                    result.Add(new AgentStateFrame { Revision = revision, NpcId = npcId });
                }
            }
            return result.ToArray();
        }
    }

    public CommittedAgentUtterance[] UtterancesAfter(long sequence, ISet<int> npcIds)
    {
        lock (_gate)
        {
            return _utterances.Where(x => x.Sequence > sequence && npcIds.Contains(x.NpcId)).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var attachment in _byId.Values) attachment.Pending?.Bytes.Dispose();
            foreach (var npcId in _attachmentByNpc.Keys.ToArray()) Touch(npcId);
            _byId.Clear();
            _attachmentByNpc.Clear();
            _utterances.Clear();
            _viewerNpcs.Clear();
        }
    }

    public void Sweep(WorldHost? host, int worldGeneration)
    {
        lock (_gate) SweepLocked(host, worldGeneration);
    }

    private void SweepLocked(WorldHost? host, int worldGeneration)
    {
        var now = _now();
        var stale = _byId.Values.Where(x =>
            (worldGeneration != int.MaxValue && x.WorldGeneration != worldGeneration) ||
            (now - x.LastSeen).TotalSeconds >= x.TtlSeconds ||
            (host != null && !host.Read(world =>
                world.Entities.Npcs.TryGetValue(new EntityId(x.NpcId), out var npc) &&
                npc.Health > 0f && !npc.IsDying))).ToArray();
        foreach (var attachment in stale) RemoveLocked(attachment);

        foreach (var attachment in _byId.Values)
        {
            var pending = attachment.Pending;
            if (pending != null && (now - pending.StartedUtc).TotalSeconds >= 30)
            {
                pending.Bytes.Dispose();
                attachment.Pending = null;
            }
        }
    }

    private bool TryOwned(string id, string owner, int worldGeneration,
        out Attachment attachment, out string reason)
    {
        if (!_byId.TryGetValue(id, out attachment!) ||
            attachment.WorldGeneration != worldGeneration)
        {
            reason = "AttachmentMissing";
            return false;
        }
        if (!string.Equals(attachment.Owner, owner, StringComparison.Ordinal))
        {
            reason = "NotAttachmentOwner";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private void RemoveLocked(Attachment attachment)
    {
        attachment.Pending?.Bytes.Dispose();
        _utterances.RemoveAll(item => item.NpcId == attachment.NpcId);
        _byId.Remove(attachment.Id);
        _attachmentByNpc.Remove(attachment.NpcId);
        Touch(attachment.NpcId);
    }

    private AgentAttachmentSnapshot Snapshot(Attachment attachment) => new(
        attachment.Id, attachment.NpcId, attachment.DisplayName, attachment.Capabilities,
        attachment.Phase, attachment.IntentSummary, attachment.RelationView,
        attachment.JournalEntry, IsPlayerPresentLocked(attachment.NpcId),
        attachment.TtlSeconds);

    private bool IsPlayerPresentLocked(int npcId) => _viewerNpcs.Values.Any(set => set.Contains(npcId));

    private HashSet<int> PresenceSetLocked()
    {
        var result = new HashSet<int>();
        foreach (var set in _viewerNpcs.Values) result.UnionWith(set);
        return result;
    }

    private void Touch(int npcId)
    {
        _revision++;
        _stateRevisionByNpc[npcId] = _revision;
    }

    private static void Remember(HashSet<string> set, Queue<string> order,
        string value, int capacity)
    {
        set.Add(value);
        order.Enqueue(value);
        while (order.Count > capacity && order.TryDequeue(out var old)) set.Remove(old);
    }

    private static bool ValidSha(string value) =>
        value is { Length: 64 } && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

public readonly struct AgentAttachmentSnapshot
{
    public AgentAttachmentSnapshot(string attachmentId, int npcId, string displayName,
        AgentCapabilities capabilities, AgentPhase phase, string intentSummary,
        string relationView, string journalEntry, bool playerPresent, int ttlSeconds)
    {
        AttachmentId = attachmentId;
        NpcId = npcId;
        DisplayName = displayName;
        Capabilities = capabilities;
        Phase = phase;
        IntentSummary = intentSummary;
        RelationView = relationView;
        JournalEntry = journalEntry;
        PlayerPresent = playerPresent;
        TtlSeconds = ttlSeconds;
    }

    public string AttachmentId { get; }
    public int NpcId { get; }
    public string DisplayName { get; }
    public AgentCapabilities Capabilities { get; }
    public AgentPhase Phase { get; }
    public string IntentSummary { get; }
    public string RelationView { get; }
    public string JournalEntry { get; }
    public bool PlayerPresent { get; }
    public int TtlSeconds { get; }

    public AgentStateFrame ToWire(long revision) => new()
    {
        Revision = revision,
        NpcId = NpcId,
        Attached = true,
        AttachmentId = AttachmentId,
        DisplayName = DisplayName,
        Capabilities = Capabilities,
        Phase = Phase,
        IntentSummary = IntentSummary,
        RelationView = RelationView,
        JournalEntry = JournalEntry,
        PlayerPresent = PlayerPresent,
    };
}

public readonly struct AgentInboxMessage
{
    public AgentInboxMessage(long sequence, string messageId, string language,
        string text, DateTimeOffset createdUtc)
    {
        Sequence = sequence;
        MessageId = messageId;
        Language = language;
        Text = text;
        CreatedUtc = createdUtc;
    }
    public long Sequence { get; }
    public string MessageId { get; }
    public string Language { get; }
    public string Text { get; }
    public DateTimeOffset CreatedUtc { get; }
}

public readonly struct AgentInboxRead
{
    public AgentInboxRead(AgentInboxMessage[] messages, long watermark, bool gap, bool truncated)
    {
        Messages = messages;
        Watermark = watermark;
        Gap = gap;
        Truncated = truncated;
    }
    public AgentInboxMessage[] Messages { get; }
    public long Watermark { get; }
    public bool Gap { get; }
    public bool Truncated { get; }
}

public sealed class AgentUtteranceMetadata
{
    public string UtteranceId { get; init; } = string.Empty;
    public string TurnId { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Emotion { get; init; } = string.Empty;
    public AgentSpeechDelivery Delivery { get; init; }
    public AgentSpeechPriority Priority { get; init; }
    public int TotalBytes { get; init; }
    public int DurationMilliseconds { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class CommittedAgentUtterance
{
    public CommittedAgentUtterance(long sequence, int npcId,
        AgentUtteranceMetadata metadata, byte[] bytes)
    {
        Sequence = sequence;
        NpcId = npcId;
        Metadata = metadata;
        Bytes = bytes;
    }
    public long Sequence { get; }
    public int NpcId { get; }
    public AgentUtteranceMetadata Metadata { get; }
    public byte[] Bytes { get; }
}

internal static class WavePcm16Mono44100
{
    public static bool TryValidate(byte[] wav, out int durationMilliseconds, out string reason)
    {
        durationMilliseconds = 0;
        reason = string.Empty;
        if (wav.Length < 44 || wav.Length > AgentWire.MaxAudioBytes ||
            !Ascii(wav, 0, "RIFF") || !Ascii(wav, 8, "WAVE") ||
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)) != wav.Length - 8)
        {
            reason = "InvalidWave";
            return false;
        }

        ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
        uint sampleRate = 0, byteRate = 0;
        var fmtSeen = false;
        var dataBytes = -1;
        for (var offset = 12; offset < wav.Length;)
        {
            if (offset + 8 > wav.Length) { reason = "InvalidWaveChunk"; return false; }
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            if (size < 0 || offset + 8L + size + (size & 1) > wav.Length)
            {
                reason = "InvalidWaveChunk";
                return false;
            }
            if (Ascii(wav, offset, "fmt "))
            {
                if (fmtSeen || size < 16) { reason = "InvalidWaveFormat"; return false; }
                fmtSeen = true;
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 8, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 10, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 12, 4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 16, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 20, 2));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(offset + 22, 2));
            }
            else if (Ascii(wav, offset, "data"))
            {
                if (dataBytes >= 0) { reason = "DuplicateWaveData"; return false; }
                dataBytes = size;
            }
            offset += 8 + size + (size & 1);
        }

        if (format != 1 || channels != 1 || sampleRate != 44100 || byteRate != 88200 ||
            blockAlign != 2 || bits != 16 || dataBytes < 2 || (dataBytes & 1) != 0)
        {
            reason = "UnsupportedWaveFormat";
            return false;
        }
        durationMilliseconds = (int)Math.Round(dataBytes * 1000d / (44100d * 2d));
        if (durationMilliseconds < 0 || durationMilliseconds > 30000)
        {
            reason = "WaveTooLong";
            return false;
        }
        return true;
    }

    private static bool Ascii(byte[] bytes, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > bytes.Length) return false;
        for (var i = 0; i < value.Length; i++) if (bytes[offset + i] != value[i]) return false;
        return true;
    }
}

}
