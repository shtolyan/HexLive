using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        // Спек §59.3, и это не опция: без экспортированных каталогов прогон
        // мерил бы код-дефолты вместо оттюненной игры. Require бросает.
        try
        {
            SimDataFile.Require(options.SimDataPath);
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
            // Ровно то же, что делает AbuseTestBootstrap перед первым тиком:
            // перевести часы ЗА льготные сутки §81 и раздать то единственное,
            // что арена добавляет от себя. Без этого мир формально тот же, а
            // сцена не случается никогда — отсрочка считается от DayLengthTicks
            // (24000), и короткий прогон до неё просто не доживает.
            world.Tick = Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks + 900;

            for (var i = 0; i < 3; i++)
            {
                if (world.Entities.Npcs.TryGetValue(
                        new EntityId(HexLive.UnityPresentation.AbuseTest.AbuseTestWorld.GirlId + i),
                        out var girl))
                {
                    girl.Inventory.Items.Add(ContentIds.Knife);
                }
            }

            if (world.Entities.Npcs.TryGetValue(
                    new EntityId(HexLive.UnityPresentation.AbuseTest.AbuseTestWorld.OutsiderId),
                    out var outsider))
            {
                outsider.Inventory.Items.Add(ContentIds.Spear);
                outsider.Inventory.Items.Add(ContentIds.Knife);
            }
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
        world.FlightRecorder = FlightRecorder.ForBehavior();

        var metrics = new SoakMetrics
        {
            Seed = seed,
            NpcsAtStart = world.Entities.Npcs.Count,
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

        for (var i = 0; i < options.Ticks && !world.Completed; i++)
        {
            engine.Step();
            watermark = Drain(world, watermark, metrics, trace, options.TraceTypes,
                options, ref explained);
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

        metrics.TicksRun = world.Tick;
        metrics.NpcsAtEnd = world.Entities.Npcs.Count;
        metrics.Seconds = stopwatch.Elapsed.TotalSeconds;
        return metrics;
    }

    /// <summary>
    /// Вычерпывает всё, что новее watermark'а. Пропуск, когда кольцо успело
    /// подрезать хвост, — это ПОТЕРЯ, а не повод перечитать остаток: перечитывание
    /// дало бы дубликаты в счётчиках.
    /// </summary>
    private static long Drain(WorldState world, long watermark, SoakMetrics metrics,
        StreamWriter trace, HashSet<string> traceTypes, SoakOptions options, ref int explained)
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
                ExplainStuck(world, simulationEvent);
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
    /// Печатает застой вместе с тем, что этот NPC делал ДО него.
    /// <para>
    /// В §102 именно этого не хватало: событие «стоит» ответило бы на «что», но
    /// не на «почему», а общее кольцо к моменту застоя уже тысячу раз вытеснено
    /// чужой болтовнёй. Хвост самописца вытесняют только её собственные события,
    /// поэтому там так и лежат последние решения перед остановкой.
    /// </para>
    /// </summary>
    private static void ExplainStuck(WorldState world, SimulationEvent stuck)
    {
        Console.WriteLine();
        Console.WriteLine("  ── застой: тик " + stuck.Tick + ", NPC " +
                          (stuck.EntityId?.ToString() ?? "-"));
        Console.WriteLine("     " + stuck.Message);

        if (world.FlightRecorder == null || stuck.EntityId == null)
        {
            return;
        }

        var tail = world.FlightRecorder.Tail(stuck.EntityId.Value, 12);
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
