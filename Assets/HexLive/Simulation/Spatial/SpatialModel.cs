using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{

public sealed class FragmentMap
{
    public Dictionary<FragmentId, Fragment> Items { get; } = new();
}

public sealed class TileMap
{
    public Dictionary<TileCoord, Tile> Items { get; } = new();
}

public sealed class JunctionMap
{
    public Dictionary<JunctionId, Junction> Items { get; } = new();
}

public sealed class Fragment
{
    public FragmentId Id { get; set; }

    public Dictionary<TileCoord, Tile> Tiles { get; } = new();

    public List<FragmentLink> Links { get; } = new();
}

public sealed class Tile
{
    public TileCoord Coord { get; set; } = TileCoord.Zero;

    public TileFlags Flags { get; set; } = TileFlags.None;

    public List<JunctionId> Junctions { get; } = new();

    public float TemperatureModifier { get; set; }
}

public sealed class Junction
{
    public JunctionId Id { get; set; }

    public FragmentId Fragment { get; set; }

    public Float2 WorldPosition { get; set; } = Float2.Zero;

    public List<TileCoord> Tiles { get; } = new();

    public bool Blocked { get; set; }

    public List<JunctionId> Neighbors { get; } = new();
}

public sealed class FragmentLink
{
    public FragmentId A { get; set; }

    public FragmentId B { get; set; }

    public TileCoord EntryA { get; set; } = TileCoord.Zero;

    public TileCoord EntryB { get; set; } = TileCoord.Zero;
}

[System.Flags]
public enum TileFlags
{
    None = 0,
    Walkable = 1 << 0,
    Blocked = 1 << 1,
    Indoor = 1 << 2
}

}
