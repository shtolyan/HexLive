using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §26.6A r5 — сквозь ЧУЖОЕ тело рука не проходит.
/// <para>
/// Скриншот пользователя: девушка вскрывает кокос, стоя по ту сторону пальмы.
/// Это была не промашка анимации, а арифметика, работавшая ровно как написана:
/// шаг субсетки 0.375 wu, <c>BesideReach(0)</c> = 0.80 wu — то есть рука
/// достаёт на ДВА шага, то есть ровно через один занятый узел. Ствол пальмы —
/// ровно один занятый узел (<c>tree.palm</c> без <c>ObstacleRadius</c>, закрыт
/// только якорь), а кокос падает на свободный узел того же тайла. r4 при этом
/// разрешал ободу проходить сквозь ЛЮБОЙ футпринт объекта, обрывая ветку только
/// на терраине — значит и метрика, и проходимость говорили «законно».
/// </para>
/// <para>
/// r5 различает СВОЙ футпринт и чужой: свой переступить можно (иначе не
/// поработать у костра с обода), чужой — стена, как обрыв. Тест строит ту самую
/// геометрию в НАСТОЯЩЕЙ топологии, а не в удобной выдуманной.
/// </para>
/// </summary>
public sealed class ReachThroughObstacleTests
{
    /// <summary>Занятый узел препятствия + два его проходимых соседа НАПРОТИВ
    /// друг друга (0.75 wu — ровно «через ствол», обойти внутри 0.80 wu
    /// нельзя, общий сосед у них в треугольной решётке ровно один: сам ствол).</summary>
    private static (WorldState world, WorldObjectState trunk, Junction stand, Junction prize) AcrossTheTrunk()
    {
        var world = TestWorld.CreateWorld();
        var reach = SpatialQueries.BesideReach(0f);

        foreach (var obstacle in world.Entities.Objects.Values.ToList())
        {
            foreach (var blockedId in obstacle.BlockedJunctions)
            {
                if (!world.Junctions.Items.TryGetValue(blockedId, out var blocked))
                {
                    continue;
                }

                var open = blocked.Neighbors
                    .Where(id => world.Junctions.Items.TryGetValue(id, out var j) && !j.Blocked &&
                                 !SpatialQueries.IsAllWaterJunction(world, id))
                    .Select(id => world.Junctions.Items[id])
                    .ToList();

                foreach (var stand in open)
                {
                    foreach (var prize in open)
                    {
                        var gap = HexSpatialMath.Distance(stand.WorldPosition, prize.WorldPosition);
                        // >0.7 = пара НАПРОТИВ (0.75), а не «через угол» (0.65):
                        // угловую пару обойти внутри радиуса можно, и это законно.
                        if (gap > 0.7f && gap <= reach && prize.Tiles.Count > 0)
                        {
                            return (world, obstacle, stand, prize);
                        }
                    }
                }
            }
        }

        Assert.Fail("В топологии не нашлось «занятый узел между двумя свободными» — " +
                    "тогда тест меряет не ту геометрию и его надо переписать.");
        return default;
    }

    private static WorldObjectState PutCoconut(WorldState world, Junction at) =>
        WorldObjectMutations.SpawnObject(
            world, ContentIds.Coconut, world.Fragments.Items.Keys.First(), at.Tiles[0], at.Id);

    private static NPCState StandAt(WorldState world, Junction junction)
    {
        var npc = world.Entities.Npcs.Values.First();
        npc.CurrentJunction = junction.Id;
        npc.Position = junction.WorldPosition;
        npc.Tile = junction.Tiles.Count > 0 ? junction.Tiles[0] : npc.Tile;
        return npc;
    }

    [Test]
    public void HandsDoNotPassThroughAnotherObjectsBody()
    {
        var (world, trunk, stand, prize) = AcrossTheTrunk();
        var coconut = PutCoconut(world, prize);
        var npc = StandAt(world, stand);

        // Дистанция ОДОБРЯЕТ — иначе тест поймал бы не тот отказ и молча
        // «проходил» бы даже с откаченным r5.
        Assert.That(
            InteractionReach.CheckStart(world, npc, prize.WorldPosition,
                SpatialQueries.BesideReach(0f), "control"),
            Is.True,
            "Пара обязана быть в пределах BesideReach — весь смысл в том, что " +
            "отказывает БАРЬЕР, а не метрика.");

        Assert.That(InteractionReach.CheckObjectStart(world, npc, coconut, 0f), Is.False,
            $"Через тело {trunk.DefinitionId} дотягиваться нельзя: занятый узел " +
            "между рукой и добычей — стена, ровно как обрыв (§26.6A r5).");
    }

    [Test]
    public void OwnFootprintStaysCrossable()
    {
        var (world, trunk, stand, _) = AcrossTheTrunk();
        var npc = StandAt(world, stand);

        // Костёр, кровать, сама пальма: работать с обода СВОЕЙ занятой клетки
        // обязано остаться можно, иначе r5 забирает у костра весь его смысл.
        Assert.That(
            InteractionReach.CheckObjectStart(world, npc, trunk,
                world.Content.ObjectDefinitions.TryGetValue(trunk.DefinitionId, out var def)
                    ? def.ObstacleRadius : 0f),
            Is.True,
            $"У {trunk.DefinitionId} она стоит на ободе его СОБСТВЕННОГО футпринта — " +
            "это и есть законная поза для работы, а не дотягивание сквозь.");
    }

    [Test]
    public void AnHonestNeighbourStillReaches()
    {
        var (world, _, _, prize) = AcrossTheTrunk();
        var coconut = PutCoconut(world, prize);

        var clear = prize.Neighbors
            .Where(id => world.Junctions.Items.TryGetValue(id, out var j) && !j.Blocked &&
                         j.Tiles.Count > 0 && !SpatialQueries.IsAllWaterJunction(world, id))
            .Select(id => world.Junctions.Items[id])
            .FirstOrDefault();
        Assert.That(clear, Is.Not.Null, "У кокоса должен быть хоть один свободный сосед.");

        var npc = StandAt(world, clear);
        Assert.That(InteractionReach.CheckObjectStart(world, npc, coconut, 0f), Is.True,
            "r5 не должен запрещать обычное: со свободного соседнего узла кокос " +
            "берётся как всегда. Иначе цена правила — голод, а не реализм.");
    }

    /// <summary>
    /// Вторая половина r5: раз рука больше не проходит сквозь тело, мир обязан
    /// класть добро туда, где до него дотянутся. Пока выкладка была first-fit
    /// «первый проходимый узел», пальма роняла кокос вплотную к собственному
    /// стволу — и это уже не «неудобно», а еда, которая сгниёт нетронутой
    /// (замер: 12 сидов × 10 дней, упало столько же, подобрано 1772 → 1595).
    /// </summary>
    [Test]
    public void EveryDroppedItemHasSomewhereToStand()
    {
        var engine = TestWorld.CreateEngine(12345);
        var world = engine.World;
        for (var i = 0; i < 4000; i++)
        {
            engine.Step();
        }

        var checkedItems = 0;
        var stranded = new List<string>();
        foreach (var obj in world.Entities.Objects.Values.ToList())
        {
            // Только лежащее на земле добро: у построек футпринт свой, и обод
            // им считается иначе (см. OwnFootprintStaysCrossable).
            if (obj.Junctions.Count == 0 || obj.BlockedJunctions.Count > 0 ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) ||
                def.Tags.Contains("Obstacle"))
            {
                continue;
            }

            checkedItems++;
            if (InteractionReach.CountApproaches(world, obj.Junctions[0]) == 0)
            {
                stranded.Add($"{obj.DefinitionId}@j{obj.Junctions[0].Value}");
            }
        }

        Assert.That(checkedItems, Is.GreaterThan(20), "Проверено подозрительно мало предметов.");
        Assert.That(stranded, Is.Empty,
            "Предмет, до которого не дотянуться ни с одной свободной клетки, — " +
            "это ресурс, который никто никогда не поднимет. Выкладка обязана " +
            "считать подходы тем же предикатом, которым потом меряют сборщицу.");
    }

    /// <summary>
    /// Инвариант предиката отдельно от геометрии: вода никогда не барьер (§31C.7 —
    /// перегнуться через берег законно), а <c>owner == null</c> — самое строгое
    /// чтение, и вызывающему без объекта в руках нужно именно оно.
    /// </summary>
    [Test]
    public void WaterIsNeverABarrierAndNullOwnerIsStrictest()
    {
        var world = TestWorld.CreateWorld();
        var checkedBlocked = 0;

        foreach (var obstacle in world.Entities.Objects.Values.ToList())
        {
            foreach (var blockedId in obstacle.BlockedJunctions)
            {
                Assert.That(SpatialQueries.IsBarrierFor(world, blockedId, null), Is.True,
                    "Без объекта в руках любое занятое тело — стена.");
                Assert.That(SpatialQueries.IsBarrierFor(world, blockedId, obstacle), Is.False,
                    "Своё тело обязано остаться проходимым для обода.");
                checkedBlocked++;
            }
        }

        Assert.That(checkedBlocked, Is.GreaterThan(0), "В мире не нашлось ни одного футпринта.");

        var wet = new List<JunctionId>();
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked && SpatialQueries.IsAllWaterJunction(world, junction.Id))
            {
                wet.Add(junction.Id);
            }
        }

        foreach (var id in wet)
        {
            Assert.That(SpatialQueries.IsBarrierFor(world, id, null), Is.False,
                "Вода не барьер ни для кого: перегнуться через берег за флотсамом " +
                "или напиться было законно всегда.");
        }
    }
}

}
