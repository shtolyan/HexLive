using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HexLive.Server.Accounts;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace HexLive.Server.Tests.Accounts;

public sealed class AccountStoreTests
{
    private DirectoryInfo _directory = null!;
    private string StorePath => Path.Combine(_directory.FullName, "accounts.json");
    [SetUp] public void SetUp() => _directory = Directory.CreateTempSubdirectory("accounts-store-");
    [TearDown] public void TearDown() => _directory.Delete(true);

    [Test]
    public void SessionsSurviveRestartHaveOnlyHashesAndRevokeIndividually()
    {
        var store = new AccountStore(StorePath, FirebaseFixture.Project);
        var session = store.CreateSession(new("firebase-uid", "google.com"));
        var second = store.CreateSession(new("firebase-uid", "apple.com"));
        Assert.That(second.PlayerId, Is.EqualTo(session.PlayerId));
        Assert.That(second.SessionToken, Is.Not.EqualTo(session.SessionToken));
        Assert.That(PlayerCharacterAssignments.TryNormalizePlayerId(session.PlayerId, out _), Is.True);
        var json = File.ReadAllText(StorePath);
        Assert.That(json, Does.Not.Contain(session.SessionToken).And.Not.Contain(second.SessionToken).And.Not.Contain("email"));
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("accounts").GetArrayLength(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("accounts")[0].GetProperty("providers").GetArrayLength(), Is.EqualTo(2));
        if (!OperatingSystem.IsWindows()) Assert.That(File.GetUnixFileMode(StorePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        store = new AccountStore(StorePath, FirebaseFixture.Project);
        Assert.That(store.TryResolve(session.SessionToken, out var id), Is.True); Assert.That(id, Is.EqualTo(session.PlayerId));
        using var connection = store.Connect(session.SessionToken)!;
        using var other = store.Connect(second.SessionToken)!;
        Assert.That(store.Revoke(session.SessionToken), Is.True);
        Assert.That(connection.Lifetime.IsCancellationRequested, Is.True);
        Assert.That(other.Lifetime.IsCancellationRequested, Is.False);
        Assert.That(store.TryResolve(session.SessionToken, out _), Is.False);
        store = new AccountStore(StorePath, FirebaseFixture.Project);
        Assert.That(store.TryResolve(session.SessionToken, out _), Is.False);
        Assert.That(store.TryResolve(second.SessionToken, out _), Is.True);
        Assert.That(store.Connect(session.SessionToken), Is.Null);
        Assert.That(_directory.GetFiles("*.tmp-*").Length, Is.Zero);
    }

    [Test]
    public void ThirtyDayExpiryAndDifferentUids()
    {
        var clock = new TestClock(); var store = new AccountStore(StorePath, FirebaseFixture.Project, clock);
        var session = store.CreateSession(new("a", "password"));
        Assert.That(session.ExpiresAt - clock.Now, Is.EqualTo(TimeSpan.FromDays(30)));
        Assert.That(store.CreateSession(new("b", "password")).PlayerId, Is.Not.EqualTo(session.PlayerId));
        clock.Now = session.ExpiresAt;
        Assert.That(store.TryResolve(session.SessionToken, out _), Is.False);
        Assert.That(store.Connect(session.SessionToken), Is.Null);
    }

    [Test]
    public async Task ConcurrentSignInsCannotCreateDuplicateAccounts()
    {
        var store = new AccountStore(StorePath, FirebaseFixture.Project);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.CreateSession(new("a", "google.com")))));
        Assert.That(sessions.Select(s => s.PlayerId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(sessions.Select(s => s.SessionToken).Distinct().Count(), Is.EqualTo(20));
        var loaded = new AccountStore(StorePath, FirebaseFixture.Project);
        Assert.That(sessions.All(s => loaded.TryResolve(s.SessionToken, out _)), Is.True);
    }

    [Test]
    public void BrokenStoreOrDifferentProjectDoesNotResetAccounts()
    {
        var store = new AccountStore(StorePath, FirebaseFixture.Project);
        store.CreateSession(new("a", "google.com"));
        Assert.Throws<InvalidDataException>(() => new AccountStore(StorePath, "different-project"));
        File.WriteAllText(StorePath, "{");
        Assert.Throws<InvalidDataException>(() => new AccountStore(StorePath, FirebaseFixture.Project));
        Assert.That(File.ReadAllText(StorePath), Is.EqualTo("{"));
    }

    [Test]
    public void FailedWriteDoesNotMintOrRevokeInMemory()
    {
        var store = new AccountStore(StorePath, FirebaseFixture.Project);
        var session = store.CreateSession(new("a", "google.com"));
        File.Delete(StorePath); Directory.CreateDirectory(StorePath);
        Assert.Catch<IOException>(() => store.CreateSession(new("a", "google.com")));
        Assert.Catch<IOException>(() => store.Revoke(session.SessionToken));
        Assert.That(store.TryResolve(session.SessionToken, out _), Is.True);
        Assert.That(_directory.GetFiles("*.tmp-*").Length, Is.Zero);
    }

    [Test]
    public void HttpIdentityUsesAccountBeforeClientIdAndRetainsSharedAccess()
    {
        var store = new AccountStore(StorePath, FirebaseFixture.Project);
        var session = store.CreateSession(new("a", "google.com"));
        var identity = new PlayerHttpIdentity(t => t == "fixture-shared", store);
        var ctx = new DefaultHttpContext(); var impostor = Guid.NewGuid().ToString("N");
        ctx.Request.Headers.Authorization = "Bearer " + session.SessionToken;
        ctx.Request.Headers["X-HexLive-Client-Id"] = impostor;
        Assert.That(identity.TryResolve(ctx, out var id), Is.True); Assert.That(id, Is.EqualTo(session.PlayerId));
        ctx.Request.Headers.Authorization = "Bearer fixture-shared";
        Assert.That(identity.TryResolve(ctx, out id), Is.True); Assert.That(id, Is.EqualTo(impostor));
    }

    [Test]
    public void RateLimitUsesIndependentIpWindows()
    {
        var clock = new TestClock(); var limiter = new SessionRateLimit(clock);
        for (var i = 0; i < 10; i++) Assert.That(limiter.Allow("a"), Is.True);
        Assert.That(limiter.Allow("a"), Is.False); Assert.That(limiter.Allow("b"), Is.True);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.That(limiter.Allow("a"), Is.True);
    }
}
