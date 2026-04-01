using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Core
{

public sealed class WorldState
{
    public int Tick { get; set; }

    public float TickDeltaTime { get; set; }

    public FragmentMap Fragments { get; } = new();

    public TileMap Tiles { get; } = new();

    public JunctionMap Junctions { get; } = new();

    public EntityRepository Entities { get; } = new();

    public ReservationState Reservations { get; } = new();

    public OccupancyState Occupancy { get; } = new();

    public EnvironmentState Environment { get; } = new();

    public RuntimeCaches Caches { get; } = new();

    public SimulationEventBuffer Events { get; } = new();

    public ContentCatalog Content { get; } = new();
}

}
