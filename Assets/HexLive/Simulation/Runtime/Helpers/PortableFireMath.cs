using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{
/// <summary>§175: the physical igniter wears only on success; a torch spends its own lifetime.</summary>
internal static class PortableFireMath
{
    internal const float IgnitionWear = 0.02f;
    internal static float TorchLifetimeTicks => System.Math.Max(1, WorldBalance.DayLengthTicks / 4);

    internal static string HeldTorch(WorldState world, NPCState npc)
    {
        if (!npc.Body.CanUseToolsOrWeapons || npc.IsCarryingPerson || npc.IsLyingDown(world.Tick)) return string.Empty;
        foreach (var item in npc.Inventory.Items)
            if (item.DefinitionId == ContentIds.Torch && item.Durability > 0f) return ContentIds.Torch;
        return string.Empty;
    }

    internal static void WearIgniter(WorldState world, NPCState npc)
    {
        var tool = npc.Execution.ActionItemsBound ? npc.Execution.ActionTool :
            GearCatalog.BestToolFor(npc.Inventory.Items, GearCapability.Ignite, npc.Body.IntactHands);
        if (tool == null || !InventoryMath.ContainsReference(npc.Inventory.Items, tool) ||
            (GearCatalog.For(tool.DefinitionId).Capabilities & GearCapability.Ignite) == 0) return;
        float wear = tool.DefinitionId switch
        {
            ContentIds.DisposableLighter => .04f,
            ContentIds.WindproofLighter => .01f,
            _ => IgnitionWear,
        };
        tool.Durability = System.Math.Max(0f, tool.Durability - wear);
        if (tool.Durability <= 0.000001f)
        {
            InventoryMath.RemoveReference(npc.Inventory.Items, tool);
            if (SimTrace.Enabled) Trace.Debug(world, npc.Id, "ItemDestroyed", $"{tool.DefinitionId} used up lighting fires");
        }
    }

    internal static void AdvanceLights(System.Collections.Generic.List<ItemInstance> items, int ticks)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.DefinitionId == ContentIds.Flashlight)
            {
                FlashlightMath.DrainItem(item, ticks);
                continue;
            }
            if (item.DefinitionId != ContentIds.Torch) continue;
            item.Durability = System.Math.Max(0f, item.Durability - ticks / TorchLifetimeTicks);
            if (item.Durability <= 0f) items.RemoveAt(i);
        }
    }
}
}
