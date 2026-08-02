using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HexLive.Simulation.Soak
{

/// <summary>Разбор аргументов и наборы типов событий для записи трассы.</summary>
public sealed class SoakOptions
{
    public List<int> Seeds = new List<int> { 12345 };
    public int Ticks = 4000;
    public string SimDataPath;
    public string MetricsJsonPath;
    public string TraceOutPath;
    public HashSet<string> TraceTypes;
    public int StateHashEvery;
    public bool Quiet;

    /// <summary>
    /// Сколько первых застоев разобрать вслух: печатается хвост бортового
    /// самописца — что этот NPC делал ПЕРЕД тем, как замереть. Ровно то, что
    /// в §102 добывалось ручным printf'ом раз в 120 тиков.
    /// </summary>
    public int ExplainStuck = 3;

    /// <summary>Какой мир строить: прототипный остров или арена абьюза (§91).</summary>
    public string Arena = "prototype";

    /// <summary>Печатать по-тиковую раскадровку боя: замах, попадание,
    /// готовность, такт сцены. То, чего не видно ни в одном событии.</summary>
    public bool CombatFrames;

    /// <summary>Все удары по таймлайну замаха (§104 r8), поверх simdata.</summary>
    public bool TimedMelee;

    /// <summary>
    /// Пресеты трассы. <c>decisions</c> — дёшево и без шума, для проверки
    /// «поведение не поехало» на гигиенических правках. <c>scores</c> добавляет
    /// <c>GoalScored</c>: это ТОЧНЫЙ float-выхлоп каждого скоринг-блока, поэтому
    /// перестановка слагаемых во время распила god-метода не пройдёт незамеченной.
    /// </summary>
    private static readonly Dictionary<string, string[]> Presets =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["decisions"] = new[] { "GoalSelected", "PlanStarted", "PlanFailed" },
            ["scores"] = new[] { "GoalSelected", "PlanStarted", "PlanFailed", "GoalScored" },
            ["execution"] = new[]
            {
                "GoalSelected", "PlanStarted", "PlanFailed", "PlanAborted",
                "ExecFailed", "GoalInterrupted", "StuckDetected"
            },
        };

    public static SoakOptions Parse(string[] args, out string error)
    {
        var options = new SoakOptions();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next(string name)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("У " + name + " нет значения.");
                }

                return args[++i];
            }

            try
            {
                switch (arg)
                {
                    case "--seed":
                        options.Seeds = new List<int> { int.Parse(Next(arg), CultureInfo.InvariantCulture) };
                        break;
                    case "--seeds":
                        options.Seeds = Next(arg).Split(',')
                            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture))
                            .ToList();
                        break;
                    case "--seed-count":
                        var count = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        options.Seeds = Enumerable.Range(0, count).Select(n => 12345 + n).ToList();
                        break;
                    case "--ticks":
                        options.Ticks = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--simdata":
                        options.SimDataPath = Next(arg);
                        break;
                    case "--metrics-json":
                        options.MetricsJsonPath = Next(arg);
                        break;
                    case "--trace-out":
                        options.TraceOutPath = Next(arg);
                        break;
                    case "--trace-preset":
                        var preset = Next(arg);
                        if (!Presets.TryGetValue(preset, out var types))
                        {
                            throw new ArgumentException(
                                "Неизвестный пресет '" + preset + "'. Есть: " +
                                string.Join(", ", Presets.Keys));
                        }

                        options.TraceTypes = new HashSet<string>(types, StringComparer.Ordinal);
                        break;
                    case "--trace-types":
                        options.TraceTypes = new HashSet<string>(
                            Next(arg).Split(',').Select(s => s.Trim()), StringComparer.Ordinal);
                        break;
                    case "--state-hash-every":
                        options.StateHashEvery = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--explain-stuck":
                        options.ExplainStuck = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--arena":
                        options.Arena = Next(arg);
                        break;
                    case "--combat-frames":
                        options.CombatFrames = true;
                        break;
                    // §104 r8: A/B-переключатель миграции ударов на таймлайн.
                    // Ставится ПОСЛЕ применения simdata, поэтому перебивает
                    // экспорт — иначе сравнить две модели одним бинарём нельзя.
                    case "--timed-melee":
                        options.TimedMelee = true;
                        break;
                    case "--quiet":
                        options.Quiet = true;
                        break;
                    case "--help":
                    case "-h":
                        error = Usage;
                        return null;
                    default:
                        throw new ArgumentException("Неизвестный аргумент " + arg);
                }
            }
            catch (Exception exception)
            {
                error = exception.Message + "\n\n" + Usage;
                return null;
            }
        }

        if (options.TraceOutPath != null && options.TraceTypes == null)
        {
            options.TraceTypes = new HashSet<string>(Presets["decisions"], StringComparer.Ordinal);
        }

        options.SimDataPath ??= Path.Combine(RepoPaths.Root, "SimData", "simdata.json");
        return options;
    }

    public const string Usage = @"hexsoak — прогон мира без Unity.

  --seed N                один сид (по умолчанию 12345)
  --seeds A,B,C           список сидов
  --seed-count N          N сидов подряд от 12345
  --ticks N               длина прогона (по умолчанию 4000)
  --simdata PATH          путь к simdata.json (по умолчанию <корень репо>/SimData)

  --metrics-json PATH     выгрузить метрики в JSON

  --trace-out PATH        писать трассу (эталон для golden_trace.sh)
  --trace-preset NAME     decisions | scores | execution (по умолчанию decisions)
  --trace-types A,B,C     свой список типов вместо пресета
  --state-hash-every N    добавлять в трассу хэш полного кадра раз в N тиков

  --arena NAME            prototype (по умолчанию) | abuse — арена §91
  --combat-frames         по-тиковая раскадровка боя: замах/попадание/готовность
  --explain-stuck N       разобрать первые N застоев: печатает хвост событий
                          зависшего NPC (по умолчанию 3, 0 — выключить)
  --quiet                 без человекочитаемого вывода

Несколько сидов и --trace-out: файл на сид, суффикс .seed<N>.";
}

}
