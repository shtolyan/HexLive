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
        // The schedule is a pure function of (seed, cycle) — a per-tick
        // Bernoulli roll mixed badly on the 16-tick stride (spec 35.5).
        // NOTE: the index is the EVENT CYCLE (2400 ticks), not the visual day
        // (24000). The clock was stretched 10x for the calendar's sake; keying
        // the weather off it would have made rain 10x rarer in REAL time.
        var env = world.Environment;
        var cycle = world.Tick / EnvironmentSystem.EventCycleTicks;
        var raining = false;
        if (MathUtil.Hash01(world.Seed, cycle, 17, 3301) < 0.45f)
        {
            var start = cycle * EnvironmentSystem.EventCycleTicks +
                (int)(MathUtil.Hash01(world.Seed, cycle, 18, 3301) * 2100f);
            var duration = 300 + (int)(600f * MathUtil.Hash01(world.Seed, cycle, 19, 3302));
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
        // A seeded swing catastrophe (pure function of seed+cycle, like rain):
        // losing progress stretches the run, and a longer run means more
        // night-raid rolls — the two catastrophes compound into real 50/50
        // tension without making daily survival harsher.
        if (world.RaftProgress > 0 &&
            world.Tick == cycle * EnvironmentSystem.EventCycleTicks + StormSurgeOffsetTicks &&
            MathUtil.Hash01(world.Seed, cycle, 5151) < StormChancePerDay)
        {
            var washed = System.Math.Min(world.RaftProgress, StormRaftLogLoss);
            world.RaftProgress -= washed;
            Trace.EmitSystem(world, "StormSurge",
                $"-{washed} raft logs -> {world.RaftProgress}/{WorldState.RaftTarget} (cycle {cycle})");
        }

        // §63: the SURF GIFT — at 06:00 after every five complete visual days,
        // the tide beaches one random girl-compatible garment per living girl
        // in the player's colony. Tick 0 is the beginning of day one, not a
        // delivery; the first delivery is tick 5*DayLengthTicks (day 6, 06:00).
        // The schedule is deterministic and follows the actual game clock, not
        // the short weather/raid event cycle.
        var elapsedDays = world.Tick / EnvironmentSystem.DayLengthTicks;
        if (world.Tick > 0 &&
            world.Tick % EnvironmentSystem.DayLengthTicks == 0 &&
            elapsedDays % SurfGiftIntervalDays == 0)
        {
            TrySpawnSurfGarments(world, elapsedDays, CountColonyGirls(world));
        }
    }

    private static int CountColonyGirls(WorldState world)
    {
        var count = 0;
        foreach (var npc in world.Entities.Npcs.Values)
        {
            // Entities.Npcs is the living roster. "In our camp" means colony
            // membership, not where she happens to stand at 06:00: a hunter on
            // the far beach is still one of the camp's girls.
            if (npc.Faction == Faction.Colony && npc.Sex == GarmentSex.Female)
            {
                count++;
            }
        }

        return count;
    }

    // §63: choose one garment wearable by a female body and one distinct free
    // shoreline junction for every girl. Each piece arrives soaked and worn-in
    // (durability 0.55-0.95) — driftwood clothing, not a shop delivery.
    private static void TrySpawnSurfGarments(WorldState world, int giftDay, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var wardrobe = new System.Collections.Generic.List<GarmentParams>();
        foreach (var garment in GarmentLibrary.Active)
        {
            if (garment.Sex != GarmentSex.Male)
            {
                wardrobe.Add(garment);
            }
        }

        if (wardrobe.Count == 0)
        {
            return;
        }

        for (var giftIndex = 0; giftIndex < count; giftIndex++)
        {
            var shore = FindFreeShore(world, giftDay, giftIndex);
            if (shore is null)
            {
                break;
            }

            var pick = (int)(MathUtil.Hash01(world.Seed, giftDay, giftIndex, 6365) * wardrobe.Count);
            pick = System.Math.Min(pick, wardrobe.Count - 1);
            var garment = wardrobe[pick];

            var spawned = WorldObjectMutations.SpawnObject(
                world, garment.Id, shore.Fragment, shore.Tiles[0], shore.Id);
            spawned.Wetness = 1f;
            spawned.Durability = 0.55f + 0.4f *
                MathUtil.Hash01(world.Seed, giftDay, giftIndex, 6366);
            spawned.Dirtiness = 0.1f + 0.2f *
                MathUtil.Hash01(world.Seed, giftDay, giftIndex, 6367);
            Trace.EmitSystem(world, "SurfGift",
                $"{garment.Id} washed ashore at Tile={shore.Tiles[0].Q},{shore.Tiles[0].R} " +
                $"(dur={spawned.Durability:F2} day {giftDay} gift {giftIndex + 1}/{count})");
        }
    }

    private static Junction FindFreeShore(WorldState world, int giftDay, int giftIndex)
    {
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
            // (seed, day, item), different spot for every piece. Earlier gifts
            // are already occupied and therefore excluded from later picks.
            var roll = MathUtil.Hash01(
                world.Seed, giftDay, junction.Id.Value, 6364 + giftIndex * 17);
            if (roll > bestRoll)
            {
                bestRoll = roll;
                shore = junction;
            }
        }

        return shore;
    }

    // §46 v2: storm-surge catastrophe knobs. Offset 1600 keeps the tick on
    // the Slow (16-tick) grid this system runs on.
    private static float StormChancePerDay => WorldBalance.StormChancePerDay; // §21.21B v4 recalibration: circle-climb reshuffle left 4/12 lone-survivor TIMEOUTS (6-10 storms outpaced a solo raft rebuild) — fewer surges converts stalls into decided runs
    private static int StormRaftLogLoss => WorldBalance.StormRaftLogLoss;
    private static int StormSurgeOffsetTicks => WorldBalance.StormSurgeOffsetTicks;

    // §63 surf gift cadence in complete visual days. Defensive clamp keeps a
    // malformed remote simdata value from causing a modulo-by-zero crash.
    private static int SurfGiftIntervalDays =>
        System.Math.Max(1, WorldBalance.SurfGiftIntervalDays);
}

}
