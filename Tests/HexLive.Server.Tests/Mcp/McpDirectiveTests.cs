using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Runtime;
using HexLive.Server.Mcp;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

/// <summary>§167.8: указания через MCP — крик, просьба агенту, текст агент→агент.</summary>
public sealed class McpDirectiveTests
{
    [Test]
    public void CatalogAdvertisesTheDirectiveToolsWithExplicitKinds()
    {
        var shout = McpTools.Catalog.Single(t => t.Name == "shout_directive").InputSchema;
        var kinds = shout.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.That(kinds, Is.EquivalentTo(new[] { "Build", "StockFood", "StockWater", "Firewood" }));
        Assert.That(McpTools.Catalog.Any(t => t.Name == "respond_directive"), Is.True);
        Assert.That(McpTools.Catalog.Any(t => t.Name == "send_agent_message"), Is.True);
    }

    [Test]
    public async Task ShoutNeedsTheLeaseAndReachesTheSimulation()
    {
        using var host = CreateHost();
        await RunOneTick(host);
        var pair = ColonyPair(host);
        var tools = new McpTools(host, new ControlLeases(45));
        var args = JsonSerializer.SerializeToElement(new { npcId = pair[0], kind = "StockWater" });
        tools.Call("shout_directive", args, "mcp:directive-test", out var error);
        Assert.That(error, Is.True, "No lease — no shout.");
        var bad = tools.Call("shout_directive", JsonSerializer.SerializeToElement(new { npcId = pair[0], kind = "None" }),
            "mcp:directive-test", out error);
        Assert.That(error, Is.True);
        Assert.That(bad, Does.Contain("InvalidKind"));

        var acquired = tools.Call("acquire_npc_control", JsonSerializer.SerializeToElement(new { npcId = pair[0] }),
            "mcp:directive-test", out error);
        Assert.That(error, Is.False, acquired);
        var result = tools.Call("shout_directive", args, "mcp:directive-test", out error);
        Assert.That(error, Is.False, result);
        host.Read(world =>
        {
            Assert.That(world.Events.Items.Any(e => e.Type == "DirectiveShout" && e.EntityId == pair[0]), Is.True);
            return true;
        });
    }

    [Test]
    public async Task PendingAskLandsInTheInboxAndRespondAccepts()
    {
        using var host = CreateHost();
        await RunOneTick(host);
        var pair = ColonyPair(host);
        var registry = new AgentSessionRegistry();
        var tools = new McpTools(() => host, () => 0, new ControlLeases(45), registry);
        var attach = tools.Call("attach_agent", JsonSerializer.SerializeToElement(new
        {
            npcId = pair[1], displayName = "Ника", capabilities = new[] { "playerText", "worldActions" }
        }), "mcp:agent-b", out var error);
        Assert.That(error, Is.False, attach);
        var attachmentId = JsonDocument.Parse(attach).RootElement.GetProperty("attachmentId").GetString()!;

        // The simulation side of the routing (ExternalControl → PendingDirective) is
        // covered by DirectiveTests; here the pending request is staged directly.
        host.Read(world =>
        {
            var target = world.Entities.Npcs[new EntityId(pair[1])];
            Assert.That(target.Mind.ExternalControl?.IsActive, Is.True, "attach_agent binds external control");
            target.Mind.PendingDirective = new PendingDirective
            {
                Kind = DirectiveKind.StockWater, FromId = new EntityId(pair[0]), SinceTick = world.Tick
            };
            return true;
        });

        var inbox = tools.Call("read_agent_inbox", JsonSerializer.SerializeToElement(new { attachmentId }),
            "mcp:agent-b", out error);
        Assert.That(error, Is.False, inbox);
        var messages = JsonDocument.Parse(inbox).RootElement.GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(1));
        var message = messages[0];
        Assert.That(message.GetProperty("senderId").GetString(), Is.EqualTo("npc:" + pair[0]));
        Assert.That(message.GetProperty("directive").GetProperty("kind").GetString(), Is.EqualTo("StockWater"));
        Assert.That(message.GetProperty("directive").GetProperty("fromNpcId").GetInt32(), Is.EqualTo(pair[0]));

        // A second poll must not duplicate the request.
        inbox = tools.Call("read_agent_inbox", JsonSerializer.SerializeToElement(new { attachmentId }),
            "mcp:agent-b", out error);
        Assert.That(JsonDocument.Parse(inbox).RootElement.GetProperty("messages").GetArrayLength(), Is.EqualTo(1));

        var responded = tools.Call("respond_directive", JsonSerializer.SerializeToElement(new
        {
            attachmentId, fromNpcId = pair[0], kind = "StockWater", accept = true
        }), "mcp:agent-b", out error);
        Assert.That(error, Is.False, responded);
        host.Read(world =>
        {
            var target = world.Entities.Npcs[new EntityId(pair[1])];
            Assert.That(target.Mind.PendingDirective, Is.Null);
            Assert.That(target.Mind.Directive?.Kind, Is.EqualTo(DirectiveKind.StockWater));
            Assert.That(target.Mind.Directive!.FromId, Is.EqualTo(new EntityId(pair[0])));
            return true;
        });

        var again = tools.Call("respond_directive", JsonSerializer.SerializeToElement(new
        {
            attachmentId, fromNpcId = pair[0], kind = "StockWater", accept = false
        }), "mcp:agent-b", out error);
        Assert.That(error, Is.True);
        Assert.That(again, Does.Contain("NoPendingDirective"));
    }

    [Test]
    public async Task AgentToAgentTextNeedsTheListenerCapabilityAndProximity()
    {
        using var host = CreateHost();
        await RunOneTick(host);
        var pair = ColonyPair(host);
        var registry = new AgentSessionRegistry();
        var tools = new McpTools(() => host, () => 0, new ControlLeases(45), registry);
        var a = Attach(tools, pair[0], "mcp:agent-a", new[] { "playerText", "agentText" });
        var b = Attach(tools, pair[1], "mcp:agent-b", new[] { "playerText" });

        var send = JsonSerializer.SerializeToElement(new
        {
            attachmentId = a, targetNpcId = pair[1], language = "ru", text = "Ника, ты строишь, я ношу воду?", messageId = "m1"
        });
        var refused = tools.Call("send_agent_message", send, "mcp:agent-a", out var error);
        Assert.That(error, Is.True);
        Assert.That(refused, Does.Contain("AgentNotListening"));

        tools.Call("detach_agent", JsonSerializer.SerializeToElement(new { attachmentId = b }), "mcp:agent-b", out error);
        b = Attach(tools, pair[1], "mcp:agent-b", new[] { "playerText", "agentText" });
        var distance = host.Read(world => HexLive.Simulation.Spatial.HexSpatialMath.HexDistance(
            world.Entities.Npcs[new EntityId(pair[0])].Tile, world.Entities.Npcs[new EntityId(pair[1])].Tile));
        var delivered = tools.Call("send_agent_message", send, "mcp:agent-a", out error);
        if (distance > Spec167.ShoutRadiusTiles)
        {
            Assert.That(error, Is.True);
            Assert.That(delivered, Does.Contain("TooFar"));
            return;
        }

        Assert.That(error, Is.False, delivered);
        var inbox = tools.Call("read_agent_inbox", JsonSerializer.SerializeToElement(new { attachmentId = b }),
            "mcp:agent-b", out error);
        Assert.That(error, Is.False, inbox);
        var messages = JsonDocument.Parse(inbox).RootElement.GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(1));
        Assert.That(messages[0].GetProperty("senderId").GetString(), Is.EqualTo("npc:" + pair[0]));
        Assert.That(messages[0].GetProperty("text").GetString(), Does.Contain("Ника"));
        Assert.That(messages[0].GetProperty("directive").ValueKind, Is.EqualTo(JsonValueKind.Null));

        // Idempotent by (sender, messageId).
        tools.Call("send_agent_message", send, "mcp:agent-a", out error);
        inbox = tools.Call("read_agent_inbox", JsonSerializer.SerializeToElement(new { attachmentId = b }),
            "mcp:agent-b", out error);
        Assert.That(JsonDocument.Parse(inbox).RootElement.GetProperty("messages").GetArrayLength(), Is.EqualTo(1));

        // A plain viewer-side player never reaches another agent's inbox through this tool.
        var self = tools.Call("send_agent_message", JsonSerializer.SerializeToElement(new
        {
            attachmentId = a, targetNpcId = pair[0], language = "ru", text = "себе"
        }), "mcp:agent-a", out error);
        Assert.That(error, Is.True, self);
    }

    private static string Attach(McpTools tools, int npcId, string owner, string[] capabilities)
    {
        var attach = tools.Call("attach_agent", JsonSerializer.SerializeToElement(new
        {
            npcId, displayName = "Агент " + npcId, capabilities
        }), owner, out var error);
        Assert.That(error, Is.False, attach);
        return JsonDocument.Parse(attach).RootElement.GetProperty("attachmentId").GetString()!;
    }

    private static int[] ColonyPair(WorldHost host) =>
        host.Read(world => world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony)
            .OrderBy(n => n.Id.Value).Take(2).Select(n => n.Id.Value).ToArray());

    private static async Task RunOneTick(WorldHost host)
    {
        using var cancellation = new CancellationTokenSource();
        var before = host.Tick;
        var run = Task.Run(() => host.Run(cancellation.Token));
        try { await host.WaitForNextTickAsync(before, TimeSpan.FromSeconds(30), cancellation.Token); }
        finally { cancellation.Cancel(); await run; }
    }

    private static WorldHost CreateHost()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        return new WorldHost(12345, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), "mcp-directive-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(dir!.FullName, "SimData/simdata.json"), false);
    }
}
