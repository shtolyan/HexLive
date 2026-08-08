using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates
{

/// <summary>
/// У каждой цели есть строка в таблице — и таблица не разошлась с тем, что
/// раньше говорили switch'и.
/// <para>
/// Первая проверка — та, ради которой таблица и заводилась: раньше забытая цель
/// не падала нигде, она молча получала поведение по умолчанию, и узнать об этом
/// можно было только по тому, что цель не работает.
/// </para>
/// </summary>
public sealed class GoalCatalogGateTests
{
    [Test]
    public void EveryGoalHasADescriptor()
    {
        var missing = Enum.GetValues(typeof(GoalType))
            .Cast<GoalType>()
            .Where(goal => GoalCatalog.For(goal) == null)
            .Select(goal => goal.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.That(missing, Is.Empty,
            "У цели нет строки в GoalCatalog. Раньше это молчало и давало ей " +
            "поведение по умолчанию; теперь падает здесь:\n  " +
            string.Join("\n  ", missing));
    }

    [Test]
    public void DescriptorsAreNotDuplicated()
    {
        var dupes = GoalCatalog.All
            .Where(d => d != null)
            .GroupBy(d => d.Goal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();

        Assert.That(dupes, Is.Empty, "Цель описана дважды: " + string.Join(", ", dupes));
    }

    /// <summary>
    /// Пин на перенос: набор целей, у которых есть взаимодействие, обязан
    /// совпасть с тем, что перечислял 41-рукавный <c>GoalToInteraction</c>.
    /// Проверяется по СЛЕДСТВИЮ — цель за целью, а не сравнением текстов.
    /// </summary>
    [Test]
    public void InteractionColumnMatchesTheSwitchItReplaced()
    {
        // Ровно то, что стояло в PlanningSystem.GoalToInteraction до переноса.
        var historical = new Dictionary<GoalType, InteractionType>
        {
            [GoalType.GetFood] = InteractionType.PickUp,
            [GoalType.GatherWood] = InteractionType.PickUp,
            [GoalType.GatherTools] = InteractionType.PickUp,
            [GoalType.GetWater] = InteractionType.PickUp,
            [GoalType.StowBottle] = InteractionType.PlaceVessel,
            [GoalType.TendFire] = InteractionType.Fuel,
            [GoalType.CraftSpear] = InteractionType.Craft,
            [GoalType.CookMeat] = InteractionType.Craft,
            [GoalType.CraftLeather] = InteractionType.Craft,
            [GoalType.CraftAxe] = InteractionType.Craft,
            [GoalType.CraftPickaxe] = InteractionType.Craft,
            [GoalType.CraftRack] = InteractionType.Craft,
            [GoalType.CraftBed] = InteractionType.Craft,
            [GoalType.CraftTent] = InteractionType.Craft,
            [GoalType.BuildRaft] = InteractionType.BuildRaft,
            [GoalType.CraftBow] = InteractionType.Craft,
            [GoalType.CraftArrows] = InteractionType.Craft,
            [GoalType.DryClothes] = InteractionType.Hang,
            [GoalType.GatherStone] = InteractionType.PickUp,
            [GoalType.HarvestTree] = InteractionType.Harvest,
            [GoalType.MineBoulder] = InteractionType.Harvest,
            [GoalType.SplitLog] = InteractionType.Process,
            [GoalType.ChopCrown] = InteractionType.Process,
            [GoalType.GatherLeaves] = InteractionType.PickUp,
            [GoalType.HarvestYucca] = InteractionType.Harvest,
            [GoalType.Butcher] = InteractionType.Butcher,
            [GoalType.Build] = InteractionType.Build,
            [GoalType.BuildFurniture] = InteractionType.Build,
            [GoalType.Mourn] = InteractionType.Observe,
            [GoalType.WarmUp] = InteractionType.Observe,
            [GoalType.HaulToFire] = InteractionType.Observe,
            [GoalType.GatherHerb] = InteractionType.PickUp,
            [GoalType.CraftBandage] = InteractionType.Craft,
            [GoalType.GatherFiber] = InteractionType.PickUp,
            [GoalType.CraftRope] = InteractionType.Craft,
            [GoalType.CraftCloth] = InteractionType.Craft,
            [GoalType.CraftKnife] = InteractionType.Craft,
            // §118 additions: append-only craft goals deliberately extend the
            // interaction table that replaced the historical switch.
            [GoalType.CraftSplint] = InteractionType.Craft,
            [GoalType.CraftWoodenArm] = InteractionType.Craft,
            [GoalType.CraftWoodenLeg] = InteractionType.Craft,
            // §28.15C v3: Bury СНЯТА — тело остаётся лежать там, где упало, и
            // хоронить его больше некому. Отсутствие строки здесь и есть
            // утверждение «взаимодействия у цели больше нет».
            //
            // §28.15F: LootCorpse, наоборот, ДОПИСАНА — цели не было во времена
            // switch'а, и её появление здесь это заявка, а не расхождение.
            // Пин сторожит не «список не менялся», а «менялся осознанно».
            [GoalType.LootCorpse] = InteractionType.Loot,
            [GoalType.Sleep] = InteractionType.Sleep,
            [GoalType.Sit] = InteractionType.Sit,
            [GoalType.Dress] = InteractionType.Dress,
        };

        var wrong = new List<string>();
        foreach (GoalType goal in Enum.GetValues(typeof(GoalType)))
        {
            var expected = historical.TryGetValue(goal, out var i) ? (InteractionType?)i : null;
            var actual = GoalCatalog.InteractionFor(goal);
            if (!Nullable.Equals(expected, actual))
            {
                wrong.Add(goal + ": было " + Describe(expected) + ", стало " + Describe(actual));
            }
        }

        Assert.That(wrong, Is.Empty,
            "Колонка Interaction разошлась со switch'ем, который она заменила:\n  " +
            string.Join("\n  ", wrong));
    }

    /// <summary>
    /// Реактивные цели ставятся системами напрямую, минуя аукцион. Если цель
    /// помечена реактивной, но никто её не присваивает — она мертва, а флаг
    /// это скрывает.
    /// </summary>
    [Test]
    public void ReactiveGoalsAreActuallyAssignedBySystems()
    {
        var assigned = SourceScan.DirectGoalAssignments()
            .Select(h => h.Value)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = GoalCatalog.All
            .Where(d => d != null && d.IsReactive)
            .Where(d => !assigned.Contains(d.Goal.ToString()))
            .Select(d => d.Goal.ToString())
            .ToList();

        Assert.That(orphans, Is.Empty,
            "Цель объявлена реактивной, но ни одна система её не присваивает:\n  " +
            string.Join("\n  ", orphans));
    }

    [Test]
    public void CraftOutputsOnlyOnCraftGoals()
    {
        var wrong = GoalCatalog.All
            .Where(d => d != null && d.CraftGroundOutputs != null)
            .Where(d => d.Interaction != InteractionType.Craft)
            .Select(d => d.Goal.ToString())
            .ToList();

        Assert.That(wrong, Is.Empty,
            "Цель кладёт выход крафта на землю, но крафтом не является:\n  " +
            string.Join("\n  ", wrong));
    }

    private static string Describe(InteractionType? value) =>
        value.HasValue ? value.Value.ToString() : "ничего";
}

}
