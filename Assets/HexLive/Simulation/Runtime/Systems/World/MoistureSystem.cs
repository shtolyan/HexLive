using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec 35.5 / §120: wetting and drying for every item instance in the world —
// body, exposed worn items, protected carried clothing, and every loose pickup
// lying on the ground.
public sealed class MoistureSystem : ISimulationSystem
{
    public string Name => nameof(MoistureSystem);

    public TickLayer Layer => TickLayer.Slow;

    public ChunkPolicy ChunkPolicy => ChunkPolicy.PerChunk;

    private static readonly System.Collections.Generic.List<ItemInstance> _wornOutScratch = new();

    private static float DryBase => WorldBalance.MoistureDryBase;

    public void Run(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            var indoor = ShelterMath.IsIndoor(world, npc.Tile);
            world.Tiles.Items.TryGetValue(npc.Tile, out var tile);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            var touchesWater = onWater || ShelterMath.RainReaches(world, npc.Tile);
            var bodyDryRate = DryBase * DryMultiplier(
                world, npc.Tile, indoor, stationBoost: 0f, naturalMultiplier: 1f);
            var ordinaryItemDryRate = bodyDryRate;
            var clothingDryRate = DryBase * DryMultiplier(
                world, npc.Tile, indoor, stationBoost: 0f,
                naturalMultiplier: WorldBalance.ClothingNaturalDryMultiplier);

            // Spec 35.5: the body soaks too — rain/water wet the skin directly,
            // so a naked or near-naked survivor still reads as soaked even with
            // no wet garment. Snap wet on exposure, dry gradually like an item.
            if (touchesWater)
            {
                npc.BodyWetness = 1f;
            }
            else
            {
                npc.BodyWetness = System.MathF.Max(0f, npc.BodyWetness - bodyDryRate);
            }

            UpdateItems(world, npc, npc.WornItems, touchesWater,
                ordinaryItemDryRate, clothingDryRate, worn: true);
            UpdateItems(world, npc, npc.Inventory.Items, touchesWater,
                ordinaryItemDryRate, clothingDryRate, worn: false);

            // Spec 35.6: worn cloth loses ClothingPassiveWearPerDay every 150
            // slow ticks (= 2400 ticks = 10 real minutes). The 150 is a
            // per-slow-tick divisor, NOT the day length — it deliberately does
            // not follow the (10x stretched) visual clock, so the real-time
            // wear rate is unchanged. Visual tearing now starts later, so the
            // HP bar and cloth condition read closer together.
            _wornOutScratch.Clear();
            foreach (var item in npc.WornItems)
            {
                // §52.8: gear (the tool holster) is leather and buckles — it
                // ages ~50x slower than cloth and is meant to be a keeper.
                item.Durability -= SimBalance.ClothingPassiveWearPerDay / 150f *
                    HolsterCatalog.WearMultiplier(item.DefinitionId);
                if (item.Durability <= 0f)
                {
                    _wornOutScratch.Add(item);
                }
            }

            EquipmentMath.DestroyWornItems(world, npc, _wornOutScratch);
            EquipmentMath.Recalculate(world, npc);
        }

        // Every loose pickup can get wet on the ground — boards, sticks, food,
        // tools and clothing all obey the same roof. Structures themselves do
        // not acquire item wetness. The drying-rack boost still applies only
        // when a wearable is actually hung there.
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !IsLoosePickup(definition))
            {
                continue;
            }

            if (!ChunkMath.IsAwake(world, obj.Tile))
            {
                continue;
            }

            var indoor = ShelterMath.IsIndoor(world, obj.Tile);
            world.Tiles.Items.TryGetValue(obj.Tile, out var tile);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            var naturalMultiplier = definition.Layer is not null
                ? WorldBalance.ClothingNaturalDryMultiplier
                : 1f;

            // §156.4: за проспанное вещь ТОЛЬКО сохнет, и только базовой
            // ставкой — сушилка, костёр и солнце это взаимодействие с соседями,
            // от которого в спящем чанке мы отказались осознанно. Промежуточные
            // дожди следа не оставляют: актуальный вернёт единицу строкой ниже.
            // Ошибка невидима — базовой ставки хватает высушить всё за ~800
            // тиков, а любой содержательный сон длиннее.
            var sleptSlowTicks = ChunkMath.SleptSlowTicks(world, obj.Tile);
            if (sleptSlowTicks > 0)
            {
                obj.Wetness = System.MathF.Max(
                    0f, obj.Wetness - DryBase * naturalMultiplier * sleptSlowTicks);
            }

            if (onWater || ShelterMath.RainReaches(world, obj.Tile))
            {
                obj.Wetness = 1f;
                continue;
            }

            var stationBoost = StationDryMultiplier(world, obj);
            obj.Wetness = System.MathF.Max(0f,
                obj.Wetness - DryBase * DryMultiplier(
                    world, obj.Tile, indoor, stationBoost, naturalMultiplier));
        }
    }

    private static bool IsLoosePickup(ObjectDefinition definition)
    {
        if (definition.Layer is not null ||
            definition.HasTag(ObjectTags.Resource) ||
            definition.HasTag(ObjectTags.Food) ||
            definition.HasTag(ObjectTags.Tool) ||
            definition.HasTag(ObjectTags.Weapon) ||
            definition.HasTag(ObjectTags.Medicine))
        {
            return true;
        }

        foreach (var interaction in definition.Interactions)
        {
            if (interaction.Type == InteractionType.PickUp) return true;
        }

        return false;
    }

    private static void UpdateItems(
        WorldState world, NPCState npc,
        System.Collections.Generic.List<ItemInstance> items,
        bool touchesWater, float ordinaryDryRate, float clothingDryRate, bool worn)
    {
        foreach (var item in items)
        {
            var clothing = IsClothing(world, item.DefinitionId);
            // §35.5 r2: Inventory.Items is the shared pack behind the derived
            // backpack/garment-pocket layout (§52). Water stops at that outer
            // worn shell: the backpack or even the panties can soak, but a
            // jacket stowed inside does not soak through. Carried non-clothing
            // keeps its established item wetness behaviour.
            var getsWetter = touchesWater && (worn || !clothing);
            if (getsWetter)
            {
                var before = item.Wetness;
                item.Wetness = 1f;
                if (worn && before <= 0.5f && item.Wetness > 0.5f)
                {
                    if (SimTrace.Enabled)
                    {
                        Trace.Debug(world, npc.Id, "SoakedThrough",
                            $"{item.DefinitionId} Wetness={item.Wetness:F2}");
                    }
                }
            }
            else
            {
                var dryRate = clothing
                    ? clothingDryRate
                    : ordinaryDryRate;
                item.Wetness = System.MathF.Max(0f, item.Wetness - dryRate);
            }
        }
    }

    private static bool IsClothing(WorldState world, string definitionId) =>
        world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) &&
        definition.Layer is not null;

    // Spec 35.5: clothing's ordinary world rate is ×0.1 (sun ×0.3), while
    // lit campfire ×4 and rack ×5 remain absolute multipliers of DryBase.
    // Body/non-clothing pass naturalMultiplier=1 and keep the old rates.
    // §133: гардероб приходит сюда своим множителем (×5 при живом очаге,
    // ×1.5 при потухшем), поэтому станция передаётся числом, а не флагом.
    private static float DryMultiplier(
        WorldState world, TileCoord tile, bool indoor,
        float stationBoost, float naturalMultiplier)
    {
        var best = naturalMultiplier;
        if (stationBoost > 0f)
        {
            best = stationBoost;
        }
        else if (NearLitCampfire(world, tile))
        {
            best = 4f;
        }

        var sunMultiplier = 3f * naturalMultiplier;
        if (best < sunMultiplier && !indoor && world.Environment.UvIndex > 0.3f &&
            !TemperatureSystem.IsShaded(world, tile))
        {
            best = sunMultiplier;
        }

        return best;
    }

    // PERF (Aug-2026): все три вопроса ниже — радиус ≤1 тайла, и отвечает на
    // них ObjectsByTile (образец: TemperatureSystem, WaterCollectorMath).
    // Старый полный перебор Entities.Objects звался на КАЖДЫЙ лежащий предмет —
    // ~1.8 млн итераций за slow-тик на большом острове (24 мс замером).
    private static bool NearLitCampfire(WorldState world, TileCoord tile)
    {
        for (var dq = -1; dq <= 1; dq++)
        {
            var lo = System.Math.Max(-1, -dq - 1);
            var hi = System.Math.Min(1, -dq + 1);
            for (var dr = lo; dr <= hi; dr++)
            {
                var coord = new TileCoord(tile.Q + dq, tile.R + dr);
                if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var onTile))
                {
                    continue;
                }

                foreach (var id in onTile)
                {
                    if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                        obj.DefinitionId == ContentIds.Campfire && obj.ResourceAmount > 0f)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// На какой сушильной станции лежит вещь: 0 — ни на какой, ×5 — уличная
    /// сушилка (§35.5B), гардероб (§133) — ×5 при горящем очаге в том же доме и
    /// ×1.5 при потухшем: тепло даёт очаг, крыша уже отсекает дождь.
    /// </summary>
    private static float StationDryMultiplier(WorldState world, WorldObjectState item)
    {
        if (item.Junctions.Count == 0)
        {
            return 0f;
        }

        // Соседи по джанкшену стоят только на тайлах этого джанкшена — их у
        // узла не больше трёх, и все они в индексе.
        var anchor = item.Junctions[0];
        if (!world.Junctions.Items.TryGetValue(anchor, out var junction))
        {
            return 0f;
        }

        foreach (var coord in junction.Tiles)
        {
            if (!world.Caches.ObjectsByTile.TryGetValue(coord, out var onTile))
            {
                continue;
            }

            foreach (var id in onTile)
            {
                if (!world.Entities.Objects.TryGetValue(id, out var obj) ||
                    obj.Junctions.Count == 0 || !obj.Junctions[0].Equals(anchor))
                {
                    continue;
                }

                if (obj.DefinitionId == ContentIds.DryingRack)
                {
                    return 5f;
                }

                if (world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var def) &&
                    def.HasTag(ObjectTags.Wardrobe))
                {
                    return HearthLitOn(world, obj.Tile)
                        ? Spec133.WardrobeDryMultiplierLit
                        : Spec133.WardrobeDryMultiplierUnlit;
                }
            }
        }

        return 0f;
    }

    private static bool HearthLitOn(WorldState world, TileCoord tile)
    {
        if (!world.Caches.ObjectsByTile.TryGetValue(tile, out var onTile))
        {
            return false;
        }

        foreach (var id in onTile)
        {
            if (world.Entities.Objects.TryGetValue(id, out var obj) &&
                obj.DefinitionId == ContentIds.Campfire && obj.ResourceAmount > 0f)
            {
                return true;
            }
        }

        return false;
    }
}

}
