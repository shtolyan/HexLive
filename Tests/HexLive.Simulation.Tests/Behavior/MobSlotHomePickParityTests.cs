using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Common;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §158.5: дом слота выбирается по индексу в ОТФИЛЬТРОВАННОМ списке, но сам
/// список больше не строится — исключённые позиции собираются по дискам и
/// пропускаются. Тест строит прежний список честно (полный обход + фильтр) и
/// требует, чтобы i-й допустимый элемент совпадал для каждого i.
/// </summary>
public sealed class MobSlotHomePickParityTests
{
    [TestCase(Content.MobIds.Dog)]
    [TestCase(Content.MobIds.Crab)]
    public void SkippingExcludedPositionsMatchesTheFilteredList(string mobId)
    {
        var world = TestWorld.CreateWorld();
        var baseList = MobSlots.SlotHomeBase(world, mobId);
        Assert.That(baseList, Is.Not.Empty);
        for (var i = 1; i < baseList.Count; i++)
        {
            Assert.That(baseList[i].Value, Is.GreaterThan(baseList[i - 1].Value), "базовый список обязан быть отсортирован по id");
        }

        var filtered = baseList.Where(id => MobSlots.FarFromPeople(world, id, mobId)).ToList();
        var excluded = new List<int>();
        MobSlots.CollectExcluded(world, baseList, mobId, excluded);
        Assert.That(baseList.Count - excluded.Count, Is.EqualTo(filtered.Count),
            "число исключённых позиций разошлось с честным фильтром");
        for (var i = 0; i < filtered.Count; i++)
        {
            Assert.That(baseList[MobSlots.SkipExcluded(i, excluded)], Is.EqualTo(filtered[i]),
                $"элемент {i}: пропуск исключённых выбрал другой узел");
        }
    }
}

}
