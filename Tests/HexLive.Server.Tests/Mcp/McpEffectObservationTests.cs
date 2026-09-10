using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HexLive.Server.Mcp;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp;

public sealed class McpEffectObservationTests
{
    [Test]
    public void DescribeMatchesUiStatusesAndActualComfortBloodCauses()
    {
        using var host = CreateHost();
        var expected = host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            npc.Needs.Hunger = 0.1f;
            npc.Needs.Comfort = 0.9f;
            npc.Needs.Sunburn = 0.6f;
            npc.Needs.Blood = 0.7f;
            npc.Wounds.Add(new WoundState { Zone = BodyPart.Torso, Severity = 0.2f, Heal01 = 0f, Clot01 = 0f });
            npc.Body.Parts[BodyPart.Torso] = 0.3f;
            new NeedsDecaySystem().Run(world);
            new TemperatureSystem().Run(world);
            return WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id == npc.Id);
        });
        using var response = Describe(host, expected.Id.Value);
        var root = response.RootElement;
        Assert.That(StatusRows(root), Is.EqualTo(expected.Effects));
        Assert.That(ImpactRows(root), Is.EqualTo(expected.EffectImpacts));
        var kinds = root.GetProperty("effects").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.That(kinds, Does.Contain("WellFed").And.Contain("Content").And.Contain("Sunburnt").And.Contain("Bleeding"));
        Assert.That(ImpactRows(root), Does.Contain("Comfort\tSunburnt\tNegative").And.Contain("Blood\tBleeding\tNegative"));
        Assert.That(root.GetProperty("effectTerms").GetProperty("effect.wellfed.title").GetProperty("ru").GetString(), Is.EqualTo("Сыта"));
        host.Read(world =>
        {
            var npc = world.Entities.Npcs[expected.Id];
            foreach (var wound in npc.Wounds) { wound.Clot01 = 1f; wound.Stabilized = true; }
            return true;
        });
        using var clotted = Describe(host, expected.Id.Value);
        Assert.That(clotted.RootElement.GetProperty("effects").EnumerateArray().Any(e => e.GetProperty("kind").GetString() == "Bleeding"), Is.False);
    }

    [Test]
    public void EveryCatalogNeedAndConcreteCauseResolvesWithoutMissingTermFailures()
    {
        using var host = CreateHost();
        host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            foreach (var kind in Enum.GetValues<EffectKind>())
                npc.EffectImpacts.Record(NeedKind.Comfort, kind, EffectImpactDirection.Negative, EffectImpactCadence.Fast);
            foreach (var need in Enum.GetValues<NeedKind>())
                npc.EffectImpacts.Record(need, EffectKind.NaturalDecay, EffectImpactDirection.Negative, EffectImpactCadence.Slow);
            var all = McpEffectObservations.Read(world, npc);
            Assert.That(all.Definitions.Keys, Is.EquivalentTo(Enum.GetNames<EffectKind>()));
            foreach (var value in all.Terms.Values)
            { Assert.That(value.en, Is.Not.Empty); Assert.That(value.ru, Is.Not.Empty); }

            var setups = new (Action Set, string Expected)[]
            {
                (() => npc.Mind.ComaCause = ComaCause.BloodLoss, "effect.coma.reason.bloodloss"),
                (() => npc.Mind.ComaCause = ComaCause.Exhaustion, "effect.fainted.reason.energy"),
                (() => { npc.Mind.ComaCause = ComaCause.None; npc.Mind.FaintedUntilTick = world.Tick + 10; npc.Needs.Blood = 0.2f; }, "effect.fainted.reason.bloodloss"),
                (() => { npc.Needs.Blood = 1f; npc.Needs.Hunger = 0.95f; }, "effect.fainted.reason.starvation"),
                (() => npc.Needs.Hunger = 0.5f, "effect.fainted.reason.exhaustion"),
                (() => npc.Mind.CryingUntilTick = world.Tick + 10, "effect.crying.reason.breakdown"),
                (() => npc.Mind.DyingCause = DyingCause.BloodLoss, "effect.dying.reason.bloodloss"),
                (() => npc.Mind.DyingCause = DyingCause.VitalCrushed, "effect.dying.reason.vitalcrushed"),
                (() => npc.Mind.DyingCause = DyingCause.Starvation, "effect.dying.reason.starvation"),
                (() => npc.Mind.DyingCause = DyingCause.Dehydration, "effect.dying.reason.dehydration"),
            };
            foreach (var setup in setups)
            {
                setup.Set();
                var view = McpEffectObservations.Read(world, npc);
                Assert.That(view.Effects.Any(e => e.detailKey == setup.Expected), Is.True, setup.Expected);
                Assert.That(view.Terms[setup.Expected].ru, Is.Not.Empty, setup.Expected);
                var snapshot = WorldSnapshotExporter.Export(world).Npcs.Single(n => n.Id == npc.Id);
                Assert.That(snapshot.Effects, Is.EqualTo(view.Effects.Select(e => e.kind + "\t" +
                    e.intensity.ToString("0.###", CultureInfo.InvariantCulture) + (e.detailKey.Length == 0 ? "" : "\t" + e.detailKey))));
            }
            return true;
        });
    }

    [Test]
    public void DefinitionsAreLimitedToThisBodyAndForeignPrivateEffectsStayHidden()
    {
        using var host = CreateHost();
        var ids = host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            var other = world.Entities.Npcs.Values.First(n => n.Id != npc.Id);
            npc.Needs.Sunburn = 0;
            npc.SunExposure = 0;
            npc.Perception.Agents.Clear();
            npc.Perception.Hostiles.Clear();
            other.Needs.Sunburn = 1f;
            other.Mind.DyingCause = DyingCause.Dehydration;
            npc.Perception.Agents.Add(new PerceivedAgent { Id = other.Id, CanSee = true, Faction = other.Faction });
            return (npc.Id.Value, other.Id.Value);
        });
        using var response = Describe(host, ids.Item1);
        var root = response.RootElement;
        var used = root.GetProperty("effects").EnumerateArray().Select(e => e.GetProperty("kind").GetString())
            .Concat(root.GetProperty("effectImpacts").EnumerateArray().Select(e => e.GetProperty("kind").GetString())).Distinct();
        Assert.That(root.GetProperty("effectDefinitions").EnumerateObject().Select(e => e.Name), Is.EquivalentTo(used));
        Assert.That(root.GetProperty("effectDefinitions").TryGetProperty("Sunburnt", out _), Is.False);
        foreach (var visible in root.GetProperty("visibleNpcs").EnumerateArray())
        { Assert.That(visible.TryGetProperty("effects", out _), Is.False); Assert.That(visible.TryGetProperty("effectImpacts", out _), Is.False); }
        var tools = new McpTools(host, new ControlLeases(45));
        var denied = tools.Call("describe_colonist", JsonSerializer.SerializeToElement(new { npcId = ids.Item2 }), "mcp:393", out var error, id => id == ids.Item1);
        Assert.That(error, Is.True);
        Assert.That(denied, Does.Contain("NpcAccessDenied").And.Not.Contain("effects"));
    }

    [Test]
    public void RequestAllocationAndPayloadAreMeasuredSeparatelyFromReaderOverhead()
    {
        using var host = CreateHost();
        var id = host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First();
            npc.Needs.Sunburn = 0.6f;
            npc.Needs.Hunger = 0.1f;
            npc.EffectImpacts.Record(NeedKind.Comfort, EffectKind.Sunburnt, EffectImpactDirection.Negative, EffectImpactCadence.Slow);
            return npc.Id.Value;
        });
        var effectBlock = host.Read(world =>
        {
            var npc = world.Entities.Npcs.Values.First(n => n.Id.Value == id);
            return MeasureJson(() =>
            {
                var view = McpEffectObservations.Read(world, npc);
                return JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["effects"] = view.Effects, ["effectImpacts"] = view.Impacts,
                    ["effectDefinitions"] = view.Definitions, ["effectTerms"] = view.Terms,
                }, McpJson.Options);
            });
        });
        var tools = new McpTools(host, new ControlLeases(45));
        var arguments = JsonSerializer.SerializeToElement(new { npcId = id });
        var fullDescribe = MeasureJson(() =>
        {
            var json = tools.Call("describe_colonist", arguments, "mcp:393", out var error);
            if (error) throw new InvalidOperationException(json);
            return json;
        });
        var result = new { effectBlock, fullDescribe };
        var path = Path.Combine(Root(), "Build", "bug393-validation");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mcp-payload-profile.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.WriteLine(JsonSerializer.Serialize(result));
    }

    private static object MeasureJson(Func<string> readAndSerialize)
    {
        for (var i = 0; i < 100; i++) readAndSerialize();
        var timings = new long[200];
        var bytes = 0;
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < timings.Length; i++)
        {
            var tick = Stopwatch.GetTimestamp();
            var json = readAndSerialize();
            bytes = Encoding.UTF8.GetByteCount(json);
            timings[i] = Stopwatch.GetTimestamp() - tick;
        }
        var allocations = GC.GetAllocatedBytesForCurrentThread() - start;
        Array.Sort(timings);
        Assert.That(bytes, Is.GreaterThan(0));
        return new { calls = timings.Length, allocatedBytesPerReadAndJson = allocations / timings.Length,
            payloadUtf8Bytes = bytes, medianUs = timings[timings.Length / 2] * 1_000_000d / Stopwatch.Frequency,
            p95Us = timings[timings.Length * 95 / 100] * 1_000_000d / Stopwatch.Frequency,
            maxUs = timings[^1] * 1_000_000d / Stopwatch.Frequency };
    }

    private static string[] StatusRows(JsonElement root) => root.GetProperty("effects").EnumerateArray().Select(e =>
        e.GetProperty("kind").GetString() + "\t" + e.GetProperty("intensity").GetSingle().ToString("0.###", CultureInfo.InvariantCulture) +
        (e.GetProperty("detailKey").GetString() is { Length: > 0 } detail ? "\t" + detail : "")).ToArray();
    private static string[] ImpactRows(JsonElement root) => root.GetProperty("effectImpacts").EnumerateArray().Select(e =>
        e.GetProperty("need").GetString() + "\t" + e.GetProperty("kind").GetString() + "\t" + e.GetProperty("direction").GetString()).ToArray();
    private static JsonDocument Describe(WorldHost host, int npcId)
    {
        var result = new McpTools(host, new ControlLeases(45)).Call("describe_colonist",
            JsonSerializer.SerializeToElement(new { npcId }), "mcp:393", out var error);
        Assert.That(error, Is.False, result);
        return JsonDocument.Parse(result);
    }
    private static string Root()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SimData/simdata.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Source root");
    }
    private static WorldHost CreateHost() => new(393, GameMode.Feud,
        Path.Combine(Path.GetTempPath(), "mcp-effects-" + Guid.NewGuid().ToString("N") + ".sav"),
        Path.Combine(Root(), "SimData/simdata.json"), false);
}
