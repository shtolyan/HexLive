using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Agents
{

public sealed class NPCState
{
    public EntityId Id { get; set; }

    public FragmentId Fragment { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public PointId? Point { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 180f;

    public float EquippedWarmth { get; set; }

    public NPCNeeds Needs { get; } = new();

    public NPCMind Mind { get; } = new();

    public NPCPlanState Plan { get; } = new();

    public NPCExecutionState Execution { get; } = new();

    public MovementState Movement { get; } = new();

    public PerceptionSnapshot Perception { get; } = new();

    public MemoryState Memory { get; } = new();

    public SocialState Social { get; } = new();
}

}
