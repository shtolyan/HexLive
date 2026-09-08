using System;
using System.IO;
using System.Text;

namespace HexLive.Simulation.Wire
{
    /// <summary>§163 optional /watch extension, sent only on explicit pairing requests.</summary>
    public static class AgentPairingWire
    {
        public const int MaxBytes = 512;
        public static byte[] Encode(string requestId, string code, bool approve, bool reply = false)
        {
            if (requestId == null || requestId.Length > 64 || code == null || code.Length > 128)
                throw new InvalidDataException("Invalid pairing frame");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true));
            writer.Write((byte)(reply ? FrameKind.AgentPairingResult : FrameKind.AgentPairingInput));
            writer.Write(requestId); writer.Write(code); writer.Write(approve);
            var bytes = stream.ToArray();
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Pairing frame too large");
            return bytes;
        }
        public static (string Id, string Text, bool Approved) Decode(byte[] payload)
        {
            if (payload.Length > MaxBytes) throw new InvalidDataException("Pairing frame too large");
            try
            {
                using var stream = new MemoryStream(payload);
                using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
                var id = reader.ReadString(); var text = reader.ReadString(); var approved = reader.ReadByte();
                if (id.Length > 64 || text.Length > 128 || approved > 1 || stream.Position != stream.Length)
                    throw new InvalidDataException("Invalid pairing frame");
                return (id, text, approved == 1);
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is DecoderFallbackException || ex is FormatException)
            { throw new InvalidDataException("Invalid pairing frame", ex); }
        }
    }
}
