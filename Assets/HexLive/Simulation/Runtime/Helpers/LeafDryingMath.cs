using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§54 / #469: detached leaves age everywhere; rain wetness is separate.</summary>
public static class LeafDryingMath
{
    public static bool HasLeaves(string id) => id is ContentIds.PalmLeaf or
        "resource.palm_crown" or "resource.palm_crown_small" or ContentIds.BedBasic or
        ContentIds.BedLeaf or ContentIds.HutBed or ContentIds.WaterCollector or "architecture.roof.palm";

    public static float Fraction(int tick, int startTick) => startTick < 0 || SimBalance.PalmLeafWitherTicks <= 0
        ? 0f : Math.Clamp(((long)tick-startTick)/(float)SimBalance.PalmLeafWitherTicks,0f,1f);

    // Presentation precision only: avoid changing thousands of object delta
    // records every tick for an imperceptible shade change. Fuel uses Fraction.
    public static float VisualFraction(int tick,int startTick) => (float)Math.Floor(Fraction(tick,startTick)*500f)/500f;

    public static int Start(WorldObjectState obj) => obj.LeafDryingStartTick >= 0
        ? obj.LeafDryingStartTick : Math.Max(0,obj.SpawnTick);

    public static void Initialize(ItemInstance item, int tick)
    {
        if (HasLeaves(item.DefinitionId) && item.LeafDryingStartTick < 0)
            item.LeafDryingStartTick=Math.Max(0,tick);
    }

    public static bool IsDry(WorldState world, ItemInstance item) => item.DefinitionId == ContentIds.PalmLeaf &&
        Fraction(world.Tick,item.LeafDryingStartTick) >= 1f;

    public static float HeldFraction(WorldState world,NPCState npc)
    {
        var id=ActionItemResolver.HeldId(world,npc);
        if (!HasLeaves(id)) return 0f;
        var item=npc.Execution.CurrentInteraction==InteractionType.Fuel
            ? ContainerLootMath.FindCarriedCampfireFuel(world,npc)
            : npc.Execution.TargetInventoryItem?.DefinitionId==id ? npc.Execution.TargetInventoryItem
            : npc.Inventory.Items.Find(candidate=>candidate.DefinitionId==id);
        return item is not null ? VisualFraction(world.Tick,item.LeafDryingStartTick) : 0f;
    }

    public static void Receive(WorldObjectState destination, ItemInstance leaf, int tick)
    {
        if (leaf.DefinitionId != ContentIds.PalmLeaf) return;
        Initialize(leaf,tick);
        destination.LeafDryingStartTick=destination.LeafDryingStartTick < 0
            ? leaf.LeafDryingStartTick : Math.Min(destination.LeafDryingStartTick,leaf.LeafDryingStartTick);
    }

    // Old carried leaves had no clock. Start them at restore, never invent an
    // age or silently mark them all dry. New harvested instances start at harvest.
    public static void InitializeCarried(WorldState world)
    {
        foreach (var npc in world.Entities.Npcs.Values)
        {
            foreach (var item in npc.Inventory.Items) Initialize(item,world.Tick);
            foreach (var item in npc.WornItems) Initialize(item,world.Tick);
        }
    }
}
}
