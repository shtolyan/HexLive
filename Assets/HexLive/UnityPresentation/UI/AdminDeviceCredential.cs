using System;
using System.Security.Cryptography;

namespace HexLive.UnityPresentation.UI
{
/// <summary>§161: separate device proof for the game admin bus, never the player login key.</summary>
public static class AdminDeviceCredential
{
    public static string ReadOrCreate(Func<string> read, Action<string> write)
    {
        var secret = read(); // storage denial must not rotate an existing identity
        if (!string.IsNullOrEmpty(secret))
        {
            if (secret.Length != 64) throw new InvalidOperationException("Invalid admin device credential");
            foreach (var c in secret)
                if (!Uri.IsHexDigit(c)) throw new InvalidOperationException("Invalid admin device credential");
            return secret;
        }
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        secret = BitConverter.ToString(bytes).Replace("-", "");
        write(secret); // no memory-only fallback when the protected store rejects a write
        return secret;
    }
}
}
