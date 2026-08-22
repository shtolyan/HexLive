using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §132: одна новая девушка в начале каждой N-й календарной даты.
/// Это полноценный NPCState: тот же сидированный облик, характеристики,
/// черты, одежда, инвентарь и GOAP, что у стартовой колонии.
/// </summary>
public sealed class ColonyArrivalSystem : ISimulationSystem
{
    private const int RuntimeColonistIdBase = 2000;

    public string Name => nameof(ColonyArrivalSystem);
    public TickLayer Layer => TickLayer.Medium;

    // §146.6: порядок обхода лагерей — ординал фракции (правило BedSiteSystem):
    // порядок словаря не смеет попадать в реплей.
    private static readonly System.Collections.Generic.List<Faction> _campScratch = new();

    public void Run(WorldState world)
    {
        var interval = WorldBalance.ColonyArrivalIntervalDays;
        if (interval <= 0)
        {
            return;
        }

        _campScratch.Clear();
        foreach (var faction in world.FactionHomes.Keys)
        {
            if (FactionRelations.IsColonyKind(faction))
            {
                _campScratch.Add(faction);
            }
        }

        _campScratch.Sort((a, b) => ((int)a).CompareTo((int)b));

        var opportunitiesDue = EnvironmentSystem.CalendarDay(world.Tick) / interval;
        foreach (var faction in _campScratch)
        {
            RunForCamp(world, faction, opportunitiesDue);
        }
    }

    // §146.6: у каждого лагеря СВОЯ лодка раз в неделю — курсоры независимы,
    // полный лагерь съедает свою неделю, не трогая чужие.
    private static void RunForCamp(WorldState world, Faction faction, int opportunitiesDue)
    {
        var home = world.FactionHomes[faction];
        world.ColonyArrivalsProcessedByFaction.TryGetValue(faction, out var processed);
        while (processed < opportunitiesDue)
        {
            var arrival = processed + 1;

            // A full camp consumes THIS week's boat. If a place opens tomorrow,
            // nobody materialises from a backlog: the next chance is the next
            // visible weekly boundary. This is the survival rhythm the player
            // can plan around, and mirrors the capped enemy schedule.
            if (!PopulationArrivalMath.HasRoom(world, faction))
            {
                processed = arrival;
                world.ColonyArrivalsProcessedByFaction[faction] = processed;
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "ColonyArrivalSkippedCapacity",
                        $"Arrival={arrival} Camp={faction} Alive={world.Entities.Npcs.Count} " +
                        $"WorldCap={PopulationArrivalMath.MaxLivingNpcsFor(world.Mode)} " +
                        $"CampCap={PopulationArrivalMath.MaxCampNpcsFor(world.Mode)}");
                }
                continue;
            }

            if (!TrySpawn(world, faction, home, arrival))
            {
                // No free landing point is not a consumed life event. Retry on
                // the next medium pass; a walking body may clear the point.
                return;
            }

            processed = arrival;
            world.ColonyArrivalsProcessedByFaction[faction] = processed;
        }
    }

    private static bool TrySpawn(WorldState world, Faction faction, TileCoord home, int arrival)
    {
        // Полоса в 500 id на лагерь: Colony остаётся на прежних 2000+ (сейвы
        // режима 0 не двигаются), Colony2 — 3000+, Colony3 — 3500+ и далее
        // полосами по 500 вплоть до Colony6. Рейдеры
        // живут на 1000+, стартовые девушки — на 1..22: пересечений нет.
        var id = new EntityId(RuntimeColonistIdBase + (int)faction * 500 + arrival);
        if (world.Entities.Npcs.ContainsKey(id) || world.Entities.Corpses.ContainsKey(id))
        {
            // Save counter lagged behind a completed arrival. The stable id is
            // authoritative; advancing the schedule must not clone or revive her.
            return true;
        }

        if (!PopulationArrivalMath.TryPickLanding(
                world, home, arrival, salt: 13201, out var landing))
        {
            return false;
        }

        var look = PopulationArrivalMath.RollFemaleLook(world, id.Value);
        var npc = new NPCState
        {
            Id = id,
            DisplayName = look.NameId,
            ActorMesh = look.Mesh,
            SkinSet = look.SkinSet,
            EyeColor = look.EyeColor,
            Hairstyle = look.Hairstyle,
            VoiceBank = look.VoiceBank,
            Faction = faction,
            Fragment = landing.Fragment,
            Tile = landing.Tile,
            Position = landing.Position,
            CurrentJunction = landing.Junction,
        };

        // She has survived the crossing, but does not arrive as a free crisis:
        // tired and uncomfortable enough to join the survival loop, not one tick
        // from coma/death. Afterwards the ordinary needs systems own her fully.
        npc.Needs.Hunger = 0.50f;
        npc.Needs.Thirst = 0.45f;
        npc.Needs.Energy = 0.65f;
        npc.Needs.Comfort = 0.30f;
        npc.Needs.Social = 0.45f;
        npc.Needs.ThermalDiscomfort = 0.40f;

        npc.CompassionTrait = Spec53.TraitMin +
            MathUtil.Hash01(world.Seed, id.Value, 53, 5301) *
            (Spec53.TraitMax - Spec53.TraitMin);
        AttributeMath.Roll(npc, world.Seed, id.Value);
        TraitMath.Roll(npc, world.Seed, id.Value);

        // The same castaway baseline as the authored women: a personal empty
        // bottle and light female clothes drawn from the live garment catalog.
        npc.Inventory.Items.Add(new ItemInstance(GearCatalog.Bottle));
        RaidSpawnWardrobe.EquipFemale(world, npc);
        EquipmentMath.Recalculate(world, npc);

        PopulationArrivalMath.AddToWorld(world, npc, landing.Junction);
        if (SimTrace.Enabled)
        {
            Trace.Debug(world, npc.Id, "ColonyArrivalSpawned",
                $"Arrival={arrival} Name={npc.DisplayName} " +
                $"Tile={npc.Tile.Q},{npc.Tile.R} Junction={landing.Junction.Value}");
        }

        return true;
    }
}

}
