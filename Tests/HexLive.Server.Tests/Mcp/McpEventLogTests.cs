using HexLive.Server.Mcp;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Server.Tests.Mcp
{

/// <summary>
/// Контракт §144.6: чем именно агент рискует, спрашивая «что было после Seq=N».
/// <para>
/// Тесты написаны от трёх способов соврать, а не от happy path. Тихо потерять
/// событие, тихо отдать его дважды и тихо выдать чужой мир за свой — три ошибки,
/// которые агент не заметит в момент совершения и обнаружит через сотню тиков
/// как необъяснимое поведение колонии.
/// </para>
/// </summary>
public sealed class McpEventLogTests
{
    private static SimulationEvent Visible(string type = "TalkStarted", int? entityId = null) =>
        new() { Tick = 1, Type = type, Message = "Kind=Chat", EntityId = entityId };

    /// <summary>Болтовня ИИ: занимает seq, но наружу не идёт.</summary>
    private static SimulationEvent Chatter() =>
        new() { Tick = 1, Type = "GoalScored", Message = "Goal=Idle Score=0.15" };

    private static (McpEventLog log, SimulationEventBuffer buffer) Fresh()
    {
        var log = new McpEventLog();
        var buffer = new SimulationEventBuffer();
        return (log, buffer);
    }

    [Test]
    public void WatermarkAdvancesPastFilteredChatter()
    {
        var (log, buffer) = Fresh();
        buffer.Add(Visible());
        for (var i = 0; i < 50; i++)
        {
            buffer.Add(Chatter());
        }

        var drained = log.Drain(buffer, 0);

        // Ровно тот инвариант, что у зрителя: слив дошёл до вершины кольца, а не
        // до последнего ПОКАЗАННОГО события. Иначе те же 50 записей болтовни
        // пересматривались бы на каждом вызове до скончания века.
        Assert.That(drained, Is.EqualTo(buffer.HighestSeq));

        var batch = log.Read(0, 100, null);
        Assert.That(batch.Events, Has.Count.EqualTo(1));
        Assert.That(batch.Watermark, Is.EqualTo(buffer.HighestSeq),
            "Вотермарка обязана уйти за отфильтрованное, иначе агент вечно " +
            "переспрашивает про события, которые ему никогда не отдадут.");
    }

    [Test]
    public void TruncationDoesNotAdvancePastUndeliveredEvents()
    {
        var (log, buffer) = Fresh();
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(Visible());
        }

        log.Drain(buffer, 0);
        var batch = log.Read(0, 3, null);

        Assert.That(batch.Truncated, Is.True);
        Assert.That(batch.Events, Has.Count.EqualTo(3));

        // Самая опасная из трёх ошибок: объявить прочитанным то, чего не отдали.
        // Агент подставит вотермарку обратно и семь событий исчезнут навсегда —
        // без gap, без единого признака.
        Assert.That(batch.Watermark, Is.EqualTo(batch.Events[^1].Seq));
        Assert.That(batch.Watermark, Is.LessThan(log.HighestSeq));

        var rest = log.Read(batch.Watermark, 100, null);
        Assert.That(rest.Events, Has.Count.EqualTo(7), "Остаток обязан дочитаться.");
    }

    [Test]
    public void TrimmedPastCallerIsReportedAsGap()
    {
        var log = new McpEventLog(capacity: 4);
        var buffer = new SimulationEventBuffer();
        for (var i = 0; i < 12; i++)
        {
            buffer.Add(Visible());
        }

        log.Drain(buffer, 0);

        var batch = log.Read(1, 100, null);
        Assert.That(batch.Gap, Is.True,
            "Подрезка мимо позиции агента обязана называться, а не выглядеть " +
            "как «просто ничего не было».");
        Assert.That(batch.OldestRetainedSeq, Is.GreaterThan(2));

        // Тот, кто не отставал, дыры не видит.
        Assert.That(log.Read(log.HighestSeq - 1, 100, null).Gap, Is.False);
    }

    [Test]
    public void SeqBelowCallerWatermarkIsASessionReset()
    {
        var (log, buffer) = Fresh();
        buffer.Add(Visible());
        log.Drain(buffer, 0);

        var batch = log.Read(9000, 100, null);

        Assert.That(batch.SessionReset, Is.True,
            "Seq не может уйти назад в пределах одного мира — значит мир другой.");
        Assert.That(batch.Events, Is.Empty);
    }

    [Test]
    public void MissingWatermarkMeansFromNowNotEverythingHeld()
    {
        var (log, buffer) = Fresh();
        for (var i = 0; i < 20; i++)
        {
            buffer.Add(Visible());
        }

        log.Drain(buffer, 0);

        var batch = log.Read(null, 100, null);

        // Пришедший агент получает точку отсчёта, а не двадцать событий, которых
        // он не вызывал и с текущим состоянием соотнести не может.
        Assert.That(batch.Events, Is.Empty);
        Assert.That(batch.Watermark, Is.EqualTo(log.HighestSeq));
        Assert.That(batch.Gap, Is.False);
        Assert.That(batch.SessionReset, Is.False);

        // Ноль — это НЕ «не передали»: он значит «с самого начала».
        Assert.That(log.Read(0, 100, null).Events, Has.Count.EqualTo(20));
    }

    [Test]
    public void NpcFilterIsAViewAndDoesNotMoveTheWatermark()
    {
        var (log, buffer) = Fresh();
        buffer.Add(Visible(entityId: 3));
        buffer.Add(Visible(entityId: 7));
        buffer.Add(Visible(entityId: 3));
        log.Drain(buffer, 0);

        var mine = log.Read(0, 100, entityId: 3);

        Assert.That(mine.Events, Has.Count.EqualTo(2));

        // Иначе два агента, смотрящие за разными колонистками, держали бы
        // вотермарки, означающие разное — и один «терял» бы события другого.
        Assert.That(mine.Watermark, Is.EqualTo(log.HighestSeq));
    }

    [Test]
    public void ControllerEventsReachTheAgentThoughTheColonyNeverSeesThem()
    {
        var (log, buffer) = Fresh();
        buffer.Add(new SimulationEvent
        {
            Tick = 5,
            Type = "ManualOrderRejected",
            Message = "Order=MoveTo Reason=Unreachable",
            EntityId = 3,
        });

        log.Drain(buffer, 0);

        Assert.That(log.Read(0, 100, null).Events, Has.Count.EqualTo(1),
            "Отказ приказа адресован контроллеру. В GameEventTypes его нет " +
            "намеренно — там он засорил бы хронику колонии и дневники NPC.");
        Assert.That(GameEventTypes.IsPlayerVisible("ManualOrderRejected"), Is.False,
            "Если это имя однажды окажется в общем вайтлисте — «MoveTo " +
            "Reason=Unreachable» поедет каждому зрителю и в дневник колонистки.");
    }

    [Test]
    public void ResetDropsTheChronicleOfAWorldThatNoLongerHappened()
    {
        var (log, buffer) = Fresh();
        buffer.Add(Visible("TreeChopped"));
        log.Drain(buffer, 0);
        var before = log.SessionEpoch;

        log.Reset();

        Assert.That(log.Read(0, 100, null).Events, Is.Empty,
            "После загрузки сейва дерево снова стоит — рассказывать, что его " +
            "срубили, значит врать про мир, которого нет.");
        Assert.That(log.SessionEpoch, Is.Not.EqualTo(before),
            "Смена эпохи — единственный признак, по которому агент отличит " +
            "подменённый мир от нормального хода событий.");
    }
}

}
