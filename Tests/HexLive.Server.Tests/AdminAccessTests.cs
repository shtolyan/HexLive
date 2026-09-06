using System;
using System.IO;
using HexLive.Server.GodMode;
using NUnit.Framework;

namespace HexLive.Server.Tests;
public sealed class AdminAccessTests
{
    [Test]
    public void ClientIdIsNotAuthority_CodeIsSingleUse_GrantSurvivesReload_RevocationWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hexlive-admin-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "grants.json"); var id = Guid.NewGuid().ToString("N");
        try
        {
            var store = new AdminAccess(path); var code = store.Issue(id);
            Assert.That(store.Authorized(id, code), Is.False);
            Assert.That(store.Exchange(Guid.NewGuid().ToString("N"), code), Is.Null);
            var token = store.Exchange(id, code)!;
            Assert.That(token, Is.Not.Null);
            Assert.That(store.Exchange(id, code), Is.Null);
            Assert.That(store.Authorized(id, token), Is.True);
            Assert.That(File.ReadAllText(path), Does.Not.Contain(token));
            store = new AdminAccess(path); Assert.That(store.Authorized(id, token), Is.True);
            store.Revoke(id); Assert.That(store.Authorized(id, token), Is.False);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Test]
    public void RebindingInvalidatesOldToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hexlive-admin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AdminAccess(Path.Combine(dir, "grants.json")); var id = Guid.NewGuid().ToString("N");
            var old = store.Exchange(id, store.Issue(id))!;
            var current = store.Exchange(id, store.Issue(id))!;
            Assert.That(store.Authorized(id, old), Is.False); Assert.That(store.Authorized(id, current), Is.True);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
