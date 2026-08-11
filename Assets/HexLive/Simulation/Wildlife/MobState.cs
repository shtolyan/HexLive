using HexLive.Simulation.Common;

namespace HexLive.Simulation.Wildlife
{

// Spec 29C.3: a mob (wolf, shark, any future tiger) is a three-state machine,
// not an NPC — no needs, plans, or perception pipeline. Every mob can roam,
// chase and ATTACK; what the attack looks like (a bite, a peck, a paw swipe)
// is presentation — the sim only knows a timed windup that lands damage.
public sealed class MobState
{
    public int Id { get; set; }

    // Which MobCatalog sheet drives this creature ("dog" today; a tiger
    // spawner sets its own id). Systems read stats via MobCatalog.For(MobId),
    // presentation picks the view prefab by it. Persisted (save blob v5).
    public string MobId { get; set; } = Content.MobIds.Dog;

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId Junction { get; set; }

    // The RENDERED position. MobSystem moves the dog by junction (logical),
    // but Position now glides toward TargetPosition a little each fast tick
    // (AnimalMovementSystem) — the same continuous per-tick motion NPCs get
    // from MovementSystem, so presentation interpolates it smoothly instead
    // of teleporting a whole junction at once (the old "рывками" gait).
    public Float2 Position { get; set; } = Float2.Zero;

    // Where Position is gliding to — set to the current junction's world
    // position whenever the dog changes junction.
    public Float2 TargetPosition { get; set; } = Float2.Zero;

    // Per-segment constant glide speed (world units / sim-second), recomputed
    // when a new TargetPosition arrives so the hop is covered over one
    // movement segment. GlideAnchor detects that target change.
    public float GlideSpeed { get; set; }

    public Float2 GlideAnchor { get; set; } = Float2.Zero;

    public float Health { get; set; } = Content.MobCatalog.For(Content.MobIds.Dog).MaxHealth;

    public MobStatus Status { get; set; } = MobStatus.Roaming;

    public EntityId? TargetNpc { get; set; }

    // Timed attack exchange (AnimalCombatSystem): >0 while an attack is
    // winding up — it lands at exactly this tick (even if the target slipped
    // away). Not persisted; a loaded save simply restarts the windup.
    public int AttackLandsAtTick { get; set; }

    // The tick that windup STARTED — presentation pulses the bite on a start
    // it has not played yet. The windup is ~2 ticks, so an unrendered tick
    // otherwise swallows the whole lunge; see NPCState.SwingStartTick.
    public int AttackStartTick { get; set; }

    // Next tick a new attack may start winding up (cooldown gate).
    public int AttackReadyAtTick { get; set; }

    // Spec 29C.3 (stuck-chase give-up): first tick this chase failed to move
    // the mob (no walkable route to the quarry). 0 = not stalled. Transient —
    // not persisted; a loaded save simply re-judges the chase fresh.
    public int ChaseStallSinceTick { get; set; }

    // After abandoning a hopeless chase the mob ignores prey until this tick,
    // so it actually wanders off instead of re-acquiring the same unreachable
    // girl on the very next pass. Transient — not persisted.
    public int NextHuntAllowedTick { get; set; }

    // §46 v4: ГОСТЬ или ЖИТЕЛЬ. 0 = житель: живёт на острове, пока его не
    // убьют. >0 = ночная стая пришла и после этого тика собирается уходить.
    // ⭐ ПЕРСИСТЕНТНО (blob v38), и это здесь главное: без записи в сейв
    // каждая загрузка производила гостей в жители — а стая накапливалась
    // именно так, потому что рейд спавнил мимо потолка, и уйти собака могла
    // только смертью.
    public int LeavesAtTick { get; set; }
}

public enum MobStatus
{
    Roaming,
    Chasing,
    Fighting
}

// Spec 29F.1: prey — grazes, hops, flees; spooked after a missed strike.
public sealed class RabbitState
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId Junction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public int SpookedUntilTick { get; set; }
}

// Spec 40.18: a shark — patrols water only, bites any NPC that swims. Like a
// dog it's a simple roamer, not an NPC (no needs/plans). Dormant against the
// land colony (it can't leave the water) until swimming gives it prey.
public sealed class SharkState
{
    public int Id { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId Junction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;
}

}
