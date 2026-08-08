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
    }

    public WorldState World { get; }

    public SimulationSettings Settings { get; }

    public SimulationClock Clock { get; }

    public IReadOnlyList<ISimulationSystem> Systems => _systems;

    /// <summary>§121: приказы игрока, ждущие ближайшего тика. Кладёт сюда
    /// только презентация; опустошается в начале <see cref="Step"/>.</summary>
    public SimulationCommandQueue Commands { get; } = new();

    public void Register(ISimulationSystem system) => _systems.Add(system);

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
            ManualCommandExecutor.Apply(World, command);
        }

        var isMedium = World.Tick % Settings.MediumInterval == 0;
        var isSlow = World.Tick % Settings.SlowInterval == 0;

        if (SimTrace.Verbose)
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
            RunLayer(TickLayer.Slow);
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
