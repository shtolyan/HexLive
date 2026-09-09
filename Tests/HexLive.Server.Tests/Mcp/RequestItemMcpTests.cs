using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp
{

[NonParallelizable]
public sealed class RequestItemMcpTests
{
    [Test]
    public void ContractAsksForOneDefinitionWithoutExposingAnInventoryIndex()
    {
        var tool = McpTools.Catalog.Single(t => t.Name == "request_item");
        Assert.That(tool.InputSchema.GetProperty("required").EnumerateArray()
            .Select(p => p.GetString()), Is.EquivalentTo(new[] { "npcId", "targetNpcId", "definitionId" }));
        Assert.That(tool.InputSchema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name), Is.EquivalentTo(new[] { "npcId", "targetNpcId", "definitionId" }));
        Assert.That(tool.Description, Does.Contain("1.95"));
        Assert.That(tool.Description, Does.Contain("TooFar"));
    }

    [Test]
    public void ScopedRequesterNeedsALeaseAndReceivesCompletedOnlyAfterVoluntaryTransfer()
    {
        using var host = new WorldHost(12345, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), $"hexlive-request-item-{Guid.NewGuid():N}.sav"),
            SimDataPath(), verboseTrace: false);
        host.EnableMcpEventLog();
        var (requester, owner) = host.Read(PrepareMeeting);
        var tools = new McpTools(host, new ControlLeases(120));
        const string controller = "mcp:item-request";
        bool Scope(int id) => id == requester.Id.Value;
        var args = JsonSerializer.SerializeToElement(new
            { npcId = requester.Id.Value, targetNpcId = owner.Id.Value, definitionId = GearCatalog.Lighter });
        var selected = owner.Inventory.Items[0];
        var spare = owner.Inventory.Items[1];

        tools.Call("request_item", args, controller, out var error, Scope);
        Assert.That(error, Is.True, "The caller must own the requester's lease.");
        Assert.That(requester.Inventory.Items, Is.Empty);
        var denied = tools.Call("request_item", args, controller, out error, _ => false);
        Assert.That(error, Is.True);
        Assert.That(denied, Does.Contain("NpcAccessDenied"));
        Assert.That(owner.Inventory.Items, Has.Count.EqualTo(2));

        var acquired = tools.Call("acquire_npc_control",
            JsonSerializer.SerializeToElement(new { npcId = requester.Id.Value }), controller, out error, Scope);
        Assert.That(error, Is.False, acquired);
        var answer = tools.Call("request_item", args, controller, out error, Scope);
        Assert.That(error, Is.False, answer);
        using (var completed = JsonDocument.Parse(answer))
        {
            Assert.That(completed.RootElement.GetProperty("status").GetString(), Is.EqualTo("Completed"));
            Assert.That(completed.RootElement.GetProperty("outcome").GetString(), Is.EqualTo("Transferred"));
            Assert.That(completed.RootElement.GetProperty("count").GetInt32(), Is.EqualTo(1));
        }
        Assert.That(requester.Inventory.Items.Single(), Is.SameAs(selected));
        Assert.That(owner.Inventory.Items.Single(), Is.SameAs(spare));

        var refused = tools.Call("request_item", args, controller, out error, Scope);
        Assert.That(error, Is.True, refused);
        using (var rejection = JsonDocument.Parse(refused))
        {
            Assert.That(rejection.RootElement.GetProperty("status").GetString(), Is.EqualTo("Rejected"));
            Assert.That(rejection.RootElement.GetProperty("reason").GetString(), Is.EqualTo("NeededByOwner"));
        }
        Assert.That(requester.Inventory.Items.Single(), Is.SameAs(selected));
        Assert.That(owner.Inventory.Items.Single(), Is.SameAs(spare));
        var events = tools.Call("read_events", JsonSerializer.SerializeToElement(new
            { npcId = requester.Id.Value, sinceSeq = 0 }), controller, out error, Scope);
        Assert.That(error, Is.False, events);
        using var log = JsonDocument.Parse(events);
        var requests = log.RootElement.GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "ItemRequestResult").ToArray();
        Assert.That(requests, Has.Length.EqualTo(2));
        Assert.That(requests[0].GetProperty("message").GetString(), Does.Contain("Outcome=Transferred"));
        Assert.That(requests[1].GetProperty("message").GetString(), Does.Contain("Outcome=NeededByOwner"));
    }

    // Canonical WorldHost factory and actual adjacent dry junctions. This is a
    // controlled feature fixture, not a replay of a historical production save.
    private static (NPCState requester, NPCState owner) PrepareMeeting(WorldState world)
    {
        var people = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        foreach (var npc in people)
        {
            npc.Inventory.Items.Clear();
            npc.Needs.Hunger = npc.Needs.Thirst = 0f;
            npc.Execution.Status = ExecutionStatus.None;
            npc.Execution.CurrentInteraction = null;
            npc.Movement.IsMoving = false;
        }
        people[1].Social.GetOrCreate(people[0].Id).Affinity = 0f;
        people[1].Inventory.Items.Add(new ItemInstance(GearCatalog.Lighter) { Durability = 0.42f });
        people[1].Inventory.Items.Add(new ItemInstance(GearCatalog.Lighter) { Durability = 0.93f });
        var from = world.Junctions.Items.Values.First(j =>
            SpatialQueries.IsJunctionFree(world, j.Id) && !SpatialQueries.IsAllWaterJunction(world, j.Id) &&
            j.Neighbors.Any(id => SpatialQueries.IsJunctionFree(world, id) &&
                !SpatialQueries.IsAllWaterJunction(world, id) &&
                SpatialQueries.CanTouchAcross(world, j.Id, id, HexSpatialMath.HexRadius * 1.3f)));
        var to = world.Junctions.Items[from.Neighbors.First(id =>
            SpatialQueries.IsJunctionFree(world, id) && !SpatialQueries.IsAllWaterJunction(world, id) &&
            SpatialQueries.CanTouchAcross(world, from.Id, id, HexSpatialMath.HexRadius * 1.3f))];
        Place(world, people[0], from);
        Place(world, people[1], to);
        return (people[0], people[1]);
    }

    private static void Place(WorldState world, NPCState npc, Junction at)
    {
        if (npc.CurrentJunction is { } previous)
        {
            SpatialMutations.FreeJunction(world, previous, npc.Id);
            SpatialMutations.ReleaseJunctionReservation(world, previous, npc.Id);
        }
        var oldTile = npc.Tile;
        npc.CurrentJunction = at.Id;
        npc.Position = at.WorldPosition;
        npc.Tile = at.Tiles[0];
        npc.Fragment = at.Fragment;
        SpatialMutations.MoveEntityToTile(world, npc.Id, oldTile, npc.Tile);
        SpatialMutations.OccupyJunction(world, at.Id, npc.Id);
    }

    private static string SimDataPath()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
             directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SimData", "simdata.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("SimData/simdata.json");
    }
}

}
