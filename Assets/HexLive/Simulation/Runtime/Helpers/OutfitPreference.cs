using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

internal static class OutfitPreference
{
    internal static bool IsPuttingOnHuntArmor(NPCState npc) =>
        npc.Plan.Status == PlanStatus.Active && npc.Plan.Goal == GoalType.Hunt &&
        npc.Plan.Steps.Count > 0 && npc.Plan.Steps[^1].Interaction == InteractionType.Dress;

    internal static bool CanChooseForTaste(NPCState npc) =>
        !npc.IsFighting && !npc.IsCarryingPerson && !npc.IsBeingCarried &&
        npc.Mind.CurrentGoal is not (GoalType.Hunt or GoalType.GroupHunt) &&
        npc.Memory.Dangers.Count == 0 && npc.Mind.RedressGarments.Count == 0 &&
        npc.Needs.Hunger < 0.6f && npc.Needs.Thirst < 0.6f &&
        npc.Needs.ThermalDiscomfort < Spec133.UndressAtHomeMaxDiscomfort;

    internal static bool IsPreferred(WorldState world, NPCState npc, string id,
        float durability, float wetness, float forecastOutdoorTemperature)
    {
        if (durability <= 0f || wetness > 0.05f ||
            !GarmentLibrary.FitsSex(npc.Sex, id) ||
            !world.Content.ObjectDefinitions.TryGetValue(id, out var candidate) ||
            candidate.Layer is null or WearLayer.Bags)
        {
            return false;
        }

        var warmthGain = EquipmentMath.WarmthGainFromWearing(world, npc, id, wetness);
        var futureWithClothes = forecastOutdoorTemperature + (npc.EquippedWarmth + warmthGain) * 10f;
        var currentWithClothes = world.Environment.GlobalTemperature + (npc.EquippedWarmth + warmthGain) * 10f;
        if (futureWithClothes < SimBalance.DressColdTemp ||
            (warmthGain > 0f && currentWithClothes > 24f))
        {
            return false;
        }

        var affinity = ItemAffinity.For(npc.Id.Value, id);
        foreach (var worn in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var definition) &&
                WearSlotCatalog.Occupies(candidate, definition) &&
                affinity <= ItemAffinity.For(npc.Id.Value, worn.DefinitionId))
            {
                return false;
            }
        }

        return !ModestyMath.OutsiderKnown(world, npc);
    }

    internal static void TryWearCarriedFavorite(
        WorldState world, NPCState npc, float forecastOutdoorTemperature)
    {
        if (!GiftGarmentChoice.CanDecide(world, npc) || !CanChooseForTaste(npc) ||
            npc.Plan.Status == PlanStatus.Active)
        {
            return;
        }

        ItemInstance best = null;
        var bestAffinity = -1f;
        foreach (var item in npc.Inventory.Items)
        {
            if (item.OwnerId != npc.Id.Value ||
                !IsPreferred(world, npc, item.DefinitionId, item.Durability, item.Wetness, forecastOutdoorTemperature) ||
                !InventoryMath.FitsAfterWear(world, npc, item))
            {
                continue;
            }

            var affinity = ItemAffinity.For(npc.Id.Value, item.DefinitionId);
            if (affinity > bestAffinity)
            {
                best = item;
                bestAffinity = affinity;
            }
        }

        if (best is not null) ExecutionSystem.WearCarriedItem(world, npc, best);
    }

    internal static float ArmorGainFromWearing(WorldState world, NPCState npc, ObjectDefinition candidate)
    {
        var (_, gain) = EquipmentMath.ItemValues(world, candidate.Id);
        foreach (var worn in npc.WornItems)
        {
            if (world.Content.ObjectDefinitions.TryGetValue(worn.DefinitionId, out var definition) &&
                WearSlotCatalog.Occupies(candidate, definition))
            {
                gain -= EquipmentMath.ItemValues(world, worn.DefinitionId).Armor;
            }
        }

        return gain;
    }
}

}
