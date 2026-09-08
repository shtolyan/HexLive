using System.Security.Cryptography;

namespace HexLive.AgentCore.Studio;

/// <summary>Versioned authenticated encryption for local state and password-protected exports.</summary>
public static class ProtectedPayload
{
    private static ReadOnlySpan<byte> Magic => "HAST01"u8;
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    private const int HeaderSize = 6 + 16 + 12 + 16;
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key)
    {
        if (plaintext.Length > MaxPayloadBytes || key.Length != 32) throw new InvalidDataException("InvalidProtectedPayload");
        var result = new byte[HeaderSize + plaintext.Length];
        Magic.CopyTo(result);
        RandomNumberGenerator.Fill(result.AsSpan(6, 16));
        RandomNumberGenerator.Fill(result.AsSpan(22, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(22, 12), plaintext, result.AsSpan(HeaderSize), result.AsSpan(34, 16), result.AsSpan(0, 22));
        return result;
    }
    public static byte[] Decrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key)
    {
        Validate(payload);
        if (key.Length != 32) throw new InvalidDataException("InvalidProtectedKey");
        var clear = new byte[payload.Length - HeaderSize];
        using var aes = new AesGcm(key, 16);
        try { aes.Decrypt(payload.Slice(22, 12), payload[HeaderSize..], payload.Slice(34, 16), clear, payload[..22]); }
        catch { CryptographicOperations.ZeroMemory(clear); throw; }
        return clear;
    }
    public static byte[] Export(ReadOnlySpan<byte> plaintext, string password)
    {
        if (password.Length < 12) throw new InvalidDataException("ExportPasswordTooShort");
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            var inner = Encrypt(plaintext, key);
            var result = new byte[16 + inner.Length];
            salt.CopyTo(result, 0); inner.CopyTo(result, 16);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Import(ReadOnlySpan<byte> payload, string password)
    {
        if (payload.Length < 16 + HeaderSize || payload.Length > MaxPayloadBytes + HeaderSize + 16)
            throw new InvalidDataException("InvalidProtectedExport");
        Validate(payload[16..]);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, payload[..16], 600_000, HashAlgorithmName.SHA256, 32);
        try { return Decrypt(payload[16..], key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static void Validate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize || payload.Length > HeaderSize + MaxPayloadBytes || !payload[..6].SequenceEqual(Magic))
            throw new InvalidDataException("InvalidProtectedPayload");
    }
}
