using System.Collections.Generic;

namespace HexLive.Simulation.Bootstrap
{

public sealed class WorldBootstrapDefinition
{
    public SimulationBootstrapSettings Simulation { get; set; } = new();

    public EnvironmentBootstrap Environment { get; set; } = new();

    public List<FragmentBootstrap> Fragments { get; set; } = new();

    public List<ObjectBootstrap> Objects { get; set; } = new();

    public List<NpcBootstrap> Npcs { get; set; } = new();
}

public sealed class SimulationBootstrapSettings
{
    public float TickDeltaTime { get; set; } = 0.25f;

    public int MediumTickInterval { get; set; } = 4;

    public int SlowTickInterval { get; set; } = 16;
}

public sealed class EnvironmentBootstrap
{
    public float GlobalTemperature { get; set; } = 20f;
}

public sealed class FragmentBootstrap
{
    public int Id { get; set; }

    public List<TileBootstrap> Tiles { get; set; } = new();
}

public sealed class TileBootstrap
{
    public int Q { get; set; }

    public int R { get; set; }

    public bool Walkable { get; set; } = true;

    public bool Indoor { get; set; }

    public bool Blocked { get; set; }

    public List<int> BlockedSlots { get; set; } = new();
}

public sealed class ObjectBootstrap
{
    public int Id { get; set; }

    public string DefinitionId { get; set; } = string.Empty;

    public int FragmentId { get; set; }

    public int TileQ { get; set; }

    public int TileR { get; set; }

    public List<int> JunctionSlots { get; set; } = new();
}

public sealed class NpcBootstrap
{
    public int Id { get; set; }

    public int FragmentId { get; set; }

    public int TileQ { get; set; }

    public int TileR { get; set; }

    public float Hunger { get; set; }

    public float Energy { get; set; }

    public float Comfort { get; set; }

    public float Social { get; set; }

    public float ThermalDiscomfort { get; set; }
}

}
