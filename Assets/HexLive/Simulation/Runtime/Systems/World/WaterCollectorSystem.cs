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

    public void Run(WorldState world)
    {
        foreach (var obj in world.Entities.Objects.Values)
        {
            if (obj.DefinitionId != WaterCollectorMath.CollectorId)
            {
                continue;
            }

            if (!ChunkMath.IsAwake(world, obj.Tile))
            {
                continue;
            }

            // §156: такты считаются двумя источниками, и это не дублирование.
            // ТЕКУЩИЙ такт берётся у мира: Environment.IsRaining — единственная
            // правда про «сейчас», её выставляет WeatherSystem раньше в реестре,
            // и её же подменяют стенды и тесты, которым нужен дождь по заказу.
            // ПРОСПАННЫЕ такты у мира спросить не у кого — там работает
            // расписание (§156.5). Формула отвечает ровно за ту дыру, которую
            // без неё пришлось бы выдумывать.
            var slow = world.SlowIntervalTicks;
            var slept = RainMath.RainSlowTicksInWindow(
                world.Seed,
                ChunkMath.SleepWindowStart(world, obj.Tile),
                world.Tick - slow,
                slow);
            var rainSlowTicks = slept + (world.Environment.IsRaining ? 1L : 0L);
            if (rainSlowTicks <= 0)
            {
                continue;
            }

            var vessel = WaterCollectorMath.FindVessel(world, obj);
            if (vessel is null || vessel.ResourceAmount >= 1f)
            {
                continue;
            }

            vessel.ResourceAmount = System.Math.Min(
                1f,
                vessel.ResourceAmount +
                rainSlowTicks * slow / (float)SimBalance.WaterCollectorFillTicks);
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
