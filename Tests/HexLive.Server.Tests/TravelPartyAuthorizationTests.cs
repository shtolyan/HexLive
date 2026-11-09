using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests;

[NonParallelizable]
public sealed class TravelPartyAuthorizationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ActualViewerIngressGrantsOnlyItsLeaderPartyAndCleanupReleasesIt(bool expire)
    {
        var root=FindRoot();
        var fixture=Path.Combine(root,"Build","Diagnostics","wave2-colony","party-server-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            using var host=new WorldHost(12345,GameMode.Feud,Path.Combine(fixture,"world.sav"),Path.Combine(root,"SimData","simdata.json"),false);
            var pair=host.Read(world=> {
                var people=world.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).OrderBy(n=>n.Id.Value).Take(2).ToArray();
                var leader=people[0]; var member=people[1];
                var anchor=world.Junctions.Items.Values.First(j=>!j.Blocked && j.Tiles.Count>0 && SpatialQueries.IsJunctionFree(world,j.Id));
                var neighbour=SpatialQueries.GetPassableNeighbors(world,anchor.Id).First(j=>SpatialQueries.IsJunctionFree(world,j));
                foreach(var entry in new[]{(leader,anchor),(member,world.Junctions.Items[neighbour])})
                {
                    var person=entry.Item1; var junction=entry.Item2;
                    if(person.CurrentJunction is {} old) SpatialMutations.FreeJunction(world,old,person.Id);
                    var oldTile=person.Tile; person.Tile=junction.Tiles[0]; person.Fragment=junction.Fragment;
                    person.CurrentJunction=junction.Id; person.Position=junction.WorldPosition;
                    SpatialMutations.MoveEntityToTile(world,person.Id,oldTile,person.Tile); SpatialMutations.OccupyJunction(world,junction.Id,person.Id);
                    person.Needs.Hunger=person.Needs.Thirst=0f; person.Needs.Energy=1f;
                }
                member.Faction=Faction.Colony2;
                member.Social.GetOrCreate(leader.Id).Trust=0.7f;
                return(leader:leader.Id,member:member.Id);
            });
            var permanentBefore=host.Read(w=>w.PlayerControlledNpcs.ToArray());
            var now=DateTimeOffset.UnixEpoch;
            var leases=new ControlLeases(timeoutSeconds:30,now:()=>now); using var socket=new CaptureSocket();
            var viewer=new ViewerConnection(host,socket,"{}",false,"ws:leader",leases,new[]{pair.leader.Value});
            Assert.That((await Send(viewer,socket,new SetManualControlCommand(pair.leader,true))).Accepted,Is.True);
            Assert.That((await Send(viewer,socket,new RequestTravelPartyCommand(pair.leader,pair.member))).Accepted,Is.True);
            Assert.That(leases.HolderOf(pair.member.Value),Is.EqualTo("ws:leader"));
            var move=host.Read(w=>w.Entities.Npcs[pair.leader].Position);
            Assert.That((await Send(viewer,socket,new GroupMoveCommand(new[]{pair.leader,pair.member},move))).Accepted,Is.True);

            using var strangerSocket=new CaptureSocket();
            var stranger=new ViewerConnection(host,strangerSocket,"{}",false,"ws:other",leases,new[]{99999});
            var denied=await Send(stranger,strangerSocket,new SetManualControlCommand(pair.member,true));
            Assert.That(denied.Accepted,Is.False); Assert.That(denied.Reason,Is.EqualTo("NotAssigned"));
            if(expire)
            {
                now=now.AddSeconds(20);
                Assert.That((await Send(viewer,socket,new SetManualControlCommand(pair.leader,true))).Accepted,Is.True);
                now=now.AddSeconds(20);
                var expired=new List<(int NpcId,string Owner)>();
                leases.CollectExpired(expired);
                Assert.That(expired,Is.Empty,"Leader heartbeat renews unselected companions too.");
                now=now.AddSeconds(20);
                leases.CollectExpired(expired);
                Assert.That(expired.Select(e=>e.NpcId),Does.Contain(pair.leader.Value));
                foreach(var entry in expired) AgentControlActions.Release(host,entry.NpcId);
            }
            else viewer.Disconnect();
            Assert.That(host.Read(w=>w.Entities.Npcs[pair.member].Mind.TravelLeaderNpcId),Is.Null);
            Assert.That(host.Read(w=>w.Entities.Npcs[pair.member].Mind.ManualControl),Is.False);
            Assert.That(leases.HolderOf(pair.member.Value),Is.Empty);
            Assert.That(host.Read(w=>w.PlayerControlledNpcs.ToArray()),Is.EquivalentTo(permanentBefore));
            Assert.That(host.Read(w=>w.Entities.Npcs[pair.member].Faction),Is.EqualTo(Faction.Colony2));
        }
        finally { Directory.Delete(fixture,true); }
    }

    private static async Task<Frame.CommandResultFrame> Send(ViewerConnection viewer,CaptureSocket socket,ISimulationCommand command)
    {
        var bytes=Frame.NpcCommand(653,command);
        // The exact production ingress decodes the frame, checks assignments
        // and leases, submits to the real host and writes the real result.
        var method=typeof(ViewerConnection).GetMethod("HandleNpcCommandAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)method.Invoke(viewer,new object[]{bytes,new WebSocketReceiveResult(bytes.Length,WebSocketMessageType.Binary,true),CancellationToken.None})!;
        return Frame.ReadCommandResult(socket.Frames.Last()[1..]);
    }

    private static string FindRoot()
    {
        for(var dir=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);dir!=null;dir=dir.Parent)
            if(File.Exists(Path.Combine(dir.FullName,"SimData","simdata.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("HexLive repository");
    }

    private sealed class CaptureSocket:WebSocket
    {
        public readonly List<byte[]> Frames=new();
        public override WebSocketCloseStatus? CloseStatus=>null;
        public override string? CloseStatusDescription=>null;
        public override WebSocketState State=>WebSocketState.Open;
        public override string? SubProtocol=>null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus status,string? description,CancellationToken cancellationToken)=>Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? description,CancellationToken cancellationToken)=>Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellationToken)=>throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool endOfMessage,CancellationToken cancellationToken)
        { Frames.Add(buffer.ToArray()); return Task.CompletedTask; }
    }
}
