using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>§176: the battery and switch belong to the same physical flashlight.</summary>
public static class FlashlightMath
{
    public const string SwitchOnInteractionId = "flashlight.on";
    public const string SwitchOffInteractionId = "flashlight.off";
    internal static float BatteryLifetimeTicks => System.Math.Max(1, WorldBalance.DayLengthTicks / 3);

    // Charge is Durability. ResourceAmount carries the switch through inventory, saves and wire snapshots.
    public static bool IsOn(float switchState, float charge) => switchState > 0f && charge > 0f;

    internal static string HeldId(WorldState world, NPCState npc)
    {
        if (!npc.Body.HasUsableHand || npc.IsCarryingPerson || npc.IsLyingDown(world.Tick)) return null;
        foreach (var item in npc.Inventory.Items)
            if (item.DefinitionId == ContentIds.Flashlight && IsOn(item.ResourceAmount, item.Durability))
                return ContentIds.Flashlight;
        return null;
    }

    internal static bool TryToggleCarried(NPCState npc, ItemInstance item, out string reason)
    {
        var switchOn = !IsOn(item.ResourceAmount, item.Durability);
        reason = SwitchRefusal(npc, switchOn, item.Durability);
        if (reason != null) return false;
        item.ResourceAmount = switchOn ? 1f : 0f;
        reason = string.Empty;
        return true;
    }

    internal static string GroundSwitchRefusal(NPCState npc, WorldObjectState obj, string interactionId)
    {
        if (obj.DefinitionId != ContentIds.Flashlight ||
            interactionId is not (SwitchOnInteractionId or SwitchOffInteractionId)) return "NoSuchAction";
        return SwitchRefusal(npc, interactionId == SwitchOnInteractionId, obj.Durability);
    }

    private static string SwitchRefusal(NPCState npc, bool switchOn, float charge)
    {
        if (!npc.Body.HasUsableHand) return "NeedsHand";
        return switchOn && charge <= 0f ? "BatteryEmpty" : null;
    }

    internal static void DrainItem(ItemInstance item, int elapsed)
    {
        if (item.ResourceAmount <= 0f) return;
        item.Durability = ChargeAfter(item.Durability, elapsed);
        if (item.Durability == 0f) item.ResourceAmount = 0f;
    }

    internal static void DrainGround(WorldObjectState obj, int elapsed)
    {
        if (obj.ResourceAmount <= 0f) return;
        obj.Durability = ChargeAfter(obj.Durability, elapsed);
        if (obj.Durability == 0f) obj.ResourceAmount = 0f;
    }

    private static float ChargeAfter(float charge, int elapsed)
    {
        var remaining = charge - elapsed / BatteryLifetimeTicks;
        return remaining > .00001f ? remaining : 0f;
    }
}

}
