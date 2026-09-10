using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpKnownObjectObservationTests
{
    [Test]
    public void QueryUsesAuthenticatedNpcScopeAndNeedsNoControlLease()
    {
        using var host = CreateHost();
        var ids = host.Read(world => world.Entities.Npcs.Keys.Select(n => n.Value).Take(2).ToArray());
        var leases = new ControlLeases(45);
        var tools = new McpTools(host, leases);
        var own = tools.Call("query_known_objects", JsonSerializer.SerializeToElement(new { npcId = ids[0] }),
            "mcp:388", out var ownError, id => id == ids[0]);
        Assert.That(ownError, Is.False, own);
        Assert.That(leases.TryRenew(ids[0], "mcp:388", out _), Is.False, "Query never acquires control");
        Assert.That(host.Read(world => world.Entities.Npcs.First(n => n.Key.Value == ids[0]).Value.Mind.ManualControl), Is.False);
        var foreign = tools.Call("query_known_objects", JsonSerializer.SerializeToElement(new { npcId = ids[1] }),
            "mcp:388", out var foreignError, id => id == ids[0]);
        Assert.That(foreignError, Is.True);
        Assert.That(foreign, Does.Contain("NpcAccessDenied").And.Not.Contain("lastKnown"));
    }

    [TestCase("\"limit\":0")]
    [TestCase("\"limit\":65")]
    [TestCase("\"limit\":\"16\"")]
    [TestCase("\"limit\":1.5")]
    [TestCase("\"limit\":null")]
    [TestCase("\"limit\":1,\"limit\":2")]
    [TestCase("\"definitionPrefix\":7")]
    [TestCase("\"interaction\":\"999\"")]
    [TestCase("\"interaction\":\"unknown\"")]
    [TestCase("\"secret\":true")]
    public void MalformedFiltersFailInsteadOfSilentlyReturningUnfilteredKnowledge(string tail)
    {
        using var host = CreateHost();
        var id = host.Read(world => world.Entities.Npcs.Keys.First().Value);
        using var arguments = JsonDocument.Parse("{\"npcId\":" + id + "," + tail + "}");
        var result = new McpTools(host, new ControlLeases(45)).Call("query_known_objects", arguments.RootElement,
            "mcp:388", out var error);
        Assert.That(error, Is.True);
        Assert.That(result, Is.EqualTo("{\"error\":\"InvalidKnowledgeQueryArguments\"}"));
    }

    [Test]
    public void RememberedCoconutSurvivesHiddenMoveChangeAndRemovalWithoutLiveFields()
    {
        using var host = CreateHost();
        host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            var live = world.Entities.Objects.Values.First();
            world.Tick = 1000;
            npc.Memory.KnownObjects.Clear();
            npc.Perception.Objects.Clear();
            npc.Memory.KnownObjects[live.Id] = new ObjectMemory
            {
                Id = live.Id, DefinitionId = ContentIds.Coconut, Tile = new TileCoord(33, 44), LastSeenTick = 900
            };
            var otherDefinition = world.Content.ObjectDefinitions.Keys.First(id => !id.StartsWith("food.coconut"));
            for (var i = 0; i < 24; i++)
            {
                var otherId = new ObjectId(500001 + i);
                npc.Memory.KnownObjects[otherId] = new ObjectMemory { Id = otherId, DefinitionId = otherDefinition,
                    Tile = npc.Tile, LastSeenTick = 999 };
            }
            var summary = LlmDecisionContextBuilder.Build(world, npc).MemorySummary;
            Assert.That(summary, Does.Not.Contain("id=" + live.Id.Value + ", definition=" + ContentIds.Coconut),
                "This known coconut is outside the old 24-row summary budget");
            var query = new McpKnownObjectObservations.Query("food.coconut", null, 16);
            var before = JsonSerializer.Serialize(McpKnownObjectObservations.Read(world, npc, query), McpJson.Options);
            live.Tile = new TileCoord(-70, -70);
            live.ResourceAmount = 0;
            live.DefinitionId = "private-hidden-change";
            Assert.That(JsonSerializer.Serialize(McpKnownObjectObservations.Read(world, npc, query), McpJson.Options), Is.EqualTo(before));
            world.Entities.Objects.Remove(live.Id);
            Assert.That(JsonSerializer.Serialize(McpKnownObjectObservations.Read(world, npc, query), McpJson.Options), Is.EqualTo(before));
            using var json = JsonDocument.Parse(before);
            var row = json.RootElement.GetProperty("objects")[0];
            Assert.That(row.GetProperty("lastKnownTile").GetProperty("q").GetInt32(), Is.EqualTo(33));
            var tileCenter = HexLive.Simulation.Spatial.HexSpatialMath.TileToWorld(new TileCoord(33, 44));
            Assert.That(row.GetProperty("lastKnownTileCenter").GetProperty("x").GetSingle(), Is.EqualTo(tileCenter.X));
            Assert.That(row.GetProperty("lastKnownTileCenter").GetProperty("y").GetSingle(), Is.EqualTo(tileCenter.Y));
            Assert.That(row.GetProperty("ageTicks").GetInt32(), Is.EqualTo(100));
            foreach (var forbidden in new[] { "lastKnownPosition", "reachable", "occupied", "amount", "resourceAmount", "owner", "durability", "exists" })
                Assert.That(row.TryGetProperty(forbidden, out _), Is.False, forbidden);
            Assert.That(npc.Memory.KnownObjects.Count, Is.EqualTo(25), "Read does not maintain or invalidate memory");
            return true;
        });
    }

    [Test]
    public void ExpiryHomeKnowledgeAndBoundedOrderDoNotDependOnDictionaryInsertionOrder()
    {
        using var host = CreateHost();
        host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            world.Tick = 10000;
            npc.Memory.KnownObjects.Clear();
            for (var i = 100; i >= 1; i--)
            {
                var id = new ObjectId(i);
                npc.Memory.KnownObjects[id] = new ObjectMemory { Id = id, DefinitionId = ContentIds.Coconut,
                    Tile = npc.Tile, LastSeenTick = world.Tick - AiBalance.MemoryTtlTicks };
            }
            var expired = new ObjectId(200);
            npc.Memory.KnownObjects[expired] = new ObjectMemory { Id = expired, DefinitionId = ContentIds.Coconut,
                Tile = npc.Tile, LastSeenTick = world.Tick - AiBalance.MemoryTtlTicks - 1 };
            var home = new ObjectId(201);
            npc.Memory.KnownObjects[home] = new ObjectMemory { Id = home, DefinitionId = ContentIds.Coconut,
                Tile = npc.Tile, LastSeenTick = 0, IsPermanent = true };
            var query = new McpKnownObjectObservations.Query("food.coconut", null, 16);
            var result = McpKnownObjectObservations.Read(world, npc, query);
            Assert.That(result.totalMatches, Is.EqualTo(101));
            Assert.That(result.truncated, Is.True);
            Assert.That(result.objects.Select(r => r.objectId), Is.EqualTo(Enumerable.Range(1, 16)));
            var stored = npc.Memory.KnownObjects.Values.ToArray();
            npc.Memory.KnownObjects.Clear();
            foreach (var item in stored.Reverse()) npc.Memory.KnownObjects.Add(item.Id, item);
            Assert.That(JsonSerializer.Serialize(McpKnownObjectObservations.Read(world, npc, query)), Is.EqualTo(JsonSerializer.Serialize(result)));
            Assert.That(npc.Memory.KnownObjects.Count, Is.EqualTo(102), "Existing maintenance owns expiry");
            return true;
        });
    }

    [Test]
    public void QueryAllocationDependsOnOutputLimitRatherThanRememberedCount()
    {
        using var host = CreateHost();
        host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            npc.Memory.KnownObjects.Clear();
            void Add(int from, int through)
            {
                for (var i = from; i <= through; i++)
                {
                    var id = new ObjectId(i);
                    npc.Memory.KnownObjects[id] = new ObjectMemory { Id = id, DefinitionId = ContentIds.Coconut,
                        Tile = npc.Tile, LastSeenTick = world.Tick, IsPermanent = true };
                }
            }
            var query = new McpKnownObjectObservations.Query("food.coconut", null, 16);
            var calibrationStart = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[4096]);
            var calibration = GC.GetAllocatedBytesForCurrentThread() - calibrationStart;
            (long Bytes, double MedianUs, double P95Us, double MaxUs, int PayloadBytes) Sample()
            {
                for (var i = 0; i < 100; i++) McpKnownObjectObservations.Read(world, npc, query);
                var timings = new long[100];
                McpKnownObjectObservations.View? last = null;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < timings.Length; i++)
                {
                    var tick = Stopwatch.GetTimestamp();
                    last = McpKnownObjectObservations.Read(world, npc, query);
                    timings[i] = Stopwatch.GetTimestamp() - tick;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Array.Sort(timings);
                var payload = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(last, McpJson.Options));
                return (bytes, timings[50] * 1_000_000d / Stopwatch.Frequency,
                    timings[95] * 1_000_000d / Stopwatch.Frequency, timings[99] * 1_000_000d / Stopwatch.Frequency, payload);
            }
            Add(1, 256);
            var small = Sample();
            Add(257, 4096); // Oversized permanent home knowledge must still have bounded request storage.
            var large = Sample();
            Assert.That(calibration, Is.GreaterThanOrEqualTo(4096));
            Assert.That(small.Bytes, Is.GreaterThan(0));
            Assert.That(large.Bytes, Is.LessThanOrEqualTo(small.Bytes), "No allocation growth with source count");
            object Profile(int count, (long Bytes, double MedianUs, double P95Us, double MaxUs, int PayloadBytes) sample) =>
                new { memoryRecords = count, calls = 100, limit = 16, allocatedBytesPerRead = sample.Bytes / 100d,
                    medianUs = sample.MedianUs, p95Us = sample.P95Us, maxUs = sample.MaxUs, payloadUtf8Bytes = sample.PayloadBytes };
            var result = new { calibration, ordinary = Profile(256, small), largeHomeKnowledge = Profile(4096, large) };
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "SimData/simdata.json"))) root = root.Parent;
            var path = Path.Combine(root!.FullName, "Build", "bug388-validation");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "query-profile.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.WriteLine(JsonSerializer.Serialize(result));
            return true;
        });
    }

    private static WorldHost CreateHost()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SimData/simdata.json"))) root = root.Parent;
        return new WorldHost(388, GameMode.Feud,
            Path.Combine(Path.GetTempPath(), "mcp-known-" + Guid.NewGuid().ToString("N") + ".sav"),
            Path.Combine(root!.FullName, "SimData/simdata.json"), false);
    }
}
