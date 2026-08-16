using System.Linq;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §40.19 r2 (баг #165). Рюкзак на земле лежит СВОЕЙ формой, как ботинок, а не
/// расплющивается в блин, как рубашка. Игрок прочитал сплющенный рюкзак как
/// «модели рюкзака нет».
/// </summary>
public sealed class BagDropShapeTests
{
    [Test]
    public void ABackpackKeepsItsShapeOnTheGround()
    {
        Assert.That(GarmentStorageCategories.KeepsShapeOnGround("gear.backpack_riot"), Is.True);
        Assert.That(GarmentStorageCategories.IsBag("gear.backpack_riot"), Is.True);
    }

    [Test]
    public void EveryBagsLayerGarmentCountsAsABag()
    {
        var bags = GarmentLibrary.Active
            .Where(garment => garment != null && garment.Layer == WearLayer.Bags)
            .ToArray();
        Assert.That(bags, Is.Not.Empty, "в каталоге обязаны быть вещи слоя Bags");

        foreach (var bag in bags)
        {
            Assert.That(GarmentStorageCategories.KeepsShapeOnGround(bag.Id), Is.True,
                $"{bag.Id} — сумка, а значит держит форму");
        }
    }

    [Test]
    public void AShirtStillLiesFlat()
    {
        Assert.That(GarmentStorageCategories.KeepsShapeOnGround("clothing.croptop_fit"), Is.False);
    }
}

}
