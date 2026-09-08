using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpDiplomacyTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void MergeUsesNormalDiplomacyAndRequestedHome(bool targetHome)
    {
        using var host = CreateHost();
        var tools = new McpTools(host, new ControlLeases(45));
        var setup = Prepare(host);
        Call(tools, "acquire_npc_control", new { npcId = setup.actor }, out var error);
        Assert.That(error, Is.False);
        object arguments = targetHome
            ? new { npcId = setup.actor, targetNpcId = setup.target, useTargetCamp = true }
            : new { npcId = setup.actor, targetNpcId = setup.target };
        var response = Call(tools, "merge_camps", arguments, out error);
        Assert.That(error, Is.False, response);
        host.Read(w =>
        {
            Assert.That(w.Entities.Npcs[new EntityId(setup.target)].Faction, Is.EqualTo(Faction.Colony));
            Assert.That(w.FactionHomes[Faction.Colony], Is.EqualTo(targetHome ? setup.theirs : setup.ours));
            Assert.That(w.FactionHomes.ContainsKey(Faction.Colony2), Is.False);
            Assert.That(w.Entities.Npcs.Values.Any(n => n.Faction == Faction.Colony3), Is.True);
            return true;
        });
    }

    [TestCase("relationship", "RelationshipTooLow")]
    [TestCase("unconscious", "TargetUnavailable")]
    [TestCase("far", "TooFarToTalk")]
    [TestCase("lease", "")]
    public void RefusalDoesNotForceFactionOrHomeChange(string condition, string expected)
    {
        using var host = CreateHost();
        var tools = new McpTools(host, new ControlLeases(45));
        var setup = Prepare(host);
        if (condition != "lease") Call(tools, "acquire_npc_control", new { npcId = setup.actor }, out _);
        host.Read(w =>
        {
            var actor = w.Entities.Npcs[new EntityId(setup.actor)];
            var target = w.Entities.Npcs[new EntityId(setup.target)];
            if (condition == "relationship") target.Social.GetOrCreate(actor.Id).Affinity = 0.50f;
            if (condition == "unconscious") target.Mind.FaintedUntilTick = w.Tick + 100;
            if (condition == "far") target.Position = new Float2(actor.Position.X + 100, actor.Position.Y);
            return true;
        });
        var response = Call(tools, "merge_camps", new { npcId = setup.actor, targetNpcId = setup.target }, out var error);
        Assert.That(error, Is.True, response);
        if (expected.Length > 0) Assert.That(response, Does.Contain(expected));
        host.Read(w =>
        {
            Assert.That(w.Entities.Npcs[new EntityId(setup.target)].Faction, Is.EqualTo(Faction.Colony2));
            Assert.That(w.FactionHomes[Faction.Colony], Is.EqualTo(setup.ours));
            Assert.That(w.FactionHomes[Faction.Colony2], Is.EqualTo(setup.theirs));
            return true;
        });
    }

    private static string Call(McpTools tools, string name, object args, out bool error) =>
        tools.Call(name, JsonSerializer.SerializeToElement(args), "mcp:diplomacy-test", out error);

    private static (int actor, int target, TileCoord ours, TileCoord theirs) Prepare(WorldHost host) =>
        host.Read(w =>
        {
            var actor = w.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
            var target = w.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony2);
            actor.Social.GetOrCreate(target.Id).Affinity = 0.8f;
            target.Social.GetOrCreate(actor.Id).Affinity = 0.8f;
            target.Position = actor.Position;
            return (actor.Id.Value, target.Id.Value, w.FactionHomes[Faction.Colony], w.FactionHomes[Faction.Colony2]);
        });

    private static WorldHost CreateHost()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        if (dir == null) throw new DirectoryNotFoundException("Source root");
        return new WorldHost(12345, GameMode.HugeIsland,
            Path.Combine(Path.GetTempPath(), "mcp-diplomacy-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(dir.FullName, "SimData/simdata.json"), false);
    }
}
