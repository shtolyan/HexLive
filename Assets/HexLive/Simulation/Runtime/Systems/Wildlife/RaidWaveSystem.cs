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
            Spec72.RaidWaveIntervalDays <= 0 ||
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

            // §132: a full camp consumes this scheduled wave. A later death
            // opens a seat for the NEXT boundary; it must not unleash every
            // missed attacker immediately from a hidden backlog.
            if (!PopulationArrivalMath.HasRoom(world, Faction.Outsiders))
            {
                world.RaidWavesSpawned = wave;
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "RaidWaveSkippedCapacity",
                        $"Wave={wave} Alive={world.Entities.Npcs.Count} " +
                        $"WorldCap={WorldBalance.MaxLivingNpcs} " +
                        $"OutsiderCap={WorldBalance.MaxOutsiderNpcs}");
                }
                continue;
            }

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
        if (!PopulationArrivalMath.TryPickLanding(
                world, home, wave, salt: 7213, out var landing))
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
        var look = female ? PopulationArrivalMath.RollFemaleLook(world, id.Value) : default;
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
            Fragment = landing.Fragment,
            Tile = landing.Tile,
            Position = landing.Position,
            CurrentJunction = landing.Junction,
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

        // §126: волна приходит с тем же характером, что и первый чужак —
        // авторски, не роллом. Этот спавн идёт МИМО WorldStateFactory, поэтому
        // ролл и переопределения бутстрапа сюда не доходят: без этих двух строк
        // прибывший молча перестал бы гнобить, а гейт §81 читал бы черту,
        // которой у него нет.
        npc.Traits.Add(Agents.TraitKind.Abuser);
        npc.Traits.Add(Agents.TraitKind.Slob);

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

        PopulationArrivalMath.AddToWorld(world, npc, landing.Junction);
        EquipmentMath.Recalculate(world, npc);

        if (SimTrace.Enabled)
        {
            Trace.Debug(world, id, "RaidWaveSpawned",
                $"Wave={wave} Sex={(female ? "Female" : "Male")} " +
                $"Weapon={WeaponForWave(wave)} Armor={npc.EquippedArmor:F2} " +
                $"Strength={npc.Attributes.Strength:F2} " +
                $"Tile={landing.Tile.Q},{landing.Tile.R} Junction={landing.Junction.Value}");
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
                !definition.HasTag("Clothing") ||
                !GarmentLibrary.IsSpawnable(piece))
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

}

}
