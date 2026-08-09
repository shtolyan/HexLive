using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{

/// <summary>
/// Spec §122. Детектор ЛАЙВЛОКА: ловит круг и молчит на работе.
///
/// <para>
/// Как и у §30.15, здесь зарегистрирован ТОЛЬКО наблюдатель. Полный движок не
/// годится: он бы за те же тики трижды перепланировал, и тест проверял бы
/// способность колонии случайно закрутиться, а не сам детектор.
/// </para>
/// <para>
/// ⭐ Половина тестов здесь — про МОЛЧАНИЕ. Детектор петель опаснее детектора
/// застоя ровно тем, что честная длинная работа выглядит как петля: стройка
/// кровати честно повторяет GatherWood десятки раз. Отличает их ровно одно —
/// завершения, и это должно быть доказано, а не заявлено.
/// </para>
/// </summary>
public sealed class LoopDiagnosticTests
{
    private static (SimulationEngine engine, NPCState npc) Arena()
    {
        var world = TestWorld.CreateWorld();
        var engine = new SimulationEngine(world, new SimulationSettings(), new SimulationClock());
        engine.Clock.Resume();
        engine.Register(new LoopDiagnosticSystem());

        var npc = world.Entities.Npcs.Values.First();
        // Нужды в норме: иначе подпись NeedStarved вмешалась бы в тесты про
        // Сизифа и качели, и падение читалось бы не про то.
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Needs.Energy = 1f;

        engine.Step(); // первый взгляд: сторож только запоминает состояние
        return (engine, npc);
    }

    private static int Loops(WorldState world, string reason) =>
        world.Events.Items.Count(e =>
            e.Type == "LoopDetected" && e.Message.Contains("Reason=" + reason));

    private static string LastLoopMessage(WorldState world) =>
        world.Events.Items.Where(e => e.Type == "LoopDetected")
            .Select(e => e.Message).LastOrDefault() ?? string.Empty;

    /// <summary>Одна попытка: план ожил, поработал и кончился заданным исходом.</summary>
    private static void Attempt(SimulationEngine engine, NPCState npc,
        GoalType goal, int targetId, PlanStatus ending)
    {
        npc.Mind.CurrentGoal = goal;
        npc.Plan.Goal = goal;
        npc.Plan.TargetObjectId = new ObjectId(targetId);
        npc.Plan.Status = PlanStatus.Active;
        engine.Step();

        npc.Plan.Status = ending;
        engine.Step();
    }

    // ── Сизиф: один прицел, много попыток, ноль завершений ───────────────

    /// <summary>
    /// ⭐ Баги #67 (кокос), #78 (инструменты), #91 (взяла-положила). Та же
    /// форма: ходит к одному и тому же и ни разу не доводит.
    /// </summary>
    [Test]
    public void SameTargetTriedAgainAndAgainIsReported()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts + 1; i++)
        {
            Attempt(engine, npc, GoalType.GetWater, 77, PlanStatus.Failed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonSisyphus), Is.GreaterThan(0),
            "Пять заходов к одному объекту без единого завершения — это петля, " +
            "и она обязана называться вслух: StuckDiagnosticSystem её не видит " +
            "по построению, NPC всё это время деятельна.");
    }

    /// <summary>
    /// ⭐ Главный тест на ложное срабатывание. Стройка честно ходит к одному
    /// складу десятки раз — но ДОВОДИТ. Разница только в исходе, и именно она
    /// отделяет работу от круга.
    /// </summary>
    [Test]
    public void SameTargetWithCompletionsIsHonestWork()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts * 3; i++)
        {
            Attempt(engine, npc, GoalType.GatherWood, 42, PlanStatus.Completed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonSisyphus), Is.Zero,
            "Детектор принял честную работу за петлю. Такой шум обесценивает " +
            "его быстрее, чем молчание: стройка кровати повторяет ходку за " +
            "материалом десятки раз и обязана оставаться незамеченной.");
    }

    /// <summary>Меньше порога — молчим: одна временная пробка не тупик.</summary>
    [Test]
    public void FewRetriesStaySilent()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts - 2; i++)
        {
            Attempt(engine, npc, GoalType.GetFood, 5, PlanStatus.Failed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonSisyphus), Is.Zero,
            "Сработало раньше бюджета ретраев маршрута — значит сторож " +
            "жалуется на временно занятую дорогу, а не на бессмысленный поход.");
    }

    /// <summary>
    /// Разные объекты — не Сизиф. Обошла пять кокосов и ни один не сработал:
    /// объект тут ни при чём, и «отвернуться от него» ничего не лечит. Это
    /// работа для NeedStarved, а не для этой подписи.
    /// </summary>
    [Test]
    public void DifferentTargetsAreNotSisyphus()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts + 2; i++)
        {
            Attempt(engine, npc, GoalType.GetWater, 100 + i, PlanStatus.Failed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonSisyphus), Is.Zero,
            "Каждый заход был к НОВОМУ объекту — у Сизифа прицел один и тот же.");
    }

    // ── Качели: две цели меняются местами ────────────────────────────────

    /// <summary>⭐ Баг #90: «то хочет докопаться, то хочет пить».</summary>
    [Test]
    public void TwoGoalsTradingPlacesIsReported()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopOscillations + 2; i++)
        {
            var goal = i % 2 == 0 ? GoalType.GatherTools : GoalType.Drink;
            Attempt(engine, npc, goal, 9, PlanStatus.Failed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonOscillation), Is.GreaterThan(0),
            "Шесть качаний между двумя целями без единого завершения — это " +
            "ровно баг #90, и он должен читаться из трассы, а не с экрана игрока.");
    }

    /// <summary>Чередование, в котором дела ДОВОДЯТСЯ, — это распорядок дня.</summary>
    [Test]
    public void AlternatingGoalsThatCompleteAreNotSwings()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopOscillations * 2; i++)
        {
            var goal = i % 2 == 0 ? GoalType.Eat : GoalType.Drink;
            Attempt(engine, npc, goal, 9, PlanStatus.Completed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonOscillation), Is.Zero,
            "Поела — попила — поела это жизнь, а не качели.");
    }

    // ── Что сторож не считает вовсе ──────────────────────────────────────

    /// <summary>
    /// ⭐ §81.14/§109.6: одержимость липкая НАМЕРЕННО. Доложить о ней можно и
    /// нужно, но политика обязана ехать в сообщении — иначе читающий трассу
    /// решит, что сторож проспал, а фаза 2 однажды порвёт сцену «по числам».
    /// </summary>
    [Test]
    public void StickyGoalIsReportedWithItsPolicy()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts + 1; i++)
        {
            Attempt(engine, npc, GoalType.Abuse, 3, PlanStatus.Failed);
        }

        Assert.That(Loops(engine.World, LoopDiagnosticSystem.ReasonSisyphus), Is.GreaterThan(0),
            "О липкой цели тоже надо докладывать — запрещён только автовыход.");
        Assert.That(LastLoopMessage(engine.World), Does.Contain("Policy=Sticky"),
            "Политика цели не доехала до сообщения. Без неё §81.14 держится на " +
            "памяти читающего, а не на данных.");
    }

    /// <summary>
    /// Idle/Explore — это ЧЕМ петлю разрывают. Считать их попытками значит
    /// мерить лекарство вместе с болезнью.
    /// </summary>
    [Test]
    public void AmbientGoalsAreNotCounted()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopOscillations * 3; i++)
        {
            var goal = i % 2 == 0 ? GoalType.Idle : GoalType.Explore;
            Attempt(engine, npc, goal, 9, PlanStatus.Failed);
        }

        Assert.That(engine.World.Events.Items.Count(e => e.Type == "LoopDetected"), Is.Zero,
            "Простой и осмотр посчитались за петлю — тогда любая пауза в " +
            "работе колонии будет докладываться как баг.");
    }

    /// <summary>Без сознания круг наматывать невозможно (§30.15, §110, §105.14).</summary>
    [Test]
    public void UnconsciousIsNotALoop()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts + 1; i++)
        {
            Attempt(engine, npc, GoalType.GetWater, 77, PlanStatus.Failed);
        }

        var beforeComa = engine.World.Events.Items.Count(e => e.Type == "LoopDetected");
        Assert.That(beforeComa, Is.GreaterThan(0), "Предпосылка теста не выполнилась.");

        npc.Mind.ComaCause = ComaCause.Exhaustion;
        for (var i = 0; i < 40; i++)
        {
            engine.Step();
        }

        Assert.That(engine.World.Events.Items.Count(e => e.Type == "LoopDetected"),
            Is.EqualTo(beforeComa),
            "Сторож жалуется на лежащую в коме — она обязана лежать неподвижно.");
    }

    /// <summary>
    /// Жалоба повторяется, но редко: длинная петля должна быть видна в хвосте
    /// трассы и не должна его топить.
    /// </summary>
    [Test]
    public void RepeatComplaintIsThrottled()
    {
        var (engine, npc) = Arena();

        for (var i = 0; i < AiBalance.LoopRepeatAttempts + 1; i++)
        {
            Attempt(engine, npc, GoalType.GetWater, 77, PlanStatus.Failed);
        }

        var afterOnset = engine.World.Events.Items.Count(e => e.Type == "LoopDetected");
        for (var i = 0; i < AiBalance.LoopRepeatEmitTicks / 2; i++)
        {
            engine.Step();
        }

        Assert.That(engine.World.Events.Items.Count(e => e.Type == "LoopDetected"),
            Is.EqualTo(afterOnset),
            "Повтор пришёл раньше LoopRepeatEmitTicks — так хвост самописца " +
            "забьётся одной и той же жалобой, и история до петли пропадёт.");
    }

    // ── Реестр намерений сам по себе ─────────────────────────────────────

    /// <summary>
    /// Переприцеливание внутри одной цели — это ДВЕ попытки. Иначе «обошла пять
    /// кокосов» и «пять раз ходила к одному» неразличимы, а лечатся они по-разному.
    /// </summary>
    [Test]
    public void RetargetSplitsOneGoalIntoTwoAttempts()
    {
        var ledger = new IntentLedger();
        ledger.Open(1, 10, GoalType.GetWater, new ObjectId(5), null);
        ledger.Retarget(1, 20, GoalType.GetWater, new ObjectId(6), null);
        ledger.Close(1, 30, IntentOutcome.Failed);

        ledger.CountGoal(1, GoalType.GetWater, 0, out var attempts, out var completions);
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(completions, Is.Zero);

        var first = new IntentRecord(0, 0, GoalType.GetWater, new ObjectId(5), null, IntentOutcome.Open);
        ledger.CountAim(1, first, 0, out var firstAttempts, out _);
        Assert.That(firstAttempts, Is.EqualTo(1),
            "Прицел считается вместе с объектом — иначе Сизиф сливается с обходом.");
    }

    /// <summary>Окно и правда окно: вчерашняя неудача в сегодняшний счёт не идёт.</summary>
    [Test]
    public void CountingHonoursTheWindow()
    {
        var ledger = new IntentLedger();
        var aim = new IntentRecord(0, 0, GoalType.Eat, new ObjectId(1), null, IntentOutcome.Open);

        ledger.Open(1, 10, GoalType.Eat, new ObjectId(1), null);
        ledger.Close(1, 20, IntentOutcome.Failed);
        ledger.Open(1, 900, GoalType.Eat, new ObjectId(1), null);
        ledger.Close(1, 910, IntentOutcome.Failed);

        ledger.CountAim(1, aim, 500, out var attempts, out _);
        Assert.That(attempts, Is.EqualTo(1),
            "В окно попала попытка, открытая до его начала.");
    }
}

}
