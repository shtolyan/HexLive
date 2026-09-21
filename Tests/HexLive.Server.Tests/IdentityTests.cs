extern alias IdentityService;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KeyStore = IdentityService::HexLive.Identity.KeyStore;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[TestFixture]
public sealed class IdentityTests
{
    private string _root = null!;
    [SetUp] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "hexlive-identity-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Test]
    public void KeysPersistAsHashesAndRotationKeepsAccount()
    {
        var path = Path.Combine(_root, "accounts.json");
        var store = new KeyStore(path);
        var first = store.Issue("Tester");
        Assert.That(store.Resolve(first.Key), Is.EqualTo(first.Account.Id));
        Assert.That(File.ReadAllText(path), Does.Not.Contain(first.Key));
        var restarted = new KeyStore(path);
        Assert.That(restarted.Resolve(first.Key), Is.EqualTo(first.Account.Id));
        var replacement = restarted.Issue("Tester", first.Account.Id);
        Assert.That(replacement.Account.Id, Is.EqualTo(first.Account.Id));
        Assert.That(restarted.Resolve(first.Key), Is.Null);
        Assert.That(restarted.Resolve(replacement.Key), Is.EqualTo(first.Account.Id));
        restarted.Revoke(first.Account.Id);
        Assert.That(new KeyStore(path).Resolve(replacement.Key), Is.Null);
    }

    [Test]
    public void PlayersHaveDistinctAccountsAndMalformedKeysDoNotAuthenticate()
    {
        var store = new KeyStore(Path.Combine(_root, "accounts.json"));
        var one = store.Issue("One"); var two = store.Issue("Two");
        Assert.That(one.Account.Id, Is.Not.EqualTo(two.Account.Id));
        Assert.That(store.Resolve(one.Key + "x"), Is.Null);
        Assert.That(store.Resolve("hexplay_legacy"), Is.Null);
        Assert.That(() => store.Issue("Unknown", Guid.NewGuid().ToString("N")), Throws.ArgumentException);
    }

    [Test]
    public void OperatorBootstrapCanPreserveVerifiedExistingPlayerIdentityOnlyInEmptyRegistry()
    {
        var store = new KeyStore(Path.Combine(_root, "accounts.json"));
        var id = Guid.NewGuid().ToString("N");
        var owner = store.Issue("Owner", permissions: KeyStore.Permissions, bootstrapId: id);
        Assert.That(store.Resolve(owner.Key), Is.EqualTo(id));
        Assert.Throws<ArgumentException>(() => store.Issue("Another", bootstrapId: Guid.NewGuid().ToString("N")));
    }

    [Test]
    public void RightsAreMutableAuditedAndLastOwnerIsProtected()
    {
        var store = new KeyStore(Path.Combine(_root, "accounts.json"));
        var owner = store.Issue("Owner", permissions: KeyStore.Permissions);
        Assert.Throws<ArgumentException>(() => store.Revoke(owner.Account.Id, 1));
        Assert.Throws<ArgumentException>(() => store.SetPermissions(owner.Account.Id, [], 1, owner.Account.Id));
        var player = store.Issue("Tester", permissions: ["game.play"]);
        store.SetPermissions(player.Account.Id, ["bugs.create"], 1, owner.Account.Id);
        Assert.That(store.Authenticate(player.Key)!.Permissions, Is.EqualTo(new[] { "bugs.create" }));
        Assert.Throws<KeyStore.ConflictException>(() => store.SetPermissions(player.Account.Id, [], 1, owner.Account.Id));
        var rotated = store.Issue("Tester", player.Account.Id, expectedRevision: 2);
        Assert.That(rotated.Account.Permissions, Is.EqualTo(new[] { "bugs.create" }));
        Assert.That(store.AuthenticateHash(player.Account.KeyHash), Is.Null);
        Assert.That(store.History().Length, Is.EqualTo(4));
        Assert.Throws<ArgumentException>(() => store.Issue("Bad", permissions: ["root"]));
    }

    private sealed class Reply : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public Reply(HttpStatusCode code, string json) { _code = code; _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/api/identity/v1/validate"));
            Assert.That(request.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
            Assert.That(request.Headers.Contains("X-HexLive-Client-Id"), Is.False);
            return Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_json) });
        }
    }

    [Test]
    public async Task BothServersUseTheAuthorityAccount()
    {
        var id = Guid.NewGuid().ToString("N");
        var key = "hexlive_" + new string('a', 64);
        using var nyc = new IdentityClient("https://keys.example", new Reply(HttpStatusCode.OK, "{\"accountId\":\"" + id + "\",\"permissions\":[\"game.play\"]}"));
        using var singapore = new IdentityClient("https://keys.example", new Reply(HttpStatusCode.OK, "{\"accountId\":\"" + id + "\",\"permissions\":[\"game.play\"]}"));
        Assert.That(await nyc.ResolveAsync(key), Is.EqualTo(id));
        Assert.That(await singapore.ResolveAsync(key), Is.EqualTo(id));
    }

    [Test]
    public async Task RevocationAndAuthorityFailureNeverFallBack()
    {
        var key = "hexlive_" + new string('a', 64);
        using var revoked = new IdentityClient("https://keys.example", new Reply(HttpStatusCode.Unauthorized, ""));
        Assert.That(await revoked.ResolveAsync(key), Is.Null);
        using var down = new IdentityClient("https://keys.example", new Reply(HttpStatusCode.ServiceUnavailable, ""));
        Assert.ThrowsAsync<HttpRequestException>(async () => await down.ResolveAsync(key));
        using var invalid = new IdentityClient("https://keys.example", new Reply(HttpStatusCode.OK, "{\"accountId\":\"spoofed\"}"));
        Assert.ThrowsAsync<HttpRequestException>(async () => await invalid.ResolveAsync(key));
        Assert.That(() => new IdentityClient("http://keys.example"), Throws.ArgumentException);
    }
}
