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
        RunLayer(TickLayer.Fast);

        if (World.Tick % Settings.MediumInterval == 0)
        {
            RunLayer(TickLayer.Medium);
        }

        if (World.Tick % Settings.SlowInterval == 0)
        {
            RunLayer(TickLayer.Slow);
        }

        World.Tick += 1;
    }

    private void RunLayer(TickLayer layer)
    {
        foreach (var system in _systems)
        {
            if (system.Layer != layer)
            {
                continue;
            }

            system.Run(World);
        }
    }
}

}
