using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Мёртвая зона §102 — как класс ошибки, а не как один случай.
/// <para>
/// Было так: преследование меряло МЕТРИЧЕСКОЙ дистанцией разговора (2R), а
/// старт сцены — СОСЕДСТВОМ УЗЛОВ. В кольце «ближе 2R, но не на смежном узле»
/// молчали оба условия: он не догонял, потому что уже близко, и не начинал,
/// потому что рукой не достаёт. 2951 из 12000 тиков, застой 2872 тика подряд.
/// </para>
/// <para>
/// Тест ищет это кольцо В РЕАЛЬНОЙ ТОПОЛОГИИ и требует от единственного
/// авторитета внятного ответа. Пока «идти» определено как «не действовать»
/// одной функцией, третьему состоянию взяться неоткуда — но проверка стоит
/// здесь, чтобы будущая попытка снова развести мерки падала тестом, а не
/// обнаруживалась через полгода по жалобе «она зависла».
/// </para>
/// </summary>
public sealed class ReachRuleTests
{
    /// <summary>
    /// Ставит двоих в то самое кольцо: близко по прямой, но узлы НЕ смежные.
    /// </summary>
    private static (WorldState world, NPCState a, NPCState b) InTheDeadRing()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToList();
        Assert.That(npcs.Count, Is.EqualTo(2), "Нужны хотя бы двое.");

        foreach (var from in world.Junctions.Items.Values)
        {
            foreach (var to in world.Junctions.Items.Values)
            {
                if (from.Id.Equals(to.Id) || to.Neighbors.Contains(from.Id))
                {
                    continue;
                }

                var distance = HexSpatialMath.Distance(from.WorldPosition, to.WorldPosition);
                if (distance > InteractionReach.Talk)
                {
                    continue;
                }

                npcs[0].CurrentJunction = from.Id;
                npcs[0].Position = from.WorldPosition;
                npcs[1].CurrentJunction = to.Id;
                npcs[1].Position = to.WorldPosition;
                return (world, npcs[0], npcs[1]);
            }
        }

        Assert.Fail("В топологии не нашлось пары «близко по прямой, но не смежные» — " +
                    "тогда этот тест не про то, и его надо переписать.");
        return default;
    }

    [Test]
    public void CloseButNotAdjacentMeansApproach_NotSilence()
    {
        var (world, actor, target) = InTheDeadRing();

        Assert.That(InteractionReach.CanStrike(world, actor, target), Is.False,
            "Пара обязана быть НЕ в ударной дистанции — иначе кольцо выбрано неверно.");
        Assert.That(
            HexSpatialMath.Distance(actor.Position, target.Position),
            Is.LessThanOrEqualTo(InteractionReach.Talk),
            "…и при этом внутри разговорной дистанции — это и есть кольцо.");

        var verdict = InteractionReach.AssessMelee(world, actor, target,
            sceneStarted: false, "test");

        Assert.That(verdict, Is.EqualTo(MeleeApproach.Approach),
            "Из мёртвого кольца обязан быть выход: не достаю — иду. Именно здесь " +
            "раньше молчали ОБА условия, и NPC замирал напротив жертвы навсегда.");
    }

    /// <summary>
    /// Ответа ровно два, и «действовать» означает «дотягиваюсь» — при незапущенной
    /// сцене это в точности <see cref="InteractionReach.CanStrike"/>. Пока это одно
    /// и то же, зазору между «пора идти» и «можно начинать» неоткуда взяться.
    /// </summary>
    [Test]
    public void ApproachIsExactlyTheNegationOfActing()
    {
        var world = TestWorld.CreateWorld();
        var npcs = world.Entities.Npcs.Values.Take(2).ToList();
        var a = npcs[0];
        var b = npcs[1];

        var checkedPairs = 0;
        foreach (var junction in world.Junctions.Items.Values.Take(400))
        {
            a.CurrentJunction = junction.Id;
            a.Position = junction.WorldPosition;

            foreach (var other in junction.Neighbors.Concat(new[] { junction.Id }))
            {
                if (!world.Junctions.Items.TryGetValue(other, out var otherJunction))
                {
                    continue;
                }

                b.CurrentJunction = otherJunction.Id;
                b.Position = otherJunction.WorldPosition;

                var canStrike = InteractionReach.CanStrike(world, a, b);
                var verdict = InteractionReach.AssessMelee(world, a, b,
                    sceneStarted: false, "test");

                Assert.That(verdict == MeleeApproach.Act, Is.EqualTo(canStrike),
                    "«Действовать» и «дотягиваюсь» обязаны совпадать до старта сцены.");
                checkedPairs++;
            }
        }

        Assert.That(checkedPairs, Is.GreaterThan(100), "Проверено подозрительно мало пар.");
    }

    /// <summary>
    /// Гистерезис вход/удержание — намеренный и должен сохраниться: войти в
    /// сцену можно только вплотную (§98), а удерживать её позволено на
    /// разговорной дистанции (§89 — иначе сцена рвалась на каждом её шаге).
    /// </summary>
    [Test]
    public void StartedSceneToleratesTheRingThatBlocksStarting()
    {
        var (world, actor, target) = InTheDeadRing();

        Assert.That(
            InteractionReach.AssessMelee(world, actor, target, sceneStarted: false, "test"),
            Is.EqualTo(MeleeApproach.Approach));
        Assert.That(
            InteractionReach.AssessMelee(world, actor, target, sceneStarted: true, "test"),
            Is.EqualTo(MeleeApproach.Act),
            "Начатую сцену пара шагов рвать не должна — за узкую мерку внутри " +
            "сцены уже платили десятками срывов за прогон (§89).");
    }
}

}
