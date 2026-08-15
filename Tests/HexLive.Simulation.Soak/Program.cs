using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.Simulation.Soak
{

public static class Program
{
    public static int Main(string[] args)
    {
        var options = SoakOptions.Parse(args, out var error);
        if (options == null)
        {
            Console.WriteLine(error);
            return error == SoakOptions.Usage ? 0 : 2;
        }

        // §30.17: трасса — продукт этого инструмента, а не побочный шум, и
        // включается ДО первого мира: метрики соака, --explain-stuck,
        // --explain-loops и эталонные трассы читают ровно её. В игре тот же
        // поток по умолчанию молчит.
        if (!options.NoTrace)
        {
            SimTrace.EnableAll();
        }

        // Спек §59.3, и это не опция: без экспортированных каталогов прогон
        // мерил бы код-дефолты вместо оттюненной игры. Require бросает.
        try
        {
            SimDataFile.Require(options.SimDataPath);
            if (options.TimedMelee)
            {
                // ПОСЛЕ Require: экспорт несёт своё значение флага, а ключ
                // командной строки для того и нужен, чтобы сравнить две модели
                // одним бинарём и одним simdata.
                SimBalance.TimedMeleeEverywhere = true;
            }

            // §122 фаза 2: тот же приём — ПОСЛЕ Require, чтобы одним бинарём и
            // одним simdata сравнить колонию с лестницей выхода и без неё.
            if (options.LoopEscape is { } loopEscape)
            {
                AiBalance.LoopEscapeEnabled = loopEscape;
            }

            if (options.LoopMaxRung is { } loopMaxRung)
            {
                AiBalance.LoopMaxRung = loopMaxRung;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("simdata: " + exception.Message);
            return 3;
        }

        var reports = new List<string>();
        foreach (var seed in options.Seeds)
        {
            var metrics = RunOne(seed, options);
            reports.Add(metrics.ToJson());

            if (!options.Quiet)
            {
                Console.Write(metrics.Report());
                Console.WriteLine();
            }
        }

        if (options.MetricsJsonPath != null)
        {
            File.WriteAllText(options.MetricsJsonPath, "[" + string.Join(",", reports) + "]");
        }

        return 0;
    }

    private static SoakMetrics RunOne(int seed, SoakOptions options)
    {
        // Те же три вызова, что делает сервер (WorldHost) — харнесс, собирающий
        // мир по-своему, меряет не ту игру.
        // Арена абьюза строится ТЕМ ЖЕ классом, что и сцена в Unity: иначе
        // мы смотрим на два разных мира и спорим о показаниях.
        var definition = options.Arena == "abuse"
            ? HexLive.UnityPresentation.AbuseTest.AbuseTestWorld.Build(seed)
            : PrototypeWorldDefinitionFactory.Create(seed);
        var world = new WorldStateFactory().Create(definition);

        if (options.Arena == "abuse")
        {
            // Подготовка живёт в самой арене — ровно то же делает сцена Unity и
            // гейт контракта снапшота. Копия здесь уже разошлась однажды.
            HexLive.UnityPresentation.AbuseTest.AbuseTestWorld.Prepare(world);
        }

        var settings = new SimulationSettings
        {
            TickDeltaTime = definition.Simulation.TickDeltaTime,
            MediumInterval = definition.Simulation.MediumTickInterval,
            SlowInterval = definition.Simulation.SlowTickInterval,
        };

        var clock = new SimulationClock();
        clock.Resume();

        var engine = new SimulationEngine(world, settings, clock);
        SimulationSystemRegistry.RegisterDefaults(engine);
        DefinitionIdTable.Build(world.Content);

        // §30.14: в headless-прогоне самописец включён всегда — здесь он ничего
        // не стоит, а без него событие застоя сообщает только ЧТО, но не ПОЧЕМУ.
        // A dying window can last hundreds of medium ticks. Keep enough
        // per-NPC history to retain the fight/flee decisions which preceded
        // it when the caller explicitly asks for death explanations.
        world.FlightRecorder = FlightRecorder.ForBehavior(
            options.ExplainDeaths > 0 ? 512 : 64);

        // Раскадровка копится в статике: без сброса отчёт этого сида включал бы
        // строки предыдущего.
        CombatFrames.Reset();

        var metrics = new SoakMetrics
        {
            Seed = seed,
            NpcsAtStart = world.Entities.Npcs.Count,
            ColonistsAtStart = CountColony(world),
        };

        StreamWriter trace = null;
        if (options.TraceOutPath != null)
        {
            var path = options.Seeds.Count > 1
                ? options.TraceOutPath + ".seed" + seed
                : options.TraceOutPath;
            trace = new StreamWriter(path, append: false, Encoding.UTF8);
        }

        // Кольцо событий вмещает 2048 записей и на многословной трассе
        // оборачивается примерно каждые 11 тиков — поэтому вычерпываем КАЖДЫЙ
        // тик и по watermark'у, а не по идентичности объектов.
        var watermark = world.Events.HighestSeq;
        var stopwatch = Stopwatch.StartNew();

        var explained = 0;
        var explainedLoops = 0;
        var explainedDeaths = 0;

        for (var i = 0; i < options.Ticks && !world.Completed; i++)
        {
            engine.Step();
            watermark = Drain(world, watermark, metrics, trace, options.TraceTypes,
                options, ref explained, ref explainedLoops, ref explainedDeaths);
            metrics.SampleTick(world);

            if (options.CombatFrames)
            {
                CombatFrames.Sample(world);
            }

            if (options.StateHashEvery > 0 && world.Tick % options.StateHashEvery == 0)
            {
                trace?.WriteLine(world.Tick + "|-|STATE|" + StateHash.Of(world));
            }
        }

        stopwatch.Stop();
        trace?.Dispose();

        if (options.CombatFrames)
        {
            CombatFrames.Report();
        }

        if (options.Journal >= 0 && !options.Quiet)
        {
            PrintJournal(world, options.Journal);
        }

        metrics.TicksRun = world.Tick;
        metrics.NpcsAtEnd = world.Entities.Npcs.Count;
        metrics.ColonistsAtEnd = CountColony(world);
        metrics.MobsAtEnd = world.Mobs.Count;
        metrics.Seconds = stopwatch.Elapsed.TotalSeconds;
        metrics.Finish(world);
        return metrics;
    }

    /// <summary>
    /// Живые КОЛОНИСТКИ. Общий счёт NPC включает чужака (§72), и его смерть в
    /// строке «NPC 4 → 3» неотличима от смерти колонистки — хотя это ровно
    /// противоположная новость. Цель баланса ставится в колонии, значит и
    /// мерить надо колонию.
    /// </summary>
    private static int CountColony(WorldState world)
    {
        var count = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            if (npc.Faction == Faction.Colony)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Вычерпывает всё, что новее watermark'а. Пропуск, когда кольцо успело
    /// подрезать хвост, — это ПОТЕРЯ, а не повод перечитать остаток: перечитывание
    /// дало бы дубликаты в счётчиках.
    /// </summary>
    private static long Drain(WorldState world, long watermark, SoakMetrics metrics,
        StreamWriter trace, HashSet<string> traceTypes, SoakOptions options,
        ref int explained, ref int explainedLoops, ref int explainedDeaths)
    {
        var items = world.Events.Items;
        var lowest = world.Events.LowestSeq;
        if (watermark < lowest - 1)
        {
            Console.Error.WriteLine(
                "тик " + world.Tick + ": кольцо событий подрезало хвост, пропущено " +
                (lowest - 1 - watermark) + " записей — метрики занижены.");
            watermark = lowest - 1;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var simulationEvent = items[i];
            if (simulationEvent.Seq <= watermark)
            {
                continue;
            }

            watermark = simulationEvent.Seq;
            metrics.CountEvent(simulationEvent);

            if (explained < options.ExplainStuck && !options.Quiet &&
                simulationEvent.Type == "StuckDetected" &&
                simulationEvent.Message.Contains("ONSET", StringComparison.Ordinal))
            {
                explained++;
                ExplainWithTail(world, simulationEvent, "застой");
            }

            // §122: петля разбирается тем же хвостом и по своему счётчику.
            // Общий счётчик означал бы, что шумный застой съедает бюджет петель
            // и наоборот — а это разные болезни, и смотрят их порознь.
            if (explainedLoops < options.ExplainLoops && !options.Quiet &&
                simulationEvent.Type == "LoopDetected" &&
                simulationEvent.Message.Contains("ONSET", StringComparison.Ordinal))
            {
                explainedLoops++;
                ExplainWithTail(world, simulationEvent, "петля");
            }

            if (explainedDeaths < options.ExplainDeaths && !options.Quiet &&
                simulationEvent.Type == "NpcDied")
            {
                explainedDeaths++;
                ExplainWithTail(world, simulationEvent, "смерть", death: true);
            }

            if (trace == null || traceTypes == null || !traceTypes.Contains(simulationEvent.Type))
            {
                continue;
            }

            // Seq намеренно НЕ пишется: он сдвигается, стоит любой другой системе
            // эмитить на одно событие больше, а это не изменение поведения в том
            // подмножестве, которое мы сравниваем.
            trace.WriteLine(simulationEvent.Tick + "|" +
                            (simulationEvent.EntityId?.ToString() ?? "-") + "|" +
                            simulationEvent.Type + "|" + simulationEvent.Message);
        }

        return watermark;
    }

    /// <summary>
    /// §136: печатает дневник одной колонистки — СЫРЫМИ полями записи.
    ///
    /// <para>
    /// Фразы здесь быть не может и не должно: текст живёт в терминах I2, а
    /// они в Unity-слое (§58.3). Смотреть надо на другое — не спамит ли
    /// дневник, попадают ли в записи люди, меняется ли регистр вместе с её
    /// состоянием и не выродились ли двое суток в сорок восемь «тихо».
    /// </para>
    /// </summary>
    private static void PrintJournal(WorldState world, int npcId)
    {
        var id = new HexLive.Simulation.Common.EntityId(npcId);
        if (!world.Entities.Npcs.TryGetValue(id, out var npc) &&
            !world.Entities.Corpses.TryGetValue(id, out npc))
        {
            Console.WriteLine();
            Console.WriteLine("§136: NPC " + npcId + " в этом мире нет.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("── дневник NPC " + npcId + " (" + npc.DisplayName + "), записей: " +
                          npc.Journal.Entries.Count);

        foreach (var entry in npc.Journal.Entries)
        {
            var day = HexLive.Simulation.Runtime.EnvironmentSystem.CalendarDay(entry.Tick);
            var progress = (entry.Tick % HexLive.Simulation.Runtime.EnvironmentSystem.DayLengthTicks) /
                           (float)HexLive.Simulation.Runtime.EnvironmentSystem.DayLengthTicks;
            var clock = HexLive.Simulation.Runtime.EnvironmentSystem.FormatClock(progress);

            if (entry.IsQuiet)
            {
                var chores = string.Join(", ", new[] { entry.Chore0, entry.Chore1, entry.Chore2 }
                    .Where(c => !string.IsNullOrEmpty(c)));
                Console.WriteLine($"  день {day} {clock}  [{entry.Register}] тихо ×{entry.QuietHours}" +
                                  (chores.Length == 0 ? "" : "  дела: " + chores));
                continue;
            }

            var who = string.IsNullOrEmpty(entry.SubjectNameId)
                ? ""
                : $"  о {entry.SubjectNameId} ({entry.Bond})";
            var extra = string.IsNullOrEmpty(entry.Extra) ? "" : $"  [{entry.Extra}]";
            Console.WriteLine($"  день {day} {clock}  [{entry.Register}] " +
                              $"{entry.Type} ({entry.Perspective}){who}{extra}  v{entry.Variant}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Печатает застой вместе с тем, что этот NPC делал ДО него.
    /// <para>
    /// В §102 именно этого не хватало: событие «стоит» ответило бы на «что», но
    /// не на «почему», а общее кольцо к моменту застоя уже тысячу раз вытеснено
    /// чужой болтовнёй. Хвост самописца вытесняют только её собственные события,
    /// поэтому там так и лежат последние решения перед остановкой.
    /// </para>
    /// </summary>
    private static void ExplainWithTail(
        WorldState world, SimulationEvent stuck, string label, bool death = false)
    {
        Console.WriteLine();
        Console.WriteLine("  ── " + label + ": тик " + stuck.Tick + ", NPC " +
                          (stuck.EntityId?.ToString() ?? "-"));
        Console.WriteLine("     " + stuck.Message);

        if (world.FlightRecorder == null || stuck.EntityId == null)
        {
            return;
        }

        var tail = world.FlightRecorder.Tail(stuck.EntityId.Value, death ? 512 : 12);
        if (death)
        {
            // Once she is down, PlanningSystem's harmless None pass can emit
            // one identical PlanStarted every medium tick. It must not erase
            // the attack, flight or aid failure that actually explains death.
            tail = tail
                .Where(entry => !(entry.Type == "PlanStarted" &&
                                  entry.Message.Contains("Goal=None")))
                .TakeLast(20)
                .ToList();
        }
        if (tail.Count == 0)
        {
            Console.WriteLine("     (самописец пуст — она не эмитила вообще ничего)");
            return;
        }

        Console.WriteLine("     что было до этого:");
        foreach (var entry in tail)
        {
            var message = entry.Message.Length > 96
                ? entry.Message.Substring(0, 96) + "…"
                : entry.Message;
            Console.WriteLine("       [" + entry.Tick + "] " + entry.Type + "  " + message);
        }

        Console.WriteLine();
    }
}

}
