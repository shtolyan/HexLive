#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Config;
using NUnit.Framework;

namespace HexLive.Tests
{
// Calls the actual bridge used by ExportSimData before serialization.
public sealed class GarmentTuningBackfillTests
{
    [TestCase(0)]
    [TestCase(7)]
    public void CapacityBackfillPreservesAuthoredMetadataAndDoesNotOverrideNonzeroCapacity(int capacity)
    {
        var defaults = GarmentLibrary.Defaults.Single(g => g.Id == "clothing.skirt_anarchy");
        var input = Garment(defaults.Id, capacity, retired: true);
        var actual = Backfill(input);
        Assert.That(actual.Capacity, Is.EqualTo(capacity == 0 ? defaults.Capacity : capacity));
        Assert.That(actual.Retired, Is.True, "An authored retirement must survive an active default.");
        AssertMetadata(input, actual);
    }

    [Test]
    public void OldAssetWithoutRetiredFlagCannotReviveTheRetiredJaneSkirt()
    {
        var defaults = GarmentLibrary.Defaults.Single(g => g.Id == "clothing.skirt_jane");
        Assert.That(defaults.Retired, Is.True);
        var input = Garment(defaults.Id, 0, retired: false);
        var actual = Backfill(input);
        Assert.That(actual.Retired, Is.True);
        Assert.That(actual.Capacity, Is.EqualTo(defaults.Capacity));
        AssertMetadata(input, actual);
    }

    [Test]
    public void UnknownDefinitionPreservesItsOwnMetadataAndZeroCapacity()
    {
        var input = Garment("test.state649.unknown", 0, retired: true);
        Assert.That(GarmentLibrary.Defaults.Any(g => g.Id == input.Id), Is.False);
        var actual = Backfill(input);
        Assert.That(actual.Capacity, Is.Zero);
        Assert.That(actual.Retired, Is.True);
        AssertMetadata(input, actual);
    }

    private static GarmentParams Garment(string id, int capacity, bool retired) =>
        new GarmentParams(id, "Authored metadata", WearLayer.Outerwear, .37f, .12f, -.23f,
            13, capacity, GarmentSex.Any, BodyPart.Head, BodyPart.Torso)
        {
            PrototypeId = "test.state649.prototype",
            AlternateFitId = "test.state649.paired",
            // Deliberately differs from the id-derived Bottom category.
            Category = GarmentCategory.Jewellery,
            Retired = retired
        };

    private static GarmentParams Backfill(GarmentParams input)
    {
        var method = typeof(GarmentTuning).GetMethod("BackfillCapacity", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.That(method, Is.Not.Null);
        var actual = (List<GarmentParams>)method.Invoke(null, new object[] { new List<GarmentParams> { input } });
        Assert.That(actual, Has.Count.EqualTo(1));
        Assert.That(actual[0], Is.Not.SameAs(input), "The real bridge reconstructs its output row.");
        return actual[0];
    }

    private static void AssertMetadata(GarmentParams expected, GarmentParams actual)
    {
            Assert.That(actual.Id, Is.EqualTo(expected.Id));
            Assert.That(actual.PrototypeId, Is.EqualTo(expected.PrototypeId));
            Assert.That(actual.AlternateFitId, Is.EqualTo(expected.AlternateFitId));
            Assert.That(actual.Category, Is.EqualTo(expected.Category));
            Assert.That(actual.DisplayName, Is.EqualTo(expected.DisplayName));
            Assert.That(actual.Layer, Is.EqualTo(expected.Layer));
            Assert.That(actual.Warmth, Is.EqualTo(expected.Warmth));
            Assert.That(actual.Armor, Is.EqualTo(expected.Armor));
            Assert.That(actual.ThermalDelta, Is.EqualTo(expected.ThermalDelta));
            Assert.That(actual.DressDurationTicks, Is.EqualTo(expected.DressDurationTicks));
            Assert.That(actual.Covers, Is.EqualTo(expected.Covers));
    }
}
}
#endif
