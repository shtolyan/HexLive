using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Content;
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
        var definition = PrototypeWorldDefinitionFactory.Create(seed);
        var world = new WorldStateFactory().Create(definition);

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

        for (var i = 0; i < options.Ticks && !world.Completed; i++)
        {
            engine.Step();
            watermark = Drain(world, watermark, metrics, trace, options.TraceTypes);
            metrics.SampleTick(world);

            if (options.StateHashEvery > 0 && world.Tick % options.StateHashEvery == 0)
            {
                trace?.WriteLine(world.Tick + "|-|STATE|" + StateHash.Of(world));
            }
        }

        stopwatch.Stop();
        trace?.Dispose();

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
        StreamWriter trace, HashSet<string> traceTypes)
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
}

}
