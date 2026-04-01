using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Runtime
{

public sealed class SimulationCommandQueue
{
    private readonly Queue<ISimulationCommand> _commands = new();

    public void Enqueue(ISimulationCommand command) => _commands.Enqueue(command);

    public bool TryDequeue(out ISimulationCommand? command)
    {
        if (_commands.Count == 0)
        {
            command = null;
            return false;
        }

        command = _commands.Dequeue();
        return true;
    }
}

public interface ISimulationCommand
{
    EntityId? TargetEntity { get; }
}

}
