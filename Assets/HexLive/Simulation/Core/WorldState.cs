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

    // Runtime-spawned objects allocate ids from here; bootstrap ids stay below 1000.
    public int NextRuntimeObjectId { get; set; } = 1000;

    // Spec 29C.1: all chance rolls mix this seed.
    public int Seed { get; set; } = 12345;

    // Spec 29C.3: lightweight wildlife, not NPCs.
    public System.Collections.Generic.List<Wildlife.DogState> Dogs { get; } = new();

    public int NextDogId { get; set; } = 1;

    // Spec 35.1: junction connected-components cache for O(1) reachability.
    // TopologyVersion increments whenever junction blocking changes (walls).
    public int TopologyVersion { get; set; } = 1;

    public int ComponentsBuiltVersion { get; set; }

    public System.Collections.Generic.Dictionary<JunctionId, int> JunctionComponents { get; } = new();

    // Spec 35.3: the communal hut project (null once cleanup removes it).
    public BuildProject? Project { get; set; }

    // Spec 29F.1: prey.
    public System.Collections.Generic.List<Wildlife.RabbitState> Rabbits { get; } = new();

    public int NextRabbitId { get; set; } = 1;
}

// Spec 35.3: one communal hut — floor, five walls, one home-facing door.
public sealed class BuildProject
{
    public Common.TileCoord Tile { get; set; }

    public int DoorEdge { get; set; }

    public bool FloorDone { get; set; }

    public bool[] EdgeDone { get; } = new bool[6];

    public bool Completed { get; set; }
}

}
