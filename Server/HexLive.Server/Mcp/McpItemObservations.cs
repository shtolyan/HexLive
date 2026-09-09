using System;
using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.Server.Mcp;

/// <summary>§133/§151: instance state that the aggregated object summary cannot preserve.</summary>
internal static class McpItemObservations
{
    public static List<object> Visible(WorldState world, NPCState observer)
    {
        var ids = new SortedSet<int>();
        foreach (var perceived in observer.Perception.Objects)
            if (!perceived.FromMemory) ids.Add(perceived.Id.Value);

        var rows = new List<object>();
        foreach (var id in ids)
        {
            if (!world.Entities.Objects.TryGetValue(new ObjectId(id), out var item)) continue;
            var row = Item(world, observer, item.DefinitionId, item.Owner?.Value ?? 0, item.OwnerFaction);
            row["objectId"] = id;
            if (item.DefinitionId == ContentIds.Bottle && WaterCollectorMath.IsParked(world, item))
            {
                // Collector fill is fractional; ordinary ground bottles already store sips.
                var sips = Math.Clamp(item.ResourceAmount, 0f, 1f) * SimBalance.BottleCapacity;
                Water(row, sips, WaterCollectorMath.ChargesIn(item), SimBalance.BottleCapacity,
                    item.WaterKind == WaterKind.None && sips > 0f ? WaterKind.Rain : item.WaterKind);
            }
            else
            {
                PortableWater(row, item.DefinitionId, item.ResourceAmount, item.WaterKind);
            }
            rows.Add(row);
        }
        return rows;
    }

    public static List<object> Carried(WorldState world, NPCState observer) =>
        Instances(world, observer, observer.Inventory.Items);

    public static List<object> Worn(WorldState world, NPCState observer) =>
        Instances(world, observer, observer.WornItems);

    private static List<object> Instances(WorldState world, NPCState observer, List<ItemInstance> items)
    {
        var rows = new List<object>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = Item(world, observer, item.DefinitionId, item.OwnerId, null);
            // This is the existing manual-command list index, valid only for this snapshot.
            row["sourceIndex"] = index;
            PortableWater(row, item.DefinitionId, item.ResourceAmount, item.WaterKind);
            rows.Add(row);
        }
        return rows;
    }

    private static Dictionary<string, object?> Item(
        WorldState world, NPCState observer, string definitionId, int ownerId, Faction? objectFaction)
    {
        world.Entities.Npcs.TryGetValue(new EntityId(ownerId), out var owner);
        var ownerAlive = owner != null && owner.Health > 0f;
        var faction = objectFaction ?? (ownerAlive ? owner!.Faction : (Faction?)null);
        return new Dictionary<string, object?>
        {
            ["itemId"] = definitionId,
            ["ownerNpcId"] = ownerId == 0 ? null : ownerId,
            ["ownerAlive"] = ownerId == 0 ? null : ownerAlive,
            ["ownerFaction"] = faction?.ToString(),
            ["sameCamp"] = faction.HasValue ? faction.Value == observer.Faction : null,
        };
    }

    private static void PortableWater(
        Dictionary<string, object?> row, string definitionId, float amount, WaterKind kind)
    {
        if (definitionId == ContentIds.Bottle)
        {
            var bottle = new ItemInstance(definitionId) { ResourceAmount = amount, WaterKind = kind };
            var sips = kind == WaterKind.None ? 0f : Math.Clamp(amount, 0f, SimBalance.BottleCapacity);
            Water(row, sips, BottleInventoryMath.Charges(bottle), SimBalance.BottleCapacity, kind);
        }
        else if (definitionId == ContentIds.CoconutPierced)
        {
            var sips = Math.Clamp(amount, 0f, SimBalance.CoconutWaterCapacity);
            Water(row, sips, (int)sips, SimBalance.CoconutWaterCapacity, WaterKind.Coconut);
        }
    }

    private static void Water(Dictionary<string, object?> row, float amount, int drinkable, int capacity, WaterKind kind)
    {
        row["waterKind"] = (amount > 0f ? kind : WaterKind.None).ToString();
        row["waterSips"] = amount;
        row["drinkableSips"] = Math.Clamp(drinkable, 0, capacity);
        row["capacitySips"] = capacity;
    }
}
