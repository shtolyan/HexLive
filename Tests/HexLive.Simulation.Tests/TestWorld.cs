using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;

namespace HexLive.Simulation.Tests
{

/// <summary>
/// Мир для теста — теми же тремя вызовами, что делает сервер
/// (<c>WorldHost</c>) и что предписывает CLAUDE.md. Своей сборки мира здесь
/// нет намеренно: харнесс, собирающий мир по-своему, меряет не ту игру.
/// </summary>
public static class TestWorld
{
    public static SimulationEngine CreateEngine(int seed = 12345)
    {
        // §30.17: в тестах трасса включена всегда — половина поведенческих
        // проверок ассертит именно на события, а в игре тот же поток по
        // умолчанию молчит.
        SimTrace.EnableAll();

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

        // Иначе объекты доедут безымянными, а рендер нарисует серые шары —
        // молчаливый отказ, поэтому строим до первого кадра (CLAUDE.md §83).
        DefinitionIdTable.Build(world.Content);

        return engine;
    }

    public static WorldState CreateWorld(int seed = 12345) => CreateEngine(seed).World;
}

}
