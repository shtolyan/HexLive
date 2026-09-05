using System;
using System.Collections.Generic;
using HexLive.Server;
using NUnit.Framework;

namespace HexLive.Server.Tests
{

/// <summary>
/// §144.2/§121.9: единый реестр лиз — владение явное, одно на колонистку,
/// с неймспейсом владельца (mcp:/ws:). Истечение больше не молчит: пары
/// (npcId, owner) копятся для фонового свипа, который возвращает брошенных
/// под ИИ; world-swap чистит всё; у оператора есть force-release.
/// </summary>
public sealed class ControlLeasesTests
{
    private static DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    private static ControlLeases Create(int timeoutSeconds = 30) =>
        new(timeoutSeconds, () => _now);

    [SetUp]
    public void Reset() => _now = DateTimeOffset.UnixEpoch;

    [Test]
    public void SecondOwnerIsRefusedWhileTheFirstHolds()
    {
        var leases = Create();
        Assert.That(leases.TryAcquire(7, "ws:player", out _, out _), Is.True);
        Assert.That(leases.TryAcquire(7, "mcp:agent", out _, out var heldBy), Is.False);
        Assert.That(heldBy, Is.EqualTo("ws:player"),
            "Отказ обязан называть владельца — «занято кем-то» непроверяемо.");

        // Повтор тем же владельцем — продление, не отказ (идемпотентность).
        Assert.That(leases.TryAcquire(7, "ws:player", out _, out _), Is.True);
    }

    [Test]
    public void ExpiredLeasesAreCollectedForTheSweepExactlyOnce()
    {
        var leases = Create(timeoutSeconds: 30);
        leases.TryAcquire(7, "ws:player", out _, out _);
        leases.TryAcquire(9, "mcp:agent", out _, out _);

        _now = _now.AddSeconds(31);
        var expired = new List<(int NpcId, string Owner)>();
        leases.CollectExpired(expired);

        Assert.That(expired, Is.EquivalentTo(new[] { (7, "ws:player"), (9, "mcp:agent") }),
            "Истёкший лиз обязан дойти до свипа: раньше истечение молча " +
            "оставляло колонистку в ручном режиме до таймаута §121.7.");

        expired.Clear();
        leases.CollectExpired(expired);
        Assert.That(expired, Is.Empty, "Повторный сбор не должен дублировать свип.");
    }

    [Test]
    public void RenewKeepsALeaseAliveAcrossTheTimeout()
    {
        var leases = Create(timeoutSeconds: 30);
        leases.TryAcquire(7, "ws:player", out _, out _);

        _now = _now.AddSeconds(20);
        Assert.That(leases.TryRenew(7, "ws:player", out _), Is.True);

        _now = _now.AddSeconds(20);
        Assert.That(leases.TryRenew(7, "ws:player", out _), Is.True,
            "Команда продлевает лиз — 40 секунд с двумя командами это не простой.");

        var expired = new List<(int NpcId, string Owner)>();
        leases.CollectExpired(expired);
        Assert.That(expired, Is.Empty);
    }

    [Test]
    public void RequestedCompanionTtlOverridesDefaultAndStillExpiresExactlyOnce()
    {
        var leases = Create(timeoutSeconds: 120);
        Assert.That(leases.TryAcquire(901, "mcp:masha", 45, out _, out _), Is.True);

        _now = _now.AddSeconds(44);
        Assert.That(leases.HolderOf(901), Is.EqualTo("mcp:masha"));
        _now = _now.AddSeconds(1);

        var expired = new List<(int NpcId, string Owner)>();
        leases.CollectExpired(expired);
        Assert.That(expired, Is.EqualTo(new[] { (901, "mcp:masha") }));
        Assert.That(leases.HolderOf(901), Is.Empty);
    }

    [Test]
    public void ForceReleaseReportsThePreviousOwner()
    {
        var leases = Create();
        leases.TryAcquire(7, "mcp:agent", out _, out _);

        Assert.That(leases.ForceRelease(7, out var previous), Is.True);
        Assert.That(previous, Is.EqualTo("mcp:agent"));
        Assert.That(leases.HolderOf(7), Is.Empty);
        Assert.That(leases.ForceRelease(7, out _), Is.False);
    }

    [Test]
    public void ClearForgetsHeldAndExpiredAlike()
    {
        var leases = Create(timeoutSeconds: 30);
        leases.TryAcquire(7, "ws:player", out _, out _);
        leases.TryAcquire(9, "mcp:agent", out _, out _);
        _now = _now.AddSeconds(31);
        leases.TryAcquire(11, "ws:player", out _, out _);

        leases.Clear();

        var expired = new List<(int NpcId, string Owner)>();
        leases.CollectExpired(expired);
        Assert.That(expired, Is.Empty,
            "После world-swap возвращать под ИИ некого: мира с теми id нет.");
        Assert.That(leases.Snapshot(), Is.Empty);
    }
}

}
