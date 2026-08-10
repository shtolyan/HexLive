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
// body, worn/carried items, and every loose pickup lying on the ground.
public sealed class MoistureSystem : ISimulationSystem
{
    public string Name => nameof(MoistureSystem);

    public TickLayer Layer => TickLayer.Slow;

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
            var dryRate = DryBase * DryMultiplier(world, npc.Tile, indoor, rackBoost: false);

            // Spec 35.5: the body soaks too — rain/water wet the skin directly,
            // so a naked or near-naked survivor still reads as soaked even with
            // no wet garment. Snap wet on exposure, dry gradually like an item.
            if (touchesWater)
            {
                npc.BodyWetness = 1f;
            }
            else
            {
                npc.BodyWetness = System.MathF.Max(0f, npc.BodyWetness - dryRate);
            }

            UpdateItems(world, npc, npc.WornItems, touchesWater, dryRate, worn: true);
            UpdateItems(world, npc, npc.Inventory.Items, touchesWater, dryRate, worn: false);

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

            var indoor = ShelterMath.IsIndoor(world, obj.Tile);
            world.Tiles.Items.TryGetValue(obj.Tile, out var tile);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            if (onWater || ShelterMath.RainReaches(world, obj.Tile))
            {
                obj.Wetness = 1f;
                continue;
            }

            var onRack = IsOnRack(world, obj);
            obj.Wetness = System.MathF.Max(0f,
                obj.Wetness - DryBase * DryMultiplier(world, obj.Tile, indoor, onRack));
        }
    }

    private static bool IsLoosePickup(ObjectDefinition definition)
    {
        if (definition.Layer is not null ||
            definition.Tags.Contains(ObjectTags.Resource) ||
            definition.Tags.Contains(ObjectTags.Food) ||
            definition.Tags.Contains(ObjectTags.Tool) ||
            definition.Tags.Contains(ObjectTags.Weapon) ||
            definition.Tags.Contains(ObjectTags.Medicine))
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
        bool touchesWater, float dryRate, bool worn)
    {
        foreach (var item in items)
        {
            if (touchesWater)
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
                item.Wetness = System.MathF.Max(0f, item.Wetness - dryRate);
            }
        }
    }

    // Spec 35.5: best of sun x3 / lit campfire x4 / rack x5, else x1.
    private static float DryMultiplier(WorldState world, TileCoord tile, bool indoor, bool rackBoost)
    {
        var best = 1f;
        if (rackBoost)
        {
            best = 5f;
        }
        else if (NearLitCampfire(world, tile))
        {
            best = 4f;
        }

        if (best < 3f && !indoor && world.Environment.UvIndex > 0.3f &&
            !TemperatureSystem.IsShaded(world, tile))
        {
            best = 3f;
        }

        return best;
    }

    private static bool NearLitCampfire(WorldState world, TileCoord tile)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.Campfire && obj.ResourceAmount > 0f &&
                HexSpatialMath.HexDistance(tile, obj.Tile) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnRack(WorldState world, WorldObjectState item)
    {
        if (item.Junctions.Count == 0)
        {
            return false;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId == ContentIds.DryingRack && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(item.Junctions[0]))
            {
                return true;
            }
        }

        return false;
    }
}

}
