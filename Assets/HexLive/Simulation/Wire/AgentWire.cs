using System;
using System.IO;
using System.Text;

namespace HexLive.Simulation.Wire
{

/// <summary>§160 capability bits advertised by one MCP attachment.</summary>
[Flags]
public enum AgentCapabilities : uint
{
    None = 0,
    PlayerText = 1 << 0,
    Speech = 1 << 1,
    WorldActions = 1 << 2,
    RelationView = 1 << 3,
    Journal = 1 << 4,
}

public enum AgentPhase : byte
{
    Ready = 0,
    Thinking = 1,
    Acting = 2,
    Speaking = 3,
    Sleeping = 4,
    Error = 5,
}

public enum AgentSpeechDelivery : byte
{
    PlayerReply = 0,
    World = 1,
}

public enum AgentSpeechPriority : byte
{
    Talk = 0,
    Ambient = 1,
}

public sealed class AgentStateFrame
{
    public long Revision { get; set; }
    public int NpcId { get; set; }
    public bool Attached { get; set; }
    public string AttachmentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AgentCapabilities Capabilities { get; set; }
    public AgentPhase Phase { get; set; }
    public string IntentSummary { get; set; } = string.Empty;
    public string RelationView { get; set; } = string.Empty;
    public string JournalEntry { get; set; } = string.Empty;
    public bool PlayerPresent { get; set; }
}

public readonly struct SttTokenResultFrame
{
    public SttTokenResultFrame(int correlationId, bool accepted, string token,
        long expiresUtcMilliseconds, string reason)
    {
        CorrelationId = correlationId;
        Accepted = accepted;
        Token = token ?? string.Empty;
        ExpiresUtcMilliseconds = expiresUtcMilliseconds;
        Reason = reason ?? string.Empty;
    }

    public int CorrelationId { get; }
    public bool Accepted { get; }
    public string Token { get; }
    public long ExpiresUtcMilliseconds { get; }
    public string Reason { get; }
}

public readonly struct AgentTextInputFrame
{
    public string ExpectedAttachmentId { get; }
    public AgentTextInputFrame(int correlationId, int npcId, string messageId,
        string language, string text, string expectedAttachmentId)
    {
        ExpectedAttachmentId = expectedAttachmentId ?? string.Empty;
        CorrelationId = correlationId;
        NpcId = npcId;
        MessageId = messageId ?? string.Empty;
        Language = language ?? string.Empty;
        Text = text ?? string.Empty;
    }

    public int CorrelationId { get; }
    public int NpcId { get; }
    public string MessageId { get; }
    public string Language { get; }
    public string Text { get; }
}

public readonly struct AgentTextResultFrame
{
    public AgentTextResultFrame(int correlationId, bool accepted, string messageId, string reason)
    {
        CorrelationId = correlationId;
        Accepted = accepted;
        MessageId = messageId ?? string.Empty;
        Reason = reason ?? string.Empty;
    }

    public int CorrelationId { get; }
    public bool Accepted { get; }
    public string MessageId { get; }
    public string Reason { get; }
}

public sealed class AgentSpeechBeginFrame
{
    public long Sequence { get; set; }
    public int NpcId { get; set; }
    public string UtteranceId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Emotion { get; set; } = string.Empty;
    public AgentSpeechDelivery Delivery { get; set; }
    public AgentSpeechPriority Priority { get; set; }
    public int TotalBytes { get; set; }
    public int DurationMilliseconds { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public readonly struct AgentSpeechChunkFrame
{
    public AgentSpeechChunkFrame(string utteranceId, int index, byte[] bytes)
    {
        UtteranceId = utteranceId ?? string.Empty;
        Index = index;
        Bytes = bytes ?? Array.Empty<byte>();
    }

    public string UtteranceId { get; }
    public int Index { get; }
    public byte[] Bytes { get; }
}

public readonly struct AgentSpeechEndFrame
{
    public AgentSpeechEndFrame(string utteranceId, string sha256)
    {
        UtteranceId = utteranceId ?? string.Empty;
        Sha256 = sha256 ?? string.Empty;
    }

    public string UtteranceId { get; }
    public string Sha256 { get; }
}

/// <summary>Strict append-only wire codecs for §160 frame kinds 14..21.</summary>
public static class AgentWire
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    public const int MaxTextCharacters = 240;
    public const int MaxPlayerTextCharacters = 4096;
    public const int MaxRelationCharacters = 1024;
    public const int MaxSpeechCharacters = 600;
    public const int MaxSpeechDurationMs = 60000;
    public const int MaxJournalCharacters = 400;
    public const int MaxAudioBytes = 6 * 1024 * 1024;
    public const int MaxChunkBytes = 192 * 1024;

    public static byte[] AgentState(AgentStateFrame value) => Encode(FrameKind.AgentState, w =>
    {
        w.Write(value.Revision);
        w.Write(value.NpcId);
        w.Write(value.Attached);
        WriteBounded(w, value.AttachmentId, 80);
        WriteBounded(w, value.DisplayName, 48);
        w.Write((uint)value.Capabilities);
        w.Write((byte)value.Phase);
        WriteBounded(w, value.IntentSummary, MaxTextCharacters);
        WriteBounded(w, value.RelationView, MaxRelationCharacters);
        WriteBounded(w, value.JournalEntry, MaxJournalCharacters);
        w.Write(value.PlayerPresent);
    });

    public static AgentStateFrame ReadAgentState(byte[] payload) => Decode(payload, r =>
        new AgentStateFrame
        {
            Revision = r.ReadInt64(),
            NpcId = r.ReadInt32(),
            Attached = r.ReadBoolean(),
            AttachmentId = ReadBounded(r, 80),
            DisplayName = ReadBounded(r, 48),
            Capabilities = (AgentCapabilities)r.ReadUInt32(),
            Phase = ReadEnum<AgentPhase>(r.ReadByte()),
            IntentSummary = ReadBounded(r, MaxTextCharacters),
            RelationView = ReadBounded(r, MaxRelationCharacters),
            JournalEntry = ReadBounded(r, MaxJournalCharacters),
            PlayerPresent = r.ReadBoolean(),
        });

    public static byte[] SttTokenRequest(int correlationId) =>
        Encode(FrameKind.SttTokenRequest, w => w.Write(correlationId));

    public static int ReadSttTokenRequest(byte[] payload) =>
        Decode(payload, r => r.ReadInt32());

    public static byte[] SttTokenResult(int correlationId, bool accepted, string token,
        long expiresUtcMilliseconds, string reason) => Encode(FrameKind.SttTokenResult, w =>
    {
        w.Write(correlationId);
        w.Write(accepted);
        WriteBounded(w, token, 8192);
        w.Write(expiresUtcMilliseconds);
        WriteBounded(w, reason, 128);
    });

    public static SttTokenResultFrame ReadSttTokenResult(byte[] payload) => Decode(payload, r =>
        new SttTokenResultFrame(r.ReadInt32(), r.ReadBoolean(), ReadBounded(r, 8192),
            r.ReadInt64(), ReadBounded(r, 128)));

    public static byte[] AgentTextInput(int correlationId, int npcId, string messageId,
        string language, string text, string expectedAttachmentId) => Encode(FrameKind.AgentTextInput, w =>
    {
        w.Write(correlationId);
        w.Write(npcId);
        WriteBounded(w, messageId, 80);
        WriteBounded(w, language, 16);
        WriteBounded(w, text, MaxPlayerTextCharacters);
        WriteBounded(w, expectedAttachmentId, 80);
    });

    public static AgentTextInputFrame ReadAgentTextInput(byte[] payload) => Decode(payload, r =>
        new AgentTextInputFrame(r.ReadInt32(), r.ReadInt32(), ReadBounded(r, 80),
            ReadBounded(r, 16), ReadBounded(r, MaxPlayerTextCharacters), ReadBounded(r, 80)));

    public static byte[] AgentTextResult(int correlationId, bool accepted,
        string messageId, string reason) => Encode(FrameKind.AgentTextResult, w =>
    {
        w.Write(correlationId);
        w.Write(accepted);
        WriteBounded(w, messageId, 80);
        WriteBounded(w, reason, 128);
    });

    public static AgentTextResultFrame ReadAgentTextResult(byte[] payload) => Decode(payload, r =>
        new AgentTextResultFrame(r.ReadInt32(), r.ReadBoolean(), ReadBounded(r, 80),
            ReadBounded(r, 128)));

    public static byte[] AgentSpeechBegin(AgentSpeechBeginFrame value) =>
        Encode(FrameKind.AgentSpeechBegin, w =>
        {
            w.Write(value.Sequence);
            w.Write(value.NpcId);
            WriteBounded(w, value.UtteranceId, 80);
            WriteBounded(w, value.TurnId, 80);
            WriteBounded(w, value.Language, 16);
            WriteBounded(w, value.Text, MaxSpeechCharacters);
            WriteBounded(w, value.Emotion, 32);
            w.Write((byte)value.Delivery);
            w.Write((byte)value.Priority);
            w.Write(value.TotalBytes);
            w.Write(value.DurationMilliseconds);
            WriteBounded(w, value.Sha256, 64);
        });

    public static AgentSpeechBeginFrame ReadAgentSpeechBegin(byte[] payload) => Decode(payload, r =>
        new AgentSpeechBeginFrame
        {
            Sequence = r.ReadInt64(),
            NpcId = r.ReadInt32(),
            UtteranceId = ReadBounded(r, 80),
            TurnId = ReadBounded(r, 80),
            Language = ReadBounded(r, 16),
            Text = ReadBounded(r, MaxSpeechCharacters),
            Emotion = ReadBounded(r, 32),
            Delivery = ReadEnum<AgentSpeechDelivery>(r.ReadByte()),
            Priority = ReadEnum<AgentSpeechPriority>(r.ReadByte()),
            TotalBytes = ReadRange(r.ReadInt32(), 0, MaxAudioBytes, "audio size"),
            DurationMilliseconds = ReadRange(r.ReadInt32(), 0, MaxSpeechDurationMs, "audio duration"),
            Sha256 = ReadBounded(r, 64),
        });

    public static byte[] AgentSpeechChunk(string utteranceId, int index, byte[] bytes) =>
        Encode(FrameKind.AgentSpeechChunk, w =>
        {
            if (bytes == null || bytes.Length > MaxChunkBytes)
                throw new InvalidDataException("Agent speech chunk is too large.");
            WriteBounded(w, utteranceId, 80);
            w.Write(index);
            w.Write(bytes.Length);
            w.Write(bytes);
        });

    public static AgentSpeechChunkFrame ReadAgentSpeechChunk(byte[] payload) => Decode(payload, r =>
    {
        var id = ReadBounded(r, 80);
        var index = ReadRange(r.ReadInt32(), 0, 16384, "chunk index");
        var count = ReadRange(r.ReadInt32(), 0, MaxChunkBytes, "chunk size");
        var bytes = r.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException("Agent speech chunk is truncated.");
        return new AgentSpeechChunkFrame(id, index, bytes);
    });

    public static byte[] AgentSpeechEnd(string utteranceId, string sha256) =>
        Encode(FrameKind.AgentSpeechEnd, w =>
        {
            WriteBounded(w, utteranceId, 80);
            WriteBounded(w, sha256, 64);
        });

    public static AgentSpeechEndFrame ReadAgentSpeechEnd(byte[] payload) => Decode(payload, r =>
        new AgentSpeechEndFrame(ReadBounded(r, 80), ReadBounded(r, 64)));

    private static byte[] Encode(FrameKind kind, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8);
        write(writer);
        writer.Flush();
        return Frame.Wrap(kind, stream.ToArray());
    }

    private static T Decode<T>(byte[] payload, Func<BinaryReader, T> read)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new BinaryReader(stream, StrictUtf8);
        var result = read(reader);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Agent frame contains trailing bytes.");
        return result;
    }

    private static void WriteBounded(BinaryWriter writer, string value, int maxCharacters)
    {
        value ??= string.Empty;
        if (value.Length > maxCharacters) throw new InvalidDataException("Agent text exceeds its limit.");
        writer.Write(value);
    }

    private static string ReadBounded(BinaryReader reader, int maxCharacters)
    {
        uint count = 0;
        for (var shift = 0; ; shift += 7)
        {
            var next = reader.ReadByte();
            if (shift == 28 && next > 7) throw new InvalidDataException("Invalid agent string size.");
            count |= (uint)(next & 127) << shift;
            if ((next & 128) == 0) break;
            if (shift >= 28) throw new InvalidDataException("Invalid agent string size.");
        }
        if (count > maxCharacters * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Agent string is truncated or exceeds its limit.");
        string value;
        try { value = StrictUtf8.GetString(reader.ReadBytes((int)count)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid agent UTF-8.", ex); }
        if (value.Length > maxCharacters) throw new InvalidDataException("Agent text exceeds its limit.");
        return value;
    }

    private static T ReadEnum<T>(byte raw) where T : struct
    {
        if (!Enum.IsDefined(typeof(T), raw)) throw new InvalidDataException("Invalid agent enum value.");
        return (T)Enum.ToObject(typeof(T), raw);
    }

    private static int ReadRange(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new InvalidDataException($"Invalid {name}.");
        return value;
    }
}

}
