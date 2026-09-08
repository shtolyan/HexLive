using System.Text;
using System.Text.Json;
using HexLive.AgentHost;

namespace HexLive.AgentCore.Studio;

public sealed record PairingTicket(string Id, string Code, string PollSecret, DateTimeOffset ExpiresUtc);
public sealed record AvailableCharacter(int NpcId, string Name, string ProfileId, bool Available)
{
    public override string ToString() => $"{Name} · NPC{NpcId}";
}
public sealed record ServerRoster(string WorldId, bool Paused, IReadOnlyList<AvailableCharacter> Characters);

public sealed class AgentServerConnection(ISecretStore secrets, HttpMessageHandler? handler = null)
{
    public async Task<ServerRoster> ReadAsync(ServerProfile server, CancellationToken token)
    {
        server.Validate();
        var credential = await secrets.ReadAsync(server.CredentialId, token) ?? throw new InvalidOperationException("MissingServerCredential");
        return await ReadWithCredentialAsync(server, credential, token);
    }

    // Validate access without attaching an NPC or invoking a model; persist only on success.
    public async Task ImportAdministratorAsync(ServerProfile server, string credential, CancellationToken token)
    {
        server.Validate();
        credential = credential.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(credential, @"\Ahexmcp_[a-f0-9]{48}\z"))
            throw new InvalidDataException("InvalidAdministratorCredential");
        await ReadWithCredentialAsync(server, credential, token);
        token.ThrowIfCancellationRequested();
        await secrets.WriteAsync(server.CredentialId, credential, token);
    }

    public async Task ImportServerTokenAsync(ServerProfile server, string credential, CancellationToken token)
    {
        server.Validate(); credential = credential.Trim();
        if (credential.Length is < 16 or > 256 || credential.Any(char.IsControl)) throw new InvalidDataException("InvalidServerToken");
        await ReadWithCredentialAsync(server, credential, token);
        token.ThrowIfCancellationRequested();
        await secrets.WriteAsync(server.CredentialId, credential, token);
    }

    private async Task<ServerRoster> ReadWithCredentialAsync(ServerProfile server, string credential, CancellationToken token)
    {
        using var mcp = new McpClient(new AgentProviderOptions { McpUri = server.McpEndpoint, McpToken = credential, PlayerClientId = server.PlayerClientId,
            XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "" }, handler);
        var world = await mcp.CallToolAsync("world_status", new { }, token);
        var roster = await mcp.CallToolAsync("list_colonists", new { }, token);
        return new(world.GetProperty("worldId").GetString()!, world.GetProperty("paused").GetBoolean(),
            roster.GetProperty("colonists").EnumerateArray().Select(n => new AvailableCharacter(
                n.GetProperty("npcId").GetInt32(), n.GetProperty("name").GetString()!,
                n.GetProperty("profileId").GetString()!, n.GetProperty("health").GetSingle() > 0)).ToArray());
    }

    public static async Task<PairingTicket> RequestPairingAsync(Uri endpoint, string displayName, CancellationToken token) =>
        (await PairingCall(endpoint, "request_agent_pairing", new { displayName }, token)).Deserialize<PairingTicket>()
        ?? throw new InvalidDataException("InvalidPairingTicket");

    public async Task CompletePairingAsync(ServerProfile server, PairingTicket ticket, CancellationToken token)
    {
        server.Validate();
        while (DateTimeOffset.UtcNow < ticket.ExpiresUtc)
        {
            var result = await PairingCall(server.McpEndpoint, "poll_agent_pairing", new { pairingId = ticket.Id, pollSecret = ticket.PollSecret }, token);
            var state = result.GetProperty("State").GetString();
            if (state == "Approved")
            {
                var credential = result.GetProperty("Credential").GetString();
                if (credential == null || credential.Length != 73 || !credential.StartsWith("hexagent_", StringComparison.Ordinal))
                    throw new InvalidDataException("InvalidPairingCredential");
                await secrets.WriteAsync(server.CredentialId, credential, token);
                return;
            }
            if (state != "Pending") throw new InvalidOperationException("PairingExpired");
            await Task.Delay(1000, token);
        }
        throw new TimeoutException("PairingExpired");
    }


    private static async Task<JsonElement> PairingCall(Uri endpoint, string name, object arguments, CancellationToken token)
    {
        new ServerProfile(Guid.NewGuid(), "Pairing", endpoint, "pairing").Validate();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("PairingUnavailable", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream(); var buffer = new byte[1024]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (bytes.Length + read > 8192) throw new InvalidDataException("PairingResponseTooLarge"); bytes.Write(buffer, 0, read); }
        using var json = JsonDocument.Parse(bytes.ToArray());
        if (!json.RootElement.TryGetProperty("result", out var result)) throw new InvalidOperationException("PairingRejected");
        using var payload = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        return payload.RootElement.Clone();
    }
}
