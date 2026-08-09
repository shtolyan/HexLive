using System.Linq;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §21.21B v17: прыжок нельзя бросить в воздухе.
/// <para>
/// Жалоба игрока: «спрыгнула, резко развернулась — и её телепает наверх, она
/// уже не прыгает»; и то же у воды: «пошла стирать, прыгнула в воду без плюха,
/// отшвырнуло обратно на берег». Причина одна: смена цели гасила окно прыжка
/// (<c>PlanInterruption</c>), и NPC оставалась с позицией на полпути между
/// уровнями, но с ВЗЛЁТНЫМ <c>npc.Tile</c>. Вид рисует актёра по высоте
/// <c>npc.Tile</c>, поэтому её мгновенно возвращало на уступ, с которого она
/// только что спрыгнула.
/// </para>
/// <para>
/// Проверяется ровно это: прерываем план в полёте и требуем, чтобы окно
/// закрылось, а позиция и тайл сошлись на ОДНОМ конце прыжка. Инвариант «позиция
/// лежит в своём гексе» тут НЕ годится — граничные junction'ы принадлежат двум-трём
/// тайлам, и при ходьбе вдоль границы позиция законно уезжает за пределы гекса
/// того тайла, который выбрал направленный резолвер.
/// </para>
/// </summary>
public sealed class HopAtomicityTests
{
    [Test]
    public void PlanInterruptedMidFlight_StillLands()
    {
        var engine = TestWorld.CreateEngine();
        var world = engine.World;
        var interruptions = 0;

        for (var tick = 0; tick < 6000 && interruptions < 10; tick++)
        {
            engine.Step();

            // Ловим её В СЕРЕДИНЕ полёта: у самой точки взлёта прерывание
            // безобидно (она там и стоит), и тест прошёл бы даже на сломанном
            // коде — проверено, именно так первая версия и обманулась.
            // §21.21B v23: порог «середины» масштабируется от фактической длины
            // полёта (2×EdgePadding из simdata) — с коротким оттюненным прыжком
            // (0.2 wu) жёсткие 0.15 не оставляли середины вовсе, и тест
            // вырождался в «ни одного прерывания».
            var flightLength =
                HexLive.Simulation.Navigation.HexHopTuning.EdgePadding * 2f;
            var midMargin = System.MathF.Min(0.15f, flightLength * 0.25f);
            var flying = world.Entities.Npcs.Values.FirstOrDefault(n =>
                n.Movement.HopTimer > 0f && !n.Movement.HopCrossed &&
                HexSpatialMath.Distance(n.Position, n.Movement.HopFrom) > midMargin &&
                HexSpatialMath.Distance(n.Position, n.Movement.HopTo) > midMargin);
            if (flying is null)
            {
                continue;
            }

            var hopFrom = flying.Movement.HopFrom;
            var hopTo = flying.Movement.HopTo;
            var targetTile = flying.Movement.HopTargetTile;
            var takeoffTile = flying.Tile;

            // Ровно то, что делает игра, когда цель меняется на ходу.
            PlanInterruption.Abort(world, flying, "test: turned around mid-flight");
            interruptions++;

            // Окно обязано закрыться само. Раньше оно висело вечно: путь пуст,
            // IsMoving=false, и блок полёта был недостижим.
            for (var settle = 0; settle < 16 && flying.Movement.HopTimer > 0f; settle++)
            {
                engine.Step();
            }

            Assert.That(flying.Movement.HopTimer, Is.LessThanOrEqualTo(0f),
                $"t{world.Tick}: окно прыжка не закрылось после прерывания плана — " +
                "NPC осталась висеть в воздухе.");

            var atLanding = HexSpatialMath.Distance(flying.Position, hopTo);
            var atTakeoff = HexSpatialMath.Distance(flying.Position, hopFrom);
            Assert.That(System.MathF.Min(atLanding, atTakeoff), Is.LessThanOrEqualTo(0.05f),
                $"t{world.Tick}: после прерывания она стоит в " +
                $"{Trace.FormatPos(flying.Position)} — это не взлёт " +
                $"{Trace.FormatPos(hopFrom)} и не посадка {Trace.FormatPos(hopTo)}, " +
                "то есть между уровнями.");

            // И главное: позиция и тайл — про один и тот же конец прыжка.
            if (atLanding <= atTakeoff)
            {
                Assert.That(flying.Tile, Is.EqualTo(targetTile),
                    $"t{world.Tick}: стоит на точке ПОСАДКИ, а тайл остался " +
                    $"{flying.Tile.Q},{flying.Tile.R} вместо {targetTile.Q},{targetTile.R}. " +
                    "Вид рисует её по высоте тайла — это и есть телепорт на уступ.");
            }
            else
            {
                Assert.That(flying.Tile, Is.EqualTo(takeoffTile),
                    $"t{world.Tick}: вернулась на точку ВЗЛЁТА, но тайл уже " +
                    $"{flying.Tile.Q},{flying.Tile.R}.");
            }
        }

        Assert.That(interruptions, Is.GreaterThan(0),
            "Ни один прыжок не удалось прервать в полёте — тест ничего не проверил.");
    }
}

}
