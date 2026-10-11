using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HexLive.VoiceBrain;

/// <summary>
/// §171.7: the browser forwards an opaque ticket through ElevenLabs
/// (<c>customLlmExtraBody</c>) to the brain. Only this server can mint one, so a
/// client cannot speak AS another character by editing the request.
/// </summary>
public sealed record SessionTicket(string SessionId, string OwnerUserId, string CharacterId, string WorldId, int NpcId,
    long ExpiresUnix);

public sealed class SessionTickets(byte[] key, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    public string Mint(string owner, string characterId, string worldId, int npcId) =>
        Sign(new SessionTicket(Guid.NewGuid().ToString("N"), owner, characterId, worldId, npcId,
            _clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds()));

    public string Sign(SessionTicket ticket)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(ticket));
        return body + "." + Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
    }

    public bool TryVerify(string? token, out SessionTicket ticket)
    {
        ticket = null!;
        if (token is not { Length: > 0 and < 4096 }) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;
        var body = token[..dot];
        var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body));
        byte[] actual;
        try { actual = FromBase64Url(token[(dot + 1)..]); } catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;
        try { ticket = JsonSerializer.Deserialize<SessionTicket>(FromBase64Url(body))!; }
        catch (Exception ex) when (ex is JsonException or FormatException) { return false; }
        return ticket != null && ticket.ExpiresUnix > _clock.GetUtcNow().ToUnixTimeSeconds();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
