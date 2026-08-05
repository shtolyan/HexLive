using HexLive.Simulation.Core;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Navigation;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Memory;
using HexLive.Simulation.Social;

namespace HexLive.Simulation.Runtime
{

// Spec 29E.3: campfires burn their fuel down; FireOut when it runs dry.
public sealed class FireSystem : ISimulationSystem
{
    public string Name => nameof(FireSystem);

    public TickLayer Layer => TickLayer.Slow;

    private static float BurnPerSlowTick => WorldBalance.FireBurnPerSlowTick;

    public void Run(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.ResourceAmount <= 0f ||
                !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) ||
                !definition.Tags.Contains("Campfire"))
            {
                continue;
            }

            // Spec 42: rain douses the fire — not instantly, but a downpour
            // eats fuel 4x faster, so a full stack dies in ~40 game minutes.
            // A dry night by the fire is the warm-up plan; a wet one isn't.
            var burn = FuelBurnPerSlowTick(world, obj);

            obj.ResourceAmount = System.Math.Max(0f, obj.ResourceAmount - burn);
            if (obj.ResourceAmount <= 0f)
            {
                Trace.EmitSystem(world, "FireOut",
                    $"{obj.DefinitionId} at Tile={obj.Tile.Q},{obj.Tile.R} burned out" +
                    (world.Environment.IsRaining ? " (doused by rain)" : ""));
            }

            RoastHangingMeat(world, obj);
        }
    }

    // One source of truth for the actual fuel clock and §49.9's bedtime
    // reserve. reserveForRain plans for a downpour even when the sky is dry at
    // bedtime; the live burn still uses the current weather.
    internal static float FuelBurnPerSlowTick(
        WorldState world, WorldObjectState fire, bool reserveForRain = false)
    {
        var burn = BurnPerSlowTick *
            (reserveForRain || world.Environment.IsRaining ? 4f : 1f);
        // §54.14 (r2): a finished stone ring (stage 2) banks the coals —
        // fuel burns at half rate, so the same wood keeps the fire twice
        // as long.
        if (BuildSiteMath.CampfireRingComplete(fire))
        {
            burn *= SimBalance.CampfireRingBurnMultiplier;
        }

        return burn;
    }

    // §54.14 (r2): stage 3 — meat hung on the spit roasts while the fire is
    // lit. Roast progress rides on the hanging ItemInstance's ResourceAmount
    // (in ticks); when done the raw chunk becomes cooked meat and KEEPS
    // hanging until someone takes it (GetFood). A dead fire pauses the roast,
    // it never spoils on the spit.
    private static void RoastHangingMeat(WorldState world, WorldObjectState fire)
    {
        for (var i = 0; i < fire.Contents.Count; i++)
        {
            var item = fire.Contents[i];
            if (item.DefinitionId != ContentIds.MeatRaw)
            {
                continue;
            }

            item.ResourceAmount += BurnPerSlowTick; // slow tick = 16 base ticks
            if (item.ResourceAmount < SimBalance.MeatRoastDurationTicks)
            {
                continue;
            }

            fire.Contents[i] = new ItemInstance(ContentIds.MeatCooked);
            Trace.EmitSystem(world, "MeatRoasted",
                $"food.meat_raw -> food.meat_cooked on the spit at " +
                $"Tile={fire.Tile.Q},{fire.Tile.R} " +
                $"(hanging cooked={BuildSiteMath.HangingMeat(fire, ContentIds.MeatCooked)})");
        }
    }
}

}
