using HexLive.Simulation.Agents;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime.Journal;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Spec §136: закрывает игровой час и оставляет в дневнике каждой NPC ровно
/// одну запись.
///
/// <para>
/// Регистрируется ПОСЛЕДНЕЙ — как и диагностические наблюдатели (§30.15). Час
/// закрывается только когда все системы тика уже отговорили, иначе событие,
/// случившееся под конец часа, опоздало бы на свою же запись.
/// </para>
/// <para>
/// Система ничего в мире не меняет: читает состояние, пишет только в кольцо
/// дневника. Настроение и отношение вычисляются В МОМЕНТ ЗАКРЫТИЯ — одна и та
/// же перевязка звучит по-разному от умирающей и от здоровой, и правдой здесь
/// является то, как ей было, когда она села писать.
/// </para>
/// </summary>
public sealed class JournalSystem : ISimulationSystem
{
    public string Name => nameof(JournalSystem);

    public TickLayer Layer => TickLayer.Slow;

    private int _lastClosedHour = int.MinValue;

    public void Run(WorldState world)
    {
        if (!Spec136.Enabled)
        {
            return;
        }

        var hour = world.Tick / NpcJournalIntake.HourTicks();

        // Первый прогон — просто встаём на текущий час, не выдумывая записи за
        // время, которого не видели.
        //
        // Час УШЁЛ НАЗАД — значит в этот движок загрузили сейв более раннего
        // времени. Тогда тоже перевстаём: иначе система молчала бы до тех пор,
        // пока мир не догонит прежний счётчик, то есть загруженная игра часами
        // не писала бы ни строчки, и это выглядело бы как «дневник сломался».
        if (_lastClosedHour == int.MinValue || hour < _lastClosedHour)
        {
            _lastClosedHour = hour;
            return;
        }

        if (hour == _lastClosedHour)
        {
            return;
        }

        _lastClosedHour = hour;
        var tick = world.Tick;

        foreach (var npc in world.Entities.Npcs.Values)
        {
            Close(npc, tick);
        }
    }

    private static void Close(NPCState npc, int tick)
    {
        var journal = npc.Journal;

        if (journal.HasPending)
        {
            journal.Add(new JournalEntry
            {
                Tick = tick,
                Type = journal.PendingType,
                Register = JournalMood.Of(npc, journal.PendingPerspective),
                Bond = JournalMood.BondTo(npc, journal.PendingSubjectId),
                Perspective = journal.PendingPerspective,
                SubjectNameId = journal.PendingSubjectNameId,
                Extra = journal.PendingExtra,
                Variant = JournalMood.Variant(npc.Id.Value, tick, journal.PendingType),
                QuietHours = 0
            });

            journal.ClearPending();
            return;
        }

        // Час прошёл без единого значимого события. Продлеваем предыдущую тихую
        // запись, если она есть, — иначе двое спокойных суток забили бы кольцо
        // сорока восемью «ничего не происходило» и вытеснили бы всё живое.
        // Перечень дел у продлённой записи остаётся от ПЕРВОГО тихого часа:
        // «ничего особенного» с растущим списком дел читалось бы как отчёт.
        if (journal.TryExtendQuiet(tick))
        {
            journal.ClearPending();
            return;
        }

        journal.Add(new JournalEntry
        {
            Tick = tick,
            Type = null,
            Register = JournalMood.Of(npc, JournalPerspective.Received),
            Bond = JournalBond.Neutral,
            Perspective = JournalPerspective.Received,
            Variant = JournalMood.Variant(npc.Id.Value, tick, "quiet"),
            QuietHours = 1,
            Chore0 = journal.Chore0,
            Chore1 = journal.Chore1,
            Chore2 = journal.Chore2
        });

        journal.ClearPending();
    }
}

}
