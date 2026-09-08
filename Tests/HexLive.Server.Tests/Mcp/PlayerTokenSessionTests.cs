using System;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class PlayerTokenSessionTests
{
    [Test]
    public void SessionBindsPlayerAndWorldAndExpiresOrCloses()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new PlayerTokenSessions(() => now);
        var id = sessions.Create("player", "world");
        Assert.That(sessions.Validate(id, "other", "world"), Is.False);
        Assert.That(sessions.Validate(id, "player", "other"), Is.False);
        Assert.That(sessions.Validate("invented", "player", "world"), Is.False);
        Assert.That(sessions.Validate(id, "player", "world"), Is.True);
        now += TimeSpan.FromMinutes(5);
        Assert.That(sessions.Validate(id, "player", "world"), Is.False);
        id = sessions.Create("player", "world");
        sessions.Close(id);
        Assert.That(sessions.Validate(id, "player", "world"), Is.False);
    }
}
