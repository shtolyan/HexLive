using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

[NonParallelizable]
public sealed class AgentCommandReceiptTests
{
    private string _directory = "";
    [SetUp] public void Setup() => _directory = Directory.CreateTempSubdirectory("command-receipt-").FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_directory, true);

    [Test]
    public void ReplayAndSaveReloadDoNotExecuteTheCommandAgain()
    {
        using (var host = Host())
        {
            var tools = Tools(host);
            Assert.That(Execute(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo("completed"));
            MarkInput(host);
            Assert.That(Execute(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo("completed"));
            AssertInputUnchanged(host);
            host.Save();
        }
        using var restored = Host();
        var after = Tools(restored);
        MarkInput(restored);
        Assert.That(Execute(after, 1).GetProperty("outcome").GetString(), Is.EqualTo("completed"));
        AssertInputUnchanged(restored);
        Assert.That(Read(after, 1).GetProperty("highestSequence").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public void EvictionCannotMakeAnOldCommandExecutableAndIdentityChangesAreRejected()
    {
        using var host = Host(); var tools = Tools(host);
        for (var sequence = 1; sequence <= 40; sequence++) Execute(tools, sequence);
        Assert.That(host.Read(w => w.AgentCommands[901].Receipts.Count), Is.EqualTo(AgentCommandLedger.Capacity));
        MarkInput(host);
        Assert.That(Execute(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo("unknown"));
        AssertInputUnchanged(host);
        var changed = Call(tools, "execute_agent_command", new { npcId = 901, sequence = 40, commandId = "different",
            tool = "stop", arguments = new { } });
        Assert.That(changed.GetProperty("reason").GetString(), Is.EqualTo("CommandIdentityConflict"));
        AssertInputUnchanged(host);
    }

    [TestCase(false, "completed")]
    [TestCase(true, "failed")]
    public void RealRouteReceiptTracksCompletionOrInterruption(bool interrupt, string expected)
    {
        using var host = Host(); var engine = Engine(host);
        host.Read(w => { engine.Step(); return true; });
        var tools = Tools(host);
        var destinations = host.Read(w => w.Junctions.Items.Values.Where(j => !j.Blocked && SpatialQueries.IsJunctionFree(w, j.Id))
            .OrderByDescending(j => Math.Abs(j.WorldPosition.X - w.Entities.Npcs[new EntityId(901)].Position.X)).ToArray());
        var destination = destinations.First(j => {
            tools.Call("move_to", JsonSerializer.SerializeToElement(new { npcId = 901, x = j.WorldPosition.X, y = j.WorldPosition.Y }), "fixture", out var error);
            return !error;
        });
        Call(tools, "stop", new { npcId = 901 });
        var accepted = Call(tools, "execute_agent_command", new { npcId = 901, sequence = 1, commandId = "command-1", tool = "move_to",
            arguments = new { x = destination.WorldPosition.X, y = destination.WorldPosition.Y } });
        Assert.That(accepted.GetProperty("outcome").GetString(), Is.EqualTo("accepted"));
        host.Read(w => {
            var npc = w.Entities.Npcs[new EntityId(901)];
            if (interrupt) PlanInterruption.TryAbort(w, npc, InterruptionCause.PathFailure, "fixture");
            else for (var tick = 0; tick < 1500 && npc.Plan.Status == PlanStatus.Active; tick++) engine.Step();
            return true;
        });
        Assert.That(Read(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo(expected));
    }

    [Test]
    public void NativeRejectionIsReturnedAsAKnownFailedReceipt()
    {
        using var host = Host(); var tools = Tools(host);
        var result = Call(tools, "execute_agent_command", new
        { npcId = 901, sequence = 1, commandId = "command-1", tool = "interact",
            arguments = new { objectId = int.MaxValue, interaction = "PickUp" } });
        Assert.That(result.GetProperty("outcome").GetString(), Is.EqualTo("failed"));
        Assert.That(result.GetProperty("reason").GetString(), Is.EqualTo("TargetGone"));
        Assert.That(Read(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo("failed"));
    }

    [Test]
    public void BoundedRestCannotRunOutsideTheTrackedContractOrWithInvalidTargets()
    {
        using var host = Host(); var tools = Tools(host);
        tools.Call("rest_until", JsonSerializer.SerializeToElement(new { npcId = 901, need = "Energy", target = .8 }), "fixture", out var error);
        Assert.That(error, Is.True);
        foreach (var (need, target) in new[] { ("Energy", 0d), ("Stamina", 1.1d), ("Hunger", .8d) })
        {
            tools.Call("execute_agent_command", JsonSerializer.SerializeToElement(new
            { npcId = 901, sequence = 1, commandId = "command-1", tool = "rest_until", arguments = new { need, target } }), "fixture", out error);
            Assert.That(error, Is.True);
        }
        Assert.That(host.Read(w => w.AgentCommands.Count), Is.Zero);
    }

    [TestCase("Energy")]
    [TestCase("Stamina")]
    public void AlreadyRestoredNeedsCompleteOnceWithoutSearchingForASittingOrSleepingSpot(string need)
    {
        using var host = Host(); var tools = Tools(host);
        host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Needs.Energy = npc.Needs.Stamina = 1f;
            foreach (var id in w.Junctions.Items.Keys) w.Occupancy.JunctionOwner[id] = new EntityId(999999);
            return true;
        });
        var arguments = new { npcId = 901, sequence = 1, commandId = "command-1", tool = "rest_until",
            arguments = new { need, target = .8 } };
        var first = Call(tools, "execute_agent_command", arguments);
        Assert.That(first.GetProperty("outcome").GetString(), Is.EqualTo("completed"));
        Assert.That(first.GetProperty("reason").GetString(), Is.EqualTo("RestTargetReached"));
        var events = host.Read(w => w.Events.HighestSeq);
        Assert.That(Call(tools, "execute_agent_command", arguments).GetRawText(), Is.EqualTo(first.GetRawText()));
        Assert.That(host.Read(w => w.Events.HighestSeq), Is.EqualTo(events), "Duplicate request must not dispatch another Stop.");
        Assert.That(host.Read(w => w.AgentCommands[901].ActiveSequence), Is.Zero);
        Assert.That(host.Read(w => w.AgentCommands[901].RestNeed), Is.Empty);
    }

    [Test]
    public void InterruptingBoundedRestFailsItsReceiptAndClearsItsWakeTarget()
    {
        using var host = Host(); var tools = Tools(host);
        host.Read(w =>
        {
            var npc = w.Entities.Npcs[new EntityId(901)];
            npc.Needs.Energy = .2f; npc.Mind.AdrenalineUntilTick = 0;
            npc.Perception.Hostiles.Clear(); npc.Perception.Mobs.Clear();
            return true;
        });
        var accepted = Call(tools, "execute_agent_command", new
        { npcId = 901, sequence = 1, commandId = "command-1", tool = "rest_until", arguments = new { need = "Energy", target = .8 } });
        Assert.That(accepted.GetProperty("outcome").GetString(), Is.EqualTo("accepted"));
        host.Read(w =>
        {
            PlanInterruption.TryAbort(w, w.Entities.Npcs[new EntityId(901)], InterruptionCause.PathFailure, "fixture");
            Assert.That(w.AgentCommands[901].RestNeed, Is.Empty);
            Assert.That(w.AgentCommands[901].RestTarget, Is.Zero);
            return true;
        });
        Assert.That(Read(tools, 1).GetProperty("outcome").GetString(), Is.EqualTo("failed"));
    }

    [Test]
    public void ReceiptToolsCannotBypassActorScopeOrLease()
    {
        using var host = Host(); var tools = new McpTools(host, new ControlLeases(45));
        var args = JsonSerializer.SerializeToElement(new { npcId = 901, sequence = 1, commandId = "command-1", tool = "stop", arguments = new { } });
        tools.Call("execute_agent_command", args, "fixture", out var denied);
        Assert.That(denied, Is.True);
        Assert.That(host.Read(w => w.AgentCommands.Count), Is.Zero);
        tools.Call("read_agent_command", args, "fixture", out denied, _ => false);
        Assert.That(denied, Is.True);
        Assert.That(host.Read(w => w.AgentCommands.Count), Is.Zero);
    }

    private WorldHost Host() => new(12345, GameMode.Feud, Path.Combine(_directory, "world.sav"),
        Path.Combine(Root(), "SimData", "simdata.json"), false, companionProfile: "masha");
    private static McpTools Tools(WorldHost host)
    {
        var tools = new McpTools(host, new ControlLeases(45));
        Call(tools, "acquire_npc_control", new { npcId = 901 }); return tools;
    }
    private static JsonElement Execute(McpTools tools, long sequence) => Call(tools, "execute_agent_command",
        new { npcId = 901, sequence, commandId = "command-" + sequence, tool = "stop", arguments = new { } });
    private static JsonElement Read(McpTools tools, long sequence) => Call(tools, "read_agent_command",
        new { npcId = 901, sequence, commandId = "command-" + sequence });
    private static JsonElement Call(McpTools tools, string tool, object args)
    {
        var result = tools.Call(tool, JsonSerializer.SerializeToElement(args), "fixture", out var error);
        Assert.That(error, Is.False, result); return JsonSerializer.Deserialize<JsonElement>(result);
    }
    private static void MarkInput(WorldHost host) => host.Read(w => { w.Entities.Npcs[new EntityId(901)].Mind.LastManualInputTick = -765; return true; });
    private static void AssertInputUnchanged(WorldHost host) => Assert.That(host.Read(w => w.Entities.Npcs[new EntityId(901)].Mind.LastManualInputTick), Is.EqualTo(-765));
    private static SimulationEngine Engine(WorldHost host) => (SimulationEngine)typeof(WorldHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static string Root()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "SimData", "simdata.json"))) return path.FullName;
        throw new DirectoryNotFoundException("SimData root");
    }
}
