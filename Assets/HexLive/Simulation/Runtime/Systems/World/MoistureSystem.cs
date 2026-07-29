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

// Spec 35.5: wetting and drying for every item instance in the world —
// worn, carried, and wearables lying on the ground (incl. the rack).
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
            var indoor = world.Tiles.Items.TryGetValue(npc.Tile, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Indoor);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            var touchesWater = onWater || world.Environment.IsRaining && !indoor;
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

            // Spec 35.6: worn cloth loses durability every game-day (150 slow
            // ticks). Visual tearing now starts later, so the HP bar and cloth
            // condition read closer together.
            _wornOutScratch.Clear();
            foreach (var item in npc.WornItems)
            {
                item.Durability -= SimBalance.ClothingPassiveWearPerDay / 150f;
                if (item.Durability <= 0f)
                {
                    _wornOutScratch.Add(item);
                }
            }

            EquipmentMath.DestroyWornItems(world, npc, _wornOutScratch);
            EquipmentMath.Recalculate(world, npc);
        }

        // Ground wearables: rained on outdoors, dry otherwise; x5 on the rack.
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (!world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                definition.Layer is null)
            {
                continue;
            }

            var indoor = world.Tiles.Items.TryGetValue(obj.Tile, out var tile) &&
                tile.Flags.HasFlag(TileFlags.Indoor);
            var onWater = tile is not null && tile.Flags.HasFlag(TileFlags.Water);
            if (onWater || world.Environment.IsRaining && !indoor)
            {
                obj.Wetness = 1f;
                continue;
            }

            var onRack = IsOnRack(world, obj);
            obj.Wetness = System.MathF.Max(0f,
                obj.Wetness - DryBase * DryMultiplier(world, obj.Tile, indoor, onRack));
        }
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
                    Trace.Emit(world, npc.Id, "SoakedThrough",
                        $"{item.DefinitionId} Wetness={item.Wetness:F2}");
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
            if (obj.DefinitionId == "campfire.spot" && obj.ResourceAmount > 0f &&
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
            if (obj.DefinitionId == "station.drying_rack" && obj.Junctions.Count > 0 &&
                obj.Junctions[0].Equals(item.Junctions[0]))
            {
                return true;
            }
        }

        return false;
    }
}

}
