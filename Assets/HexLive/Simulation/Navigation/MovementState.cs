using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Navigation
{

public sealed class MovementState
{
    // Spec 24.3: ticks spent politely waiting for a housemate to move.
    public int BlockedWaitTicks { get; set; }

    public bool IsMoving { get; set; }

    public List<JunctionId> JunctionPath { get; } = new();

    public int PathIndex { get; set; }

    public Float2 DesiredDirection { get; set; } = Float2.Zero;

    public float DesiredRotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 180f;

    public MovementStatus Status { get; set; } = MovementStatus.Idle;

    public string StopReason { get; set; } = string.Empty;

    public float PostTurnDelay { get; set; }
    public float PostTurnTimer { get; set; }

    // Generic standing pause (hop landing idle, swim-entry treading, ...).
    public float ClimbPauseTimer { get; set; }

    // §21.21B hex-step hop (timing in HexHopTuning): while HopTimer > 0 the
    // NPC is traversing the jump path at constant speed (HopDistance over
    // HopSeconds); on landing ClimbPauseTimer holds the plain-idle stand.
    // HopPathIndex remembers which path step already hopped so a resumed walk
    // doesn't re-hop. Transient — not persisted in saves.
    public float HopTimer { get; set; }
    // §21.21B v6 lattice-point jump: a straight flight from where she stood
    // when the edge junction became her target to the lattice point AFTER it
    // (the edge junction itself is excluded from walking); LandingIndex is
    // that point's path index, Crossed latches the touchdown bookkeeping.
    public int HopLandingIndex { get; set; }
    // Committed to an approaching hop: walk to the FIXED HopFrom (takeoff),
    // no per-tick rescan (rescanning flipped the target and jittered her).
    public bool HopArmed { get; set; }
    public Float2 HopFrom { get; set; }
    public Float2 HopTo { get; set; }
    public bool HopCrossed { get; set; }
    public bool HopUp { get; set; }
    // Where the hop lands — the presentation reads the exact target ground
    // height from it (water dives land BELOW the surface, not one step down).
    public TileCoord HopTargetTile { get; set; }
    // Legacy arm-flag kept for external callers (swim test); the hop itself
    // now always starts on the approach segment before the edge junction.
    public bool PendingDownHop { get; set; }
    public int HopPathIndex { get; set; } = -1;
}

public enum MovementStatus
{
    Idle,
    Rotating,
    Moving,
    Arrived,
    Blocked,
    Waiting,
    Invalid
}

}
