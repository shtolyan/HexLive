using System;
using System.IO;
using System.Text;

namespace HexLive.Simulation.Wire
{
/// <summary>§161 bounded JSON envelope; both endpoints share strict UTF-8 and frame limits.</summary>
public static class AdminWire
{
    public const int MaxBytes = 16384;
    public static byte[] Encode(string json, bool reply = false)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Admin frame too large");
        var result = new byte[bytes.Length + 1]; result[0] = (byte)(reply ? FrameKind.AdminResult : FrameKind.AdminInput);
        Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length); return result;
    }
    public static string Decode(byte[] payload)
    {
        if (payload.Length > MaxBytes) throw new InvalidDataException("Admin frame too large");
        try { return new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid admin UTF-8", e); }
    }
}
}
