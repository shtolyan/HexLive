using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Simulation.Soak
{

/// <summary>
/// Профиль производительности прогона (<c>hexsoak --profile</c>): куда уходит
/// тик по системам, как распределён шаг, сколько живёт в куче и сколько
/// аллоцируется на тик. Отвечает на «что дорого», а метрики §30.16 — на «что
/// не так в поведении»; смешивать их в одном отчёте незачем.
/// </summary>
public sealed class SoakProfile
{
    private sealed class SystemCost
    {
        public long TotalTicks;
        public long MaxTicks;
        public int Runs;
    }

    private readonly Dictionary<string, SystemCost> _systems = new Dictionary<string, SystemCost>(StringComparer.Ordinal);
    private readonly List<double> _stepMs = new List<double>();
    private readonly List<(int tick, double ms)> _worstSteps = new List<(int tick, double ms)>();

    private double _worldgenSeconds;
    private long _heapAfterWorldgen;
    private long _workingSetAfterWorldgen;
    private int _tiles;
    private int _junctions;
    private int _objectsAtStart;
    private long _allocatedBefore;
    private long _searchesBefore, _expansionsBefore, _budgetHitsBefore, _fallbacksBefore;
    private int _gen0Before, _gen1Before, _gen2Before;

    public void AfterWorldgen(WorldState world, double seconds)
    {
        _worldgenSeconds = seconds;
        _tiles = world.Tiles.Items.Count;
        _junctions = world.Junctions.Items.Count;
        _objectsAtStart = world.Entities.Objects.Count;
        _heapAfterWorldgen = GC.GetTotalMemory(forceFullCollection: true);
        using (var process = Process.GetCurrentProcess())
        {
            _workingSetAfterWorldgen = process.WorkingSet64;
        }

        _allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        _searchesBefore = HexLive.Simulation.Navigation.HexPathfinder.StatSearches;
        _expansionsBefore = HexLive.Simulation.Navigation.HexPathfinder.StatExpansions;
        _budgetHitsBefore = HexLive.Simulation.Navigation.HexPathfinder.StatBudgetHits;
        _fallbacksBefore = HexLive.Simulation.Navigation.HexPathfinder.StatFallbacks;
        _gen0Before = GC.CollectionCount(0);
        _gen1Before = GC.CollectionCount(1);
        _gen2Before = GC.CollectionCount(2);
    }

    public void OnSystemRun(ISimulationSystem system, long elapsedTicks)
    {
        if (!_systems.TryGetValue(system.Name, out var cost))
        {
            cost = new SystemCost();
            _systems[system.Name] = cost;
        }

        cost.TotalTicks += elapsedTicks;
        cost.Runs++;
        if (elapsedTicks > cost.MaxTicks)
        {
            cost.MaxTicks = elapsedTicks;
        }
    }

    public void OnStep(double milliseconds, WorldState world)
    {
        _stepMs.Add(milliseconds);
        _worstSteps.Add((world.Tick - 1, milliseconds));
        if (_worstSteps.Count > 64)
        {
            _worstSteps.Sort((a, b) => b.ms.CompareTo(a.ms));
            _worstSteps.RemoveRange(8, _worstSteps.Count - 8);
        }
    }

    public string Report(WorldState world)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        var totalMs = _stepMs.Sum();
        var sorted = _stepMs.OrderBy(v => v).ToList();
        double Pct(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

        var heapNow = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetNow, peakWorkingSet;
        using (var process = Process.GetCurrentProcess())
        {
            workingSetNow = process.WorkingSet64;
            peakWorkingSet = process.PeakWorkingSet64;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocatedBefore;

        text.AppendLine("── профиль");
        text.AppendLine("  мир                 " + _tiles + " тайлов, " + _junctions + " узлов, " +
                        _objectsAtStart + " → " + world.Entities.Objects.Count + " объектов, worldgen " +
                        _worldgenSeconds.ToString("F2", inv) + " с");
        text.AppendLine("  память              куча после worldgen " + Mb(_heapAfterWorldgen) +
                        ", в конце " + Mb(heapNow) + "; RSS после worldgen " + Mb(_workingSetAfterWorldgen) +
                        ", в конце " + Mb(workingSetNow) + ", пик " + Mb(peakWorkingSet));
        text.AppendLine("  аллокации           " + Mb(allocated) + " за прогон, " +
                        (allocated / 1024.0 / Math.Max(1, _stepMs.Count)).ToString("F1", inv) + " КБ/тик; GC gen0/1/2: " +
                        (GC.CollectionCount(0) - _gen0Before) + "/" + (GC.CollectionCount(1) - _gen1Before) + "/" +
                        (GC.CollectionCount(2) - _gen2Before));
        text.AppendLine("  шаг, мс             среднее " + (totalMs / Math.Max(1, _stepMs.Count)).ToString("F2", inv) +
                        ", p50 " + Pct(0.5).ToString("F2", inv) + ", p90 " + Pct(0.9).ToString("F2", inv) +
                        ", p99 " + Pct(0.99).ToString("F2", inv) + ", максимум " + Pct(1.0).ToString("F1", inv));
        text.AppendLine("  медленные шаги      " + string.Join(", ", _worstSteps
            .OrderByDescending(w => w.ms).Take(6)
            .Select(w => "тик " + w.tick + "=" + w.ms.ToString("F0", inv) + "мс")));

        var searches = HexLive.Simulation.Navigation.HexPathfinder.StatSearches - _searchesBefore;
        var expansions = HexLive.Simulation.Navigation.HexPathfinder.StatExpansions - _expansionsBefore;
        var budgetHits = HexLive.Simulation.Navigation.HexPathfinder.StatBudgetHits - _budgetHitsBefore;
        var fallbacks = HexLive.Simulation.Navigation.HexPathfinder.StatFallbacks - _fallbacksBefore;
        text.AppendLine("  поиски пути         " + searches + " (" +
                        (searches / (double)Math.Max(1, _stepMs.Count)).ToString("F1", inv) + " на тик), узлов развёрнуто " +
                        expansions + " (" + (expansions / (double)Math.Max(1, searches)).ToString("F0", inv) +
                        " на поиск), упёрлись в бюджет " + budgetHits + " (" +
                        (100.0 * budgetHits / Math.Max(1, searches)).ToString("F1", inv) +
                        "%), запасных проходов без avoid " + fallbacks);

        var systemsTotal = _systems.Values.Sum(c => c.TotalTicks);
        var toMs = 1000.0 / Stopwatch.Frequency;
        text.AppendLine("  системы (доля, всего мс, среднее на вызов мкс, худший вызов мс):");
        foreach (var pair in _systems.OrderByDescending(p => p.Value.TotalTicks).Take(20))
        {
            var cost = pair.Value;
            text.AppendLine("    " + pair.Key.PadRight(30) +
                            (100.0 * cost.TotalTicks / Math.Max(1, systemsTotal)).ToString("F1", inv).PadLeft(5) + "%" +
                            (cost.TotalTicks * toMs).ToString("F0", inv).PadLeft(9) +
                            (cost.TotalTicks * toMs * 1000 / Math.Max(1, cost.Runs)).ToString("F0", inv).PadLeft(9) +
                            (cost.MaxTicks * toMs).ToString("F1", inv).PadLeft(9));
        }

        var inSystems = systemsTotal * toMs;
        text.AppendLine("  вне систем          " + (totalMs - inSystems).ToString("F0", inv) +
                        " мс (" + (100.0 * (totalMs - inSystems) / Math.Max(1, totalMs)).ToString("F1", inv) +
                        "% — чанки, очередь команд, таймер)");
        return text.ToString();
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " МБ";
}

}
