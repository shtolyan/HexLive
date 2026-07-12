using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Agents
{

// Spec 19.3C: per-part health (molly BoneHealthSystem, simplified).
public sealed class BodyState
{
    public System.Collections.Generic.Dictionary<BodyPart, float> Parts { get; } = new()
    {
        [BodyPart.Head] = 1f,
        [BodyPart.Torso] = 1f,
        [BodyPart.Pelvis] = 1f,
        [BodyPart.ArmL] = 1f,
        [BodyPart.ArmR] = 1f,
        [BodyPart.LegL] = 1f,
        [BodyPart.LegR] = 1f
    };

    public float Mean()
    {
        var sum = 0f;
        foreach (var value in Parts.Values)
        {
            sum += value;
        }

        return sum / Parts.Count;
    }

    public bool VitalDestroyed(out BodyPart part)
    {
        if (Parts[BodyPart.Head] <= 0f)
        {
            part = BodyPart.Head;
            return true;
        }

        if (Parts[BodyPart.Torso] <= 0f)
        {
            part = BodyPart.Torso;
            return true;
        }

        part = BodyPart.Head;
        return false;
    }

    // Spec 19.3C: mauled legs mean hobbling, hurt arms mean weak strikes.
    public float MobilityFactor() =>
        0.4f + 0.6f * (Parts[BodyPart.LegL] + Parts[BodyPart.LegR]) * 0.5f;

    public float StrikeFactor() =>
        0.4f + 0.6f * (Parts[BodyPart.ArmL] + Parts[BodyPart.ArmR]) * 0.5f;
}

// Spec 40.8B: one landed bite/hit = one wound record. The zone-health model
// (BodyState) keeps driving HP/posture/balance exactly as before; wounds are
// the parallel VISUAL truth — where the skin is broken, how it looks and how
// far it has closed. Placement/look derive deterministically from Seed.
public sealed class WoundState
{
    public int Id { get; set; }

    public BodyPart Zone { get; set; }

    // HP the hit cost at infliction (bookkeeping/UI; balance stays in BodyState).
    public float Severity { get; set; }

    // 0 = fresh and vivid, 1 = fully closed (record removed) — the decal
    // fades with this.
    public float Heal01 { get; set; }

    public int Seed { get; set; }
}

public sealed class NPCState
{
    public EntityId Id { get; set; }

    // Spec 19.3 / iteration 23: presentation identity — the girls have
    // names and bodies; the simulation itself never branches on them.
    public string DisplayName { get; set; } = string.Empty;

    public string ActorMesh { get; set; } = string.Empty;

    public FragmentId Fragment { get; set; }

    public TileCoord Tile { get; set; } = TileCoord.Zero;

    public JunctionId? CurrentJunction { get; set; }

    public Float2 Position { get; set; } = Float2.Zero;

    public float RotationDegrees { get; set; }

    public float MoveSpeed { get; set; } = 1f;

    public float TurnSpeed { get; set; } = 90f;

    public float PostTurnPause { get; set; } = 0.4f;

    // Spec 31A.5A/35.5: worn item instances; warmth/armor are recomputed
    // from this list, never mutated directly. Wet items give no warmth.
    public System.Collections.Generic.List<ItemInstance> WornItems { get; } = new();

    public float EquippedWarmth { get; set; }

    // Spec 29C.2/29C.4: health and damage absorption.
    // Health is the mean of body parts (spec 19.3C), kept as a field for
    // snapshots and thresholds; recomputed after every wound/regen.
    public float Health { get; set; } = 1f;

    public BodyState Body { get; } = new();

    // Spec 40.8B: wounds are first-class records — one per landed bite/hit
    // (starvation/heat/sickness drain HP with NO wound). Each wound knows its
    // zone, the damage it cost, a deterministic Seed (exact decal spot & look,
    // stable across frames and save-replays) and Heal01: 0 fresh → 1 healed
    // (the decal fades with it; the record is removed when fully closed).
    public System.Collections.Generic.List<WoundState> Wounds { get; } = new();

    // Spec 44: zones currently dressed with a leaf bandage — presentation
    // draws the wrap decal; cleared as the zone heals past 0.7.
    public System.Collections.Generic.HashSet<Content.BodyPart> BandagedZones { get; } = new();

    public int NextWoundId { get; set; } = 1;

    public float EquippedArmor { get; set; }

    // Spec 29C.3: set while a dog is engaging this NPC; combat is reactive.
    public bool IsFighting { get; set; }

    // Spec 35.4: accumulated sun exposure; burns at 1.0.
    public float SunExposure { get; set; }

    public NPCNeeds Needs { get; } = new();

    public NPCMind Mind { get; } = new();

    public NPCPlanState Plan { get; } = new();

    public NPCExecutionState Execution { get; } = new();

    public MovementState Movement { get; } = new();

    public PerceptionSnapshot Perception { get; } = new();

    public MemoryState Memory { get; } = new();

    public SocialState Social { get; } = new();

    public InventoryState Inventory { get; } = new();

    // Spec 29G: junctions covered by a lying body — housemates path around.
    public System.Collections.Generic.List<HexLive.Simulation.Common.JunctionId> ClaimedJunctions { get; } = new();

    // Spec 29H: what the carried bottle currently holds (one bottle per NPC).
    public WaterKind BottleWater { get; set; } = WaterKind.None;
}

// Spec 29H: the contents of an NPC's water bottle.
public enum WaterKind
{
    None,
    Raw,    // filled at a pond/river bank — 30 % sickness on drink
    Boiled  // filled at a lit campfire with a pot — safe, quenches more
}

}
