using System;
using System.IO;
using System.Text.Json;
using HexLive.Server.GodMode;
using NUnit.Framework;

namespace HexLive.Server.Tests;
public sealed class AdminAccessTests
{
    private string _dir = null!, _path = null!, _client = null!;
    private DateTimeOffset _now;
    private const string Secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "admin-access-" + Guid.NewGuid().ToString("N")); _path = Path.Combine(_dir, "state.json"); _client = Guid.NewGuid().ToString("N"); _now = DateTimeOffset.UtcNow; }
    [TearDown] public void Clean() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private AdminAccess Open() => new(_path, () => _now);
    [Test] public void RequestIsPersistentAndIdempotent_AndApprovalNeedsExactSecret()
    {
        var access = Open(); var r = access.RequestAccess(_client, Secret);
        Assert.That(r.State, Is.EqualTo("pending")); Assert.That(access.Authorized(_client, Secret), Is.False);
        Assert.That(access.RequestAccess(_client, Secret).RequestId, Is.EqualTo(r.RequestId));
        access = Open(); Assert.That(access.Check(_client, Secret).RequestId, Is.EqualTo(r.RequestId));
        access.Decide(r.RequestId, true, null);
        Assert.That(access.Authorized(_client, Secret), Is.True);
        Assert.That(access.Authorized(_client, new string('B', 64)), Is.False);
        Assert.That(access.Authorized(Guid.NewGuid().ToString("N"), Secret), Is.False);
        Assert.That(File.ReadAllText(_path), Does.Not.Contain(Secret));
        _now += TimeSpan.FromDays(10000); Assert.That(Open().Authorized(_client, Secret), Is.True);
    }
    [Test] public void SpoofedClientIdCannotReplacePendingDeviceRequest()
    {
        var a = Open(); var original = a.RequestAccess(_client, Secret); var spoof = a.RequestAccess(_client, new string('B', 64));
        Assert.That(spoof.RequestId, Is.Not.EqualTo(original.RequestId));
        a.Decide(original.RequestId, true, null);
        Assert.That(a.Check(_client, new string('B', 64)).State, Is.EqualTo("pending"));
        Assert.That(a.Authorized(_client, new string('B', 64)), Is.False);
    }
    [Test] public void DurationStartsAtApproval_AndExpiresAtBoundary()
    {
        var a = Open(); var r = a.RequestAccess(_client, Secret); _now += TimeSpan.FromDays(2);
        a.Decide(r.RequestId, true, TimeSpan.FromHours(1)); _now += TimeSpan.FromMinutes(59);
        Assert.That(Open().Authorized(_client, Secret), Is.True); _now += TimeSpan.FromMinutes(1);
        Assert.That(Open().Check(_client, Secret).State, Is.EqualTo("expired"));
    }
    [Test] public void RejectionRevocationAndNewRequestNeedExplicitApproval()
    {
        var a = Open(); var r = a.RequestAccess(_client, Secret); a.Decide(r.RequestId, false, null);
        Assert.That(a.Check(_client, Secret).State, Is.EqualTo("rejected"));
        var again = a.RequestAccess(_client, Secret); Assert.That(again.RequestId, Is.Not.EqualTo(r.RequestId));
        a.Decide(again.RequestId, true, null); a.Revoke(_client);
        Assert.That(Open().Check(_client, Secret).State, Is.EqualTo("revoked"));
    }
    [Test] public void LegacyGrantMigratesWithoutCodeOrNewApproval()
    {
        Directory.CreateDirectory(_dir);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Secret)));
        File.WriteAllText(_path, JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string,string> { [_client] = hash }));
        var a = Open(); Assert.That(a.Authorized(_client, Secret), Is.True); a.Revoke(_client);
        Assert.That(Open().Authorized(_client, Secret), Is.False);
    }
    [Test] public void InvalidDurationAndRepeatedDecisionLeaveStateIntact()
    {
        var a = Open(); var r = a.RequestAccess(_client, Secret);
        Assert.Throws<ArgumentException>(() => a.Decide(r.RequestId, true, TimeSpan.Zero));
        Assert.That(a.Check(_client, Secret).State, Is.EqualTo("pending")); a.Decide(r.RequestId, true, null);
        Assert.Throws<ArgumentException>(() => a.Decide(r.RequestId, false, null));
        Assert.That(a.Authorized(_client, Secret), Is.True);
    }
}
