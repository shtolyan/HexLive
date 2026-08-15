using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

[TestFixture]
public sealed class GarmentStorageCategoryTests
{
    [TestCase("clothing.gloves_classic")]
    [TestCase("clothing.armguards_fighter")]
    [TestCase("clothing.armwraps_primal")]
    [TestCase("clothing.cuffs_anarchy")]
    [TestCase("clothing.sleeves_idol")]
    public void GlovesAndArmOnlyGarmentsUsePairedHandwearStorage(string definitionId)
    {
        Assert.That(GarmentStorageCategories.IsPairedHandwear(definitionId), Is.True);
    }

    [TestCase("clothing.blouse_anarchy")]
    [TestCase("clothing.boots_classic")]
    [TestCase("underwear.bra_openback")]
    public void GarmentsUsingNonArmSlotsDoNotUsePairedHandwearStorage(string definitionId)
    {
        Assert.That(GarmentStorageCategories.IsPairedHandwear(definitionId), Is.False);
    }
}
