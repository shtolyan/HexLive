using System.Collections.Generic;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public sealed class SimulationEngine
{
    private readonly List<ISimulationSystem> _systems = new();

    public SimulationEngine(WorldState world, SimulationSettings settings, SimulationClock clock)
    {
        World = world;
        Settings = settings;
        Clock = clock;
        World.RuntimeClock = clock;
        // §156: формулы догона спрашивают «когда был предыдущий slow-такт», а
        // интервал приходит из определения мира и константой быть не может.
        World.SlowIntervalTicks = settings.SlowInterval;
    }

    public WorldState World { get; }

    public SimulationSettings Settings { get; }

    public SimulationClock Clock { get; }

    public IReadOnlyList<ISimulationSystem> Systems => _systems;

    /// <summary>§121: приказы игрока, ждущие ближайшего тика. Кладёт сюда
    /// только презентация; опустошается в начале <see cref="Step"/>.</summary>
    public SimulationCommandQueue Commands { get; } = new();

    public void Register(ISimulationSystem system) => _systems.Add(system);

    /// <summary>
    /// Applies one manual-control command through the authoritative validation
    /// boundary and returns its immediate admission result. The caller owns
    /// scheduling: Unity uses <see cref="Commands"/> on its main thread, while a
    /// host must serialize this call with <see cref="Step"/> and snapshot reads.
    /// </summary>
    public ManualCommandAdmission ApplyManualCommand(ISimulationCommand command)
    {
        if (command is null)
        {
            throw new System.ArgumentNullException(nameof(command));
        }

        return ManualCommandExecutor.Apply(World, command);
    }

    public void Step()
    {
        if (World.Completed)
        {
            return;
        }

        // §121: приказы применяются ДО систем этого тика — иначе «иди туда»,
        // отданное между тиками, ждало бы своего слоя и опаздывало на проход.
        // Кладёт и опустошает один и тот же главный поток (в Unity — Update,
        // headless — цикл прогона), поэтому замка здесь нет.
        while (Commands.TryDequeue(out var command) && command is not null)
        {
            ApplyManualCommand(command);
        }

        var isMedium = World.Tick % Settings.MediumInterval == 0;
        var isSlow = World.Tick % Settings.SlowInterval == 0;

        if (SimTrace.Enabled)
        {
            World.Events.Add(new SimulationEvent
            {
                Tick = World.Tick,
                EntityId = null,
                Type = "TickStart",
                Message = $"Tick={World.Tick} Layers=[Fast{(isMedium ? ",Medium" : "")}{(isSlow ? ",Slow" : "")}] " +
                          $"Npcs={World.Entities.Npcs.Count} Objects={World.Entities.Objects.Count}"
            });
        }

        RunLayer(TickLayer.Fast);

        if (!World.Completed && isMedium)
        {
            RunLayer(TickLayer.Medium);
        }

        if (!World.Completed && isSlow)
        {
            // §156: активный набор — вывод из позиций живых NPC, и считает его
            // движок, а не сороковая система: у этой работы нет своего места в
            // слоях, а реестр §30 и его прибитый порядок трогать незачем.
            //
            // Считается ЗДЕСЬ, а не в начале тика: спрашивают о нём только
            // системы слоя Slow, а пересчёт на каждом тике был чистой растратой
            // — замер на большом острове показал, что он съедал больше, чем
            // экономил весь сон.
            ChunkMath.RebuildActiveChunks(World);
            ChunkMath.EnsureObjectIndex(World);

            RunLayer(TickLayer.Slow);

            // §156: штамп ставится ПОСЛЕ слоя — системы этого такта обязаны были
            // увидеть ещё старое окно, иначе догон пропустил бы сам себя.
            ChunkMath.StampSimulated(World);
        }

        World.Tick += 1;
    }

    private void RunLayer(TickLayer layer)
    {
        foreach (var system in _systems)
        {
            if (World.Completed)
            {
                break;
            }

            if (system.Layer != layer)
            {
                continue;
            }

            system.Run(World);
        }
    }
}

}
