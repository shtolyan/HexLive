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

    // §157.7: id островных чужаков — полоса на лагерь. 1000+ занято общими
    // волнами, 2000..5499 — недельными прибытиями (500 на лагерь), поэтому
    // база 6000 и шаг 400: Colony → 6001.., Colony6 → 8001...
    internal const int IslandRaiderIdBase = 6000;
    internal const int IslandRaiderIdStride = 400;
    // Preserve all shipped early-wave ids. Later waves use a separate band
    // with faction in the low bits, so crossing wave400 cannot impersonate
    // the next camp or disappear from the camp's living-cap calculation.
    private const int ExtendedIslandRaiderIdBase = 100000;
    private const int ExtendedIslandRaiderIdStride = 8;
    internal const int IslandShoreMinDistanceTiles = 8;
    internal const int IslandShoreMaxDistanceTiles = 30;

    private static readonly List<Faction> _islandScratch = new();

    public string Name => nameof(RaidWaveSystem);
    public TickLayer Layer => TickLayer.Medium;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.Global;

    private static int Interval(WorldState world) => world.CreationConfig?.PlayersOnly == true
        ? world.CreationConfig.PlayerHostileRaidIntervalDays
        : world.CreationConfig == null
        ? Spec72.RaidWaveIntervalDays
        : world.Mode == Bootstrap.GameMode.Islands ? world.CreationConfig.SeaRaidIntervalDays
        : world.CreationConfig.Camp(Faction.Outsiders)?.ArrivalIntervalDays ?? 0;

    public void Run(WorldState world)
    {
        var interval = Interval(world);
        if ((world.CreationConfig == null && (!Spec72.Enabled || Spec72.OutsiderCount <= 0)) || interval <= 0)
        {
            return;
        }

        // §157.7: в «Островах» стоянки чужаков нет — по одному с моря на
        // каждый остров, и только пока предыдущий мёртв.
        if (world.Mode == Bootstrap.GameMode.Islands || world.CreationConfig?.PlayersOnly == true)
        {
            RunIslands(world);
            return;
        }

        if (!world.FactionHomes.TryGetValue(Faction.Outsiders, out var home))
        {
            return;
        }

        // The promise to the player is "a new enemy on day 3, 6, 9…" — the DAY
        // is the calendar readout on screen, not the raw tick-day. Raw
        // tick / (3 * DayLengthTicks) fires a quarter-day into calendar day 4
        // (the calendar is 1-based and rolls at midnight, tick-days at 06:00),
        // which reads as "day three came and nobody arrived".
        var wavesDue = EnvironmentSystem.CalendarDay(world.Tick) /
            Interval(world);
        if (world.CreationConfig?.PlayersOnly == true && !HasInhabitedPlayerCamp(world))
        {
            world.RaidWavesSpawned = wavesDue;
            return;
        }
        while (world.RaidWavesSpawned < wavesDue)
        {
            var wave = world.RaidWavesSpawned + 1;

            // §132: a full camp consumes this scheduled wave. A later death
            // opens a seat for the NEXT boundary; it must not unleash every
            // missed attacker immediately from a hidden backlog.
            var hasRoom = world.CreationConfig?.PlayersOnly == true
                ? world.Entities.Npcs.Count < world.CreationConfig.PopulationLimit &&
                  PopulationArrivalMath.CampResidents(world, Faction.Outsiders) < world.CreationConfig.PlayerHostileCampLimit
                : PopulationArrivalMath.HasRoom(world, Faction.Outsiders);
            if (!hasRoom)
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

    private static bool HasInhabitedPlayerCamp(WorldState world)
    {
        foreach (var faction in world.FactionHomes.Keys)
            if (FactionRelations.IsGirlCamp(faction) &&
                world.CreationConfig.Camp(faction)?.Enabled == true &&
                PopulationArrivalMath.CampResidents(world, faction) > 0)
                return true;
        return false;
    }

    // §157.7: у каждого острова свой трёхдневный курсор. Живой чужак острова
    // съедает границу (второй не приходит, пока первый жив), полный мир —
    // тоже; отсутствие берега границу не съедает (повтор на следующем проходе).
    // Номер волны = индекс границы: чем дольше остров держится, тем сильнее
    // следующий, как в §72.14.
    private static void RunIslands(WorldState world)
    {
        _islandScratch.Clear();
        foreach (var faction in world.FactionHomes.Keys)
        {
            if (FactionRelations.IsGirlCamp(faction))
            {
                _islandScratch.Add(faction);
            }
        }
        _islandScratch.Sort((a, b) => ((int)a).CompareTo((int)b));

        var wavesDue = EnvironmentSystem.CalendarDay(world.Tick) / Interval(world);
        foreach (var faction in _islandScratch)
        {
            var home = world.FactionHomes[faction];
            var hasCursor = world.IslandOutsiderWavesByFaction.TryGetValue(faction, out var processed);
            if (!hasCursor && world.Mode != Bootstrap.GameMode.Islands &&
                world.CreationConfig?.PlayersOnly == true)
            {
                // The legacy shared camp cursor is already consumed history.
                // A late migration gets only the current due wave, never hundreds
                // of old arrivals or a day-three enemy on day three hundred.
                processed = System.Math.Max(world.RaidWavesSpawned, wavesDue - 1);
            }
            while (processed < wavesDue)
            {
                var wave = processed + 1;
                var camp = world.CreationConfig?.Camp(faction);
                var closed = world.CreationConfig != null && camp?.Enabled != true;
                var emptyPlayerCamp = world.CreationConfig?.PlayersOnly == true &&
                    PopulationArrivalMath.CampResidents(world, faction) == 0;
                var cap = world.CreationConfig?.PlayersOnly == true
                    ? world.CreationConfig.PlayerHostileCampLimit : 1;
                if (closed || emptyPlayerCamp || LivingIslandOutsiders(world, faction) >= cap ||
                    world.Entities.Npcs.Count >= (world.CreationConfig?.PopulationLimit ?? PopulationArrivalMath.MaxLivingNpcsFor(world.Mode)))
                {
                    processed = wave;
                    world.IslandOutsiderWavesByFaction[faction] = processed;
                    if (SimTrace.Enabled)
                    {
                        Trace.DebugSystem(world, "IslandOutsiderWaveSkipped",
                            $"Wave={wave} Island={faction} Alive={world.Entities.Npcs.Count}");
                    }
                    continue;
                }

                if (!SpawnIslandOutsider(world, faction, home, wave))
                {
                    // This camp keeps its unconsumed boundary for a safe retry;
                    // independent inhabited camps must still get their own turn.
                    break;
                }

                processed = wave;
                world.IslandOutsiderWavesByFaction[faction] = processed;
            }
        }
    }

    internal static EntityId IslandRaiderId(Faction island, int wave) =>
        wave < IslandRaiderIdStride
            ? new EntityId(IslandRaiderIdBase + (int)island * IslandRaiderIdStride + wave)
            : new EntityId(ExtendedIslandRaiderIdBase + wave * ExtendedIslandRaiderIdStride + (int)island);

    internal static bool WasLegacyRaider(WorldState world, EntityId npcId)
    {
        var id = npcId.Value;
        var globalWave = id - RuntimeRaiderIdBase;
        // The old global sequence overlaps the arrival ID band from 2000.
        // Only the exclusive part can identify a former raider by itself.
        if (globalWave is > 0 and < 1000 && globalWave <= world.RaidWavesSpawned)
            return true;

        foreach (var pair in world.IslandOutsiderWavesByFaction)
        {
            if (!FactionRelations.IsGirlCamp(pair.Key)) continue;
            var first = IslandRaiderId(pair.Key, 0).Value;
            var wave = id - first;
            if (wave is > 0 and < IslandRaiderIdStride && wave <= pair.Value)
                return true;

            var offset = id - ExtendedIslandRaiderIdBase - (int)pair.Key;
            if (offset < IslandRaiderIdStride * ExtendedIslandRaiderIdStride ||
                offset % ExtendedIslandRaiderIdStride != 0) continue;
            wave = offset / ExtendedIslandRaiderIdStride;
            if (id < Bootstrap.PlayerCharacterCreation.FirstNpcId && wave <= pair.Value)
                return true;
        }
        return false;
    }

    // «Мёртв» = его нет среди живых. Труп с тем же id границу не держит.
    internal static bool HasLivingIslandOutsider(WorldState world, Faction island)
        => LivingIslandOutsiders(world, island) > 0;

    private static int LivingIslandOutsiders(WorldState world, Faction island)
    {
        var count = 0;
        var from = IslandRaiderIdBase + (int)island * IslandRaiderIdStride;
        var to = from + IslandRaiderIdStride;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var id = npc.Id.Value;
            var legacy = id > from && id < to;
            var extended = id >= ExtendedIslandRaiderIdBase +
                    IslandRaiderIdStride * ExtendedIslandRaiderIdStride &&
                id < Bootstrap.PlayerCharacterCreation.FirstNpcId &&
                (id - ExtendedIslandRaiderIdBase) % ExtendedIslandRaiderIdStride == (int)island;
            if (npc.Faction == Faction.Outsiders && npc.Health > 0f && (legacy || extended))
            {
                count++;
            }
        }

        return count;
    }

    private static bool SpawnIslandOutsider(WorldState world, Faction island, TileCoord home, int wave)
    {
        var id = IslandRaiderId(island, wave);
        if (world.Entities.Npcs.ContainsKey(id) || world.Entities.Corpses.ContainsKey(id))
        {
            return true;
        }

        PopulationArrivalMath.Landing landing;
        var landed = world.Mode == Bootstrap.GameMode.Islands
            ? PopulationArrivalMath.TryPickShoreLanding(
                world, home, IslandShoreMinDistanceTiles, IslandShoreMaxDistanceTiles,
                sequence: wave * 10 + (int)island, salt: 15801, out landing)
            : PopulationArrivalMath.TryPickCampApproach(
                world, home, wave * 10 + (int)island, out landing);
        if (!landed) return false;

        BuildRaider(world, id, wave, lootSequence: wave * 10 + (int)island, landing);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, id, "IslandOutsiderSpawned",
                $"Wave={wave} Island={island} Tile={landing.Tile.Q},{landing.Tile.R} " +
                $"Junction={landing.Junction.Value}");
        }
        return true;
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

        BuildRaider(world, id, wave, lootSequence: wave, landing);
        return true;
    }

    // Тело волны после выбора посадки. §157.7 зовёт то же с островным id и
    // своей последовательностью лута; общие волны передают lootSequence = wave,
    // так что их хеши и порядок не сдвинулись ни на бит.
    private static void BuildRaider(
        WorldState world, EntityId id, int wave, int lootSequence,
        PopulationArrivalMath.Landing landing)
    {
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
            SpeechOrigin = NpcSpeechOrigin.Raider,
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
        npc.Inventory.Items.Add(new ItemInstance(ContentIds.LeatherFlask));
        npc.Inventory.Items.Add(new ItemInstance(WeaponForWave(wave)));

        // §116: one deterministic late-wave loot roll. A runtime wave has one
        // raider today, so this also enforces the promised maximum of one
        // complete mechanical limb per wave without a second global latch.
        if (Spec118.Enabled && Spec118.ProstheticsEnabled &&
            wave >= Spec118.MechanicalLootMinWave)
        {
            if (MathUtil.Hash01(world.Seed, lootSequence, 116, 1201) <
                Spec118.MechanicalProstheticDropChance)
            {
                var prosthetic = MathUtil.Hash01(world.Seed, lootSequence, 116, 1202) < 0.5f
                    ? ContentIds.MechanicalArm
                    : ContentIds.MechanicalLeg;
                npc.Inventory.Items.Add(new ItemInstance(prosthetic));
            }

            if (MathUtil.Hash01(world.Seed, lootSequence, 116, 2501) <
                Spec118.MechanicalPartsDropChance)
            {
                npc.Inventory.Items.Add(new ItemInstance(ContentIds.MechanicalPart));
            }
        }

        RaidSpawnWardrobe.EquipPrimal(world, npc);

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
