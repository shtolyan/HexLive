using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Server
{

/// <summary>§160 server-side exchange of the master Deepgram key for 60s JWTs.</summary>
public sealed class DeepgramTokenBroker : IDisposable
{
    public const int TokenTtlSeconds = 60;
    public const int GrantsPerMinute = 4;
    private static readonly Uri GrantUri = new("https://api.deepgram.com/v1/auth/grant");

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _rateGate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _grants = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public DeepgramTokenBroker(string? apiKey = null, HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? now = null)
    {
        _apiKey = (apiKey ?? Environment.GetEnvironmentVariable("HEXLIVE_DEEPGRAM_API_KEY") ??
            string.Empty).Trim();
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool Available => _apiKey.Length > 0;

    public async Task<DeepgramGrantResult> GrantAsync(string controlOwner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(controlOwner)) return DeepgramGrantResult.Refused("Unauthorized");
        if (!Available) return DeepgramGrantResult.Refused("SttUnavailable");
        if (!TakeRateSlot(controlOwner)) return DeepgramGrantResult.Refused("RateLimited");

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GrantUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _apiKey);
            request.Content = new StringContent("{\"ttl_seconds\":60}", Encoding.UTF8,
                "application/json");
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return DeepgramGrantResult.Refused("ProviderUnavailable");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (!root.TryGetProperty("access_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
                return DeepgramGrantResult.Refused("ProviderResponseInvalid");

            var token = tokenElement.GetString() ?? string.Empty;
            var expires = root.TryGetProperty("expires_in", out var expiresElement) &&
                          expiresElement.TryGetDouble(out var seconds)
                ? Math.Clamp(seconds, 1, TokenTtlSeconds)
                : TokenTtlSeconds;
            if (token.Length == 0) return DeepgramGrantResult.Refused("ProviderResponseInvalid");
            return DeepgramGrantResult.Granted(token, _now().AddSeconds(expires));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return DeepgramGrantResult.Refused("ProviderUnavailable");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private bool TakeRateSlot(string owner)
    {
        lock (_rateGate)
        {
            if (!_grants.TryGetValue(owner, out var queue))
                _grants[owner] = queue = new Queue<DateTimeOffset>();
            var now = _now();
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1))
                queue.Dequeue();
            if (queue.Count >= GrantsPerMinute) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
    }
}

public readonly struct DeepgramGrantResult
{
    private DeepgramGrantResult(bool accepted, string token,
        DateTimeOffset expiresUtc, string reason)
    {
        Accepted = accepted;
        Token = token;
        ExpiresUtc = expiresUtc;
        Reason = reason;
    }
    public bool Accepted { get; }
    public string Token { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string Reason { get; }
    public static DeepgramGrantResult Granted(string token, DateTimeOffset expiresUtc) =>
        new(true, token, expiresUtc, string.Empty);
    public static DeepgramGrantResult Refused(string reason) =>
        new(false, string.Empty, default, reason);
}

}
