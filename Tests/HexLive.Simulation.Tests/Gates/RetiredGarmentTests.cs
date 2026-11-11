using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// Bug #335 (§154.2): длинная ярусная юбка Jane выведена из оборота — она не
/// скинится к голеням и не гнётся в коленях. Retired-вещь остаётся в Active
/// (старые сейвы читаются), но исчезает из всех выдач нового предмета.
/// </summary>
public sealed class RetiredGarmentTests
{
    [Test]
    public void JaneTieredSkirtFamilyIsRetiredButStillDefined()
    {
        TestWorld.CreateEngine(); // прогревает SimDataFile.Require + Override

        var family = GarmentLibrary.Active
            .Where(g => g.Id.StartsWith("clothing.skirt_jane"))
            .ToList();
        Assert.That(family, Has.Count.GreaterThanOrEqualTo(9),
            "Семья юбки Jane обязана остаться в каталоге ради старых сейвов.");

        Assert.Multiple(() =>
        {
            foreach (var garment in family)
            {
                Assert.That(GarmentLibrary.IsSpawnable(garment.Id), Is.False,
                    $"{garment.Id} выведена из оборота и не должна спавниться.");
            }
        });
    }

    [TestCase("clothing.footwear_fighter", 1)]
    [TestCase("clothing.sandals_summer4", 10)]
    [TestCase("clothing.skirt_alloy", 1)]
    public void RejectedModernModelsCannotSpawnFromLegacyCatalogs(string prototypeId, int variants)
    {
        TestWorld.CreateEngine();

        var defaults = GarmentLibrary.Defaults.Where(garment =>
            garment.Id == prototypeId || garment.PrototypeId == prototypeId).ToArray();
        var loaded = GarmentLibrary.Active.Where(garment =>
            garment.Id == prototypeId || garment.PrototypeId == prototypeId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(defaults, Has.Length.EqualTo(variants));
            Assert.That(defaults.All(garment => garment.Retired), Is.True);
            Assert.That(loaded.Select(garment => garment.Id), Is.EquivalentTo(defaults.Select(garment => garment.Id)));
            Assert.That(loaded.All(garment => !GarmentLibrary.IsSpawnable(garment.Id)), Is.True);
        });
    }
}
