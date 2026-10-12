using System.Linq;
using System.IO;
using System.Text.Json;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class PrimalClothingCapacityTests
{
    [Test]
    public void CleanPreparationSourcesKeepSkirtsAndFurTransparentToLowerLayers()
    {
        var files = Directory.GetFiles(Path.Combine(RepoPaths.Root, "Assets", "Editor", "WearDrops"), "*.json")
            .Append(Path.Combine(RepoPaths.Root, "Assets", "HexLiveContent", "People", "Source", "wardrobe-authoring.json"));
        var found = 0;
        foreach (var path in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array && !root.TryGetProperty("garments", out root)) continue;
            foreach (var garment in root.EnumerateArray())
            {
                if (!garment.TryGetProperty("simId", out var idProperty)) continue;
                var id = idProperty.GetString();
                if (!id.StartsWith("clothing.skirt_") && id != "clothing.dress_primal" && id != "clothing.collar_fur") continue;
                found++;
                Assert.That(garment.GetProperty("noHide").EnumerateArray().Select(x => x.GetString()),
                    Is.EquivalentTo(garment.GetProperty("slots").EnumerateArray().Select(x => x.GetString())), path + ": " + id);
            }
        }
        Assert.That(found, Is.GreaterThanOrEqualTo(13));
    }

    [TestCase("clothing.collar_fur", 1)]
    [TestCase("clothing.collar_fur_color06", 1)]
    [TestCase("clothing.dress_primal", 2)]
    [TestCase("clothing.dress_primal_color06", 2)]
    [TestCase("clothing.skirt_primal", 2)]
    [TestCase("clothing.skirt_primal_3", 2)]
    [TestCase("underwear.briefs_primal", 1)]
    [TestCase("clothing.top_primal", 2)]
    [TestCase("clothing.armwraps_primal", 1)]
    [TestCase("clothing.wrapboots_primal", 0)]
    [TestCase("clothing.headband_primal", 0)]
    public void WornClothingAddsCapacityOnceAndRemovalRestoresIt(string id, int extra)
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();
        npc.Inventory.Items.Clear();
        var cargo = new ItemInstance(ContentIds.Stone);
        npc.Inventory.Items.Add(cargo);
        EquipmentMath.RecalculateCapacity(world, npc);
        var baseline = npc.Inventory.Capacity;
        var garment = new ItemInstance(id);
        npc.WornItems.Add(garment);
        EquipmentMath.RecalculateCapacity(world, npc);
        Assert.That(npc.Inventory.Capacity, Is.EqualTo(baseline + extra));
        EquipmentMath.RecalculateCapacity(world, npc);
        Assert.That(npc.Inventory.Capacity, Is.EqualTo(baseline + extra), "No accumulated phantom slots");
        npc.WornItems.Remove(garment);
        EquipmentMath.RecalculateCapacity(world, npc);
        Assert.That(npc.Inventory.Capacity, Is.EqualTo(baseline));
        Assert.That(npc.Inventory.Items.Single(), Is.SameAs(cargo));
    }

    [Test]
    public void CollarAndSkirtCoexistAndTheirCapacitiesAdd()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.WornItems.Clear();
        EquipmentMath.RecalculateCapacity(world, npc);
        var baseline = npc.Inventory.Capacity;
        var a = world.Content.ObjectDefinitions["clothing.collar_fur"];
        var b = world.Content.ObjectDefinitions["clothing.skirt_primal"];
        Assert.That(WearSlotCatalog.Occupies(a, b), Is.False);
        npc.WornItems.Add(new ItemInstance(a.Id));
        npc.WornItems.Add(new ItemInstance(b.Id));
        EquipmentMath.RecalculateCapacity(world, npc);
        Assert.That(npc.Inventory.Capacity, Is.EqualTo(baseline + 3));
    }
}
