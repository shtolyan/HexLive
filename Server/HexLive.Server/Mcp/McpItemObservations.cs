using System;
using System.Collections.Generic;
using System.Linq;
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
            if (world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId, out var definition))
                row["interactions"] = definition.Interactions.Select(interaction =>
                {
                    var groups = RequiredToolGroups(definition, interaction);
                    return new
                    {
                        id = interaction.Id, type = interaction.Type.ToString(), baseDurationTicks = interaction.DurationTicks,
                        requiresAnyCapability = groups.Count == 1 ? groups[0].Select(c => c.ToString()).ToArray() : Array.Empty<string>(),
                        requiresAllCapabilityGroups = groups.Select(g => g.Select(c => c.ToString()).ToArray()).ToArray(),
                        toolRequirementMet = groups.Count == 0 || observer.Body.HasUsableHand &&
                            groups.All(g => g.Any(c => GearCatalog.HasCapability(observer.Inventory.Items, c))),
                        yields = interaction.Yields.Select(y => new { definitionId = y.DefinitionId, count = y.Count }).ToArray()
                    };
                }).ToArray();
            if (!string.IsNullOrEmpty(item.BuildProduct))
                row["construction"] = new { product = item.BuildProduct, needsHammer = BuildSiteView.NeedsHammer(world, item),
                    materials = McpPlanningObservations.BuildMaterials(item) };
            Clothing(row, world, observer, item.DefinitionId, item.Durability);
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

    // ExecutionSystem retains these legacy gates even when older world data has
    // no RequiredCapabilities. Empty data must not advertise tool-free mining.
    private static List<GearCapability[]> RequiredToolGroups(ObjectDefinition definition, InteractionDefinition interaction)
    {
        var groups = new List<GearCapability[]>();
        if (interaction.RequiredCapabilities.Count > 0) groups.Add(interaction.RequiredCapabilities.ToArray());
        GearCapability? legacy = interaction.Type switch
        {
            InteractionType.Harvest when !definition.HasTag("HerbBush") => definition.HasTag("Boulder")
                ? GearCapability.Mine : definition.HasTag("Yucca") ? GearCapability.Cut : GearCapability.ChopWood,
            InteractionType.Process when groups.Count == 0 => definition.HasTag("Coconut") ? GearCapability.Cut : GearCapability.ChopWood,
            InteractionType.Butcher when groups.Count == 0 => GearCapability.Butcher,
            _ => null
        };
        if (legacy is { } capability && !groups.Any(g => g.Length == 1 && g[0] == capability)) groups.Add([capability]);
        return groups;
    }

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
            Clothing(row, world, observer, item.DefinitionId, item.Durability);
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
            ["capabilities"] = GearCatalog.Active.TryGetValue(definitionId, out var gear) ? gear.Capabilities.ToString() : "None",
            ["inventoryCapacityWhenWorn"] = world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition)
                ? definition.InventoryCapacity : 0,
            ["ownerNpcId"] = ownerId == 0 ? null : ownerId,
            ["ownerAlive"] = ownerId == 0 ? null : ownerAlive,
            ["ownerFaction"] = faction?.ToString(),
            ["sameCamp"] = faction.HasValue ? faction.Value == observer.Faction : null,
        };
    }

    // Same canonical taste and rounded condition shown by CharacterPanel.
    // Uses each already visited physical instance, never an aggregate by definition.
    private static void Clothing(Dictionary<string, object?> row, WorldState world,
        NPCState observer, string definitionId, float durability)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition) ||
            !definition.Layer.HasValue) return;
        var condition = Math.Clamp(durability, 0f, 1f);
        row["likingPercent"] = (int)MathF.Round(ItemAffinity.For(observer.Id.Value, definitionId) * 100f);
        row["durability"] = condition;
        row["conditionPercent"] = (int)MathF.Round(condition * 100f);
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
