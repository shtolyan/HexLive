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

    public void Register(ISimulationSystem system) => _systems.Add(system);

    public void Step()
    {
        if (World.Completed)
        {
            return;
        }

        var isMedium = World.Tick % Settings.MediumInterval == 0;
        var isSlow = World.Tick % Settings.SlowInterval == 0;

        World.Events.Add(new SimulationEvent
        {
            Tick = World.Tick,
            EntityId = null,
            Type = "TickStart",
            Message = $"Tick={World.Tick} Layers=[Fast{(isMedium ? ",Medium" : "")}{(isSlow ? ",Slow" : "")}] " +
                      $"Npcs={World.Entities.Npcs.Count} Objects={World.Entities.Objects.Count}"
        });

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
