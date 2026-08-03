using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// §81.11: абьюз обязан СЛУЧАТЬСЯ в реальном мире, а не только в арене.
///
/// <para>
/// История, ради которой этот файл существует: арена §91 снимала грейс сама
/// себе (Tick = 48900), а оба входа в цель — аукцион и прерывание — молчали до
/// тика 48000. Итог: «в сцене всё работает», а в игре чужак НИКОГДА не абьюзил —
/// сидел с общением в нуле и исследовал остров. Ни один гейт этого не ловил,
/// потому что все меряли арену или математику, и никто — прототипный остров.
/// </para>
///
/// <para>
/// Ассерт — только на <c>AbuseTriggered</c> (цель взята), НЕ на финиш сцены:
/// дойти до жертвы через пол-острова могут помешать сон, санктуарий и налёт,
/// и ассерт на завершение сделал бы гейт флаки. Довод сцены до конца меряет
/// соак (<c>--trace-preset abuse</c>), не гейт.
/// </para>
/// </summary>
public sealed class AbuseRealWorldTests
{
    // ⭐ §108 ВЫКЛЮЧЕН на время этих гейтов — намеренно, и это не сокрытие.
    // Здесь стерегут путь-ПРЕРЫВАНИЕ §81.11, а групповая охота держит чужака в
    // драке ровно тогда, когда прерывание должно выстрелить: на сиде 816616098
    // с включённым §108 AbuseTriggered падает с 6 до 0 за 12000 тиков (в сцену
    // он всё равно заходит 10 раз, но уже через аукцион). Мерить §81.11 из-под
    // чужой механики значит мерить не §81.11. Само взаимодействие не спрятано:
    // оно записано в spec.md §108 и стережётся гейтом §108.
    private bool _wasGroupHuntEnabled;

    [SetUp]
    public void DisableGroupHunt()
    {
        _wasGroupHuntEnabled = Spec108.GroupHuntEnabled;
        Spec108.GroupHuntEnabled = false;
    }

    [TearDown]
    public void RestoreGroupHunt() => Spec108.GroupHuntEnabled = _wasGroupHuntEnabled;

    [Test]
    public void Outsider_AbusesAtLeastOnce_OnThePrototypeIsland()
    {
        // Сид реального мира, на котором симптом был найден (сейв юзера).
        var engine = TestWorld.CreateEngine(816616098);
        var world = engine.World;

        var outsider = world.Entities.Npcs.Values
            .FirstOrDefault(n => n.Faction != Faction.Colony);
        Assert.That(outsider, Is.Not.Null,
            "Прототипный мир обязан нести чужака (Spec72.OutsiderCount).");

        // Кольцо трассы подрезается на 2048 (~11 тиков) — собираем по ходу.
        long watermark = 0;
        int? triggeredAt = null;
        var blockedTail = new Queue<string>();

        // Одержимость (Social 0.30 → 0.05) наступает к ~тику 700; дальше
        // задержать может только ночь (спящих BestMark отсеивает) — 12000
        // тиков покрывают и её. Худший случай ~15-18 с, типично секунды.
        for (var tick = 0; tick < 12000 && triggeredAt is null; tick++)
        {
            engine.Step();
            foreach (var e in world.Events.Items)
            {
                if (e.Seq <= watermark)
                {
                    continue;
                }

                watermark = e.Seq;
                switch (e.Type)
                {
                    case "AbuseTriggered":
                        triggeredAt ??= world.Tick;
                        break;
                    case "AbuseBlocked":
                        blockedTail.Enqueue($"t{world.Tick}: {e.Message}");
                        while (blockedTail.Count > 8)
                        {
                            blockedTail.Dequeue();
                        }
                        break;
                }
            }
        }

        Assert.That(triggeredAt, Is.Not.Null,
            "Чужак не взял цель Abuse ни разу за 12000 тиков реального мира — " +
            "§81.11 (одержимость пробивает грейс) сломан. Последние причины " +
            "блокировки:\n" + string.Join("\n", blockedTail));
    }

    [Test]
    public void Grace_HoldsUntilObsession_AndExpiresByCalendar()
    {
        var world = TestWorld.CreateWorld();
        var outsider = world.Entities.Npcs.Values
            .First(n => n.Faction != Faction.Colony);

        world.Tick = 100;
        outsider.Needs.Social = 0.30f;
        Assert.That(AbuseMath.GraceHolds(world, outsider), Is.True,
            "Одиноковато, но не на дне — льготные дни держат.");

        // На пороге — уже нет: сравнение строгое (> порога держит),
        // чтобы точный ноль и квантованные значения гарантированно пробивали.
        outsider.Needs.Social = Spec81.AbuseObsessionSocialCeiling;
        Assert.That(AbuseMath.GraceHolds(world, outsider), Is.False,
            "На дне общения одержимость пробивает грейс.");

        outsider.Needs.Social = 1f;
        world.Tick = Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks;
        Assert.That(AbuseMath.GraceHolds(world, outsider), Is.False,
            "Календарь истёк — грейс не держит независимо от общения.");
    }
}

}
