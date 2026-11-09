using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
public static class InventoryPlacementMath
{
    public const string PlantedTorchVariant = "planted";

    public static bool CanPlace(string definitionId) => definitionId == ContentIds.Torch ||
        PlantGrowthCatalog.TryGetBySeed(definitionId, out _);

    private static readonly InteractionDefinition TorchPlacement = new()
    {
        Id = "place.torch", Type = InteractionType.Plant, DurationTicks = 24
    };

    internal static InteractionDefinition InteractionFor(WorldState world, string definitionId)
    {
        if (!world.Content.ObjectDefinitions.TryGetValue(definitionId, out var definition)) return null;
        if (definitionId == ContentIds.Torch) return TorchPlacement;
        foreach (var interaction in definition.Interactions)
            if (interaction.Type == InteractionType.Plant) return interaction;
        return null;
    }

    internal static WorldObjectState PlaceTorch(WorldState world, NPCState npc, ItemInstance item,
        JunctionId junction, TileCoord tile)
    {
        if (item.DefinitionId != ContentIds.Torch || item.Durability <= 0f ||
            !PlantGrowthSystem.CanPlantAt(world, junction, tile, npc.Id)) return null;
        var placed = ExecutionSystem.SpawnDroppedItem(world, npc, item, tile, junction);
        placed.Variant = PlantedTorchVariant;
        return placed;
    }
}
}
