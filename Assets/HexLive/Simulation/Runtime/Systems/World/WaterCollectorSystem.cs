using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

// §54.15: rain runs down the collector's leaf funnel into the parked bottle.
// While a rain front is over the island (EnvironmentState.IsRaining, seeded by
// WeatherSystem) every collector with a vessel in its slot gains fill; a full
// bottle takes WaterCollectorFillTicks of steady rain (a quarter day). Fill
// survives dry spells — a half-full bottle just waits for the next front.
public sealed class WaterCollectorSystem : ISimulationSystem
{
    public string Name => nameof(WaterCollectorSystem);

    public TickLayer Layer => TickLayer.Slow;

    // Elapsed-ticks bookkeeping instead of assuming the slow-layer cadence —
    // robust to SlowInterval tuning and to save/load (one zero-delta run).
    private long _lastRunTick = -1;

    public void Run(WorldState world)
    {
        var elapsed = _lastRunTick < 0 ? 0 : world.Tick - _lastRunTick;
        _lastRunTick = world.Tick;
        if (elapsed <= 0 || !world.Environment.IsRaining)
        {
            return;
        }

        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != WaterCollectorMath.CollectorId)
            {
                continue;
            }

            var vessel = WaterCollectorMath.FindVessel(world, obj);
            if (vessel is null || vessel.ResourceAmount >= 1f)
            {
                continue;
            }

            vessel.ResourceAmount = System.Math.Min(
                1f, vessel.ResourceAmount + (float)elapsed / SimBalance.WaterCollectorFillTicks);
            if (vessel.ResourceAmount >= 1f)
            {
                if (SimTrace.Enabled)
                {
                    Trace.DebugSystem(world, "VesselFull",
                        $"Collector={obj.Id.Value}: the parked bottle is full of rain water");
                }
            }
        }
    }
}

}
