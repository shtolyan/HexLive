using System.Reflection;
using System.Text.Json;
using HexLive.Server;
using HexLive.Server.Mcp;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.AgentHost.Tests;

[NonParallelizable]
public sealed class AgentMcpRouteTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AcceptedMashaRouteSurvivesOrdinaryAiAndRenewalUntilArrivalOrExplicitStop(bool interrupt)
    {
        var directory = Directory.CreateTempSubdirectory("agent-mcp-route-").FullName;
        try
        {
            using var host = new WorldHost(12345, GameMode.Feud, Path.Combine(directory, "world.sav"),
                Path.Combine(FindRoot(), "SimData/simdata.json"), false, companionProfile: "masha");
            var engine = (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            host.Read(world => { engine.Step(); return true; });
            var leases = new ControlLeases(45);
            var tools = new McpTools(host, leases);
            const string owner = "mcp:route-test";
            void Call(string name, object arguments)
            {
                var result = tools.Call(name, JsonSerializer.SerializeToElement(arguments), owner, out var error);
                Assert.That(error, Is.False, name + ": " + result);
            }
            Call("acquire_npc_control", new { npcId = 901, ttlSeconds = 45 });
            var candidates = host.Read(world =>
            {
                var npc = world.Entities.Npcs[new EntityId(901)];
                return world.Junctions.Items.Values
                    .Where(j => !j.Blocked && SpatialQueries.IsJunctionFree(world, j.Id))
                    .OrderByDescending(j => (j.WorldPosition.X - npc.Position.X) * (j.WorldPosition.X - npc.Position.X) +
                        (j.WorldPosition.Y - npc.Position.Y) * (j.WorldPosition.Y - npc.Position.Y))
                    .ToArray();
            });
            Junction? destination = null;
            foreach (var candidate in candidates)
            {
                tools.Call("move_to", JsonSerializer.SerializeToElement(new
                { npcId = 901, x = candidate.WorldPosition.X, y = candidate.WorldPosition.Y, run = false }), owner, out var rejected);
                if (rejected) continue;
                destination = candidate;
                break;
            }
            Assert.That(destination, Is.Not.Null, "MCP must accept at least one distant reachable destination");
            var samples = 0;
            var arrived = false;
            for (var i = 0; i < 1200; i++)
            {
                if (i % 16 == 0)
                    Call("acquire_npc_control", new { npcId = 901, ttlSeconds = 45 });
                if (interrupt && i == 32)
                {
                    // A new explicit control decision may interrupt a route;
                    // an ordinary AI desire cannot issue this authenticated call.
                    Call("stop", new { npcId = 901 });
                    Assert.That(host.Read(world => world.Entities.Npcs[new EntityId(901)].Plan.Status), Is.Not.EqualTo(PlanStatus.Active));
                    break;
                }
                var active = host.Read(world =>
                {
                    engine.Step();
                    var npc = world.Entities.Npcs[new EntityId(901)];
                    Assert.That(npc.Mind.ManualControl, Is.True);
                    if (npc.Plan.Status == PlanStatus.Active)
                    {
                        Assert.That(npc.Mind.CurrentGoal, Is.EqualTo(GoalType.PlayerOrder), "Ordinary construction/leaf desires must not take over");
                        Assert.That(npc.Plan.TargetJunctionId, Is.EqualTo(destination!.Id));
                        samples++;
                        return true;
                    }
                    arrived = npc.CurrentJunction == destination!.Id;
                    return false;
                });
                if (!active) break;
            }
            Assert.That(samples, Is.GreaterThanOrEqualTo(32), "Exercise a long route across many ordinary AI passes");
            if (!interrupt) Assert.That(arrived, Is.True, "An unimpeded accepted route must reach its destination");
            Call("release_control", new { npcId = 901 });
            Assert.That(host.Read(world => world.Entities.Npcs[new EntityId(901)].Mind.ManualControl), Is.False);
            Assert.That(leases.HolderOf(901), Is.Empty);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void OnlyThisActorsThreatsInterruptOrdinaryActionOwnership()
    {
        var log = new McpEventLog();
        var buffer = new SimulationEventBuffer();
        buffer.Add(new SimulationEvent { Tick = 1, EntityId = 901, Type = "BuildProgress", Message = "" });
        buffer.Add(new SimulationEvent { Tick = 1, EntityId = 901, Type = "GoalScored", Message = "" });
        buffer.Add(new SimulationEvent { Tick = 1, EntityId = 902, Type = "ThreatSpotted", Message = "" });
        log.Drain(buffer, 0);
        var read = log.Read(0, 100, 901);
        bool Critical(EventBatch batch) => (bool)typeof(AgentHostRuntime)
            .GetMethod("ContainsCriticalEvent", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { JsonSerializer.SerializeToElement(new
            { events = batch.Events.Select(e => new { type = e.Type }) }) })!;
        Assert.That(Critical(read), Is.False, "Construction and a neighbour's threat must not replace this actor's route");
        var since = buffer.HighestSeq;
        buffer.Add(new SimulationEvent { Tick = 2, EntityId = 901, Type = "ThreatSpotted", Message = "" });
        buffer.Add(new SimulationEvent { Tick = 2, EntityId = 901, Type = "WoundInflicted", Message = "" });
        log.Drain(buffer, since);
        Assert.That(Critical(log.Read(since, 100, 901)), Is.True);
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { TestContext.CurrentContext.TestDirectory, Path.GetDirectoryName(sourceFile)!, Directory.GetCurrentDirectory() })
            for (var path = new DirectoryInfo(start); path != null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "SimData/simdata.json"))) return path.FullName;
        throw new DirectoryNotFoundException("HexLive source root");
    }
}
