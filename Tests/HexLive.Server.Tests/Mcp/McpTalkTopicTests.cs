using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Social;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpTalkTopicTests
{
    [Test]
    public void SchemaOffersOnlySharedNativeTopicsAndKeepsTheTopicOptional()
    {
        var schema = McpTools.Catalog.Single(t => t.Name == "talk_to").InputSchema;
        var topics = schema.GetProperty("properties").GetProperty("topic").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.That(topics, Is.EquivalentTo(new[] { "SmallTalk", "Escape", "Dogs", "Weather", "Food", "Fire", "Home", "Gossip", "Flirt", "Joke", "Grumble",
            "AskBuild", "AskStockFood", "AskStockWater", "AskFirewood" })); // §167.2
        Assert.That(topics, Is.EquivalentTo(TalkTopicRequest.Allowed.Select(t => t.ToString())));
        Assert.That(schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()), Is.EquivalentTo(new[] { "npcId", "targetNpcId" }));
        Assert.That(schema.GetProperty("additionalProperties").GetBoolean(), Is.False);
    }

    [TestCase("Hunger")]
    [TestCase("Stranger")]
    [TestCase("999")]
    [TestCase("Food, Joke")]
    [TestCase("food")]
    public void InvalidTopicReturnsAnErrorBeforeAcquiringOrChangingControl(string topic)
    {
        using var host = CreateHost();
        var tools = new McpTools(host, new ControlLeases(45));
        var id = host.Read(world => world.Entities.Npcs.Values.First().Id.Value);
        var result = tools.Call("talk_to", JsonSerializer.SerializeToElement(new { npcId = id, targetNpcId = id, topic }), "mcp:topic-test", out var error);
        Assert.That(error, Is.True);
        Assert.That(result, Does.Contain("InvalidTalkTopic"));
    }

    [Test]
    public async Task ValidTopicRequiresTheLeaseAndReachesTheNativePlan()
    {
        using var host = CreateHost();
        using var cancellation = new CancellationTokenSource();
        var before = host.Tick;
        var run = Task.Run(() => host.Run(cancellation.Token));
        try { await host.WaitForNextTickAsync(before, TimeSpan.FromSeconds(30), cancellation.Token); }
        finally { cancellation.Cancel(); await run; }
        var pair = host.Read(world => world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).OrderBy(n => n.Id.Value).Take(2).Select(n => n.Id.Value).ToArray());
        var tools = new McpTools(host, new ControlLeases(45));
        var arguments = JsonSerializer.SerializeToElement(new { npcId = pair[0], targetNpcId = pair[1], topic = "Joke" });
        tools.Call("talk_to", arguments, "mcp:topic-test", out var error);
        Assert.That(error, Is.True, "A valid topic does not grant control.");
        var acquired = tools.Call("acquire_npc_control", JsonSerializer.SerializeToElement(new { npcId = pair[0] }), "mcp:topic-test", out error);
        Assert.That(error, Is.False, acquired);
        var result = tools.Call("talk_to", arguments, "mcp:topic-test", out error);
        Assert.That(error, Is.False, result);
        host.Read(world =>
        {
            var actor = world.Entities.Npcs[new EntityId(pair[0])];
            Assert.That(actor.Plan.RequestedTalkTopic, Is.EqualTo(TalkTopic.Joke));
            Assert.That(actor.Plan.TargetAgentId, Is.EqualTo(new EntityId(pair[1])));
            return true;
        });
    }

    private static WorldHost CreateHost()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        return new WorldHost(12345, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), "mcp-topic-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(dir!.FullName, "SimData/simdata.json"), false);
    }
}
