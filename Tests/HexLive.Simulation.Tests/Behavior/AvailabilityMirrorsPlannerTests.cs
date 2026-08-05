using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Доступность обязана врать не больше, чем планировщик.
/// <para>
/// Класс ошибки: аукцион считает цель доступной, планировщик отказывается брать
/// цель, и это повторяется каждый проход. NPC спамит «выбрал → провалил», пока
/// не умрёт от той самой нужды, ради которой цель бралась. Так уже погибли —
/// от жажды, июль 2026, — и фикс тогда внесли в ОДНУ копию предиката из двух:
/// у воды фильтр появился, у еды нет, хотя план у них общий.
/// </para>
/// <para>
/// Тест проверяет не текст фильтров, а их СЛЕДСТВИЕ: объект, который
/// планировщик не возьмёт, не должен считаться доступным ни для еды, ни для
/// питья.
/// </para>
/// </summary>
public sealed class AvailabilityMirrorsPlannerTests
{
    private const string OpenCoconut = "food.coconut_open";

    /// <summary>Ставит NPC один кокос в восприятии и в мире.</summary>
    private static (WorldState world, NPCState npc, ObjectId id) WithOneCoconut()
    {
        var world = TestWorld.CreateWorld();
        var npc = world.Entities.Npcs.Values.First();

        var existing = world.Entities.Objects.Values
            .FirstOrDefault(o => o.DefinitionId == OpenCoconut);

        ObjectId id;
        if (existing != null)
        {
            id = existing.Id;
        }
        else
        {
            // Берём любой объект и переопределяем его как открытый кокос: тест
            // про ФИЛЬТРЫ, а не про то, что растёт на прототипном острове.
            var any = world.Entities.Objects.Values.First();
            any.DefinitionId = OpenCoconut;
            id = any.Id;
        }

        npc.Inventory.Items.Clear();
        npc.Perception.Objects.Clear();
        npc.Perception.Objects.Add(new PerceivedObject
        {
            Id = id,
            DefinitionId = OpenCoconut,
            IsReachable = true,
            IsOccupied = false,
            Distance = 1f,
        });

        return (world, npc, id);
    }

    [Test]
    public void PlainCoconutIsAvailable()
    {
        var (world, npc, _) = WithOneCoconut();

        Assert.That(DecisionSystem.HasCoconutMeal(npc, world), Is.True,
            "Обычный доступный кокос обязан считаться едой — иначе тест ниже " +
            "проходил бы по неправильной причине.");
    }

    /// <summary>
    /// ⭐ Тот самый расход. Шунтированный объект планировщик пропускает
    /// (недавно оказался занят по прибытии), а доступность до этой правки
    /// говорила «еда есть» — и цикл «выбрал → провалил» шёл до смерти.
    /// </summary>
    [Test]
    public void ShunnedCoconutIsNotFood()
    {
        var (world, npc, id) = WithOneCoconut();
        npc.Memory.Shun(id, world.Tick + 600);

        Assert.That(DecisionSystem.HasCoconutMeal(npc, world), Is.False,
            "Планировщик шунтированный кокос не возьмёт — значит и доступность " +
            "обязана молчать, иначе цель выигрывает аукцион и проваливается вечно.");
    }

    /// <summary>
    /// Виден в восприятии, но в мире его уже нет — кто-то успел подобрать.
    /// </summary>
    [Test]
    public void DespawnedCoconutIsNotFood()
    {
        var (world, npc, id) = WithOneCoconut();
        world.Entities.Objects.Remove(id);

        Assert.That(DecisionSystem.HasCoconutMeal(npc, world), Is.False,
            "Протухшая запись восприятия не еда: план его не найдёт.");
    }

    [Test]
    public void OccupiedCoconutIsNotFood()
    {
        var (world, npc, _) = WithOneCoconut();
        npc.Perception.Objects[0].IsOccupied = true;
        npc.Perception.Objects[0].OccupiedBy = new EntityId(9999);

        Assert.That(DecisionSystem.HasCoconutMeal(npc, world), Is.False,
            "Занятый чужим кокос план тоже пропустит.");
    }

    /// <summary>
    /// Еда и питьё смотрят на мир ОДНИМИ фильтрами. Раньше расходились именно
    /// они, и разошлись бесшумно.
    /// </summary>
    [Test]
    public void FoodAndWaterAgreeOnWhatTheWorldOffers()
    {
        foreach (var shun in new[] { false, true })
        {
            var (world, npc, id) = WithOneCoconut();
            npc.Perception.Objects[0].DefinitionId = "food.coconut";
            world.Entities.Objects[id].DefinitionId = "food.coconut";

            // Целый кокос вскрывается лезвием и годится и в еду, и в питьё —
            // значит оба предиката обязаны ответить одинаково.
            npc.Inventory.Items.Add(new ItemInstance("tool.knife"));

            if (shun)
            {
                npc.Memory.Shun(id, world.Tick + 600);
            }

            Assert.That(
                DecisionSystem.HasCoconutMeal(npc, world),
                Is.EqualTo(DecisionSystem.HasCoconutWater(npc, world)),
                "Про ОДИН И ТОТ ЖЕ целый кокос еда и питьё обязаны отвечать " +
                "одинаково (шун=" + shun + ").");
        }
    }
}

}
