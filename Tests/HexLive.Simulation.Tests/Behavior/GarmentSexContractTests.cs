using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class GarmentSexContractTests
{
    private static readonly string[] TacticalKit =
    {
        "TonnyFlash", "FCO Pants Male", "FCO Belt Male", "FCO Gloves Male",
        "FAO Harness Male", "FCO Boots Male", "FCO Legs Straps Male",
        "FCO Knee Straps Male", "FCO Waist Strappy Male"
    };

    [TearDown]
    public void RestoreCatalog() => SimDataFile.Require(RepoPaths.SimData);

    [Test]
    public void FemaleDropPrototypesAndTheirColourwaysRejectMaleWearers()
    {
        var prototypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Path.Combine(RepoPaths.Root, "Assets/Editor/WearDrops"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("garments", out var garments)) continue;
            foreach (var garment in garments.EnumerateArray())
            {
                Assert.That(garment.GetProperty("sim").GetProperty("sex").GetString(), Is.EqualTo("Female"), path);
                prototypes.Add(garment.GetProperty("simId").GetString());
            }
        }
        Assert.That(prototypes.Count, Is.EqualTo(169), "review provenance when the wardrobe changes");
        var expected = GarmentLibrary.Defaults.Where(g => prototypes.Contains(g.Id) || prototypes.Contains(g.PrototypeId)).ToArray();
        Assert.That(expected.Length, Is.EqualTo(676));
        foreach (var garment in expected)
        {
            Assert.That(garment.Sex, Is.EqualTo(GarmentSex.Female), garment.Id);
            Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, garment.Id), Is.False, garment.Id);
            Assert.That(GarmentLibrary.FitsSex(GarmentSex.Female, garment.Id), Is.True, garment.Id);
        }
        foreach (var id in TacticalKit)
        {
            Assert.That(GarmentLibrary.DefaultSex(id), Is.EqualTo(GarmentSex.Any), id);
            Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, id), Is.True, id);
            Assert.That(GarmentLibrary.FitsSex(GarmentSex.Female, id), Is.True, id);
        }
        // #329 helmets are a separate rigid Blender drop, not female DAZ clothes.
        var helmets = GarmentLibrary.Defaults.Where(g => g.Id.StartsWith("clothing.helmet_", StringComparison.Ordinal)).ToArray();
        Assert.That(helmets.Length, Is.EqualTo(13));
        Assert.That(helmets.All(g => g.Sex == GarmentSex.Any), Is.True);
    }

    [Test]
    public void ExportAndReimportPreserveExplicitServerSexIncludingAny()
    {
        var original = GarmentLibrary.Active.ToDictionary(g => g.Id, g => g.Sex);
        var json = SimDataFile.ExportJson();
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var garment in doc.RootElement.GetProperty("garments").EnumerateArray())
                Assert.That(garment.GetProperty("sex").GetString(), Is.EqualTo(original[garment.GetProperty("id").GetString()].ToString()));
        }
        Assert.That(SimDataFile.ApplyJson(json), Is.True);
        Assert.That(GarmentLibrary.Active.ToDictionary(g => g.Id, g => g.Sex), Is.EqualTo(original));

        // Explicit server authoring must win over defaults, including Any.
        Assert.That(SimDataFile.ApplyJson("""
            {"garments":[{"id":"underwear.bra_riot","sex":"Any","covers":["Torso"]},
            {"id":"custom.male","sex":"Male","covers":["Torso"]}]}
            """), Is.True);
        var authored = SimDataFile.ExportJson();
        SimDataFile.ApplyJson(authored);
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, "underwear.bra_riot"), Is.True);
        Assert.That(GarmentLibrary.Active.Single(g => g.Id == "custom.male").Sex, Is.EqualTo(GarmentSex.Male));
    }

    [Test]
    public void LegacyExportWithoutSexInheritsExactAuthoringIds()
    {
        SimDataFile.ApplyJson("""
            {"garments":[{"id":"underwear.bra_riot","covers":["Torso"]},
            {"id":"FCO Pants Male","covers":["Pelvis"]}]}
            """);
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, "underwear.bra_riot"), Is.False);
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Female, "FCO Pants Male"), Is.True);
    }
}
