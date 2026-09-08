using System;
using System.IO;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.Server.Tests;

public sealed class McpPlayerAccessTests
{
    private string _directory = null!, _path = null!, _player = null!;
    private DateTimeOffset _now;
    [SetUp] public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "agent-access-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "grants.json");
        _player = Guid.NewGuid().ToString("N");
        _now = DateTimeOffset.UtcNow;
    }
    [TearDown] public void Clean() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private McpPlayerAccess Open() => new(_path, () => _now);
    private string Approve(McpPlayerAccess access)
    {
        var ticket = access.Begin("Agent Studio", "loopback");
        Assert.That(access.Approve(ticket.Id, ticket.Code, _player), Is.True);
        return access.Poll(ticket.Id, ticket.PollSecret).Credential!;
    }
    [Test] public void ApprovalRequiresCodeAndPollingRequiresSeparateSecret()
    {
        var access = Open(); var ticket = access.Begin("Masha", "loopback");
        Assert.That(access.Poll(ticket.Id, ticket.PollSecret).State, Is.EqualTo("Pending"));
        Assert.That(access.Approve(ticket.Id, "not-code", _player), Is.False);
        Assert.That(access.Approve(ticket.Id, ticket.Code, "anonymous"), Is.False);
        Assert.That(access.Approve(ticket.Id, ticket.Code, _player), Is.True);
        Assert.That(access.Poll(ticket.Id, new string('0', 64)).State, Is.EqualTo("Unavailable"));
        var credential = access.Poll(ticket.Id, ticket.PollSecret).Credential!;
        Assert.That(access.Poll(ticket.Id, ticket.PollSecret).Credential, Is.EqualTo(credential));
        Assert.That(access.Authorize(credential)!.PlayerId, Is.EqualTo(_player));
        Assert.That(File.ReadAllText(_path), Does.Not.Contain(credential).And.Not.Contain(ticket.PollSecret));
        Assert.That(Open().Authorize(credential), Is.Not.Null);
    }
    [Test] public void SessionsAreBoundToCredentialAndWorldAndAreNotPersisted()
    {
        var access = Open(); var first = Approve(access); var second = Approve(access);
        var session = access.CreateSession(first, "world-one");
        Assert.That(access.AuthorizeSession(session, first, "world-one"), Is.Not.Null);
        Assert.That(access.AuthorizeSession(session, second, "world-one"), Is.Null);
        Assert.That(access.AuthorizeSession(session, first, "world-two"), Is.Null);
        Assert.That(Open().AuthorizeSession(session, first, "world-one"), Is.Null);
        access.CloseSession(session);
        Assert.That(access.AuthorizeSession(session, first, "world-one"), Is.Null);
    }
    [Test] public void OnlyOwnerCanRevokeAndRevocationInvalidatesSessions()
    {
        var access = Open(); var credential = Approve(access);
        var grant = access.Authorize(credential)!;
        var session = access.CreateSession(credential, "world");
        Assert.That(access.Revoke(grant.Id, Guid.NewGuid().ToString("N")), Is.False);
        Assert.That(access.Revoke(grant.Id, _player), Is.True);
        Assert.That(access.AuthorizeSession(session, credential, "world"), Is.Null);
        Assert.That(Open().Authorize(credential), Is.Null);
    }
    [Test] public void PairingsSessionsAndRateLimitsExpireAtBoundary()
    {
        var access = Open(); var ticket = access.Begin("Masha", "source");
        access.Begin("Nika", "source"); access.Begin("A", "source"); access.Begin("B", "source");
        Assert.Throws<InvalidOperationException>(() => access.Begin("C", "source"));
        _now += TimeSpan.FromMinutes(1);
        Assert.DoesNotThrow(() => access.Begin("C", "source"));
        _now += TimeSpan.FromMinutes(1);
        Assert.That(access.Poll(ticket.Id, ticket.PollSecret).State, Is.EqualTo("Unavailable"));
        Assert.That(access.Approve(ticket.Id, ticket.Code, _player), Is.False);
        var credential = Approve(access); var session = access.CreateSession(credential, "world");
        _now += TimeSpan.FromMinutes(5);
        Assert.That(access.AuthorizeSession(session, credential, "world"), Is.Null);
    }
    [Test] public void TooManyWrongCodesCannotLaterBeApproved()
    {
        var access = Open(); var ticket = access.Begin("Masha", "source");
        for (var i = 0; i < 8; i++) Assert.That(access.Approve(ticket.Id, "invalid!", _player), Is.False);
        Assert.That(access.Approve(ticket.Id, ticket.Code, _player), Is.False);
    }
}
