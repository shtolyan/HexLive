using System.Collections.Generic;
using HexLive.Simulation.AI;

namespace HexLive.Simulation.Memory
{

public sealed class MemoryState
{
    public List<MemoryRecord> WorkingMemory { get; } = new();

    public List<MemoryRecord> ShortTermMemory { get; } = new();

    public List<MemoryRecord> LongTermMemory { get; } = new();
}

public sealed class MemoryRecord
{
    public GoalType RelatedGoal { get; set; }

    public string Description { get; set; } = string.Empty;

    public float Strength { get; set; }

    public int CreatedTick { get; set; }
}

public sealed class MemorySystem
{
}

}
