using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Agents.Effects;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>§48.7: live parameter causes remain simulation-authored,
/// catalog-backed, wire-stable and reachable from every character-card cell.</summary>
public sealed class EffectImpactContractTests
{
    private static readonly EffectKind[] ImpactKinds =
    {
        EffectKind.NaturalDecay,
        EffectKind.Sleeping,
        EffectKind.DirtyClothes,
        EffectKind.CleanClothes,
        EffectKind.NearbyCompany,
        EffectKind.WitnessingSuffering,
        EffectKind.EveryoneSafe,
        EffectKind.Working,
        EffectKind.Resting,
        EffectKind.Threatened,
        EffectKind.BodyCrisis,
        EffectKind.Calm,
        EffectKind.FriendlyTalk,
        EffectKind.Washing,
        EffectKind.RainWashed,
        EffectKind.BloodRecovery,
        EffectKind.Sprinting,
        EffectKind.BreathRecovery,
        EffectKind.AmbientTemperature,
        EffectKind.Eating,
        EffectKind.Drinking,
    };

    [Test]
    public void NeedsDecayRecordsTheCauseBesideTheLiveMutation()
    {
        var world = TestWorld.CreateWorld(4807);
        var npc = world.Entities.Npcs.Values.First();
        npc.Needs.Hunger = 0.25f;

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Hunger, Is.GreaterThan(0.25f));
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Hunger &&
                    impact.Kind == EffectKind.NaturalDecay &&
                    impact.Direction == EffectImpactDirection.Negative &&
                    impact.Cadence == EffectImpactCadence.Slow),
                Is.True,
                "The hover must describe the same metabolism branch that moved Hunger.");
        });
    }

    [Test]
    public void DirtyClothesRecordNegativeComfortAtTheRealDecayBranch()
    {
        var world = TestWorld.CreateWorld(4827);
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();
        npc.WornItems.Add(new ItemInstance("clothing.jacket_autumn")
        {
            Dirtiness = 1f,
            OwnerId = npc.Id.Value,
        });
        npc.Needs.Comfort = 0.5f;

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Comfort, Is.LessThan(0.5f));
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Comfort &&
                    impact.Kind == EffectKind.DirtyClothes &&
                    impact.Direction == EffectImpactDirection.Negative &&
                    impact.Cadence == EffectImpactCadence.Slow),
                Is.True);
        });
    }

    [TestCase(0.00f,  0.0002f)]
    [TestCase(0.05f,  0.0001f)]
    [TestCase(0.10f,  0f)]
    [TestCase(0.15f,  0f)]
    [TestCase(0.20f,  0f)]
    [TestCase(0.40f, -0.0004f)]
    [TestCase(0.60f, -0.0012f)]
    [TestCase(1.00f, -0.0020f)]
    public void ClothingDirtComfortUsesCleanNeutralMildAndFullBands(
        float dirtiness, float expectedDelta)
    {
        Assert.That(EquipmentMath.ClothingDirtComfortDelta(dirtiness),
            Is.EqualTo(expectedDelta).Within(0.000001f));
    }

    [Test]
    public void FreshClothesRecordPositiveComfortWithoutDirtyTooltip()
    {
        var world = TestWorld.CreateWorld(4828);
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();
        npc.WornItems.Add(new ItemInstance("clothing.jacket_autumn")
        {
            Dirtiness = 0.05f,
            OwnerId = npc.Id.Value,
        });
        npc.Needs.Comfort = 0.5f;

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.Comfort,
                Is.GreaterThan(0.5f - SimBalance.ComfortRate),
                "Свежая одежда должна добавлять комфорт поверх обычного бодрствующего спада.");
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Comfort &&
                    impact.Kind == EffectKind.CleanClothes &&
                    impact.Direction == EffectImpactDirection.Positive),
                Is.True);
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Comfort &&
                    impact.Kind == EffectKind.DirtyClothes),
                Is.False, "Чистая одежда не должна называться грязной в hover-подсказке.");
        });
    }

    [Test]
    public void AmbientThermalDriftTargetsTemperatureWithoutInventingComfortDrain()
    {
        var world = TestWorld.CreateWorld(4837);
        var npc = world.Entities.Npcs.Values.First();
        world.Environment.GlobalTemperature = 50f;
        npc.Needs.ThermalComfort = 0.5f;
        npc.Needs.Comfort = 0.5f;
        npc.SunExposure = 0f;

        new TemperatureSystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.Needs.ThermalComfort, Is.GreaterThan(0.5f));
            Assert.That(npc.Needs.Comfort, Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Temperature &&
                    impact.Kind == EffectKind.AmbientTemperature &&
                    impact.Direction == EffectImpactDirection.Negative &&
                    impact.Cadence == EffectImpactCadence.Slow),
                Is.True);
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Comfort &&
                    impact.Kind is EffectKind.AmbientTemperature or
                        EffectKind.Hot or EffectKind.Heatstroke or
                        EffectKind.Cold or EffectKind.Freezing),
                Is.False,
                "Thermal state owns Temperature; it must not masquerade as Comfort loss.");
        });
    }

    [Test]
    public void DangerAndCalmRecordOppositeStressDirections()
    {
        var world = TestWorld.CreateWorld(4847);
        var npc = world.Entities.Npcs.Values.First();
        npc.Health = 1f;
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Needs.Stress = 0.5f;
        npc.Mind.AdrenalineUntilTick = 0;
        npc.Mind.CurrentGoal = GoalType.Flee;
        npc.IsFighting = false;
        npc.Perception.Agents.Clear();

        new NeedsDecaySystem().Run(world);

        Assert.That(npc.EffectImpacts.Items.Any(impact =>
                impact.Need == NeedKind.Stress &&
                impact.Kind == EffectKind.Threatened &&
                impact.Direction == EffectImpactDirection.Negative),
            Is.True,
            "The active flee branch must surface the same danger that raises Stress.");

        npc.Needs.Stress = 0.5f;
        npc.Needs.Hunger = 0.1f;
        npc.Needs.Thirst = 0.1f;
        npc.Mind.CurrentGoal = GoalType.None;
        npc.Perception.Agents.Clear();

        new NeedsDecaySystem().Run(world);

        Assert.Multiple(() =>
        {
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stress &&
                    impact.Kind == EffectKind.Calm &&
                    impact.Direction == EffectImpactDirection.Positive),
                Is.True,
                "The no-threat branch must surface the calm recovery it applies.");
            Assert.That(npc.EffectImpacts.Items.Any(impact =>
                    impact.Need == NeedKind.Stress &&
                    impact.Kind == EffectKind.Threatened),
                Is.False,
                "The slow owner must clear the previous danger row before rewriting calm.");
        });
    }

    [Test]
    public void CadenceRowsClearIndependentlyButExportAsOneVisibleTriple()
    {
        var world = TestWorld.CreateWorld(4817);
        var npc = world.Entities.Npcs.Values.First();
        npc.EffectImpacts.Record(
            NeedKind.Comfort,
            EffectKind.Resting,
            EffectImpactDirection.Positive,
            EffectImpactCadence.Fast);
        npc.EffectImpacts.Record(
            NeedKind.Comfort,
            EffectKind.Resting,
            EffectImpactDirection.Positive,
            EffectImpactCadence.Slow);

        Assert.That(npc.EffectImpacts.Items, Has.Count.EqualTo(2),
            "Cadence ownership must remain internal so each owner can clear only its rows.");
        Assert.That(ExportedImpacts(world, npc.Id.Value),
            Is.EqualTo(new[] { "Comfort\tResting\tPositive" }),
            "Cadence is not visible and must not create duplicate UI rows.");

        npc.EffectImpacts.Clear(EffectImpactCadence.Fast);
        Assert.Multiple(() =>
        {
            Assert.That(npc.EffectImpacts.Items, Has.Count.EqualTo(1));
            Assert.That(npc.EffectImpacts.Items[0].Cadence, Is.EqualTo(EffectImpactCadence.Slow));
            Assert.That(ExportedImpacts(world, npc.Id.Value),
                Is.EqualTo(new[] { "Comfort\tResting\tPositive" }));
        });

        npc.EffectImpacts.Clear(EffectImpactCadence.Slow);
        Assert.That(ExportedImpacts(world, npc.Id.Value), Is.Empty);
    }

    [Test]
    public void EveryImpactKindHasCatalogAndEnglishRussianTerms()
    {
        Assert.That(EffectCatalog.All.Keys.OrderBy(kind => kind),
            Is.EqualTo(Enum.GetValues<EffectKind>().OrderBy(kind => kind)),
            "Every EffectKind must resolve through the shared status/impact catalog.");

        var localization = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));
        foreach (var kind in ImpactKinds)
        {
            var slug = kind.ToString().ToLowerInvariant();
            AssertBilingual(localization, $"effect.{slug}.title");
            AssertBilingual(localization, $"effect.{slug}.desc");
        }

        AssertBilingual(localization, "effect.impacts.current");
        AssertBilingual(localization, "effect.impacts.none");
    }

    [Test]
    public void EffectImpactRowsSurviveCharacterWireRoundTrip()
    {
        var sent = new WorldSnapshot { Tick = 4807 };
        var npc = new NpcSnapshot
        {
            Id = new EntityId(7),
            DisplayName = "npc.mira.name",
        };
        npc.EffectImpacts.Add("Comfort\tDirtyClothes\tNegative");
        npc.EffectImpacts.Add("Comfort\tCozy\tPositive");
        sent.Npcs.Add(npc);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Write(sent, writer, includeDebugDetails: false);
        }

        buffer.Position = 0;
        var received = new WorldSnapshot();
        using (var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Read(reader, received);
        }

        Assert.That(received.Npcs.Single().EffectImpacts,
            Is.EqualTo(npc.EffectImpacts));
    }

    [Test]
    public void CharacterPanelMapsEveryCellAndBuildsDirectionRows()
    {
        var panel = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "UI",
            "CharacterPanel.cs"));
        var mappings = new Dictionary<string, NeedKind>
        {
            ["need.hunger"] = NeedKind.Hunger,
            ["need.thirst"] = NeedKind.Thirst,
            ["need.energy"] = NeedKind.Energy,
            ["need.comfort"] = NeedKind.Comfort,
            ["need.social"] = NeedKind.Social,
            ["need.temperature"] = NeedKind.Temperature,
            ["need.stamina"] = NeedKind.Stamina,
            ["need.blood"] = NeedKind.Blood,
            ["need.hygiene"] = NeedKind.Hygiene,
            ["need.stress"] = NeedKind.Stress,
            ["need.compassion"] = NeedKind.Compassion,
            ["need.breath"] = NeedKind.Breath,
        };

        Assert.Multiple(() =>
        {
            Assert.That(panel, Does.Contain("private static NeedKind NeedKindFor(string key)"));
            Assert.That(panel, Does.Contain("private static string NeedLocKey(NeedKind need)"));
            foreach (var mapping in mappings)
            {
                Assert.That(panel,
                    Does.Contain($"\"{mapping.Key}\" => NeedKind.{mapping.Value}"));
                Assert.That(panel,
                    Does.Contain($"NeedKind.{mapping.Value} => \"{mapping.Key}\""));
            }

            Assert.That(panel, Does.Contain("ShowNeedTooltip(NeedKind.Temperature"));
            Assert.That(panel, Does.Contain("EffectCatalog.Get(impact.Kind)"));
            Assert.That(panel, Does.Contain("positive ? \"↑\" : \"↓\""));
            Assert.That(panel, Does.Contain("HasVisibleImpact(parsed, kind, direction)"),
                "Malformed or legacy duplicate wire rows must not become duplicate visuals.");
            Assert.That(panel, Does.Contain("effect.impacts.current"));
            Assert.That(panel, Does.Contain("effect.impacts.none"));
        });
    }

    private static IReadOnlyList<string> ExportedImpacts(
        HexLive.Simulation.Core.WorldState world,
        int npcId) =>
        WorldSnapshotExporter.Export(world).Npcs
            .Single(npc => npc.Id.Value == npcId)
            .EffectImpacts;

    private static void AssertBilingual(string asset, string key)
    {
        var marker = "    - Term: " + key;
        var start = asset.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Missing I2 term {key}");
        var end = asset.IndexOf("\n    - Term: ", start + marker.Length,
            StringComparison.Ordinal);
        if (end < 0)
        {
            end = asset.IndexOf("\n    CaseInsensitiveTerms:", start,
                StringComparison.Ordinal);
        }

        var block = asset[start..end];
        var languages = Regex.Matches(block, @"(?m)^      - (.+)$");
        Assert.That(languages, Has.Count.EqualTo(2),
            $"{key} must have exactly English and Russian values");
        Assert.That(languages.Cast<Match>().All(match =>
                match.Groups[1].Value.Trim(' ', '\'', '\"').Length > 0),
            Is.True,
            $"{key} has an empty English or Russian value");
    }
}
