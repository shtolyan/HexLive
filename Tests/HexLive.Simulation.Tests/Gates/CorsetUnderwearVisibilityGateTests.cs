using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§31B.4A / bug #133: the Anarchy corset is worn over a visible bra.</summary>
public sealed class CorsetUnderwearVisibilityGateTests
{
    private const string CorsetId = "clothing.corset_anarchy";

    [Test]
    public void AnarchyCorsetSourceKeepsChestUnderwearVisible()
    {
        var manifestPath = Path.Combine(
            RepoPaths.Root, "Assets", "Editor", "WearDrops", "anarchy.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var corset = manifest.RootElement.GetProperty("garments")
            .EnumerateArray()
            .Single(item => item.GetProperty("simId").GetString() == CorsetId);
        var exceptions = corset.GetProperty("noHide")
            .EnumerateArray()
            .Select(slot => slot.GetString())
            .ToArray();

        Assert.That(exceptions, Is.EqualTo(new[] { "Chest" }),
            "The source manifest must preserve the bra visibility on a clean re-extract.");
    }

    [Test]
    public void ShippedAnarchyCorsetPrefabKeepsChestUnderwearVisible()
    {
        var prefabPath = Path.Combine(
            RepoPaths.Root, "Assets", "HexLiveContent", "Wear", CorsetId,
            "AnarchyCorset.prefab");
        var prefab = File.ReadAllText(prefabPath);

        Assert.That(prefab, Does.Contain("noHideUnderwearSlots: 04000000"),
            "VisualWearSlot.Chest (4) must remain visible under the shipped corset prefab.");
    }
}

}
