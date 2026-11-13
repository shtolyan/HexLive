using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

public sealed partial class ExecutionSystem
{
    private static bool TryBindServingPreparation(
        WorldState world, NPCState npc, WorldObjectState surface)
    {
        if (!MealServing.IsUsableSurface(world, npc, surface) || !MealServing.HasKnife(npc))
            return false;

        var selected = npc.Execution.TargetInventoryItem;
        if (selected == null && string.IsNullOrEmpty(npc.Plan.TargetItemDefinitionId))
            selected = MealServing.FirstPreparation(npc);
        if (selected == null || !MealServing.IsReadyToServe(selected.DefinitionId) ||
            !InventoryMath.ContainsReference(npc.Inventory.Items, selected))
            return false;

        npc.Execution.TargetInventoryItem = selected;
        npc.Plan.TargetItemDefinitionId = selected.DefinitionId;
        return true;
    }

    private static bool CompleteServing(WorldState world, NPCState npc, WorldObjectState surface)
    {
        var selected = npc.Execution.TargetInventoryItem;
        if (selected == null || !MealServing.IsUsableSurface(world, npc, surface) || !MealServing.HasKnife(npc) ||
            !MealServing.IsReadyToServe(selected.DefinitionId) ||
            !InventoryMath.RemoveReference(npc.Inventory.Items, selected))
        {
            return FailHearthScene(world, npc, surface, "Serving preparation or knife is no longer available");
        }

        var outputId = MealServing.OutputFor(selected.DefinitionId);
        var portionSize = outputId == ContentIds.CrabCooked ? FoodMath.BiteGrams : FoodMath.MeatPortionGrams;
        var total = MealServing.PortionGrams(selected);
        for (var left = total; left > 0; left -= portionSize)
        {
            GiveOrDrop(world, npc, new ItemInstance(outputId)
            {
                ResourceAmount = Math.Min(left, portionSize)
            });
        }

        npc.Execution.TargetInventoryItem = null;
        ReleaseHearthClaim(npc, surface);
        if (SimTrace.Enabled)
            Trace.Debug(world, npc.Id, "MealServed", $"{selected.DefinitionId} -> {total} g of {outputId}");
        return true;
    }
}
}
