using System.Net;
using System.Text;
using System.Text.Json;
using HexLive.AgentCore.Studio;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class AdministratorConnectionTests
{
    private const string Credential = "hexmcp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ServerProfile Profile => new(Guid.NewGuid(), "Admin", new Uri("https://example.test/mcp"), "server.fixture");

    [Test]
    public async Task AlreadyReadCredentialDoesNotPromptStoreAgain()
    {
        var store = new Secrets(); using var http = new Handler();
        var roster = await new AgentServerConnection(store, http).ReadWithCredentialAsync(Profile, Credential, default);
        Assert.That(store.Reads, Is.Zero);
        Assert.That(store.Value, Is.EqualTo("original"));
        Assert.That(roster.Characters.Single().NpcId, Is.EqualTo(901));
        Assert.That(http.Tools, Is.EqualTo(new[] { "world_status", "list_colonists" }));
    }

    [Test]
    public async Task GameTokenCarriesExistingClientIdentityWithoutPairing()
    {
        var store = new Secrets(); using var http = new Handler();
        var id = Guid.NewGuid().ToString("N");
        await new AgentServerConnection(store, http).ImportServerTokenAsync(Profile with { PlayerClientId = id }, "fixture-game-token", default);
        Assert.That(http.Player, Is.EqualTo(id));
        Assert.That(http.Tools, Is.EqualTo(new[] { "world_status", "list_colonists" }));
        Assert.That(store.Value, Is.EqualTo("fixture-game-token"));
    }

    [TestCase("")]
    [TestCase("invalid")]
    public void MissingGameIdentityNeverCreatesAnotherPlayer(string value) =>
        Assert.Throws<InvalidDataException>(() => GameClientIdentity.Normalize(value));

    [Test]
    public async Task ChecksRosterBeforeSavingWithoutAttachingOrGenerating()
    {
        var store = new Secrets(); using var http = new Handler();
        await new AgentServerConnection(store, http).ImportAdministratorAsync(Profile, Credential + "\n", default);
        Assert.That(store.Value, Is.EqualTo(Credential));
        Assert.That(http.Tools, Is.EqualTo(new[] { "world_status", "list_colonists" }));
        Assert.That(http.Authorization, Is.EqualTo("Bearer " + Credential));
    }

    [Test]
    public void InvalidOrRejectedCredentialNeverReplacesStoredSecret()
    {
        var store = new Secrets(); using var http = new Handler { Reject = true };
        var connection = new AgentServerConnection(store, http);
        Assert.ThrowsAsync<InvalidDataException>(() => connection.ImportAdministratorAsync(Profile, "hexagent_player", default));
        Assert.That(http.Requests, Is.Zero);
        Assert.ThrowsAsync<HttpRequestException>(() => connection.ImportAdministratorAsync(Profile, Credential, default));
        Assert.That(store.Value, Is.EqualTo("original"));
    }

    [Test]
    public void UnsafeEndpointNeverReceivesCredential()
    {
        var store = new Secrets(); using var http = new Handler();
        Assert.ThrowsAsync<InvalidDataException>(() => new AgentServerConnection(store, http).ImportAdministratorAsync(
            Profile with { McpEndpoint = new Uri("http://example.test/mcp") }, Credential, default));
        Assert.That(http.Requests, Is.Zero);
    }

    private sealed class Secrets : ISecretStore
    {
        public string Value = "original";
        public int Reads;
        public Task<string?> ReadAsync(string id, CancellationToken token) { Reads++; return Task.FromResult<string?>(Value); }
        public Task WriteAsync(string id, string value, CancellationToken token) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(string id, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Reject; public int Requests; public string? Authorization; public string? Player;
        public List<string> Tools = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Authorization = request.Headers.Authorization?.ToString();
            Player = request.Headers.TryGetValues("X-HexLive-Client-Id", out var ids) ? ids.Single() : null;
            if (Reject) return new(HttpStatusCode.Unauthorized);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = json.RootElement;
            if (root.GetProperty("method").GetString() == "notifications/initialized") return new(HttpStatusCode.Accepted);
            object result = new { };
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                var name = root.GetProperty("params").GetProperty("name").GetString()!; Tools.Add(name);
                var payload = name == "world_status" ? "{\"worldId\":\"world\",\"paused\":true}" :
                    "{\"colonists\":[{\"npcId\":901,\"name\":\"Masha\",\"profileId\":\"masha\",\"health\":1}]}";
                result = new { content = new[] { new { type = "text", text = payload } } };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result }), Encoding.UTF8, "application/json") };
        }
    }
}
