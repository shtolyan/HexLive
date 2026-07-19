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

// Spec 35.1: O(1) reachability via connected components. Path BFS remains
// only for actual movement; every "can I get there at all" check uses this.
// Spec 35.5: seeded rain fronts — no state machine beyond a deadline tick.
public sealed class WeatherSystem : ISimulationSystem
{
    public string Name => nameof(WeatherSystem);

    public TickLayer Layer => TickLayer.Slow;

    public void Run(WorldState world)
    {
        // The schedule is a pure function of (seed, day) — a per-tick
        // Bernoulli roll mixed badly on the 16-tick stride (spec 35.5).
        var env = world.Environment;
        var day = world.Tick / EnvironmentSystem.DayLengthTicks;
        var raining = false;
        if (MathUtil.Hash01(world.Seed, day, 17, 3301) < 0.45f)
        {
            var start = day * EnvironmentSystem.DayLengthTicks +
                (int)(MathUtil.Hash01(world.Seed, day, 18, 3301) * 2100f);
            var duration = 300 + (int)(600f * MathUtil.Hash01(world.Seed, day, 19, 3302));
            raining = world.Tick >= start && world.Tick < start + duration;
            env.RainUntilTick = start + duration;
        }

        if (raining != env.IsRaining)
        {
            env.IsRaining = raining;
            Trace.EmitSystem(world, raining ? "RainStarted" : "RainStopped",
                raining ? $"Until={env.RainUntilTick}" : $"Tick={world.Tick}");
        }

        // §46 v2: the STORM SURGE — the sea claws logs back off the raft.
        // A seeded swing catastrophe (pure function of seed+day, like rain):
        // losing progress stretches the run, and a longer run means more
        // night-raid rolls — the two catastrophes compound into real 50/50
        // tension without making daily survival harsher.
        if (world.RaftProgress > 0 &&
            world.Tick == day * EnvironmentSystem.DayLengthTicks + StormSurgeOffsetTicks &&
            MathUtil.Hash01(world.Seed, day, 5151) < StormChancePerDay)
        {
            var washed = System.Math.Min(world.RaftProgress, StormRaftLogLoss);
            world.RaftProgress -= washed;
            Trace.EmitSystem(world, "StormSurge",
                $"-{washed} raft logs -> {world.RaftProgress}/{WorldState.RaftTarget} (day {day})");
        }

        // §63: the SURF GIFT — the tide beaches a random piece of clothing on
        // the shoreline every few days (~2-3 per week). Worn-out garments are
        // destroyed by wear, and every lost garment is lost pocket capacity —
        // the sea keeps the island's wardrobe from bottoming out. Seeded
        // schedule like rain/storms: a pure function of (seed, day).
        if (world.Tick == day * EnvironmentSystem.DayLengthTicks + SurfGiftOffsetTicks &&
            MathUtil.Hash01(world.Seed, day, 6363) < SurfGiftChancePerDay)
        {
            TrySpawnSurfGarment(world, day);
        }
    }

    // §63: pick a random garment from the wardrobe table and beach it on a
    // free land junction that touches the water. Arrives soaked and worn-in
    // (durability 0.55-0.95) — driftwood clothing, not a shop delivery.
    private static void TrySpawnSurfGarment(WorldState world, int day)
    {
        var wardrobe = GarmentLibrary.Active;
        if (wardrobe.Count == 0)
        {
            return;
        }

        Junction shore = null;
        var bestRoll = -1f;
        foreach (var junction in world.Junctions.Items.Values)
        {
            if (junction.Blocked || junction.Tiles.Count == 0 ||
                SpatialQueries.IsAllWaterJunction(world, junction.Id) ||
                !SpatialQueries.IsJunctionFree(world, junction.Id))
            {
                continue;
            }

            var touchesWater = false;
            foreach (var neighborId in junction.Neighbors)
            {
                if (SpatialQueries.IsAllWaterJunction(world, neighborId))
                {
                    touchesWater = true;
                    break;
                }
            }

            if (!touchesWater)
            {
                continue;
            }

            // Seeded shuffle: the highest per-junction hash wins — stable for
            // (seed, day), different spot every gift.
            var roll = MathUtil.Hash01(world.Seed, day, junction.Id.Value, 6364);
            if (roll > bestRoll)
            {
                bestRoll = roll;
                shore = junction;
            }
        }

        if (shore is null)
        {
            return;
        }

        var pick = (int)(MathUtil.Hash01(world.Seed, day, 6365) * wardrobe.Count);
        pick = System.Math.Min(pick, wardrobe.Count - 1);
        var garment = wardrobe[pick];

        var spawned = WorldObjectMutations.SpawnObject(
            world, garment.Id, shore.Fragment, shore.Tiles[0], shore.Id);
        spawned.Wetness = 1f;
        spawned.Durability = 0.55f + 0.4f * MathUtil.Hash01(world.Seed, day, 6366);
        spawned.Dirtiness = 0.1f + 0.2f * MathUtil.Hash01(world.Seed, day, 6367);
        Trace.EmitSystem(world, "SurfGift",
            $"{garment.Id} washed ashore at Tile={shore.Tiles[0].Q},{shore.Tiles[0].R} " +
            $"(dur={spawned.Durability:F2} day {day})");
    }

    // §46 v2: storm-surge catastrophe knobs. Offset 1600 keeps the tick on
    // the Slow (16-tick) grid this system runs on.
    private static float StormChancePerDay => WorldBalance.StormChancePerDay; // §21.21B v4 recalibration: circle-climb reshuffle left 4/12 lone-survivor TIMEOUTS (6-10 storms outpaced a solo raft rebuild) — fewer surges converts stalls into decided runs
    private static int StormRaftLogLoss => WorldBalance.StormRaftLogLoss;
    private static int StormSurgeOffsetTicks => WorldBalance.StormSurgeOffsetTicks;

    // §63 surf gift knobs (0.35/day ≈ 2-3 garments per 7 days).
    private static float SurfGiftChancePerDay => WorldBalance.SurfGiftChancePerDay;
    private static int SurfGiftOffsetTicks => WorldBalance.SurfGiftOffsetTicks;
}

}
