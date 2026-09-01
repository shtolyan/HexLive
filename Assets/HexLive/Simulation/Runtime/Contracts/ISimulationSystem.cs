using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

public interface ISimulationSystem
{
    string Name { get; }

    TickLayer Layer { get; }

    /// <summary>
    /// §156: как система относится к спящим чанкам. Член интерфейса, а не
    /// атрибут, нарочно: пропуск обязан ловить компилятор, а не ревью — новая
    /// система выбирает политику так же обязательно, как выбирает слой.
    /// </summary>
    ChunkPolicy ChunkPolicy { get; }

    void Run(WorldState world);
}

}
