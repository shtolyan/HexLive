using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpPerceptionHistoryTests
{
    [Test]
    public void DescribeAddsPassedSightingsWithoutLookingUpTheirHiddenCurrentState()
    {
        using var fixture = new Fixture();
        fixture.Observe(10, 123, "clothing.passed");
        fixture.Observe(20, null, "");
        using var response = fixture.Describe();
        var recent = response.RootElement.GetProperty("recentPerception");
        var item = recent.GetProperty("observations").EnumerateArray().Single();
        Assert.That(item.GetProperty("definitionId").GetString(), Is.EqualTo("clothing.passed"));
        Assert.That(item.GetProperty("lastSeenTick").GetInt32(), Is.EqualTo(10));
        Assert.That(item.GetProperty("lastSeenTile").GetProperty("q").GetInt32(), Is.EqualTo(4));
        Assert.That(item.GetProperty("lastSeenTileCenter").GetProperty("x").GetSingle(), Is.Not.NaN);
        using var stranger = fixture.Describe(owner: "mcp:other");
        Assert.That(stranger.RootElement.GetProperty("recentPerception").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void EarlierTurnCursorCannotConsumeASecondObservationDuringModelRequest()
    {
        using var fixture = new Fixture();
        fixture.Observe(10, 123, "first");
        fixture.Observe(11, null, "");
        using var initial = fixture.Describe();
        var read = initial.RootElement.GetProperty("recentPerception");
        var epoch = read.GetProperty("epoch").GetString()!;
        var watermark = read.GetProperty("watermark").GetInt64();
        fixture.Observe(20, 124, "during-model");
        fixture.Observe(21, null, "");
        using var next = fixture.Describe(epoch, watermark);
        var rows = next.RootElement.GetProperty("recentPerception").GetProperty("observations");
        Assert.That(rows.EnumerateArray().Single().GetProperty("id").GetInt32(), Is.EqualTo(124));
        using var retry = fixture.Describe(epoch, watermark);
        Assert.That(retry.RootElement.GetProperty("recentPerception").GetProperty("observations").GetRawText(), Is.EqualTo(rows.GetRawText()));
    }

    [Test]
    public void DetachAndTtlDisposeBufferWhilePausedAndReattachGetsNewEpoch()
    {
        using var fixture = new Fixture();
        fixture.Observe(10, 123, "old");
        var oldBuffer = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        var oldEpoch = oldBuffer.Epoch;
        using var detached = fixture.Call("detach_agent", new { attachmentId = fixture.Attachment });
        Assert.That(oldBuffer.Read("", 0, 10).Observations, Is.Empty);
        Assert.That(fixture.Host.Read(w => oldBuffer.Capture(w, w.Entities.Npcs[new EntityId(fixture.NpcId)])), Is.False);
        fixture.Attach();
        var fresh = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        Assert.That(fresh.Epoch, Is.Not.EqualTo(oldEpoch));
        fixture.Now = fixture.Now.AddMinutes(3);
        using var expired = fixture.Describe();
        Assert.That(expired.RootElement.GetProperty("recentPerception").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(fresh.Read("", 0, 10).Observations, Is.Empty);
    }

    [Test]
    public void RegistrySweepAndDescribeAttachDoNotInvertTheWorldLock()
    {
        using var fixture = new Fixture();
        var registryGate = typeof(AgentSessionRegistry).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Registry)!;
        foreach (var attach in new[] { false, true })
        {
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try { if (attach) fixture.Attach(); else using (fixture.Describe()) { } }
                catch (Exception ex) { failure = ex; }
            });
            Task<bool>? worldRead = null;
            var worldWasAvailable = false;
            lock (registryGate)
            {
                // Simulate Sweep's registry gate before it reads the world.
                // The concurrent MCP call must wait without owning world.
                worker.Start();
                SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 3000);
                worldRead = Task.Run(() => fixture.Host.Read(_ => true));
                worldWasAvailable = worldRead.Wait(TimeSpan.FromSeconds(3));
            }
            Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(worldRead!.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(failure, Is.Null);
            Assert.That(worldWasAvailable, Is.True, attach ? "Attach held world while waiting for registry" : "Describe held world while waiting for registry");
        }
        // Exercise actual death sweep and world clear, including paused cleanup.
        var buffer = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        fixture.Host.Read(w => { w.Entities.Npcs[new EntityId(fixture.NpcId)].Health = 0; return true; });
        fixture.Registry.Sweep(fixture.Host, fixture.Generation);
        Assert.That(fixture.Host.Read(w => buffer.Capture(w, w.Entities.Npcs[new EntityId(fixture.NpcId)])), Is.False);
    }

    [Test]
    public void RegistryClearDisposesAllPerceptionBuffersImmediately()
    {
        using var fixture = new Fixture();
        fixture.Observe(10, 123, "old");
        var buffer = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        fixture.Registry.Clear();
        Assert.That(buffer.Read("", 0, 10).Observations, Is.Empty);
        Assert.That(fixture.Host.Read(w => buffer.Capture(w, w.Entities.Npcs[new EntityId(fixture.NpcId)])), Is.False);
    }

    [Test]
    public void ReconnectSameOwnerKeepsBufferButWorldGenerationReplacementDisposesIt()
    {
        using var fixture = new Fixture();
        var old = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        fixture.Attach();
        Assert.That(fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations), Is.SameAs(old));
        fixture.Generation++;
        fixture.Attach();
        var fresh = fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Perception.Observations);
        Assert.That(fresh.Epoch, Is.Not.EqualTo(old.Epoch));
        Assert.That(old.Read("", 0, 10).Observations, Is.Empty);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly WorldHost Host;
        public readonly AgentSessionRegistry Registry;
        private readonly McpTools _tools;
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public int Generation = 1;
        public readonly int NpcId;
        public string Attachment = "";
        public Fixture()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "SimData/simdata.json"))) root = root.Parent;
            Host = new WorldHost(12345, GameMode.Feud,
                Path.Combine(Path.GetTempPath(), "perception-" + Guid.NewGuid().ToString("N") + ".sav"),
                Path.Combine(root!.FullName, "SimData/simdata.json"), false);
            Host.PauseAsOperator();
            Registry = new AgentSessionRegistry(() => Now);
            _tools = new McpTools(() => Host, () => Generation, new ControlLeases(45), Registry);
            NpcId = Host.Read(w => w.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony).Id.Value);
            Attach();
        }
        public void Attach()
        {
            using var attached = Call("attach_agent", new { npcId = NpcId, displayName = "fixture", capabilities = new[] { "worldActions" } });
            Attachment = attached.RootElement.GetProperty("attachmentId").GetString()!;
        }
        public void Observe(int tick, int? objectId, string definition)
        {
            Host.Read(w =>
            {
                w.Tick = tick;
                var npc = w.Entities.Npcs[new EntityId(NpcId)];
                npc.Perception.Objects.Clear();
                npc.Perception.Agents.Clear();
                npc.Perception.Hostiles.Clear();
                npc.Perception.Mobs.Clear();
                if (objectId.HasValue) npc.Perception.Objects.Add(new PerceivedObject
                { Id = new ObjectId(objectId.Value), DefinitionId = definition, Tile = new TileCoord(4, 5) });
                npc.Perception.LastUpdatedTick = tick;
                npc.Perception.Observations.Capture(w, npc);
                return true;
            });
        }
        public JsonDocument Describe(string epoch = "", long since = 0, string owner = "mcp:history") =>
            Call("describe_colonist", new { npcId = NpcId, perceptionEpoch = epoch, perceptionSince = since }, owner);
        public JsonDocument Call(string name, object arguments, string owner = "mcp:history")
        {
            var text = _tools.Call(name, JsonSerializer.SerializeToElement(arguments), owner, out var error);
            Assert.That(error, Is.False, text);
            return JsonDocument.Parse(text);
        }
        public void Dispose() => Host.Dispose();
    }
}
