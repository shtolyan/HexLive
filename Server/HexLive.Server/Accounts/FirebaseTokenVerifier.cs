using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Server.Accounts;

public sealed record FirebaseIdentity(string Uid, string Provider);
public sealed record FirebaseVerification(FirebaseIdentity? Identity, string Error = "invalid_credentials");

/// <summary>§171.9: Firebase ID tokens only; no SDK, private keys or token logging.</summary>
/// <param name="requireVerifiedEmail">§171.9: off by default (Толя 11.10) — Firebase mail lands in spam and a
/// mangled link left new players locked out; the verification email is still sent, just not required to play.</param>
/// <param name="emulator">§171.12: LOCAL TESTING ONLY — accept the Firebase Auth emulator's unsigned tokens.</param>
public sealed class FirebaseTokenVerifier(string projectId, HttpClient http, TimeProvider? clock = null,
    bool requireVerifiedEmail = false, bool emulator = false)
{
    public const string JwksUrl = "https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com";
    public const int MaxTokenLength = 16384;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Dictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _expires;
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset _retryAfter;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public async Task<FirebaseVerification> VerifyAsync(string? token, CancellationToken cancel = default)
    {
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return new(null);
            var parts = token.Split('.');
            if (parts.Length != 3) return new(null);
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var h = header.RootElement;
            var p = payload.RootElement;
            // Reject ambiguous duplicate members, unsupported critical extensions and algorithms.
            var unsignedEmulatorToken = emulator && String(h, "alg") == "none" && parts[2].Length == 0;
            if (!Unique(h) || !Unique(p) || h.TryGetProperty("crit", out _) ||
                (!unsignedEmulatorToken && (String(h, "alg") != "RS256" || String(h, "kid") is not { Length: > 0 and <= 256 })))
                return new(null);
            var kid = String(h, "kid") ?? "";
            if (String(p, "iss") != "https://securetoken.google.com/" + projectId ||
                String(p, "aud") != projectId || String(p, "sub") is not { Length: > 0 and <= 128 } uid ||
                string.IsNullOrWhiteSpace(uid)) return new(null);
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            var skew = (long)ClockSkew.TotalSeconds;
            if (!Integer(p, "exp", out var exp) || !Integer(p, "iat", out var iat) ||
                exp <= now - skew || iat > now + skew || iat < 0 || exp <= iat)
                return new(null);
            if (p.TryGetProperty("auth_time", out _) &&
                (!Integer(p, "auth_time", out var auth) || auth < 0 || auth > now + skew)) return new(null);
            if (!unsignedEmulatorToken)
            {
                var key = await GetKeyAsync(kid, cancel).ConfigureAwait(false);
                if (key == null) return new(null);
                using var rsa = RSA.Create();
                rsa.ImportParameters(key.Value);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return new(null);
            }
            if (!p.TryGetProperty("firebase", out var firebase) || !Unique(firebase)) return new(null);
            var provider = String(firebase, "sign_in_provider");
            if (provider is not ("google.com" or "apple.com" or "password")) return new(null);
            // This actionable error is returned ONLY after signature and every identity claim pass.
            if (requireVerifiedEmail && provider == "password" && (!p.TryGetProperty("email_verified", out var verified) ||
                                           verified.ValueKind != JsonValueKind.True))
                return new(null, "email_not_verified");
            return new(new FirebaseIdentity(uid, provider));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or
                                   InvalidOperationException or ArgumentException or HttpRequestException)
        { return new(null); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        { return new(null); }
    }

    private async Task<RSAParameters?> GetKeyAsync(string kid, CancellationToken cancel)
    {
        await _refresh.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (now < _expires && _keys.TryGetValue(kid, out var cached)) return cached;
            // Unknown kids may indicate rotation, but must not turn random JWTs into a Google DoS.
            if (now < _retryAfter || (now < _expires && now < _nextRefresh)) return null;
            _nextRefresh = now.AddSeconds(30);
            _retryAfter = now.AddSeconds(5);
            using var response = await http.GetAsync(JwksUrl, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false));
            var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (!Unique(item) || String(item, "kty") != "RSA" ||
                    String(item, "kid") is not { Length: > 0 } id ||
                    (String(item, "alg") is { } alg && alg != "RS256") ||
                    (String(item, "use") is { } use && use != "sig")) continue;
                var modulus = Decode(String(item, "n") ?? "");
                var exponent = Decode(String(item, "e") ?? "");
                if (modulus.Length < 256 || modulus.Length > 1024 || exponent.Length is < 1 or > 8) continue;
                keys.Add(id, new RSAParameters { Modulus = modulus, Exponent = exponent });
            }
            var cache = response.Headers.CacheControl;
            var ttl = cache?.MaxAge ?? TimeSpan.Zero;
            ttl -= response.Headers.Age ?? TimeSpan.Zero;
            if (cache?.NoStore == true || cache?.NoCache == true || ttl < TimeSpan.Zero) ttl = TimeSpan.Zero;
            _keys = keys;
            _expires = now.Add(ttl);
            _retryAfter = default;
            return keys.TryGetValue(kid, out var key) ? key : null;
        }
        catch (KeyNotFoundException) { return null; }
        finally { _refresh.Release(); }
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name)) return false;
        return true;
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool Integer(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out number);
    }

    private static byte[] Decode(string value)
    {
        if (value.Length == 0) throw new FormatException();
        foreach (var c in value)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new FormatException();
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
