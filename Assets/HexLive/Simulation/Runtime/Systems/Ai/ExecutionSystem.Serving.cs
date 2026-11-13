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
        if (selected == null || !MealServing.IsUsableSurface(world, npc, surface) ||
            !InventoryMath.ContainsReference(npc.Inventory.Items, selected) ||
            !MealServing.TryServe(world, npc, selected, out var total))
        {
            return FailHearthScene(world, npc, surface, "Serving preparation or knife is no longer available");
        }

        npc.Execution.TargetInventoryItem = null;
        ReleaseHearthClaim(npc, surface);
        if (SimTrace.Enabled)
            Trace.Debug(world, npc.Id, "MealServed",
                $"{selected.DefinitionId} -> {total} g of {MealServing.OutputFor(selected.DefinitionId)} on {surface.DefinitionId}");
        return true;
    }
}
}
