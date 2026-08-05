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

    public int Seed;
    public int TicksRun;
    public int NpcsAtStart;
    public int NpcsAtEnd;
    public double Seconds;

    /// <summary>
    /// Делитель для «в день». По умолчанию игровой цикл событий (2400 тиков) —
    /// НЕ визуальные сутки (24000, чистая картинка). Печатается рядом с числом,
    /// чтобы метрику нельзя было прочитать неправильно.
    /// </summary>
    public int DayTicks = 2400;

    public void SampleTick(WorldState world)
    {
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
        }
    }

    public void CountEvent(SimulationEvent simulationEvent)
    {
        _eventCounts.TryGetValue(simulationEvent.Type, out var count);
        _eventCounts[simulationEvent.Type] = count + 1;
    }

    public int EventCount(string type)
    {
        _eventCounts.TryGetValue(type, out var count);
        return count;
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
        return "{" +
               "\"seed\":" + Seed +
               ",\"ticks\":" + TicksRun +
               ",\"npcsAtStart\":" + NpcsAtStart +
               ",\"npcsAtEnd\":" + NpcsAtEnd +
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
               ",\"totalEvents\":" + TotalEvents +
               ",\"butchered\":" + EventCount("Butchered") +
               ",\"meatHung\":" + EventCount("MeatHungOnSpit") +
               ",\"meatRoasted\":" + EventCount("MeatRoasted") +
               ",\"meatEaten\":" + EventCount("MeatEaten") +
               ",\"meatSpoiled\":" + EventCount("MeatSpoiled") +
               "}";
    }
}

}
