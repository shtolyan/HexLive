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
    // §146: which scenario the prototype arena generates.
    public HexLive.Simulation.Bootstrap.GameMode Mode = HexLive.Simulation.Bootstrap.GameMode.Feud;
    public int Ticks = 4000;
    public string SimDataPath;
    public string MetricsJsonPath;
    public string TraceOutPath;
    public HashSet<string> TraceTypes;
    public int StateHashEvery;
    public bool Quiet;

    /// <summary>§30.17: прогнать в режиме игры — с молчащей диагностикой.</summary>
    public bool NoTrace;

    /// <summary>
    /// Сколько первых застоев разобрать вслух: печатается хвост бортового
    /// самописца — что этот NPC делал ПЕРЕД тем, как замереть. Ровно то, что
    /// в §102 добывалось ручным printf'ом раз в 120 тиков.
    /// </summary>
    public int ExplainStuck = 3;

    /// <summary>
    /// §136: id колонистки, чей дневник напечатать в конце прогона. -1 —
    /// не печатать. Это единственный способ прочитать настоящий дневник, не
    /// открывая Unity, — то есть отревьюить текст, а не только механику.
    /// </summary>
    public int Journal = -1;

    /// <summary>
    /// §122. Сколько первых ПЕТЕЛЬ разобрать вслух. Отдельно от застоя, потому
    /// что это разные болезни: застой — «не двигается», петля — «двигается и не
    /// продвигается», и в одном прогоне их может быть по-разному много.
    /// </summary>
    public int ExplainLoops = 3;

    /// <summary>
    /// Сколько смертей разобрать вместе с персональным хвостом самописца.
    /// Причина в DeathRecord отвечает только на «от чего»; хвост нужен для
    /// поведенческого вопроса «какие решения довели её до этого».
    /// </summary>
    public int ExplainDeaths;

    /// <summary>Какой мир строить: прототипный остров или арена абьюза (§91).</summary>
    public string Arena = "prototype";

    /// <summary>Печатать по-тиковую раскадровку боя: замах, попадание,
    /// готовность, такт сцены. То, чего не видно ни в одном событии.</summary>
    public bool CombatFrames;

    /// <summary>Все удары по таймлайну замаха (§104 r8), поверх simdata.</summary>
    public bool TimedMelee;

    /// <summary>
    /// §122 фаза 2: автовыход из петель, поверх simdata. null — как в экспорте.
    /// Существует ради A/B: §35.6 помнит, как вариант abort-on-Blocked выглядел
    /// логично и дал 466 пустых отмен стирки с тройным churn. Отличить лечение
    /// от такого можно только одним сидом с флагом и без.
    /// </summary>
    public bool? LoopEscape;

    /// <summary>§122: потолок лестницы поверх simdata (0..3). null — как в экспорте.</summary>
    public int? LoopMaxRung;

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
                "ExecFailed", "GoalInterrupted", "StuckDetected", "LoopDetected"
            },
            // §122: круг и то, из чего он состоит. PlanStarted/PlanFailed рядом
            // не для полноты — по ним видно, что попытки И ПРАВДА повторялись,
            // а LoopDetected не приснился сторожу.
            ["loops"] = new[]
            {
                "LoopDetected", "StuckDetected", "GoalSelected",
                "PlanStarted", "PlanFailed", "GoalCooldownSet"
            },
            // §81.11: жизнь чужака одной строкой на событие. AbuseBlocked —
            // ПРИЧИНА, почему он сейчас НЕ абьюзит (раз в 64 тика), остальное —
            // вехи сцены. GoalSelected рядом, чтобы видеть, чем он занят вместо.
            ["abuse"] = new[]
            {
                "AbuseBlocked", "AbuseTriggered", "AbuseProwl", "AbuseSpotted",
                "AbuseRetarget", "AbusePursues", "AbuseStarted", "AbuseAbandoned",
                "AbuseDone", "AbuseRouted", "FleeStarted", "FriendGuard",
                "AnswersBlows", "GoalSelected"
            },
            // §108: дуга сговора. GroupHuntBlocked — ПРИЧИНА, почему они сейчас
            // НЕ сговариваются (раз в 64 тика), как AbuseBlocked у него;
            // остальное — вехи охоты. TalkStarted рядом, чтобы видеть, о чём
            // они вообще говорят вместо него.
            ["grouphunt"] = new[]
            {
                "GroupHuntBlocked", "GroupHuntPactFormed", "GroupHuntHolding",
                "GroupHuntEngaged", "GroupHuntStruck", "GroupHuntTargetFled",
                "GroupHuntDone", "GroupHuntFailed", "TalkStarted", "AbuseDone"
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
                    case "--mode":
                        options.Mode = Next(arg).Trim().ToLowerInvariant() switch
                        {
                            "feud" or "0" => HexLive.Simulation.Bootstrap.GameMode.Feud,
                            "bigisland" or "big-island" or "1" =>
                                HexLive.Simulation.Bootstrap.GameMode.BigIsland,
                            var other => throw new ArgumentException(
                                $"--mode {other}: feud | bigisland"),
                        };
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
                    case "--journal":
                        options.Journal = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--explain-stuck":
                        options.ExplainStuck = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--loop-escape":
                        options.LoopEscape = Next(arg) == "on";
                        break;
                    case "--loop-max-rung":
                        options.LoopMaxRung = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--explain-loops":
                        options.ExplainLoops = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                        break;
                    case "--explain-deaths":
                        options.ExplainDeaths = int.Parse(Next(arg), CultureInfo.InvariantCulture);
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
                    // §30.17: трасса в игре молчит по умолчанию, а здесь она и
                    // есть продукт — поэтому hexsoak включает её сам. Ключ
                    // оставлен, чтобы прогнать соак В РЕЖИМЕ ИГРЫ и померить,
                    // сколько стоит сама болтовня.
                    case "--no-trace":
                        options.NoTrace = true;
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
  --trace-preset NAME     decisions | scores | execution | loops | abuse | grouphunt
                          (по умолчанию decisions)
  --trace-types A,B,C     свой список типов вместо пресета
  --state-hash-every N    добавлять в трассу хэш полного кадра раз в N тиков

  --arena NAME            prototype (по умолчанию) | abuse — арена §91
  --mode NAME             feud (по умолчанию) | bigisland — режим §146
  --combat-frames         по-тиковая раскадровка боя: замах/попадание/готовность
  --journal N             §136: напечатать дневник NPC N — что она сама
                          записала о своих днях. 12000 тиков = 12 записей
  --explain-stuck N       разобрать первые N застоев: печатает хвост событий
                          зависшего NPC (по умолчанию 3, 0 — выключить)
  --explain-loops N       §122: то же для ПЕТЕЛЬ — «двигается и не продвигается»
                          (по умолчанию 3, 0 — выключить)
  --explain-deaths N      §30: разобрать первые N смертей вместе с последними
                          решениями погибшей (по умолчанию 0)
  --loop-escape on|off    §122: автовыход из петель поверх simdata (A/B-ключ)
  --loop-max-rung N       §122: докуда поднимать лестницу (0 доклад .. 3 глушение)
  --quiet                 без человекочитаемого вывода
  --no-trace              прогнать как ИГРА: диагностика молчит (§30.17).
                          Метрики и --trace-out/--explain-* при этом пусты

Несколько сидов и --trace-out: файл на сид, суффикс .seed<N>.";
}

}
