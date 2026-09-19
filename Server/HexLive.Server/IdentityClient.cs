using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HexLive.Server;

/// <summary>§166: authentication fails closed; never fall back to a shared token.</summary>
public sealed class IdentityClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _validate;
    public sealed record Identity(string AccountId, string[] Permissions, long Revision, string SubjectType = "player");

    public IdentityClient(string url, HttpMessageHandler? handler = null)
    {
        var root = new Uri(url, UriKind.Absolute);
        if (root.Scheme != "https" || !string.IsNullOrEmpty(root.UserInfo) ||
            !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new ArgumentException("Identity authority must be an HTTPS URL");
        _validate = new Uri(root, "/api/identity/v1/validate");
        _http = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    // null = invalid key; transport/service failures propagate as unavailable.
    public async Task<Identity?> AuthenticateAsync(string key, CancellationToken cancellation = default)
    {
        if (key.Length != 72 || !key.StartsWith("hexlive_", StringComparison.Ordinal)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, _validate);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        var identity = await response.Content.ReadFromJsonAsync<Identity>(cancellation);
        if (identity == null || !Guid.TryParseExact(identity.AccountId, "N", out var id))
            throw new HttpRequestException("Invalid authority response");
        return identity with { AccountId = id.ToString("N"), Permissions = identity.Permissions ?? Array.Empty<string>() };
    }

    public async Task<string?> ResolveAsync(string key, CancellationToken cancellation = default)
    {
        var account = await AuthenticateAsync(key, cancellation);
        return account != null && Array.IndexOf(account.Permissions, "game.play") >= 0 ? account.AccountId : null;
    }
    public bool Can(string key, string accountId, string permission)
    {
        try { var account = AuthenticateAsync(key).GetAwaiter().GetResult(); return account?.AccountId == accountId && Array.IndexOf(account.Permissions, permission) >= 0; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { return false; }
    }

    public async Task MonitorAsync(string key, string player, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), session.Token);
                if (await ResolveAsync(key, session.Token) != player) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
        finally { await session.CancelAsync(); }
    }

    public void Dispose() => _http.Dispose();
}
