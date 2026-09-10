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

public sealed class McpExternalControlTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void FullMcpAttachAcquireReleaseDetachKeepsOriginalPlayerSwitch(bool playerManual)
    {
        using var fixture = new Fixture(playerManual);
        Func<bool> manual = () => fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Mind.ManualControl);
        Assert.That(manual(), Is.True);
        using (fixture.Call("acquire_npc_control", new { npcId = fixture.NpcId })) { }
        using (fixture.Call("release_control", new { npcId = fixture.NpcId })) { }
        Assert.That(manual(), Is.True, "Action completion must not resume voluntary AI under attachment.");
        using (fixture.Call("detach_agent", new { attachmentId = fixture.Attachment })) { }
        Assert.That(manual(), Is.EqualTo(playerManual));
    }

    [Test]
    public void StandaloneActionAfterDetachCanStillTakeAndReleaseOrdinaryManualControl()
    {
        using var fixture = new Fixture(false);
        using (fixture.Call("detach_agent", new { attachmentId = fixture.Attachment })) { }
        using (fixture.Call("acquire_npc_control", new { npcId = fixture.NpcId })) { }
        Assert.That(fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Mind.ManualControl), Is.True);
        using (fixture.Call("release_control", new { npcId = fixture.NpcId })) { }
        Assert.That(fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Mind.ManualControl), Is.False);
    }

    [Test]
    public void AttachReturnsEventBoundaryBeforeNewObserversCanEmitFacts()
    {
        using var fixture = new Fixture(false);
        using var response = fixture.Call("attach_agent", new { npcId = fixture.NpcId,
            displayName = "fixture", capabilities = new[] { "worldActions" } });
        Assert.That(response.RootElement.GetProperty("eventWatermark").GetInt64(), Is.GreaterThanOrEqualTo(0));
        Assert.That(fixture.Host.Read(w => w.Entities.Npcs[new EntityId(fixture.NpcId)].Mind.ExternalControl.IsActive), Is.True);
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
        public Fixture(bool playerManual)
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
            Host.Read(w => { w.Entities.Npcs[new EntityId(NpcId)].Mind.ManualControl = playerManual; return true; });
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
