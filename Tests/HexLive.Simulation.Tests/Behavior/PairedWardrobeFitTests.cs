using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class PairedWardrobeFitTests
{
    GarmentParams[] previous;

    [SetUp]
    public void LoadActualPreparedCatalog()
    {
        previous = GarmentLibrary.Active.ToArray();
        var records = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root,
            "Assets/HexLiveContent/People/catalog.json")))!["records"]!.AsArray();
        var garments = new JsonArray();
        foreach (var row in records.Where(r => r!["type"]!.GetValue<string>() == "wear"))
        {
            var item = row!["metadata"]!["simulation"]!.DeepClone();
            item["id"] = row["id"]!.GetValue<string>(); garments.Add(item);
        }
        Assert.That(SimDataFile.ApplyJson(new JsonObject { ["garments"] = garments }.ToJsonString()), Is.True);
    }

    [TearDown] public void Restore() => GarmentLibrary.Override(previous);

    [Test]
    public void EveryActualColourHasAReciprocalFitForBothBodies()
    {
        Assert.That(GarmentLibrary.Active.Count, Is.EqualTo(82));
        foreach (var item in GarmentLibrary.Active)
        foreach (var body in new[] { GarmentSex.Female, GarmentSex.Male })
        {
            var fitted = GarmentLibrary.FitDefinitionId(body, item.Id);
            Assert.That(fitted, Is.Not.Null, item.Id + ":" + body);
            var target = GarmentLibrary.Active.Single(g => g.Id == fitted);
            Assert.That(target.Sex, Is.EqualTo(body));
            Assert.That(GarmentLibrary.FitDefinitionId(item.Sex, fitted), Is.EqualTo(item.Id));
        }
    }

    [Test]
    public void ExplicitFitLinksSurviveCanonicalSimDataRoundtrip()
    {
        var links = GarmentLibrary.Active.ToDictionary(g => g.Id, g => g.AlternateFitId);
        Assert.That(SimDataFile.ApplyJson(SimDataFile.ExportJson()), Is.True);
        Assert.That(GarmentLibrary.Active.ToDictionary(g => g.Id, g => g.AlternateFitId), Is.EquivalentTo(links));
    }

    [TestCase("clothing.armwraps_primal")]
    [TestCase("clothing.armwraps_primal_male")]
    public void OneSleeveItemProtectsBothArmsWithoutDoublingItsCapacity(string id)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear(); npc.Inventory.Items.Clear();
        EquipmentMath.Recalculate(world, npc); var baseline = npc.Inventory.Capacity;
        var item = new ItemInstance(id) { Durability = .7f, OwnerId = npc.Id.Value };
        npc.WornItems.Add(item); EquipmentMath.Recalculate(world, npc);
        Assert.That(EquipmentMath.ArmorForPart(world, npc, BodyPart.ArmL), Is.GreaterThan(0));
        Assert.That(EquipmentMath.ArmorForPart(world, npc, BodyPart.ArmR),
            Is.EqualTo(EquipmentMath.ArmorForPart(world, npc, BodyPart.ArmL)));
        Assert.That(npc.Inventory.Capacity, Is.EqualTo(baseline + 1));
        Assert.That(npc.WornItems.Single(), Is.SameAs(item));
        Assert.That(item.DefinitionId, Is.EqualTo(id));
        Assert.That(item.Durability, Is.EqualTo(.7f));
    }

    [Test]
    public void BrokenOrMechanicallyDifferentPairDoesNotBypassBodyRestriction()
    {
        var a = new GarmentParams("test.female", "A", WearLayer.Wear, .1f, .1f, 0, 8, 1, GarmentSex.Female, BodyPart.Torso);
        var b = new GarmentParams("test.male", "B", WearLayer.Wear, .1f, .1f, 0, 8, 1, GarmentSex.Male, BodyPart.Torso);
        a.AlternateFitId = b.Id; GarmentLibrary.Override(new[] { a, b });
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, a.Id), Is.False, "one-way reference");
        b.AlternateFitId = a.Id;
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, a.Id), Is.True);
        b.Covers.Add(BodyPart.ArmL);
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, a.Id), Is.False, "different protection");
        a.AlternateFitId = "missing";
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, a.Id), Is.False);
    }

    [Test]
    public void UnfittedLegacyBraStillRequiresItsAuthoredBody()
    {
        GarmentLibrary.ResetToDefaults();
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Male, "underwear.bra_riot"), Is.False);
        Assert.That(GarmentLibrary.FitsSex(GarmentSex.Female, "underwear.bra_riot"), Is.True);
    }
}
