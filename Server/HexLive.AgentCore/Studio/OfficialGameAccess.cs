using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace HexLive.AgentCore.Studio;

/// <summary>Closed-test desktop access. A single OS-protected key, no client-supplied identity.</summary>
public static class OfficialGameAccess
{
    public const string CredentialId = "game.unified-access";
    public static readonly Uri Authority = new("https://keys.62-146-235-120.sslip.io/api/identity/v1/validate");
    public static readonly Uri Singapore = new("https://vmi3529459.contaboserver.net/mcp");
    public static ServerProfile SingaporeProfile(ServerProfile? existing = null) => new(
        existing?.Id ?? Guid.Parse("87fc5158-1aec-40dc-8295-673a6bb1df8a"), "Singapore", Singapore, CredentialId);
    public static void RequireOfficial(ServerProfile server)
    {
        if (server.CredentialId != CredentialId || server.McpEndpoint != Singapore || server.PlayerClientId != null)
            throw new InvalidDataException("OfficialServerRequired");
    }
    // Preserve local memories and non-official configurations, but expose only this release's servers.
    public static StudioConfiguration Migrate(StudioConfiguration value)
    {
        var previous = value.Servers.FirstOrDefault(s => s.McpEndpoint == Singapore);
        var official = SingaporeProfile(previous);
        return value with { Servers = value.Servers.Where(s => s.Id != official.Id).Append(official).ToArray() };
    }
    public static async Task RequireCatalogAsync(HexLive.AgentHost.McpClient mcp, CancellationToken token)
    {
        var names = await mcp.ReadToolNamesAsync(token);
        if (!names.Contains("world_status") || !names.Contains("list_colonists"))
            throw new InvalidDataException("ServerApiIncompatible");
    }
    public sealed record Account(string AccountId, string[] Permissions);
    public static async Task<Account> ValidateAsync(string key, CancellationToken token, HttpMessageHandler? handler = null)
    {
        key = key.Trim();
        if (key.Length != 72 || !key.StartsWith("hexlive_", StringComparison.Ordinal) || key.Any(char.IsControl))
            throw new InvalidDataException("InvalidAccessKey");
        using var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, handler == null)
            { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 65536 };
        using var request = new HttpRequestMessage(HttpMethod.Post, Authority);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidDataException("InvalidAccessKey");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("IdentityUnavailable", null, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<Account>(cancellationToken: token);
        if (account == null || !Guid.TryParseExact(account.AccountId, "N", out _) || account.Permissions == null)
            throw new InvalidDataException("IdentityUnavailable");
        if (!account.Permissions.Contains("game.play")) throw new InvalidDataException("GamePermissionRequired");
        return account;
    }
}
