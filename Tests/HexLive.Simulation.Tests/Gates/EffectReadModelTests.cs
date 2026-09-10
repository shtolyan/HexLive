using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class EffectReadModelTests
{
    [Test]
    public void StreamingAndLegacyListPreserveStatusMatrixAndSnapshotRows()
    {
        var world = TestWorld.CreateWorld(393);
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();
        npc.Wounds.Clear();
        npc.Needs.Hunger = 0.5f;
        npc.Needs.Comfort = 0.5f;
        npc.Needs.Sunburn = 0f;
        npc.Needs.ThermalComfort = 0f;
        npc.Mind.ComaCause = ComaCause.None;
        var cases = new (Action Set, EffectKind Kind, string Detail)[]
        {
            (() => npc.Needs.Hunger = 0.1f, EffectKind.WellFed, ""),
            (() => npc.Needs.Comfort = 0.9f, EffectKind.Content, ""),
            (() => npc.Needs.Sunburn = 0.31f, EffectKind.Sunburnt, ""),
            (() => npc.Needs.ThermalComfort = 0.5f, EffectKind.Hot, ""),
            (() => npc.Needs.ThermalComfort = 0.9f, EffectKind.Heatstroke, ""),
            (() => npc.Needs.ThermalComfort = -0.5f, EffectKind.Cold, ""),
            (() => npc.Needs.ThermalComfort = -0.9f, EffectKind.Freezing, ""),
            (() => npc.Mind.ComaCause = ComaCause.BloodLoss, EffectKind.Coma, "effect.coma.reason.bloodloss"),
        };
        foreach (var item in cases)
        {
            item.Set();
            var sink = new Rows();
            EffectReadModel.Visit(world, npc, ref sink);
            Assert.That(sink.Effects.Any(e => e.Kind == item.Kind && e.DetailKey == item.Detail), Is.True, item.Kind.ToString());
            var legacy = new List<ActiveEffect> { new(EffectKind.PlayingDead, 1f) };
            var fire = TemperatureSystem.NearbyFireWarmth(world, npc.Tile, out _) > 0;
            var bed = npc.Execution.CurrentInteraction == InteractionType.Sleep &&
                npc.Execution.TargetObject is { } id && world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.BedBasic;
            EffectEvaluator.Collect(npc, world.Tick, TemperatureSystem.EffectiveUv(world, npc.Tile), fire, bed, legacy);
            Assert.That(sink.Effects, Is.EqualTo(legacy), "List API still clears and preserves exact floats/order/details");
            var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id == npc.Id);
            Assert.That(snapshot.Effects, Is.EqualTo(sink.Effects.Select(Encoded)), "Existing UI wire format/order");
        }
        var empty = new List<ActiveEffect> { new(EffectKind.WellFed, 1f) };
        EffectEvaluator.Collect(null, 0, 0, false, false, empty);
        Assert.That(empty, Is.Empty);
    }

    [Test]
    public void ReadModelDeduplicatesCadenceButRetainsOppositeDirectionsAndDoesNotMutate()
    {
        var world = TestWorld.CreateWorld(393);
        var npc = world.Entities.Npcs.Values.First();
        npc.EffectImpacts.Clear(EffectImpactCadence.Fast);
        npc.EffectImpacts.Clear(EffectImpactCadence.Slow);
        npc.EffectImpacts.Record(NeedKind.Comfort, EffectKind.Cozy, EffectImpactDirection.Positive, EffectImpactCadence.Fast);
        npc.EffectImpacts.Record(NeedKind.Comfort, EffectKind.Cozy, EffectImpactDirection.Positive, EffectImpactCadence.Slow);
        npc.EffectImpacts.Record(NeedKind.Comfort, EffectKind.Cozy, EffectImpactDirection.Negative, EffectImpactCadence.Slow);
        // Slow's direction replacement leaves one positive fast and one negative slow.
        npc.EffectImpacts.Record(NeedKind.Blood, EffectKind.Bleeding, EffectImpactDirection.Negative, EffectImpactCadence.Fast);
        npc.EffectImpacts.Record(NeedKind.Blood, EffectKind.Bleeding, EffectImpactDirection.Negative, EffectImpactCadence.Slow);
        var before = npc.EffectImpacts.Items.ToArray();
        var comfort = npc.Needs.Comfort;
        var blood = npc.Needs.Blood;
        var sink = new Rows();
        EffectReadModel.Visit(world, npc, ref sink);
        Assert.Multiple(() =>
        {
            Assert.That(sink.Impacts.Count, Is.EqualTo(3));
            Assert.That(sink.Impacts.Count(i => i.Kind == EffectKind.Cozy), Is.EqualTo(2));
            Assert.That(npc.EffectImpacts.Items, Is.EqualTo(before));
            Assert.That(npc.Needs.Comfort, Is.EqualTo(comfort));
            Assert.That(npc.Needs.Blood, Is.EqualTo(blood));
        });
        npc.EffectImpacts.Clear(EffectImpactCadence.Fast);
        sink = new Rows();
        EffectReadModel.Visit(world, npc, ref sink);
        Assert.That(sink.Impacts.Count, Is.EqualTo(2), "Fast clear preserves Slow owner");
    }

    [Test]
    public void WarmReaderAddsNoAllocationBeyondExistingContextWithCalibratedCounter()
    {
        var world = TestWorld.CreateWorld(393);
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Sunburn = 0.6f;
        npc.Needs.Hunger = 0.1f;
        npc.EffectImpacts.Record(NeedKind.Comfort, EffectKind.Sunburnt, EffectImpactDirection.Negative, EffectImpactCadence.Slow);
        var sink = new Counter();
        for (var i = 0; i < 1000; i++) EffectReadModel.Visit(world, npc, ref sink);
        var start = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[4096]);
        var calibration = GC.GetAllocatedBytesForCurrentThread() - start;
        var samples = new long[2000];
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++)
        {
            var tick = Stopwatch.GetTimestamp();
            EffectReadModel.Visit(world, npc, ref sink);
            samples[i] = Stopwatch.GetTimestamp() - tick;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        var uv = TemperatureSystem.EffectiveUv(world, npc.Tile);
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++) EffectReadModel.Visit(world, npc, uv, ref sink);
        var precomputedUvBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++)
            EffectEvaluator.Collect(npc, world.Tick, uv, false, false, ref sink);
        var classifierBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < samples.Length; i++)
            uv += TemperatureSystem.EffectiveUv(world, npc.Tile);
        var existingUvBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        GC.KeepAlive(uv);
        Array.Sort(samples);
        var result = new { calibrationBytes = calibration, readerBytes = allocated,
            precomputedUvBytes, classifierBytes, existingUvBytes, addedReaderBytes = allocated - existingUvBytes,
            calls = samples.Length,
            medianUs = samples[samples.Length / 2] * 1_000_000d / Stopwatch.Frequency,
            p95Us = samples[samples.Length * 95 / 100] * 1_000_000d / Stopwatch.Frequency,
            maxUs = samples[^1] * 1_000_000d / Stopwatch.Frequency, sink.Count };
        var path = Path.Combine(RepoPaths.Root, "Build", "bug393-validation");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "reader-profile.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.WriteLine(JsonSerializer.Serialize(result));
        Assert.That(calibration, Is.GreaterThanOrEqualTo(4096), "Counter must detect the control allocation");
        Assert.That(classifierBytes, Is.Zero, "Struct collector must not box");
        Assert.That(precomputedUvBytes, Is.Zero, "Snapshot path reuses its existing UV value");
        Assert.That(allocated, Is.EqualTo(existingUvBytes), "Full reader adds no allocation beyond existing shared UV probe");
    }

    private static string Encoded(ActiveEffect effect) => effect.Kind + "\t" +
        effect.Intensity.ToString("0.###", CultureInfo.InvariantCulture) +
        (effect.DetailKey.Length == 0 ? "" : "\t" + effect.DetailKey);

    private struct Rows : IEffectSink
    {
        public readonly List<ActiveEffect> Effects;
        public readonly List<EffectImpact> Impacts;
        public Rows() { Effects = new(); Impacts = new(); }
        public void Add(ActiveEffect effect) => Effects.Add(effect);
        public void Add(EffectImpact impact) => Impacts.Add(impact);
    }
    private struct Counter : IEffectSink
    {
        public int Count;
        public void Add(ActiveEffect effect) => Count++;
        public void Add(EffectImpact impact) => Count++;
    }
}
