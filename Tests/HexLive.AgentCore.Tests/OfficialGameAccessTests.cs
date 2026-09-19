using System.Net;
using System.Text;
using HexLive.AgentCore.Studio;
using HexLive.AgentHost;
using NUnit.Framework;

namespace HexLive.AgentCore.Tests;

public sealed class OfficialGameAccessTests
{
    private static readonly string Key = "hexlive_" + new string('a', 64);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(respond(request)); }
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    [Test]
    public async Task ValidKeyUsesAuthorityAndAccountRatherThanLocalPlayerId()
    {
        using var handler = new Handler(r => {
            Assert.That(r.RequestUri, Is.EqualTo(OfficialGameAccess.Authority));
            Assert.That(r.Headers.Authorization?.Parameter, Is.EqualTo(Key));
            Assert.That(r.Headers.Contains("X-HexLive-Client-Id"), Is.False);
            return Json("{\"accountId\":\"31abe676e0d04aea991796ab15ed64b5\",\"permissions\":[\"game.play\"]}");
        });
        var account = await OfficialGameAccess.ValidateAsync("  " + Key + "  ", default, handler);
        Assert.That(account.AccountId, Is.EqualTo("31abe676e0d04aea991796ab15ed64b5"));
    }
    [Test]
    public void InvalidKeyDoesNotReachNetwork()
    {
        using var handler = new Handler(_ => throw new AssertionException("Network must not be called"));
        Assert.That(Assert.ThrowsAsync<InvalidDataException>(() => OfficialGameAccess.ValidateAsync("wrong", default, handler))!.Message,
            Is.EqualTo("InvalidAccessKey"));
    }
    [Test]
    public void RevokedKeyExplainsDenial()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Unauthorized));
        Assert.That(Assert.ThrowsAsync<InvalidDataException>(() => OfficialGameAccess.ValidateAsync(Key, default, handler))!.Message,
            Is.EqualTo("InvalidAccessKey"));
    }
    [Test]
    public void ValidKeyWithoutGamePermissionIsRejected()
    {
        using var handler = new Handler(_ => Json("{\"accountId\":\"31abe676e0d04aea991796ab15ed64b5\",\"permissions\":[\"bugs.create\"]}"));
        Assert.That(Assert.ThrowsAsync<InvalidDataException>(() => OfficialGameAccess.ValidateAsync(Key, default, handler))!.Message,
            Is.EqualTo("GamePermissionRequired"));
    }
    [Test]
    public void RedirectIsNotTreatedAsSuccessfulValidation()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Redirect));
        Assert.ThrowsAsync<HttpRequestException>(() => OfficialGameAccess.ValidateAsync(Key, default, handler));
    }
    [Test]
    public void MigrationPreservesProfilesAndUsesUnifiedCredentialWithoutClientId()
    {
        var old = new ServerProfile(Guid.NewGuid(), "Old", OfficialGameAccess.Singapore, "old-key") { PlayerClientId = Guid.NewGuid().ToString("N") };
        var custom = new ServerProfile(Guid.NewGuid(), "Custom", new Uri("https://example.com/mcp"), "custom-key");
        var value = new StudioConfiguration(1, [], [old, custom]);
        var migrated = OfficialGameAccess.Migrate(value);
        Assert.That(migrated.Agents, Is.SameAs(value.Agents));
        Assert.That(migrated.Servers, Does.Contain(custom));
        var official = migrated.Servers.Single(s => s.Id == old.Id);
        OfficialGameAccess.RequireOfficial(official);
        Assert.That(official.PlayerClientId, Is.Null);
        Assert.That(OfficialGameAccess.Migrate(migrated).Servers, Is.EqualTo(migrated.Servers));
    }
    [Test]
    public void UnifiedKeyCannotBeSentToCustomEndpoint()
    {
        using var handler = new Handler(_ => throw new AssertionException("Secret must never reach this host"));
        var server = OfficialGameAccess.SingaporeProfile() with { McpEndpoint = new Uri("https://example.com/mcp") };
        Assert.ThrowsAsync<InvalidDataException>(() => new AgentServerConnection(null!, handler)
            .ReadWithCredentialAsync(server, Key, default));
        Assert.That(handler.Calls, Is.Zero);
    }
    [Test]
    public async Task CatalogIsAuthenticatedAndMissingRequiredToolsAreRejected()
    {
        using var handler = new Handler(r => {
            Assert.That(r.Headers.Authorization?.Parameter, Is.EqualTo(Key));
            var request = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
            var method = request.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            return Json(method == "tools/list" ? "{\"result\":{\"tools\":[]}}" : "{\"result\":{}}");
        });
        using var mcp = new McpClient(new AgentProviderOptions { McpUri = OfficialGameAccess.Singapore, McpToken = Key,
            XaiKey = "", ElevenLabsKey = "", XaiModel = "", ElevenLabsModel = "", ElevenLabsVoiceId = "" }, handler);
        var error = Assert.ThrowsAsync<InvalidDataException>(() => OfficialGameAccess.RequireCatalogAsync(mcp, default));
        Assert.That(error!.Message, Is.EqualTo("ServerApiIncompatible"));
        await Task.CompletedTask;
    }
}
