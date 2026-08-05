using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §54.17: выбор еды по питательности. До FoodMath ни один выбор еды не
/// смотрел на цифры: из рюкзака ели ПЕРВОЕ съедобное (порядок вставки), с
/// земли брали БЛИЖАЙШЕЕ с тегом Food — и жареное мясо (−0.9) проигрывало
/// половинке кокоса (−0.675) всегда.
/// </summary>
public sealed class FoodMathTests
{
    [Test]
    public void CookedMeatBeatsCoconutHalfInThePack()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        // Кокос лёг в рюкзак РАНЬШЕ — старый FindFirstFood вернул бы его.
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatCooked));

        Assert.That(FoodMath.BestFoodInInventory(world, npc),
            Is.EqualTo(ContentIds.MeatCooked),
            "Жареное мясо питательнее половинки кокоса и обязано побеждать " +
            "независимо от порядка, в котором предметы попали в рюкзак.");
    }

    [Test]
    public void TieKeepsInsertionOrder_PacksWithoutMeatUnchanged()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.CoconutOpen));

        Assert.That(FoodMath.BestFoodInInventory(world, npc),
            Is.EqualTo(npc.Inventory.FindFirstFood(world.Content)),
            "Без мяса в рюкзаке выбор обязан совпадать со старым FindFirstFood — " +
            "иначе golden trace зашумит там, где поведение меняться не должно.");
    }

    [Test]
    public void RawMeatWithoutInventoryEatStaysInvisible()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();
        npc.Inventory.Items.Clear();
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.MeatRaw));

        Assert.That(FoodMath.BestFoodInInventory(world, npc), Is.Null,
            "Сырое мясо намеренно несъедобно (spec §29F.3) и не должно " +
            "считаться едой в рюкзаке.");
    }

    [Test]
    public void RawMeatProspectFlipsOnUsableFire()
    {
        var world = TestWorld.CreateWorld();

        var withFire = FoodMath.ProspectiveNutrition(world, ContentIds.MeatRaw, fireUsable: true);
        var withoutFire = FoodMath.ProspectiveNutrition(world, ContentIds.MeatRaw, fireUsable: false);

        Assert.That(withFire, Is.EqualTo(SimBalance.CookedMeatHunger).Within(0.001f),
            "С рабочим вертелом сырой кусок стоит будущего жареного.");
        Assert.That(withoutFire, Is.LessThan(FoodMath.NutritionOf(world, ContentIds.CoconutOpen)),
            "Без огня сырой кусок — ставка на будущее и обязан проигрывать " +
            "открытому кокосу, который можно съесть прямо сейчас.");
    }

    [Test]
    public void CampfireCandidateReadsAsCookedChunk()
    {
        var world = TestWorld.CreateWorld();

        // Костёр — валидная цель GetFood только пока на вертеле висит жареное
        // (IsValidTargetFor), поэтому его перспектива = жареный кусок.
        Assert.That(FoodMath.ProspectiveNutrition(world, "campfire.spot", fireUsable: false),
            Is.EqualTo(SimBalance.CookedMeatHunger).Within(0.001f));
    }
}

}
