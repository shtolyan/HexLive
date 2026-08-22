using System.IO;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>§146.12: the manual camp merge remains visible and localized.</summary>
public sealed class CampDiplomacyUiContractTests
{
    [Test]
    public void NpcContextMenuOffersBothMutualAffinityMergeChoices()
    {
        var adapter = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "HexLive", "UnityPresentation",
            "Input", "SimulationInputAdapter.cs"));
        var languages = File.ReadAllText(Path.Combine(
            RepoPaths.Root, "Assets", "Resources", "I2Languages.asset"));

        Assert.Multiple(() =>
        {
            Assert.That(adapter, Does.Contain("AffinityTo(carrier!, npcId) > 0.50f"));
            Assert.That(adapter, Does.Contain("AffinityTo(target, carrier!.Id.Value) > 0.50f"));
            Assert.That(adapter, Does.Contain("menu.camp_merge.invite"));
            Assert.That(adapter, Does.Contain("menu.camp_merge.occupy"));
            Assert.That(adapter, Does.Contain("useTargetCamp: false"));
            Assert.That(adapter, Does.Contain("useTargetCamp: true"));
            Assert.That(languages, Does.Contain("Term: menu.camp_merge.invite"));
            Assert.That(languages, Does.Contain("Term: menu.camp_merge.occupy"));
            Assert.That(languages, Does.Contain("Term: menu.camp_merge.need_relation"));
            Assert.That(languages, Does.Contain("Term: menu.camp_merge.need_nearby"));
        });
    }
}

}
