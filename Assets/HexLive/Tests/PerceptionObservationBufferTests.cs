using System;
using System.Diagnostics;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Tests
{

public sealed class PerceptionObservationBufferTests
{
    // Also exposed to a bounded Editor reflection probe when the MCP test
    // runner cannot stream NUnit's captured output.
    public static string HotProfile;
    public static string ChurnProfile;
    public static string ProductionProfile;
    [Test]
    public void PassedClothingAndWoundedPersonKeepObservedCoordinatesAndFrozenState()
    {
        var world = new WorldState();
        var observer = new NPCState { Id = new EntityId(1), Tile = new TileCoord(2, 3) };
        var person = new NPCState { Id = new EntityId(2), DisplayName = "Dasha" };
        world.Entities.Npcs[person.Id] = person;
        var seen = new PerceivedAgent { Id = person.Id, CanSee = true, Tile = new TileCoord(3, 3),
            Suffering = .8f, AidKind = AidKind.Treat, IsDying = true };
        observer.Perception.Agents.Add(seen);
        observer.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(100),
            DefinitionId = "clothing.fixture", Tile = new TileCoord(2, 4) });
        using var buffer = new PerceptionObservationBuffer();
        Capture(buffer, world, observer, 10);
        // The sensor reuses mutable rows; the hidden target is allowed to move,
        // heal, change name or disappear without retroactively rewriting sight.
        seen.Tile = new TileCoord(90, 90);
        seen.Suffering = 0;
        person.DisplayName = "hidden-new-name";
        observer.Perception.Agents.Clear();
        observer.Perception.Objects.Clear();
        world.Entities.Npcs.Clear();
        Capture(buffer, world, observer, 40);
        var batch = buffer.Read("", 0, 120);
        var patient = batch.Observations.Single(o => o.Kind == "npc" && !o.Significant);
        var item = batch.Observations.Single(o => o.Kind == "object");
        {
            Assert.That(patient.NameId, Is.EqualTo("Dasha"));
            Assert.That(patient.Suffering, Is.EqualTo(.8f));
            Assert.That(patient.Dying, Is.True);
            Assert.That(patient.AidKind, Is.EqualTo(AidKind.Treat));
            Assert.That(patient.TileQ, Is.EqualTo(3));
            Assert.That(patient.ObserverTileQ, Is.EqualTo(2));
            Assert.That(patient.LastSeenTick, Is.EqualTo(10));
            Assert.That(patient.AgeTicks, Is.EqualTo(110));
            Assert.That(patient.InLatestPerception, Is.False);
            Assert.That(item.DefinitionId, Is.EqualTo("clothing.fixture"));
            Assert.That(item.TileR, Is.EqualTo(4));
            Assert.That(item.InLatestPerception, Is.False);
        }
    }

    [Test]
    public void InjuryThatHealsBetweenTurnsRemainsAnEventAlongsideLatestHealthyState()
    {
        var world = new WorldState();
        var npc = new NPCState { Id = new EntityId(1) };
        var id = new EntityId(2);
        world.Entities.Npcs[id] = new NPCState { Id = id };
        var seen = new PerceivedAgent { Id = id, CanSee = true };
        npc.Perception.Agents.Add(seen);
        using var buffer = new PerceptionObservationBuffer();
        Capture(buffer, world, npc, 1);
        var before = buffer.Read("", 0, 1);
        seen.IsDying = true;
        seen.Suffering = 1;
        seen.AidKind = AidKind.Treat;
        seen.Tile = new TileCoord(4, 5);
        Capture(buffer, world, npc, 2);
        seen.IsDying = false;
        seen.Suffering = 0;
        seen.AidKind = AidKind.None;
        seen.Tile = new TileCoord(7, 8);
        Capture(buffer, world, npc, 3);
        var batch = buffer.Read(before.Epoch, before.Watermark, 3);
        var injury = batch.Observations.Single(x => x.Significant);
        var latest = batch.Observations.Single(x => !x.Significant);
        Assert.That(injury.Dying, Is.True);
        Assert.That(injury.LastSeenTick, Is.EqualTo(2));
        Assert.That(injury.TileQ, Is.EqualTo(4));
        Assert.That(latest.Dying, Is.False);
        Assert.That(latest.LastSeenTick, Is.EqualTo(3));
        Assert.That(latest.TileQ, Is.EqualTo(7));
    }

    [Test]
    public void MemoryAndHearingNeverBecomeNewSightings()
    {
        var world = new WorldState();
        var npc = new NPCState();
        world.Entities.Npcs[new EntityId(7)] = new NPCState();
        npc.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(8), FromMemory = true });
        npc.Perception.Agents.Add(new PerceivedAgent { Id = new EntityId(7), CanHear = true });
        npc.Perception.Remembered.Add(new RememberedAgent { Id = new EntityId(9) });
        using var buffer = new PerceptionObservationBuffer();
        Capture(buffer, world, npc, 10);
        Assert.That(buffer.Read("", 0, 10).Observations, Is.Empty);
    }

    [Test]
    public void CursorPreservesSightingsDuringModelLatencyAndRepeatReadDoesNotConsume()
    {
        var world = new WorldState();
        var npc = new NPCState();
        using var buffer = new PerceptionObservationBuffer();
        npc.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(10) });
        Capture(buffer, world, npc, 10);
        var first = buffer.Read("", 0, 10);
        Assert.That(buffer.Read("", 0, 10).Watermark, Is.EqualTo(first.Watermark));
        npc.Perception.Objects[0].Tile = new TileCoord(8, 9);
        Capture(buffer, world, npc, 20);
        var duringRequest = buffer.Read(first.Epoch, first.Watermark, 20);
        Assert.That(duringRequest.Observations.Single().TileQ, Is.EqualTo(8));
        Assert.That(duringRequest.Reset, Is.False);
        Assert.That(buffer.Read(duringRequest.Epoch, duringRequest.Watermark, 20).Observations, Is.Empty);

        var id = new EntityId(2);
        world.Entities.Npcs[id] = new NPCState { Id = id };
        var person = new PerceivedAgent { Id = id, CanSee = true, IsDying = true, Suffering = 1 };
        npc.Perception.Objects.Clear();
        npc.Perception.Agents.Add(person);
        Capture(buffer, world, npc, 30);
        var severeRead = buffer.Read("", 0, 30);
        person.IsDying = false;
        person.Suffering = 0;
        Capture(buffer, world, npc, 31);
        person.Suffering = .2f;
        Capture(buffer, world, npc, 32);
        person.Suffering = 0;
        Capture(buffer, world, npc, 33);
        var afterSevereRead = buffer.Read(severeRead.Epoch, severeRead.Watermark, 33);
        var eventDuringRequest = afterSevereRead.Observations.Single(x => x.Significant);
        Assert.That(eventDuringRequest.Sequence, Is.GreaterThan(severeRead.Watermark));
        Assert.That(eventDuringRequest.Dying, Is.True, "Coalescing keeps the worst observed facts.");
        Assert.That(eventDuringRequest.LastSeenTick, Is.EqualTo(30), "Refreshing the cursor must not invent a newer injury time.");
        Assert.That(buffer.Read(afterSevereRead.Epoch, afterSevereRead.Watermark, 33).Observations, Is.Empty);
    }

    [Test]
    public void OverflowIsBoundedExplicitAndDoesNotDisplacePatientsWithOrdinaryPassersBy()
    {
        var world = new WorldState();
        var npc = new NPCState { Id = new EntityId(1) };
        using var buffer = new PerceptionObservationBuffer();
        for (var i = 0; i < PerceptionObservationBuffer.NpcCapacity + 12; i++)
        {
            var id = new EntityId(i + 2);
            world.Entities.Npcs[id] = new NPCState { Id = id };
            npc.Perception.Agents.Clear();
            npc.Perception.Agents.Add(new PerceivedAgent { Id = id, CanSee = true,
                IsDying = i < PerceptionObservationBuffer.NpcCapacity });
            Capture(buffer, world, npc, i + 1);
        }
        npc.Perception.Agents.Clear();
        for (var i = 0; i < 10000; i++)
        {
            npc.Perception.Objects.Clear();
            npc.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(i) });
            Capture(buffer, world, npc, 100 + i);
        }
        var batch = buffer.Read("", 0, 20000);
        Assert.That(batch.Gap, Is.True);
        Assert.That(batch.Observations.Count(x => x.Kind == "object"), Is.EqualTo(PerceptionObservationBuffer.ObjectCapacity));
        var patients = batch.Observations.Where(x => x.Significant).ToArray();
        Assert.That(patients.Length, Is.EqualTo(PerceptionObservationBuffer.SignificantNpcCapacity));
        Assert.That(patients.All(x => x.Dying), Is.True);
        Assert.That(batch.Observations.Any(x => x.Kind == "npc" && !x.Significant && !x.Dying), Is.True,
            "Old patients must not fill the ordinary NPC category forever.");
        Assert.That(buffer.Read(batch.Epoch, batch.Watermark, 20000).Gap, Is.False);
        Assert.That(buffer.Read(batch.Epoch, 0, 20000).Observations, Is.Empty, "Acknowledged slots release their retained sightings.");
    }

    [Test]
    public void DetachReleasesSightingsEvenWithoutAnotherWorldTickAndRollbackStartsNewEpoch()
    {
        var world = new WorldState();
        var npc = new NPCState();
        var buffer = new PerceptionObservationBuffer();
        npc.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(1) });
        Capture(buffer, world, npc, 50);
        var before = buffer.Read("", 0, 50);
        npc.Perception.Objects.Clear();
        Capture(buffer, world, npc, 2);
        var after = buffer.Read(before.Epoch, before.Watermark, 2);
        Assert.That(after.Reset, Is.True);
        Assert.That(after.Epoch, Is.Not.EqualTo(before.Epoch));
        Assert.That(after.Observations, Is.Empty);
        buffer.Dispose();
        Assert.That(buffer.Capture(world, npc), Is.False);
        Assert.That(buffer.Read("", 0, 2).Observations, Is.Empty);
    }

    [Test]
    public void FullBufferHotCaptureAllocatesZeroAndReportsMeasuredTime()
    {
        var world = new WorldState();
        var npc = new NPCState { Id = new EntityId(1) };
        for (var i = 0; i < PerceptionObservationBuffer.ObjectCapacity; i++)
            npc.Perception.Objects.Add(new PerceivedObject { Id = new ObjectId(i), DefinitionId = "item.fixture" });
        for (var i = 0; i < PerceptionObservationBuffer.NpcCapacity; i++)
        {
            var id = new EntityId(i + 2);
            world.Entities.Npcs[id] = new NPCState { Id = id, DisplayName = "Dasha" };
            npc.Perception.Agents.Add(new PerceivedAgent { Id = id, CanSee = true, Suffering = .5f });
        }
        for (var i = 0; i < PerceptionObservationBuffer.MobCapacity; i++)
            npc.Perception.Mobs.Add(new PerceivedMob { Id = i, MobId = "mob.fixture" });
        var constructionBytes = GC.GetAllocatedBytesForCurrentThread();
        using var buffer = new PerceptionObservationBuffer();
        constructionBytes = GC.GetAllocatedBytesForCurrentThread() - constructionBytes;
        for (var i = 0; i < 100; i++) Capture(buffer, world, npc, i);
        const int iterations = 10000;
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) Capture(buffer, world, npc, 100 + i);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        TestContext.WriteLine(HotProfile = $"[bug389-profile] full-buffer iterations={iterations} entries=208 totalMs={ms:F3} usPerCapture={ms * 1000 / iterations:F3} allocatedBytes={allocated} constructionBytes={constructionBytes}");
        Assert.That(allocated, Is.Zero, "Hot capture must not allocate even for mobs and injury data.");
        Assert.That(buffer.Read("", 0, 20000).Observations.Length, Is.EqualTo(208));
        // The churn case exercises eviction/dictionary free-slot reuse rather
        // than only stable IDs. Reuse the sensor row so measurement is buffer-only.
        npc.Perception.Objects.RemoveRange(1, npc.Perception.Objects.Count - 1);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            npc.Perception.Objects[0].Id = new ObjectId(1000 + i);
            Capture(buffer, world, npc, 20000 + i);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        TestContext.WriteLine(ChurnProfile = $"[bug389-profile] churn iterations={iterations} totalMs={ms:F3} usPerCapture={ms * 1000 / iterations:F3} allocatedBytes={allocated}");
        Assert.That(allocated, Is.Zero, "Unique-object churn must not grow the dictionary or allocate new slots.");
        Assert.That(buffer.Read("", 0, 40000).Observations.Length, Is.EqualTo(208));
    }

    [Test]
    public void ProductionSensorFeedsBufferAndReportsBeforeAfterCost()
    {
        var definition = PrototypeWorldDefinitionFactory.Create(12345);
        var world = new WorldStateFactory().Create(definition);
        var sensor = new PerceptionSystem();
        var npc = world.Entities.Npcs.Values.First(n => n.Faction == Faction.Colony);
        var traceBefore = SimTrace.Enabled;
        var perceptionTraceBefore = SimTrace.Perception;
        SimTrace.Enabled = false;
        SimTrace.Perception = false;
        try
        {
            for (var i = 0; i < 20; i++) { world.Tick++; sensor.Run(world); }
            using var buffer = new PerceptionObservationBuffer();
            var samples = new double[2];
            var allocations = new long[2];
            // Alternate warmed batches; same sensor and body state, only the
            // optional buffer changes. No engine/body steps contaminate comparison.
            for (var round = 0; round < 8; round++)
            {
                var withBuffer = round % 2;
                npc.Perception.Observations = withBuffer == 1 ? buffer : null;
                world.Tick++; sensor.Run(world);
                var start = Stopwatch.GetTimestamp();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 30; i++) { world.Tick++; sensor.Run(world); }
                allocations[withBuffer] += GC.GetAllocatedBytesForCurrentThread() - bytes;
                samples[withBuffer] += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            var batch = buffer.Read("", 0, world.Tick);
            TestContext.WriteLine(ProductionProfile = $"[bug389-profile] production seed=12345 npcs={world.Entities.Npcs.Count} iterationsPerMode=120 baselineMs={samples[0]:F3} bufferedMs={samples[1]:F3} baselineAlloc={allocations[0]} bufferedAlloc={allocations[1]} sightings={batch.Observations.Length}");
            Assert.That(batch.Observations.Length, Is.GreaterThan(0), "The actual production sensor must invoke capture.");
            Assert.That(batch.Observations.Any(x => x.Kind == "object"), Is.True);
            Assert.That(world.Entities.Npcs.Values.Count(n => n.Perception.Observations != null), Is.EqualTo(1));
        }
        finally { SimTrace.Enabled = traceBefore; SimTrace.Perception = perceptionTraceBefore; npc.Perception.Observations = null; }
    }

    private static void Capture(PerceptionObservationBuffer buffer, WorldState world, NPCState npc, int tick)
    {
        world.Tick = tick;
        npc.Perception.LastUpdatedTick = tick;
        buffer.Capture(world, npc);
    }
}

}
