using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Wire;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Server.Tests
{
[NonParallelizable]
public sealed class ConstructionAuthorizationTests
{
    [Test]
    public void UnassignedViewerCannotUseActorlessConstruction()
    {
        var command = new CancelBuildSiteCommand(new ObjectId(1));
        Assert.That(PlayerCommandAuthorization.TryAuthorize(command, new HashSet<int>(),
            new ControlLeases(), "ws:new-player", out var refusal), Is.False);
        Assert.That(refusal, Is.EqualTo("NotAssigned"));
        Assert.That(PlayerCommandAuthorization.TryAuthorize(command, new HashSet<int> { 1 },
            new ControlLeases(), "ws:assigned-player", out _), Is.True,
            "Construction does not seize the character's manual control lease.");
    }

    [Test]
    public async Task HostUsesOnlyThisViewersAssignmentsAndRefreshesSnapshot()
    {
        var root = FindRoot();
        using var host = new WorldHost(12345, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), "construction-493-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(root, "SimData", "simdata.json"), false);
        var fixture = host.Read(world =>
        {
            var actors = world.Entities.Npcs.Values.Where(npc => npc.Faction == Faction.Colony).Take(2).ToArray();
            actors[1].Faction = Faction.Colony2;
            world.PlayerControlledNpcs.Add(actors[1].Id.Value);
            var tile = world.Tiles.Items.Values.First(t => t.Flags.HasFlag(TileFlags.Walkable) &&
                !t.Flags.HasFlag(TileFlags.Blocked) && !t.Flags.HasFlag(TileFlags.Water)).Coord;
            world.FactionHomes.Clear();
            world.FactionHomes[Faction.Colony2] = tile;
            return (A: actors[0].Id.Value, B: actors[1].Id.Value, Tile: tile);
        });
        var placement = FreeArchitecturePlacementData.FromElement(new BlueprintElementData
        { Kind = BlueprintElementKind.FloorSector, FloorSector = new FloorSectorKey(fixture.Tile, 0) });
        var command = new ApplyFreeArchitectureCommand(new[] { placement }, Array.Empty<string>());
        var before = host.EncodeSnapshot(false);
        var denied = host.SubmitManualCommand(command, "ws:player-a", new[] { fixture.A });
        Assert.That(denied.Accepted, Is.False);
        Assert.That(denied.Reason, Is.EqualTo(ConstructionAccess.ForeignReason));
        Assert.That(host.SubmitManualCommand(command, "ws:player-a").Reason, Is.EqualTo("NotAssigned"));
        var accepted = host.SubmitManualCommand(command, "ws:player-b", new[] { fixture.B });
        Assert.That(accepted.Accepted, Is.True, accepted.Reason);
        Assert.That(host.EncodeSnapshot(false), Is.Not.EqualTo(before), "Same-tick snapshot must include the accepted site.");
        var piece = host.Read(world => FreeArchitectureRules.FreePieces(world).Single(p => p.Tile == fixture.Tile).Id);
        Assert.That(host.SubmitManualCommand(new CancelBuildSiteCommand(piece), "ws:player-a", new[] { fixture.A }).Reason,
            Is.EqualTo(ConstructionAccess.ForeignReason));
        var removal = new ApplyFreeArchitectureCommand(Array.Empty<FreeArchitecturePlacementData>(), new[] { placement.SlotKey });
        var rejected = await ThroughViewer(host, removal, fixture.A, "ws:player-a");
        Assert.That(rejected.Reason, Is.EqualTo(ConstructionAccess.ForeignReason),
            "Real viewer ingress must propagate this socket's assignments, not the union of all players.");
        Assert.That((await ThroughViewer(host, removal, fixture.B, "ws:player-b")).Accepted, Is.True);
    }

    private static async Task<Frame.CommandResultFrame> ThroughViewer(
        WorldHost host, ISimulationCommand command, int assigned, string owner)
    {
        // Local duplex WebSockets exercise the real viewer reader, codec,
        // assignment/lease gates, host admission and private result frame.
        // No HTTP server, credentials, paid providers or live game is used.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        var connect = peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        await connect;
        using var client = WebSocket.CreateFromStream(peer.GetStream(), false, null, TimeSpan.FromSeconds(30));
        using var server = WebSocket.CreateFromStream(accepted.GetStream(), true, null, TimeSpan.FromSeconds(30));
        var viewer = new ViewerConnection(host, server, File.ReadAllText(Path.Combine(FindRoot(), "SimData", "simdata.json")),
            false, owner, new ControlLeases(), new[] { assigned });
        var run = viewer.RunAsync(timeout.Token);
        try
        {
            var payload = Frame.NpcCommand(493, command);
            await client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, timeout.Token);
            var buffer = new byte[65536];
            while (true)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult message;
                do
                {
                    message = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (message.MessageType == WebSocketMessageType.Close) throw new IOException("Viewer closed before verdict");
                    frame.Write(buffer, 0, message.Count);
                } while (!message.EndOfMessage);
                var bytes = frame.ToArray();
                if (bytes.Length > 0 && bytes[0] == (byte)FrameKind.CommandResult)
                    return Frame.ReadCommandResult(bytes[1..]);
            }
        }
        finally
        {
            timeout.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SimData", "simdata.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("HexLive repository");
    }
}
}
