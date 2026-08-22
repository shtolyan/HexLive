using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

public sealed class GarmentUnderwearVisibilityGateTests
{
    private const string VestId = "clothing.vest_stars";

    [Test]
    public void Report156ContextEquipsTheStarsVest()
    {
        var engine = TestWorld.CreateEngine(-28260451);
        while (engine.World.Tick < 2565) engine.Step();

        var npc = engine.World.Entities.Npcs.Values
            .Single(candidate => candidate.Id.Value == 1);
        Assert.That(npc.WornItems.Select(item => item.DefinitionId),
            Does.Contain(VestId),
            "seed=-28260451 tick=2565 npc=1 must keep identifying the reported item.");
    }

    [Test]
    public void StarsVestSourceKeepsOnlyChestUnderwearVisible()
    {
        var manifestPath = Path.Combine(
            RepoPaths.Root, "Assets", "Editor", "WearDrops", "starsdeadly.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var vest = manifest.RootElement.GetProperty("garments")
            .EnumerateArray()
            .Single(item => item.GetProperty("simId").GetString() == VestId);
        var exceptions = vest.GetProperty("noHide")
            .EnumerateArray()
            .Select(slot => slot.GetString())
            .ToArray();

        Assert.That(exceptions, Is.EqualTo(new[] { "Chest" }),
            "The exception belongs to the open Stars vest, not a garment family.");
    }

    [Test]
    public void ShippedStarsVestPrefabKeepsChestUnderwearVisible()
    {
        var prefabPath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Wear", VestId,
            "StarsVest.prefab");
        var prefab = File.ReadAllText(prefabPath);

        Assert.That(prefab, Does.Contain("noHideUnderwearSlots: 04000000"),
            "VisualWearSlot.Chest (4) must remain visible under the shipped vest.");
    }

    [Test]
    public void RuntimeRecomputesUnderwearAgainstWearAndOuterwearTogether()
    {
        var bodyBonesPath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation", "Wearing",
            "BodyBones.cs");
        var source = File.ReadAllText(bodyBonesPath);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("private void RefreshUnderwearVisibility()"));
            Assert.That(source, Does.Contain(
                "_byLayer[VisualWearLayer.Wear].TryGetValue(slot"),
                "Wear trousers must keep masking underwear even when Outerwear exposes the slot.");
            Assert.That(source, Does.Contain(
                "_byLayer[VisualWearLayer.Outerwear].TryGetValue(slot"),
                "Outerwear must still contribute its own independent mask.");
            Assert.That(source.Split("RefreshUnderwearVisibility();").Length - 1,
                Is.GreaterThanOrEqualTo(2),
                "Both equip and take-off must recompute from the final layered outfit.");
        });
    }
}
