using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpPlayerScopeTests
{
    [Test] public void ScopeFiltersListsAndRejectsForeignReadsAndCommandsBeforeExecution()
    {
        using var host = CreateHost();
        var leases = new ControlLeases(45);
        var tools = new McpTools(host, leases);
        var ids = host.Read(w => w.Entities.Npcs.Keys.Select(x => x.Value).Take(2).ToArray());
        bool Allowed(int id) => id == ids[0];
        var result = tools.Call("list_colonists", JsonSerializer.SerializeToElement(new { }), "mcp:test", out var error, Allowed);
        Assert.That(error, Is.False);
        using var json = JsonDocument.Parse(result);
        Assert.That(json.RootElement.GetProperty("colonists").GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.RootElement.GetProperty("colonists")[0].GetProperty("npcId").GetInt32(), Is.EqualTo(ids[0]));
        foreach (var tool in new[] { "describe_colonist", "acquire_npc_control", "read_events", "attach_agent", "stop" })
        {
            result = tools.Call(tool, JsonSerializer.SerializeToElement(new { npcId = ids[1] }), "mcp:test", out error, Allowed);
            Assert.That(error, Is.True, tool);
            Assert.That(result, Does.Contain("NpcAccessDenied"), tool);
        }
        tools.Call("read_events", JsonSerializer.SerializeToElement(new { }), "mcp:test", out error, Allowed);
        Assert.That(error, Is.True, "Unscoped event stream must not leak other NPCs");
        Assert.That(leases.OwnedBy("mcp:test"), Is.Empty);
    }

    [Test] public void ChangedRightsAlsoBlockAnExistingAttachment()
    {
        using var host = CreateHost(); var agents = new AgentSessionRegistry();
        var tools = new McpTools(() => host, () => 0, new ControlLeases(45), agents);
        var id = host.Read(w => w.Entities.Npcs.Keys.First().Value);
        var allowed = true;
        var response = tools.Call("attach_agent", JsonSerializer.SerializeToElement(new
        { npcId = id, displayName = "Studio", capabilities = new[] { "playerText" }, ttlSeconds = 45 }),
            "mcp:test", out var error, npc => allowed && npc == id);
        Assert.That(error, Is.False, response);
        using var json = JsonDocument.Parse(response);
        var attachmentId = json.RootElement.GetProperty("attachmentId").GetString();
        allowed = false;
        response = tools.Call("agent_heartbeat", JsonSerializer.SerializeToElement(new { attachmentId }),
            "mcp:test", out error, npc => allowed && npc == id);
        Assert.That(error, Is.True);
        Assert.That(response, Does.Contain("NpcAccessDenied"));
    }

    private static WorldHost CreateHost()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("Source root");
        return new WorldHost(12345, GameMode.HugeIsland,
            Path.Combine(Path.GetTempPath(), "mcp-player-scope-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(dir.FullName, "SimData/simdata.json"), false);
    }
}
