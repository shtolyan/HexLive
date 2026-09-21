using System;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;

namespace HexLive.Simulation.Runtime
{
// §55.5: fractional fill is presentation data, not the floor-to-gulps drinking rule.
public static class BottleVisualMath
{
    public static float Fill(ItemInstance bottle) =>
        bottle is not null && bottle.DefinitionId == ContentIds.Bottle
            ? Fill(bottle.ResourceAmount, false) : 0f;

    public static float Fill(float amount, bool parked) =>
        float.IsNaN(amount) || float.IsInfinity(amount)
            ? 0f : Math.Clamp(amount / (parked ? 1f : SimBalance.BottleCapacity), 0f, 1f);

    public static WaterKind Appearance(ItemInstance bottle) =>
        bottle is null || Fill(bottle) <= 0f ? WaterKind.None
            : Appearance(bottle.LastAddedWaterKind, bottle.WaterKind);

    public static WaterKind Appearance(WaterKind lastAdded, WaterKind contents) =>
        lastAdded != WaterKind.None ? lastAdded : contents;
}
}
