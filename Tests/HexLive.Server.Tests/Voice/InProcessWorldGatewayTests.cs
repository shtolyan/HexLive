using System;
using System.IO;
using System.Threading.Tasks;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Server.Voice;
using HexLive.Simulation.Bootstrap;
using HexLive.VoiceBrain;
using NUnit.Framework;

namespace HexLive.Server.Tests.Voice;

// §171.8: the voice brain acts AS the player — it may read and command only that player's characters.
[NonParallelizable]
public sealed class InProcessWorldGatewayTests
{
    private string _directory = "";
    [SetUp] public void SetUp() => Directory.CreateDirectory(_directory = Path.Combine(Path.GetTempPath(), "hexlive-voice-" + Guid.NewGuid().ToString("N")));
    [TearDown] public void TearDown() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    private WorldHost Host() => new(12345, GameMode.Feud, Path.Combine(_directory, "world.sav"),
        Path.Combine(Root(), "SimData", "simdata.json"), false);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HexLive.Simulation.Standalone.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    [Test]
    public async Task OwnCharacterIsReadableAndCommandable()
    {
        using var host = Host();
        var gateway = new InProcessWorldGateway(new McpTools(host, new ControlLeases(45)), "ws:player", npc => npc == 1);
        var describe = await gateway.CallAsync("describe_colonist", new { npcId = 1 }, default);
        Assert.That(describe.GetProperty("npcId").GetInt32(), Is.EqualTo(1));
        await gateway.CallAsync("acquire_control", new { npcId = 1, ttlSeconds = 60 }, default);
        Assert.DoesNotThrowAsync(() => gateway.CallAsync("stop", new { npcId = 1 }, default));
    }

    [Test]
    public void AnotherPlayersCharacterIsRefused()
    {
        using var host = Host();
        var gateway = new InProcessWorldGateway(new McpTools(host, new ControlLeases(45)), "ws:player", npc => npc == 1);
        var read = Assert.ThrowsAsync<WorldToolRejectedException>(() => gateway.CallAsync("describe_colonist", new { npcId = 2 }, default));
        Assert.That(read!.Message, Does.Contain("NpcAccessDenied"));
        Assert.ThrowsAsync<WorldToolRejectedException>(() => gateway.CallAsync("acquire_control", new { npcId = 2, ttlSeconds = 60 }, default));
    }
}
