using System;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class AgentExternalControlLifecycleTests
{
    [TestCase("detach")]
    [TestCase("ttl")]
    [TestCase("world")]
    [TestCase("clear")]
    public void EveryAttachmentExitInvalidatesEffectiveControlWhileWorldIsPaused(string exit)
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new AgentSessionRegistry(() => now);
        Assert.That(registry.TryAttach(901, "mcp:owner", 1, "fixture", AgentCapabilities.WorldActions,
            45, out var attached, out _), Is.True);
        var token = registry.BindControl(attached.AttachmentId, "mcp:owner", 1)!;
        Assert.That(token.IsActive, Is.True);
        switch (exit)
        {
            case "detach": registry.TryDetach(attached.AttachmentId, "mcp:owner", 1, out _, out _); break;
            case "ttl": now = now.AddSeconds(46); registry.Sweep(null, 1); break;
            case "world": registry.Sweep(null, 2); break;
            case "clear": registry.Clear(); break;
        }
        Assert.That(token.IsActive, Is.False);
    }

    [Test]
    public void ReadOnlyAndOtherSessionCannotAcquireTokenAndCapabilityRemovalRevokesIt()
    {
        var registry = new AgentSessionRegistry();
        registry.TryAttach(901, "mcp:owner", 1, "fixture", AgentCapabilities.None, 45, out var attached, out _);
        Assert.That(registry.BindControl(attached.AttachmentId, "mcp:owner", 1), Is.Null);
        registry.TryAttach(901, "mcp:owner", 1, "fixture", AgentCapabilities.WorldActions, 45, out attached, out _);
        Assert.That(registry.BindControl(attached.AttachmentId, "mcp:other", 1), Is.Null);
        var token = registry.BindControl(attached.AttachmentId, "mcp:owner", 1)!;
        registry.TryAttach(901, "mcp:owner", 1, "fixture", AgentCapabilities.None, 45, out _, out _);
        Assert.That(token.IsActive, Is.False);
    }
}
