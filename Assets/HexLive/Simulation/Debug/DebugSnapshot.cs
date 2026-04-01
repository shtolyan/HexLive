using System.Collections.Generic;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Debug
{

public sealed class SimulationDebugSnapshot
{
    public int Tick { get; set; }

    public float SpeedMultiplier { get; set; }

    public List<SelectedNpcDebugView> Npcs { get; } = new();
}

public sealed class SelectedNpcDebugView
{
    public int EntityId { get; set; }

    public GoalType Goal { get; set; }

    public string PlanSummary { get; set; } = string.Empty;

    public string MovementSummary { get; set; } = string.Empty;

    public string ExecutionSummary { get; set; } = string.Empty;
}

}
