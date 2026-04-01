using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;

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

public sealed class PointMap
{
    public Dictionary<PointId, Point> Items { get; } = new();
}

public sealed class ConnectionGroupMap
{
    public Dictionary<ConnectionGroupId, ConnectionGroup> Items { get; } = new();
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

    public List<PointId> Points { get; } = new();

    public float TemperatureModifier { get; set; }
}

public sealed class Point
{
    public PointId Id { get; set; }

    public FragmentId Fragment { get; set; }

    public TileCoord AnchorTile { get; set; } = TileCoord.Zero;

    public List<TileCoord> Tiles { get; } = new();

    public PointRole Role { get; set; } = PointRole.None;

    public Float2 LocalOffset { get; set; } = Float2.Zero;

    public PointKind Kind { get; set; } = PointKind.Interior;

    public ConnectionGroupId? ConnectionGroupId { get; set; }
}

public sealed class ConnectionGroup
{
    public ConnectionGroupId Id { get; set; }

    public List<PointId> Points { get; } = new();

    public List<TileCoord> Tiles { get; } = new();
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

public enum PointKind
{
    Interior,
    Connection
}

}
