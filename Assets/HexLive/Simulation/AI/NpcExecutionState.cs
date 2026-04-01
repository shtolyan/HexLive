using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.AI
{

public sealed class NPCExecutionState
{
    public ExecutionStatus Status { get; set; } = ExecutionStatus.None;

    public InteractionType? CurrentInteraction { get; set; }

    public ObjectId? TargetObject { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }

    public string FailureReason { get; set; } = string.Empty;

    public int LastCompletedTick { get; set; } = -1;
}

public enum ExecutionStatus
{
    None,
    Starting,
    InProgress,
    Completed,
    Failed,
    Waiting
}

}
