using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// One combat/behaviour config PER MOB TYPE — the engine-free source the
    /// simulation reads for dogs, sharks and every future creature. Mirrors the
    /// <see cref="GarmentLibrary"/> pattern exactly:
    ///
    ///   • <see cref="Defaults"/> — the built-in table below. Engine-free, so
    ///     the headless soak harness and any non-Unity host run without assets.
    ///   • <see cref="Active"/> — what the sim actually reads. Starts as a copy
    ///     of the defaults; the Unity presentation layer overwrites entries at
    ///     startup from the per-mob <c>MobConfig</c> ScriptableObjects (via
    ///     MobTuning, mirroring how GarmentTuning feeds GarmentLibrary).
    ///
    /// Adding a new mob is ONE new entry here + one ScriptableObject asset — no
    /// new flat "xxxBiteDamage" fields sprinkled across the global tuning file.
    /// </summary>
    public static class MobCatalog
    {
        private static Dictionary<string, MobStats> _active;

        public static IReadOnlyDictionary<string, MobStats> Active => _active ??= BuildDefaults();

        // A fresh copy of the built-in table (never the live dictionary).
        public static IReadOnlyDictionary<string, MobStats> Defaults => BuildDefaults();

        /// <summary>The stats for a mob id — never null; an unknown id falls
        /// back to (and caches) a neutral default so callers can't NRE.</summary>
        public static MobStats For(string id)
        {
            _active ??= BuildDefaults();
            if (_active.TryGetValue(id, out var stats))
            {
                return stats;
            }

            var fallback = MobStats.NeutralDefault(id);
            _active[id] = fallback;
            return fallback;
        }

        /// <summary>Presentation-side override: replace one mob's entry from its
        /// asset. A null/empty id is ignored so a bad asset can't wipe a mob.</summary>
        public static void Override(MobStats stats)
        {
            if (stats == null || string.IsNullOrEmpty(stats.Id))
            {
                return;
            }

            _active ??= BuildDefaults();
            _active[stats.Id] = stats;
        }

        public static void ResetToDefaults()
        {
            _active = BuildDefaults();
        }

        private static Dictionary<string, MobStats> BuildDefaults()
        {
            return new Dictionary<string, MobStats>
            {
                // The wolf/pack-dog (§29C.3): the colony's core predator.
                [MobIds.Dog] = new MobStats
                {
                    Id = MobIds.Dog,
                    MaxHealth = 1.8f,             // doubled from 0.9 so the fight is readable
                    AttackDamage = 0.09f,           // per landed bite (timed, not per-tick)
                    CutFraction = 0.90f,
                    BloodLossMultiplier = 1.20f,
                    AttackWindupSeconds = 0.5f,   // bite charge-up; once started it always lands
                    AttackCooldownSeconds = 0.8f, // anim tail + recovery before the next bite
                    AggroRadiusTiles = 2,
                    RoamChance = 0.2f,
                    ChaseStepsPerTick = 3,        // junctions per medium tick while chasing (roam=1)
                    GlideSegmentSeconds = 1.0f,   // rendered-move ease over ~one medium period
                    GlideSnapDistance = 6.0f,     // teleport past this (spawn/save-load)
                    MeleeHoldDistance = 0.9f,     // rendered stand-off from the engaged quarry (wu)
                    AttackMediums = AttackMedium.Land, // §106: a swimmer is out of its reach
                    RaidChancePerDay = 0.08f,     // night pack-raid probability per day
                    RaidPackSize = 3,             // dogs per night raid
                },
                // The shark (§40.18): water-only, bites swimmers. Only AttackDamage
                // is live today; the rest are sensible placeholders.
                [MobIds.Shark] = new MobStats
                {
                    Id = MobIds.Shark,
                    MaxHealth = 1.0f,
                    AttackDamage = 0.2f,            // bite to the leg (also a sever trigger)
                    CutFraction = 1.00f,
                    BloodLossMultiplier = 1.40f,
                    AttackWindupSeconds = 0.1f,
                    AttackCooldownSeconds = 0.8f,
                    AggroRadiusTiles = 2,
                    RoamChance = 0.2f,
                    ChaseStepsPerTick = 1,
                    GlideSegmentSeconds = 1.0f,
                    GlideSnapDistance = 6.0f,
                    // A shark strikes from below the swimmer — no visible
                    // side-by-side stand-off to keep, so no clamp.
                    MeleeHoldDistance = 0f,
                    AttackMediums = AttackMedium.Water, // §106: bites swimmers only, inert ashore
                    RaidChancePerDay = 0f,
                    RaidPackSize = 0,
                },
            };
        }
    }

    /// <summary>§106: where a creature's attack works. A wolf bites on land and
    /// is dormant against a swimmer; a shark is the exact inverse. Flags so an
    /// amphibious predator (a croc, one day) is one value, not a new axis.
    /// Humans have no mob sheet — their Land-only medium is declared once in
    /// <c>CombatMedium.NpcMelee</c>, not here.</summary>
    [System.Flags]
    public enum AttackMedium
    {
        None = 0,
        Land = 1,
        Water = 2,
        Amphibious = Land | Water,
    }

    /// <summary>Known mob ids — the keys into <see cref="MobCatalog"/>. New mobs
    /// add a constant here and an entry in the catalog + a matching asset.</summary>
    public static class MobIds
    {
        public const string Dog = "dog";
        public const string Shark = "shark";
        // Wildlife with no combat sheet still identifies its VIEW config by
        // this id (crab.asset, when one exists).
        public const string Crab = "crab";
    }

    /// <summary>Everything tunable about one mob type. Plain mutable fields so
    /// the catalog override and per-frame test overrides both just assign.</summary>
    public sealed class MobStats
    {
        public string Id = string.Empty;

        // Combat.
        public float MaxHealth = 1.0f;
        public float AttackDamage = 0.09f;
        public float CutFraction = 0.90f;
        public float BloodLossMultiplier = 1.20f;
        public float AttackWindupSeconds = 0.1f;
        public float AttackCooldownSeconds = 0.8f;

        // Behaviour / movement.
        public int AggroRadiusTiles = 2;
        public float RoamChance = 0.2f;
        public int ChaseStepsPerTick = 1;
        public float GlideSegmentSeconds = 1.0f;
        public float GlideSnapDistance = 6.0f;

        // Combat spacing (§29C.3): the RENDERED glide never carries the mob
        // closer to its engaged quarry than this, so the pair squares up
        // face-to-face instead of standing inside each other. Melee reach is
        // junction-based and unaffected — junction spacing (~0.37 wu) is far
        // tighter than any model, which is exactly why this exists. 0 = off.
        public float MeleeHoldDistance = 0.9f;

        // §106: the medium this creature's attack works in. Land beasts are
        // dormant against a swimmer, water beasts against anyone ashore —
        // checked where the bite is gated, not where the swing runs.
        public AttackMedium AttackMediums = AttackMedium.Land;

        // Pack raid (a dog-pack director knob; 0 for lone creatures).
        public float RaidChancePerDay = 0f;
        public int RaidPackSize = 0;

        public static MobStats NeutralDefault(string id) => new() { Id = id };
    }
}
