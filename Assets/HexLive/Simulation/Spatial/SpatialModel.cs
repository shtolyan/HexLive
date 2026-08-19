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

    // Spec 20.16: 0 = sea floor, 1-2 lowland, 3 hills, 4-5 mountains.
    public int Elevation { get; set; } = 1;

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

    // PERF (Aug-2026, BigIsland): a process-wide stamp of the last Blocked
    // WRITE. The snapshot exporter walks every junction per tick only to
    // refresh this one flag — 194 557 iterations on the big island, ~74% of
    // the whole export. Blocked changes rarely (doors, builds, worldgen), so
    // the exporter skips the sweep while this stamp is unchanged. It lives in
    // the SETTER, not at the write sites, because a hand-bumped version is a
    // second place to forget — the exact trap the delta encoder refuses.
    // Process-wide on purpose: a bump from another world costs one harmless
    // extra sweep; a missed bump is impossible.
    public static long BlockedWriteVersion;

    private bool _blocked;

    public bool Blocked
    {
        get => _blocked;
        set
        {
            if (_blocked == value)
            {
                return;
            }

            _blocked = value;
            System.Threading.Interlocked.Increment(ref BlockedWriteVersion);
        }
    }

    // Spec 35.3: a door throat. Humans may use it only while !Blocked;
    // animals reject Door in either state.
    public bool Door { get; set; }

    public List<JunctionId> Neighbors { get; } = new();

    // §40.17 v2: the signed elevation change of the step this -> Neighbors[i],
    // precomputed at worldgen by the SAME resolver the hop arming uses
    // (HexPathfinder.TryGetDirectedStepTile), so pathfinding and execution agree
    // on what a jump is. 0 = flat, +1/-1 = a hop up/down.
    // Why per-EDGE and not per-junction: a seam junction borders both elevations,
    // so charging for ENTERING one also charged the detour that merely walks
    // ALONG the wall — the exact route the weight is supposed to encourage.
    // Worldgen-static (tile elevations never change at runtime) and rebuilt with
    // the graph when a save loads, so it is never serialized.
    public sbyte[] NeighborStepDelta;
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
    Indoor = 1 << 2,
    Water = 1 << 3, // spec 35.1: walkable shallows — river tiles
    HasFloor = 1 << 4, // spec 35.3: a built floor+roof piece
    Swimmable = 1 << 5, // spec 40.18: deep water an NPC can swim (4x cost, shark risk)
    // §35.4 r2: КРЫША, а не «дом». Indoor у нас носит второе, более широкое
    // значение — санктуарий (§72.12): им размечены и стартовый двор колонии, и
    // стоянка чужака, где никакой крыши нет и не рисуется. Солнце про это
    // ничего не знает: оно перекрывается ровно там, где над головой есть
    // перекрытие. Флаг ставится вместе с достройкой дома (см. CompleteHut).
    Roofed = 1 << 6
}

}
