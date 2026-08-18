namespace HexLive.Simulation.Bootstrap
{

// §146: which scenario a world was created as. A property of the WORLD, not of
// the build: chosen once at creation, carried by the save headers (HXLV/HXLS),
// the save blob and the network handshake, and never changed afterwards —
// static topology is regenerated from (seed, mode) on every load and on every
// connecting client, so both ends must agree before worldgen runs.
//
// APPEND-ONLY — the ordinal travels in saves and on the wire.
public enum GameMode
{
    // §146.1: the shipped game — the 29×23 island, three girls in a finished
    // hut, the outsider camp and the §72.14 raid waves.
    Feud = 0,

    // §146.1: the big island — ~6× the tiles, dense passable groves, three
    // rival girl camps (§146.3), no human enemies, no starting buildings.
    BigIsland = 1
}

}
