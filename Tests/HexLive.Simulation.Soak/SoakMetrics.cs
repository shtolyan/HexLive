using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexLive.Simulation.AI;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Soak
{

/// <summary>
/// Метрики поведения из spec §30.16 — то, чем меряются правки в AI.
/// <para>
/// Считаются ИЗ СОСТОЯНИЯ, а не разбором текста событий: <c>Message</c> — это
/// формат для людей и для истории колонии, парсить его ради метрики значило бы
/// завести второго потребителя у строки, которую нельзя менять. Событиями
/// считаются только штуки, где тип и есть факт (<c>PlanStarted</c>,
/// <c>PlanFailed</c>).
/// </para>
/// </summary>
public sealed class SoakMetrics
{
    private sealed class NpcTrack
    {
        public GoalType Goal = GoalType.None;
        public int GoalSinceTick;
        public int Changes;
        public int StuckTicks;

        /// <summary>§122: тиков, прожитых внутри распознанного круга.</summary>
        public int LoopTicks;
        public readonly List<int> Dwells = new List<int>();

        /// <summary>
        /// Последняя СОДЕРЖАТЕЛЬНАЯ цель. Нужна, потому что завершённое действие
        /// обязательно роняет цель в None перед следующим выбором (сброс цикла в
        /// ExecutionSystem), и голый счётчик изменений поля считает нормальную
        /// работу «X → None → Y» за две смены. Настоящий churn — это когда
        /// СОДЕРЖАТЕЛЬНАЯ цель сменилась на другую содержательную.
        /// </summary>
        public GoalType LastMeaningful = GoalType.None;
        public int MeaningfulSwitches;
    }

    private readonly Dictionary<int, NpcTrack> _tracks = new Dictionary<int, NpcTrack>();
    private readonly Dictionary<string, int> _eventCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>§122: сколько РАЗНЫХ петель началось у каждого NPC. Именно
    /// поимённо: одна петля одного NPC в одном сиде тонет в колониальном
    /// среднем, и ровно поэтому churn её не показывал.</summary>
    private readonly Dictionary<int, int> _loopOnsets = new Dictionary<int, int>();

    public int Seed;
    public int TicksRun;
    public int NpcsAtStart;
    public int NpcsAtEnd;
    public int MobsAtEnd;
    public int MobsPeak;
    public bool Completed;
    public double Seconds;
    private readonly Dictionary<string, int> _deathCauses =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Делитель для «в день». По умолчанию игровой цикл событий (2400 тиков) —
    /// НЕ визуальные сутки (24000, чистая картинка). Печатается рядом с числом,
    /// чтобы метрику нельзя было прочитать неправильно.
    /// </summary>
    public int DayTicks = 2400;

    public void SampleTick(WorldState world)
    {
        // §46 v4: популяция мобов — метрика, которой здесь НЕ БЫЛО, и ровно
        // поэтому храповик прожил долго. Ветка ночного рейда спавнила мимо
        // потолка, уйти моб мог только смертью, список сериализуется — у
        // игрока накопилось 14 собак при потолке 2, и заметил это не соак, а
        // замер живой сессии. Пик, а не только конец: стая, погибшая к
        // последнему тику, скрыла бы весь эпизод.
        if (world.Mobs.Count > MobsPeak)
        {
            MobsPeak = world.Mobs.Count;
        }

        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (!_tracks.TryGetValue(npc.Id.Value, out var track))
            {
                track = new NpcTrack { Goal = npc.Mind.CurrentGoal, GoalSinceTick = world.Tick };
                _tracks[npc.Id.Value] = track;
            }

            if (npc.Mind.CurrentGoal != track.Goal)
            {
                track.Dwells.Add(world.Tick - track.GoalSinceTick);
                track.Changes++;
                track.Goal = npc.Mind.CurrentGoal;
                track.GoalSinceTick = world.Tick;

                if (track.Goal != GoalType.None)
                {
                    if (track.LastMeaningful != GoalType.None && track.LastMeaningful != track.Goal)
                    {
                        track.MeaningfulSwitches++;
                    }

                    track.LastMeaningful = track.Goal;
                }
            }

            // Подпись §102: цель есть, взаимодействие не идёт, и она никуда не
            // идёт. Ровно в этом состоянии чужак простоял 2872 тика подряд, и
            // ни одно событие об этом не сказало.
            if (npc.Mind.CurrentGoal != GoalType.None &&
                npc.Execution.Status == ExecutionStatus.None &&
                !npc.Movement.IsMoving)
            {
                track.StuckTicks++;
            }

            // §122. ВРЕМЯ в петлях, а не число эпизодов: лестница выхода по
            // построению дробит одну длинную петлю на несколько коротких, и
            // счёт эпизодов растёт даже когда времени в кругах стало меньше.
            // Родня stuckNpcTicks — и мерить их надо одинаково.
            if (world.IntentLedger.IsLooping(npc.Id.Value))
            {
                track.LoopTicks++;
            }
        }
    }

    public void CountEvent(SimulationEvent simulationEvent)
    {
        _eventCounts.TryGetValue(simulationEvent.Type, out var count);
        _eventCounts[simulationEvent.Type] = count + 1;

        // §122. Петли считаются ПО СОБЫТИЮ, а не по состоянию, и это не
        // нарушение правила из шапки: разбор уже сделал сторож, тип события и
        // есть факт «здесь начался круг», а раскладка по NPC берётся из
        // EntityId — поля события, а не из Message. Парсить текст по-прежнему
        // нельзя, и здесь этого не происходит.
        if (simulationEvent.Type == "LoopDetected" &&
            simulationEvent.EntityId is { } looper &&
            simulationEvent.Message.EndsWith("ONSET", StringComparison.Ordinal))
        {
            _loopOnsets.TryGetValue(looper, out var loops);
            _loopOnsets[looper] = loops + 1;
        }
    }

    public int EventCount(string type)
    {
        _eventCounts.TryGetValue(type, out var count);
        return count;
    }

    public void Finish(WorldState world)
    {
        Completed = world.Completed;
        _deathCauses.Clear();
        foreach (var death in world.DeathRecords)
        {
            var cause = string.IsNullOrEmpty(death.Cause) ? "Unknown" : death.Cause;
            var detail = cause.IndexOf(':');
            if (detail > 0)
            {
                cause = cause.Substring(0, detail);
            }
            _deathCauses.TryGetValue(cause, out var count);
            _deathCauses[cause] = count + 1;
        }
    }

    public int TotalEvents => _eventCounts.Values.Sum();

    public int GoalChanges => _tracks.Values.Sum(t => t.Changes);

    /// <summary>
    /// Смены между СОДЕРЖАТЕЛЬНЫМИ целями — обязательный проход через None не
    /// считается. Это и есть churn в смысле §35.4a: «бросила дело ради другого
    /// дела», а не «доделала и выбрала следующее».
    /// </summary>
    public int MeaningfulSwitches => _tracks.Values.Sum(t => t.MeaningfulSwitches);

    public int StuckTicks => _tracks.Values.Sum(t => t.StuckTicks);

    /// <summary>§122: всего начатых петель за прогон.</summary>
    public int LoopOnsets => _loopOnsets.Values.Sum();

    /// <summary>§122: NPC-тиков внутри круга — главная метрика фазы 2.</summary>
    public int LoopTicks => _tracks.Values.Sum(t => t.LoopTicks);

    public double LoopShare => _tracks.Count == 0 || TicksRun == 0
        ? 0
        : LoopTicks / (double)(TicksRun * _tracks.Count);

    /// <summary>Сколько NPC хоть раз закрутились. Один NPC с десятью петлями и
    /// десять NPC с одной — совсем разные диагнозы.</summary>
    public int LoopingNpcs => _loopOnsets.Count;

    private double NpcDays => _tracks.Count * (TicksRun / (double)DayTicks);

    public double ChangesPerNpcDay => NpcDays <= 0 ? 0 : GoalChanges / NpcDays;

    public double SwitchesPerNpcDay => NpcDays <= 0 ? 0 : MeaningfulSwitches / NpcDays;

    public double StuckShare => TicksRun <= 0 || _tracks.Count == 0
        ? 0
        : StuckTicks / (double)(TicksRun * _tracks.Count);

    public int MedianDwell
    {
        get
        {
            var all = _tracks.Values.SelectMany(t => t.Dwells).OrderBy(d => d).ToList();
            return all.Count == 0 ? 0 : all[all.Count / 2];
        }
    }

    public double MeanDwell
    {
        get
        {
            var all = _tracks.Values.SelectMany(t => t.Dwells).ToList();
            return all.Count == 0 ? 0 : all.Average();
        }
    }

    public double PlanFailureRate
    {
        get
        {
            var started = EventCount("PlanStarted");
            return started == 0 ? 0 : EventCount("PlanFailed") / (double)started;
        }
    }

    public string Report()
    {
        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        text.AppendLine("сид " + Seed + ", тиков " + TicksRun +
                        " (" + (TicksRun / Math.Max(Seconds, 0.001)).ToString("F0", invariant) + " тик/с)");
        text.AppendLine("  NPC                 " + NpcsAtStart + " → " + NpcsAtEnd);
        text.AppendLine("  мобы (пик/конец)    " + MobsPeak + " / " + MobsAtEnd);
        text.AppendLine("  плот                " + (Completed ? "запущен" : "не запущен"));
        text.AppendLine("  цель менялась       " + GoalChanges +
                        "  (" + ChangesPerNpcDay.ToString("F1", invariant) +
                        " на NPC-день, день = " + DayTicks + " тиков)");
        text.AppendLine("  из них БРОСИЛА      " + MeaningfulSwitches +
                        "  (" + SwitchesPerNpcDay.ToString("F1", invariant) +
                        " на NPC-день — дело на дело, мимо None)");
        text.AppendLine("  держится за цель    медиана " + MedianDwell +
                        ", среднее " + MeanDwell.ToString("F0", invariant) + " тиков");
        text.AppendLine("  планов начато       " + EventCount("PlanStarted"));
        text.AppendLine("  планов провалено    " + EventCount("PlanFailed") +
                        "  (" + (PlanFailureRate * 100).ToString("F1", invariant) + "%)");
        text.AppendLine("  застой              " + StuckTicks + " NPC-тиков" +
                        "  (" + (StuckShare * 100).ToString("F1", invariant) +
                        "% — цель есть, дела нет, не идёт)");
        text.AppendLine("  ПЕТЛИ               " + LoopTicks + " NPC-тиков" +
                        "  (" + (LoopShare * 100).ToString("F1", invariant) +
                        "% — двигается и не продвигается)");
        text.AppendLine("  из них эпизодов     " + LoopOnsets + " у " +
                        LoopingNpcs + " NPC" + WorstLoopers());
        text.AppendLine("  событий             " + TotalEvents);
        // §54.17: вся мясная цепочка одной строкой — охота до тарелки. Ноль в
        // MeatRoasted/MeatEaten при ненулевом Butchered = цепь порвана.
        text.AppendLine("  мясо                " +
                        "Butchered=" + EventCount("Butchered") +
                        ", Hung=" + EventCount("MeatHungOnSpit") +
                        ", Roasted=" + EventCount("MeatRoasted") +
                        ", Eaten=" + EventCount("MeatEaten") +
                        ", Spoiled=" + EventCount("MeatSpoiled"));

        var top = _eventCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(6);
        text.AppendLine("  чаще всего          " +
                        string.Join(", ", top.Select(p => p.Key + "=" + p.Value)));

        return text.ToString();
    }

    public string ToJson()
    {
        var invariant = CultureInfo.InvariantCulture;
        var deaths = string.Join(",", _deathCauses
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "\"" + Escape(pair.Key) + "\":" + pair.Value));
        return "{" +
               "\"seed\":" + Seed +
               ",\"ticks\":" + TicksRun +
               ",\"npcsAtStart\":" + NpcsAtStart +
               ",\"npcsAtEnd\":" + NpcsAtEnd +
               ",\"mobsAtEnd\":" + MobsAtEnd +
               ",\"mobsPeak\":" + MobsPeak +
               ",\"completed\":" + (Completed ? "true" : "false") +
               ",\"dayTicks\":" + DayTicks +
               ",\"goalChanges\":" + GoalChanges +
               ",\"changesPerNpcDay\":" + ChangesPerNpcDay.ToString("F4", invariant) +
               ",\"meaningfulSwitches\":" + MeaningfulSwitches +
               ",\"switchesPerNpcDay\":" + SwitchesPerNpcDay.ToString("F4", invariant) +
               ",\"medianDwellTicks\":" + MedianDwell +
               ",\"meanDwellTicks\":" + MeanDwell.ToString("F2", invariant) +
               ",\"plansStarted\":" + EventCount("PlanStarted") +
               ",\"plansFailed\":" + EventCount("PlanFailed") +
               ",\"planFailureRate\":" + PlanFailureRate.ToString("F4", invariant) +
               ",\"stuckNpcTicks\":" + StuckTicks +
               ",\"stuckShare\":" + StuckShare.ToString("F4", invariant) +
               ",\"loopTicks\":" + LoopTicks +
               ",\"loopShare\":" + LoopShare.ToString("F4", invariant) +
               ",\"loopOnsets\":" + LoopOnsets +
               ",\"loopingNpcs\":" + LoopingNpcs +
               ",\"totalEvents\":" + TotalEvents +
               ",\"butchered\":" + EventCount("Butchered") +
               ",\"meatHung\":" + EventCount("MeatHungOnSpit") +
               ",\"meatRoasted\":" + EventCount("MeatRoasted") +
               ",\"meatEaten\":" + EventCount("MeatEaten") +
               ",\"meatSpoiled\":" + EventCount("MeatSpoiled") +
               ",\"deathCauses\":{" + deaths + "}" +
               "}";
    }

    /// <summary>Кто крутился больше всех — чтобы из отчёта сразу был виден
    /// номер NPC для `--seed … --explain-loops`, а не только итог.</summary>
    private string WorstLoopers()
    {
        if (_loopOnsets.Count == 0)
        {
            return string.Empty;
        }

        var worst = _loopOnsets
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(3)
            .Select(pair => "NPC" + pair.Key + "=" + pair.Value);
        return "  (" + string.Join(", ", worst) + ")";
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");
}

}
