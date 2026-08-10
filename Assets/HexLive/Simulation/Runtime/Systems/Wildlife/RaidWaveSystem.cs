using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{

// §72.14: one hostile survivor every three days. Runtime waves alternate
// female/male and become progressively more dangerous through weapon tier,
// innate attributes, armour and temperament. The authored opening outsider
// predates this sequence and keeps his established machete+knife loadout.
public sealed class RaidWaveSystem : ISimulationSystem
{
    private const int RuntimeRaiderIdBase = 1000;
    private const float AttributeGainPerWave = 0.04f;

    private static readonly string[] MaleArmor =
    {
        "TonnyFlash", "FCO Pants Male", "FCO Belt Male", "FCO Gloves Male",
        "FAO Harness Male", "FCO Boots Male", "FCO Legs Straps Male",
        "FCO Knee Straps Male", "FCO Waist Strappy Male",
    };

    // Both sets are slot-compatible. The seeded wave parity varies the look
    // without ever rolling beachwear.
    private static readonly string[][] FemaleArmor =
    {
        new[]
        {
            "underwear.panty_basic",
            "clothing.gloves_classic", "clothing.boots_cammy",
            "clothing.headdress_jaguar",
        },
        new[]
        {
            "underwear.panty_flair",
            "clothing.gloves_stars", "clothing.boots_classic",
            "clothing.cap_stars",
        },
    };

    public string Name => nameof(RaidWaveSystem);
    public TickLayer Layer => TickLayer.Medium;

    public void Run(WorldState world)
    {
        if (!Spec72.Enabled || Spec72.OutsiderCount <= 0 ||
            !world.FactionHomes.TryGetValue(Faction.Outsiders, out var home))
        {
            return;
        }

        // The promise to the player is "a new enemy on day 3, 6, 9…" — the DAY
        // is the calendar readout on screen, not the raw tick-day. Raw
        // tick / (3 * DayLengthTicks) fires a quarter-day into calendar day 4
        // (the calendar is 1-based and rolls at midnight, tick-days at 06:00),
        // which reads as "day three came and nobody arrived".
        var wavesDue = EnvironmentSystem.CalendarDay(world.Tick) /
            Spec72.RaidWaveIntervalDays;
        while (world.RaidWavesSpawned < wavesDue)
        {
            var wave = world.RaidWavesSpawned + 1;
            if (!SpawnWave(world, home, wave))
            {
                return; // retry next medium pass; never consume a failed wave
            }

            world.RaidWavesSpawned = wave;
        }
    }

    internal static string WeaponForWave(int wave) => wave switch
    {
        <= 0 => GearCatalog.Axe,
        1 => GearCatalog.Axe,
        2 => GearCatalog.Spear,
        _ => GearCatalog.Machete,
    };

    private static bool SpawnWave(WorldState world, TileCoord home, int wave)
    {
        var tile = PickLandingTile(world, home, wave);
        if (!world.Tiles.Items.TryGetValue(tile, out var landing))
        {
            return false;
        }

        var id = new EntityId(RuntimeRaiderIdBase + wave);
        if (world.Entities.Npcs.ContainsKey(id) || world.Entities.Corpses.ContainsKey(id))
        {
            // A completed spawn loaded from a save whose global counter lagged:
            // regard the fixed wave id as authoritative and advance safely.
            return true;
        }

        var female = (wave & 1) == 1;
        var look = female ? RollFemaleLook(world, id.Value) : default;
        var npc = new NPCState
        {
            Id = id,
            DisplayName = female ? look.NameId : "Kshishtof",
            ActorMesh = female ? look.Mesh : "Kshishtof",
            SkinSet = female ? look.SkinSet : string.Empty,
            EyeColor = female ? look.EyeColor : string.Empty,
            Hairstyle = female ? look.Hairstyle : string.Empty,
            VoiceBank = female ? look.VoiceBank : "kshishtof",
            Faction = Faction.Outsiders,
            Fragment = FragmentOf(world, landing),
            Tile = tile,
            Position = HexSpatialMath.TileToWorld(tile),
            CompassionTrait = System.Math.Max(0f,
                Spec72.OutsiderCompassionMax - wave * 0.04f),
        };

        // Lands ready to be a threat, not already starving/asleep. The ordinary
        // needs systems take over immediately after this authored arrival state.
        npc.Needs.Hunger = 0.25f;
        npc.Needs.Thirst = 0.25f;
        npc.Needs.Energy = 0.90f;
        npc.Needs.Comfort = 0.55f;
        npc.Needs.Social = 0.30f;
        npc.Needs.ThermalDiscomfort = 0.20f;

        ApplyWaveAttributes(npc, wave);
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Bottle));
        npc.Inventory.Items.Add(new ItemInstance(WeaponForWave(wave)));

        // §116: one deterministic late-wave loot roll. A runtime wave has one
        // raider today, so this also enforces the promised maximum of one
        // complete mechanical limb per wave without a second global latch.
        if (Spec118.Enabled && Spec118.ProstheticsEnabled &&
            wave >= Spec118.MechanicalLootMinWave)
        {
            if (MathUtil.Hash01(world.Seed, wave, 116, 1201) <
                Spec118.MechanicalProstheticDropChance)
            {
                var prosthetic = MathUtil.Hash01(world.Seed, wave, 116, 1202) < 0.5f
                    ? ContentIds.MechanicalArm
                    : ContentIds.MechanicalLeg;
                npc.Inventory.Items.Add(new ItemInstance(prosthetic));
            }

            if (MathUtil.Hash01(world.Seed, wave, 116, 2501) <
                Spec118.MechanicalPartsDropChance)
            {
                npc.Inventory.Items.Add(new ItemInstance(ContentIds.MechanicalPart));
            }
        }

        var armor = female ? FemaleArmor[wave % FemaleArmor.Length] : MaleArmor;
        EquipKnownArmor(world, npc, armor);
        if (female)
        {
            RaidSpawnWardrobe.EquipFemale(world, npc);
        }

        world.Entities.Npcs[id] = npc;
        AddToSpatialIndexes(world, npc);
        EquipmentMath.Recalculate(world, npc);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, id, "RaidWaveSpawned",
                $"Wave={wave} Sex={(female ? "Female" : "Male")} " +
                $"Weapon={WeaponForWave(wave)} Armor={npc.EquippedArmor:F2} " +
                $"Strength={npc.Attributes.Strength:F2} Tile={tile.Q},{tile.R}");
        }
        return true;
    }

    // A raid is authored from a fixed kit, but its content table may be
    // supplied by an older remote build or a partially updated external
    // wardrobe. Never put an id the current world cannot describe into an NPC:
    // it becomes an "unknown item" in the inspector and cannot be rendered or
    // looted consistently. The valid pieces keep their ordinary sim inventory
    // path; a bad content entry is visible in the trace and is retried only by
    // fixing the content, never by creating a ghost item.
    private static void EquipKnownArmor(
        WorldState world,
        NPCState npc,
        IEnumerable<string> armor)
    {
        foreach (var piece in armor)
        {
            if (string.IsNullOrEmpty(piece) ||
                !world.Content.ObjectDefinitions.TryGetValue(piece, out var definition) ||
                !definition.Tags.Contains("Clothing"))
            {
                if (SimTrace.Enabled)
                {
                    Trace.Debug(world, npc.Id, "RaidWaveArmorSkipped",
                        $"Item={piece ?? "<null>"} missing-or-not-clothing");
                }
                continue;
            }

            npc.WornItems.Add(piece);
        }
    }

    private static void ApplyWaveAttributes(NPCState npc, int wave)
    {
        var gain = wave * AttributeGainPerWave;
        npc.Attributes.Strength = MathUtil.Clamp01(Spec72.OutsiderStrength + gain);
        npc.Attributes.Agility = MathUtil.Clamp01(Spec72.OutsiderAgility + gain * 0.5f);
        npc.Attributes.Endurance = MathUtil.Clamp01(Spec72.OutsiderEndurance + gain);
        npc.Attributes.Toughness = MathUtil.Clamp01(Spec72.OutsiderToughness + gain);
        npc.Attributes.Hardiness = MathUtil.Clamp01(Spec72.OutsiderHardiness + gain);
        npc.Attributes.Wits = MathUtil.Clamp01(Spec72.OutsiderWits + gain * 0.5f);
        npc.Skills.Combat = MathUtil.Clamp01(wave * 0.12f);
    }

    private static ColonistAppearance.Look RollFemaleLook(WorldState world, int id)
    {
        var names = new HashSet<string>();
        var looks = new HashSet<string>();
        var hair = new HashSet<string>();
        void Take(NPCState npc)
        {
            if (!string.IsNullOrEmpty(npc.DisplayName)) names.Add(npc.DisplayName);
            if (!string.IsNullOrEmpty(npc.Hairstyle)) hair.Add(npc.Hairstyle);
            if (!string.IsNullOrEmpty(npc.ActorMesh))
                looks.Add(ColonistAppearance.LookKey(npc.ActorMesh, npc.SkinSet, npc.Hairstyle));
        }

        foreach (var npc in world.Entities.Npcs.Values) Take(npc);
        foreach (var npc in world.Entities.Corpses.Values) Take(npc);
        return ColonistAppearance.Roll(world.Seed, id, names, looks, hair);
    }

    private static TileCoord PickLandingTile(WorldState world, TileCoord home, int wave)
    {
        var candidates = new List<TileCoord>();
        foreach (var pair in world.Tiles.Items)
        {
            var tile = pair.Value;
            if (HexSpatialMath.HexDistance(pair.Key, home) <= 1 &&
                tile.Flags.HasFlag(TileFlags.Walkable) &&
                !tile.Flags.HasFlag(TileFlags.Blocked) &&
                !tile.Flags.HasFlag(TileFlags.Water))
            {
                candidates.Add(pair.Key);
            }
        }

        if (candidates.Count == 0) return home;
        candidates.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
        var index = (int)(MathUtil.Hash01(world.Seed, wave, 72, 7213) * candidates.Count);
        return candidates[index % candidates.Count];
    }

    private static FragmentId FragmentOf(WorldState world, Tile tile)
    {
        foreach (var junctionId in tile.Junctions)
        {
            if (world.Junctions.Items.TryGetValue(junctionId, out var junction))
                return junction.Fragment;
        }

        return new FragmentId(1);
    }

    private static void AddToSpatialIndexes(WorldState world, NPCState npc)
    {
        if (!world.Occupancy.EntitiesInTile.TryGetValue(npc.Tile, out var occupied))
        {
            occupied = new List<EntityId>();
            world.Occupancy.EntitiesInTile[npc.Tile] = occupied;
        }
        occupied.Add(npc.Id);

        if (!world.Caches.EntitiesByTile.TryGetValue(npc.Tile, out var byTile))
        {
            byTile = new List<EntityId>();
            world.Caches.EntitiesByTile[npc.Tile] = byTile;
        }
        byTile.Add(npc.Id);

        if (!world.Caches.EntitiesByFragment.TryGetValue(npc.Fragment, out var byFragment))
        {
            byFragment = new List<EntityId>();
            world.Caches.EntitiesByFragment[npc.Fragment] = byFragment;
        }
        byFragment.Add(npc.Id);
    }
}

}
