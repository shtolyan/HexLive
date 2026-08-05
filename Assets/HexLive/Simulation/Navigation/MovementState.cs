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

    // Vestigial: written each tick but never READ for behaviour (movement uses
    // a local `direction` + DesiredRotationDegrees). Still serialized, so kept
    // to avoid a save-format bump; drop with the next serializer version.
    public Float2 DesiredDirection { get; set; } = Float2.Zero;

    public float DesiredRotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 180f;

    public MovementStatus Status { get; set; } = MovementStatus.Idle;

    // §71.3: ⭐ ЛЕГАСИ-ЛАТЧ ТЕМПА ЗАЖИВЛЕНИЯ. До честного статуса `Moving`
    // ставился ТОЛЬКО веткой «шагнула, но не дошла до джанкшена» и висел до
    // следующей записи. Бегун (шаг 0.675 > 0.375 решётки) в неё не попадал
    // НИКОГДА — статус у него залипал на Rotating/Arrived, и штраф «на ходу
    // раны затягиваются вдвое медленнее» его не касался. На этой случайной
    // льготе оттюнена вся экономика сцен §81: гопник живёт на бегу и на
    // побоях, и с честным штрафом он истекает кровью и умирает на тике ~15900
    // (сид 313), не дожив ~700 тиков до сговора §108, который выигрывал гонку
    // при старой семантике.
    //
    // Поэтому темп заживления (NeedsDecaySystem) читает НЕ Status, а этот
    // латч, который повторяет старые записи дословно: его двигает SetStatus
    // (все исторические места записи), и НЕ двигает честный `Status = Moving`
    // из блока трансляции §71.3. Убрать латч = осознанное решение баланса §81,
    // а не рефакторинг.
    public bool WoundPaceMoving { get; set; }

    // §71.3: статус + легаси-латч заживления. Все места записи статуса, кроме
    // честного `Moving` из блока трансляции, обязаны ходить сюда — прямая
    // запись Status мимо латча тихо меняет темп заживления (см. WoundPaceMoving).
    public void SetStatus(MovementStatus status)
    {
        Status = status;
        WoundPaceMoving = status == MovementStatus.Moving;
    }

    public string StopReason { get; set; } = string.Empty;

    // Vestigial: only round-trips through the serializer, never used in logic
    // (the real post-turn pause is npc.PostTurnPause + PostTurnTimer). Kept for
    // save-format compat; drop with the next serializer version.
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
    // The tick the hop STARTED. Presentation starts the arc on a hop-start it
    // has not played yet instead of on the rising edge of HopKind — a skipped
    // tick otherwise loses the jump entirely and she glides up the ledge. It
    // also lets a late-observed hop start a SHORTENED arc from what is left of
    // the window rather than a full one that overshoots the landing.
    public int HopStartTick { get; set; }
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
    // Presentation-only flag (up vs down). The MODEL flight is elevation-
    // agnostic: one straight lerp HopFrom->HopTo either way — do NOT branch the
    // model on this. The view uses it to pick the ascending/descending arc.
    public bool HopUp { get; set; }
    // Where the hop lands — the presentation reads the exact target ground
    // height from it (water dives land BELOW the surface, not one step down).
    public TileCoord HopTargetTile { get; set; }
    // §21.21B v15: where the hop takes off FROM (the tile holding HopFrom). The
    // view builds the arc's height delta as target - from, so it no longer has
    // to read npc.Tile — which the sim commits to the LANDING tile while the
    // window is still open, zeroing the delta and leaving the body pinned a
    // whole elevation step off the ground until the arc was cut to zero.
    public TileCoord HopFromTile { get; set; }
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
