using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[NonParallelizable]
public sealed class PairedCraftOptionsTests
{
    [TearDown]
    public void Restore()
    {
        RecipeCatalog.ResetToDefaults();
        SimDataFile.Require(RepoPaths.SimData);
    }

    [TestCase(GarmentSex.Female)]
    [TestCase(GarmentSex.Male)]
    public void EachPairedGarmentAndColourHasOneCraftEntryForTheSelectedBody(GarmentSex sex)
    {
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root,
            "Assets/HexLiveContent/People/catalog.json")))!;
        var garments = new JsonArray();
        var recipes = new JsonArray();
        var rows = source["records"]!.AsArray().OfType<JsonObject>()
            .Where(r => r["type"]!.GetValue<string>() == "wear").ToArray();
        foreach (var row in rows)
        {
            var id = row["id"]!.GetValue<string>();
            var simulation = (JsonObject)row["metadata"]!["simulation"]!.DeepClone();
            simulation["id"] = id;
            garments.Add(simulation);
            var recipe = (JsonObject)simulation["craft"]!.DeepClone();
            recipe["output"] = id;
            recipes.Add(recipe);
        }
        Assert.That(SimDataFile.ApplyJson(new JsonObject
            { ["garments"] = garments, ["recipes"] = recipes }.ToJsonString()), Is.True);
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.ActorMesh = sex == GarmentSex.Male ? "Kshishtof" : "Marta";
        var options = new List<CraftRecipeOption>();
        CraftingOptions.TryFill(world, npc.Id, options);
        var outputs = options.Select(o => o.OutputDefinitionId).ToHashSet();
        foreach (var garment in GarmentLibrary.Active)
        {
            if (string.IsNullOrEmpty(garment.AlternateFitId)) continue;
            Assert.That(GarmentLibrary.FitDefinitionId(sex, garment.Id), Is.Not.Null, garment.Id);
            var native = GarmentLibrary.FitDefinitionId(sex, garment.Id);
            var other = native == garment.Id ? garment.AlternateFitId : garment.Id;
            Assert.That(outputs.Contains(native), Is.True, native);
            Assert.That(outputs.Contains(other), Is.False,
                "The second body fit must not duplicate the same colour/cost recipe: " + other);
        }
        Assert.That(options.Count(o => rows.Any(r => r["id"]!.GetValue<string>() == o.OutputDefinitionId)),
            Is.EqualTo(rows.Length / 2));

        // Different bills are meaningful recipe choices, not body-fit duplicates.
        RecipeCatalog.Override("clothing.headband_primal_male",
            new[] { new RecipeIngredient(ContentIds.Hide, 99) }, false, "Workbench", 24);
        CraftingOptions.TryFill(world, npc.Id, options);
        Assert.That(options.Select(o => o.OutputDefinitionId), Does.Contain("clothing.headband_primal"));
        Assert.That(options.Select(o => o.OutputDefinitionId), Does.Contain("clothing.headband_primal_male"));
    }
}

